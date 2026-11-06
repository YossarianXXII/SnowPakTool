using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SnowPakTool.Desktop;

internal sealed record PresetOverwriteItem(string Area, string State, string Target, string Change, string LocalValue, string PresetResult);

internal static class PresetOverwriteReview
{
    public static IReadOnlyList<PresetOverwriteItem> Build(string workspace, SharedPresetDocument preset,
        IReadOnlyList<WorkspaceChange> editorChanges, string? preferences = null)
    {
        var rows = new List<PresetOverwriteItem>();
        var initial = preset.Files.Where(p => p.Area == "initial").ToDictionary(p => p.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        var maps = preset.Files.Where(p => p.Area == "map").ToDictionary(p => Path.GetFileNameWithoutExtension(p.Path), StringComparer.OrdinalIgnoreCase);
        string Relative(string target) => Path.IsPathRooted(target) ? WorkspaceOriginals.GetRelativePath(workspace, target).Replace('\\', '/') : target.Replace('\\', '/');
        string MapResult(string map) => maps.ContainsKey(map) ? "Replace with shared map result" : "Remove local map output; use original map";
        string InitialResult(string path, string property)
        {
            if (!initial.ContainsKey(path)) return File.Exists(WorkspaceOriginals.GetOriginalReadPath(workspace, path)) ? "Restore original file values" : "Remove local added file";
            var detail = preset.Review?.FirstOrDefault(c => c.Target.Equals(path, StringComparison.OrdinalIgnoreCase) && c.Property == property);
            return detail is null ? "Replace with shared file values" : detail.After;
        }
        var audited = WorkspaceChangeAudit.PakFiles(workspace);
        foreach (var change in audited.Concat(editorChanges))
        {
            if (change.Property.StartsWith("Upgrade randomization:", StringComparison.Ordinal)) continue;
            var path = Relative(change.Target);
            rows.Add(new("Initial PAK", change.State == WorkspaceChange.Pending ? "Staged / editor" : change.State,
                path, change.Property, change.Before + " → " + change.After, InitialResult(path, change.Property)));
        }
        // Audit covers the editor formats; retain file-level coverage for load lists
        // and any other changed files that the existing audit does not interpret.
        var updated = WorkspaceOriginals.GetUpdatedRoot(workspace);
        var covered = audited.Select(c => Relative(c.Target)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(updated)) foreach (var file in Directory.EnumerateFiles(updated, "*", SearchOption.AllDirectories))
        {
            var path = Relative(file); if (covered.Contains(path)) continue;
            var original = WorkspaceOriginals.GetOriginalReadPath(workspace, path);
            if (File.Exists(original) && Hash(original) == Hash(file)) continue;
            rows.Add(new("Initial PAK", "In files", path, File.Exists(original) ? "Changed file" : "Added file",
                $"{new FileInfo(file).Length:N0} bytes", InitialResult(path, "")));
        }
        foreach (var change in WorkspaceChangeAudit.MapFiles(workspace).Where(c => c.State == WorkspaceChange.InFiles))
            rows.Add(new("Maps", "In files", change.Target, change.Property, change.Before + " → " + change.After, MapResult(change.Target)));
        foreach (var plan in PendingTruckReplacements.Load(workspace))
        {
            var placement = plan.Original.PlacementId.Length > 0 ? plan.Original.PlacementId : $"Placement {plan.Original.PlacementIndex}";
            rows.Add(new("Maps", plan.Staged ? "Staged" : "Draft", plan.Original.MapCode + " / " + placement,
                plan.ResetOriginal ? "Reset truck / equipment" : "Truck / equipment replacement",
                plan.Original.Truck + " → " + plan.Target.Name + (plan.Configuration is null ? "" : "\n" + JsonSerializer.Serialize(plan.Configuration)), MapResult(plan.Original.MapCode)));
        }
        foreach (var assignment in PendingTruckReplacements.LoadAssignments(workspace))
            rows.Add(new("Maps", "Saved assignment", assignment.PlacementKey, "Randomized truck / equipment",
                assignment.Target.Name + "\n" + JsonSerializer.Serialize(assignment.Configuration), "Replace saved randomization assignment"));
        var stagedPath = WorkspaceOriginals.GetMetadataPath(workspace, "upgrade-randomization", "staged.json");
        if (File.Exists(stagedPath))
        {
            var staged = JsonSerializer.Deserialize<UpgradeChangeSnapshot>(File.ReadAllText(stagedPath))
                ?? throw new InvalidDataException("Cannot read staged upgrade changes for review.");
            foreach (var edit in staged.Edits)
                rows.Add(new("Upgrades", "Staged", edit.Source,
                    edit.Zone.Length > 0 ? edit.Zone + " / Pickup " + edit.RewardIndex : edit.Element + " / " + edit.Variant + " / Exploration unlock",
                    (edit.Before ?? "Inherited") + " → " + edit.After, InitialResult(edit.Source.Replace('\\', '/'), "")));
        }
        var draft = UpgradeTrialStore.Load(workspace);
        if (draft is not null)
        {
            foreach (var assignment in draft.Assignments)
                rows.Add(new("Upgrades", "Draft", assignment.Slot.Map + " / " + assignment.Slot.Zone + " / " + assignment.Slot.RewardIndex,
                    "Upgrade pickup", assignment.Slot.OriginalUpgradeId + " → " + assignment.UpgradeId, "Replace with shared upgrade results"));
            foreach (var key in draft.ResetKeys ?? [])
                rows.Add(new("Upgrades", "Draft", key, "Reset upgrade pickup", "Restore original pickup", "Replace with shared upgrade results"));
            if (draft.Assignments.Count == 0 && (draft.ResetKeys?.Count ?? 0) == 0)
                rows.Add(new("Upgrades", "Draft", "Upgrade randomizer", "Saved draft", "Seed " + draft.Seed, "Clear local draft"));
        }
        foreach (var (path, incoming) in preset.Planning)
            CompareJson("Planning", path, ReadJson(Path.Combine(preferences ?? SharedPreset.PreferencesRoot, path)), incoming, rows);
        foreach (var (path, incoming) in preset.Metadata)
        {
            if (path == "region-maps/pending-replacements.json") continue;
            CompareJson("Settings", path, ReadJson(WorkspaceOriginals.GetMetadataPath(workspace, path.Replace('/', Path.DirectorySeparatorChar))), incoming, rows);
        }
        if (SharedPreset.HasPending(workspace))
        {
            foreach (var change in SharedPreset.Pending(workspace))
                rows.Add(new("Shared preset", "Staged", change.SummaryName.Length > 0 ? change.SummaryName : Relative(change.Target),
                    change.Property, change.Before + " → " + change.After, "Replace staged preset"));
        }
        return rows.Distinct().OrderBy(r => r.Area, StringComparer.Ordinal).ThenBy(r => r.State, StringComparer.Ordinal)
            .ThenBy(r => r.Target, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Change, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static string? ReadJson(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    private static void CompareJson(string area, string path, string? before, string? after, List<PresetOverwriteItem> rows)
    {
        var a = before is null ? null : JsonNode.Parse(before); var b = after is null ? null : JsonNode.Parse(after);
        void Visit(JsonNode? left, JsonNode? right, string property)
        {
            if (JsonNode.DeepEquals(left, right)) return;
            if (left is JsonObject lo && right is JsonObject ro)
            { foreach (var key in lo.Select(p => p.Key).Concat(ro.Select(p => p.Key)).Distinct()) Visit(lo[key], ro[key], property.Length == 0 ? key : property + "." + key); return; }
            string Display(JsonNode? node) => node is null ? "Defaults / not saved" : node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            rows.Add(new(area, "Saved", path, property.Length == 0 ? "Saved settings" : property, Display(left), Display(right)));
        }
        Visit(a, b, "");
    }
}

internal sealed class PresetOverwriteReviewDialog : Form
{
    public PresetOverwriteReviewDialog(string presetName, IReadOnlyList<PresetOverwriteItem> items)
    {
        Text = "Review changes replaced by shared preset"; StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1240, 740); MinimumSize = new Size(850, 460); ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var explanation = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 10),
            Text = $"Loading {presetName} will replace the local work listed below ({items.Count:N0} entries).\n" +
                "Unstaged previews are discarded on load. Saved edits, drafts and settings are replaced only when you Apply; backups are kept in preset-history.\n" +
                "Cancel keeps the current setup. Hover cells for full values, or copy selected rows with Ctrl+C." };
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoGenerateColumns = false, BackgroundColor = AppColors.Get("Surface"), RowTemplate = { Height = 26 },
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText };
        var table = new DataTable();
        var columns = new[] { ("Area", 45), ("State", 60), ("Target", 140), ("Change", 150), ("Local changes being replaced", 170), ("Preset result", 160) };
        foreach (var (name, weight) in columns)
        {
            table.Columns.Add(name);
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, DataPropertyName = name, FillWeight = weight });
        }
        foreach (var item in items) table.Rows.Add(item.Area, item.State, item.Target, item.Change, item.LocalValue, item.PresetResult);
        grid.DataSource = table; TableHeaderBehavior.Attach(grid);
        grid.DefaultCellStyle.BackColor = AppColors.Get("Surface"); grid.AlternatingRowsDefaultCellStyle.BackColor = AppColors.Get("AlternateRow");
        grid.CellToolTipTextNeeded += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex >= 0) e.ToolTipText = Convert.ToString(grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value); };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.No };
        var replace = new Button { Text = "Replace with preset", AutoSize = true, DialogResult = DialogResult.Yes };
        buttons.Controls.Add(cancel); buttons.Controls.Add(replace); AcceptButton = cancel; CancelButton = cancel;
        layout.Controls.Add(explanation, 0, 0); layout.Controls.Add(grid, 0, 1); layout.Controls.Add(buttons, 0, 2); Controls.Add(layout);
        Shown += (_, _) => cancel.Focus(); TextAreaStyle.Watch(this);
    }
}
