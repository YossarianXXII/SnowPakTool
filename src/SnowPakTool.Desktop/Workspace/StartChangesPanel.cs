using System.Data;
using System.Text.RegularExpressions;

namespace SnowPakTool.Desktop;

internal sealed class StartChangesPanel : UserControl
{
    private sealed record PakChangeGroup(string Name, IReadOnlyList<WorkspaceChange> Changes);
    private sealed record MapChangeGroup(string RegionCode, IReadOnlyList<WorkspaceChange> Changes);
    private sealed record ChangeGroup(string Area, string Target, string Summary, IReadOnlyList<WorkspaceChange> Changes, bool IsMap = false);

    private readonly Func<string> _workspace;
    private readonly Func<string, IReadOnlyList<WorkspaceChange>> _pendingPak;
    private readonly Action _savePak;
    private readonly Func<Task> _applyMaps;
    private readonly Func<Task> _reloadRegions;
    private readonly DataGridView _changes = Grid();
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(4, 7, 4, 2) };
    private readonly Button _refresh = new() { Text = "Refresh changes", AutoSize = true };
    private readonly Button _applyButton = new() { Text = "Apply pending changes", AutoSize = true, Enabled = false };
    private readonly Button _discardMap = new() { Text = "Discard selected pending changes", AutoSize = true, Enabled = false };
    private readonly Button _repairMaps = new() { Text = "Repair saved maps", AutoSize = true, Enabled = false };
    private readonly List<Label> _legend = [];
    private int _request;
    private string _displayedWorkspace = "";
    private bool _applying;

    public event Action<int>? PendingCountChanged;
    internal event Action? ChangesApplied;

    public StartChangesPanel(Func<string> workspace, Control pakActions, Func<string, IReadOnlyList<WorkspaceChange>> pendingPak,
        Action savePak, Func<Task> applyMaps, Func<Task> reloadRegions)
    {
        _workspace = workspace; _pendingPak = pendingPak; _savePak = savePak; _applyMaps = applyMaps; _reloadRegions = reloadRegions;
        Dock = DockStyle.Fill;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var legend = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        legend.Controls.Add(_refresh);
        legend.Controls.Add(new Label { Text = "In files vs preserved originals", AutoSize = true, BackColor = AppColors.Get("DAF8E0"), Padding = new Padding(6), Margin = new Padding(8, 3, 3, 3) });
        legend.Controls.Add(new Label { Text = "Pending — not applied", AutoSize = true, BackColor = AppColors.Get("EngineMatch"), Padding = new Padding(6) });
        _legend.AddRange(legend.Controls.OfType<Label>());
        legend.Controls.Add(_status); layout.Controls.Add(legend, 0, 0);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        if (pakActions is FlowLayoutPanel flow)
        { flow.WrapContents = false; flow.AutoSizeMode = AutoSizeMode.GrowAndShrink; flow.Padding = Padding.Empty; }
        pakActions.Dock = DockStyle.None; pakActions.Margin = Padding.Empty; tools.Controls.Add(pakActions);
        tools.Controls.Add(_applyButton); tools.Controls.Add(_discardMap); tools.Controls.Add(_repairMaps);
        layout.Controls.Add(Section("Initial PAK and map changes", tools, _changes), 0, 1); Controls.Add(layout);
        _refresh.Click += async (_, _) => await RefreshAsync();
        _applyButton.Click += async (_, _) => await ApplyPendingAsync();
        _repairMaps.Click += async (_, _) =>
        {
            var workspacePath = _workspace().Trim(); _repairMaps.Enabled = false;
            try
            {
                var repaired = await Task.Run(() => TruckReplacementStore.RepairSavedMaps(workspacePath));
                await _reloadRegions(); await RefreshAsync();
                _status.Text = $"Repaired {repaired} saved map(s); previous versions backed up. Game files were not changed.";
            }
            catch (Exception ex) { await RefreshAsync(); _status.Text = "Could not repair maps: " + ex.Message; }
        };
        _discardMap.Click += async (_, _) =>
        {
            if (_applying || _changes.CurrentRow?.DataBoundItem is not DataRowView row || row["Entry"] is not ChangeGroup { IsMap: true } group) return;
            var keys = group.Changes.Where(change => change.State == WorkspaceChange.Pending && change.Key.Length > 0).Select(change => change.Key).ToArray();
            if (keys.Length == 0) return;
            _discardMap.Enabled = false;
            try
            {
                var count = PendingTruckReplacements.DiscardMany(_workspace(), keys, stagedOnly: true);
                await _reloadRegions(); await RefreshAsync();
                _status.Text = $"Discarded {count} pending map change(s) from the selected row.";
            }
            catch (Exception ex) { await RefreshAsync(); _status.Text = "Could not discard map changes: " + ex.Message; }
        };
        _changes.CurrentCellChanged += (_, _) => UpdateDiscardButton();
        _changes.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || _changes.Rows[e.RowIndex].DataBoundItem is not DataRowView row || row["Entry"] is not ChangeGroup group) return;
            e.ToolTipText = group.IsMap
                ? string.Join(Environment.NewLine, group.Changes.GroupBy(change => change.Target).Select(map => $"{map.Key}: {map.Count()} change(s)"))
                : string.Join(Environment.NewLine, group.Changes.Select(change => $"{change.Target}{Environment.NewLine}{change.Property}: {change.Before} → {change.After}"));
        };
        _changes.Sorted += (_, _) => PaintRows(_changes);
        _changes.DataBindingComplete += (_, _) => { PaintRows(_changes); UpdateDiscardButton(); };
        AppColors.Changed += ColorsChanged; Disposed += (_, _) => AppColors.Changed -= ColorsChanged;
    }

    private async Task ApplyPendingAsync()
    {
        if (_applying) return;
        var workspace = _workspace().Trim();
        if (!Directory.Exists(workspace)) return;
        _applying = true; _applyButton.Enabled = _discardMap.Enabled = _repairMaps.Enabled = _refresh.Enabled = false;
        string message; var succeeded = false;
        try
        {
            // These remain separate stores. If the second operation fails, the first
            // stays applied and refresh shows the remaining staged work for retry.
            var pending = _pendingPak(workspace);
            var sharedPreset = pending.Any(c => c.SummaryGroup == "shared:planning");
            if (pending.Count > 0) _savePak();
            if (!workspace.Equals(_workspace().Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Workspace changed during Apply. Review the remaining pending changes.");
            if (!sharedPreset && PendingTruckReplacements.LoadStaged(workspace).Count > 0) await _applyMaps();
            await _reloadRegions();
            succeeded = true;
            message = "Pending changes applied to the workspace. Pack the initial PAK as needed; install the resulting PAK and map files manually.";
        }
        catch (Exception ex) { message = "Apply stopped: " + ex.Message + " Completed changes remain in files; remaining changes stay pending."; }
        finally { _applying = false; await RefreshAsync(); }
        _status.Text = message;
        if (succeeded) ChangesApplied?.Invoke();
    }

    private void UpdateDiscardButton() => _discardMap.Enabled = !_applying && _changes.CurrentRow?.DataBoundItem is DataRowView row
        && row["Entry"] is ChangeGroup { IsMap: true } group
        && group.Changes.Any(change => change.State == WorkspaceChange.Pending && change.Key.Length > 0);

    public async Task RefreshAsync()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        { await InvokeAsync(async (CancellationToken _) => { await RefreshAsync(); }); return; }
        var request = ++_request; var workspace = _workspace().Trim();
        if (_displayedWorkspace != workspace) { BindChanges([]); _displayedWorkspace = workspace; }
        _applyButton.Enabled = _discardMap.Enabled = _repairMaps.Enabled = false;
        if (!Directory.Exists(workspace)) { BindChanges([]); _status.Text = "Choose a workspace."; PendingCountChanged?.Invoke(0); return; }
        _status.Text = "Comparing files…"; _refresh.Enabled = false;
        try
        {
            var pending = _pendingPak(workspace);
            var sharedPreset = pending.Any(c => c.SummaryGroup == "shared:planning");
            var result = await Task.Run(() => (Pak: WorkspaceChangeAudit.PakFiles(workspace), Maps: WorkspaceChangeAudit.MapFiles(workspace),
                Trucks: RegionTruckCatalogService.LoadTrucks(workspace, WorkspaceDataCache.GetLocalizedNames(workspace), [])));
            if (sharedPreset) result.Maps = result.Maps.Where(c => c.State != WorkspaceChange.Pending).ToArray();
            if (IsDisposed) return;
            await InvokeAsync(() =>
            {
                if (request != _request || workspace != _workspace().Trim()) return;
                BindChanges(PakGroups(result.Pak.Concat(pending).ToArray(), result.Trucks).Concat(MapGroups(result.Maps,
                    WorkspaceDataCache.GetRegionMetadata(workspace).ToDictionary(pair => pair.Key, pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase))));
                _applyButton.Enabled = !_applying && (pending.Count > 0 || result.Maps.Any(change => change.State == WorkspaceChange.Pending));
                _repairMaps.Enabled = !_applying && result.Maps.Any(change => change.Property == TruckReplacementStore.RepairRequired);
                PendingCountChanged?.Invoke(pending.Count + result.Maps.Count(change => change.State == WorkspaceChange.Pending));
                _status.Text = $"{result.Pak.Count + result.Maps.Count(change => change.State == WorkspaceChange.InFiles)} in files · {pending.Count + result.Maps.Count(change => change.State == WorkspaceChange.Pending)} pending";
            });
        }
        catch (Exception ex) { if (!IsDisposed) await InvokeAsync(() => { if (request == _request) _status.Text = "Could not compare changes: " + ex.Message; }); }
        finally { if (!IsDisposed) await InvokeAsync(() => { if (request == _request) _refresh.Enabled = !_applying; }); }
    }

    private static GroupBox Section(string title, Control tools, Control grid)
    {
        var box = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(8) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(tools, 0, 0); layout.Controls.Add(grid, 0, 1); box.Controls.Add(layout); return box;
    }

    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        BackgroundColor = AppColors.Get("Surface"), ColumnHeadersDefaultCellStyle = { Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) },
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowTemplate = { Height = 26 } };

    private static IEnumerable<ChangeGroup> PakGroups(IReadOnlyList<WorkspaceChange> changes, IReadOnlyDictionary<string, (string Name, string Class)> trucks)
    {
        (string Key, string Name) Identity(WorkspaceChange change, int index)
        {
            if (change.SummaryGroup.StartsWith("shared:", StringComparison.Ordinal))
                return (change.SummaryGroup, change.SummaryName);
            if (change.SummaryGroup.StartsWith("winch:", StringComparison.OrdinalIgnoreCase))
                return (change.SummaryGroup.ToLowerInvariant(), change.SummaryName);
            var path = change.Target.Replace('\\', '/');
            var match = Regex.Match(path, @"(?:^|/)classes/trucks/([^/]+)\.xml$", RegexOptions.IgnoreCase);
            var code = match.Groups[1].Value;
            if (trucks.TryGetValue(code, out var truck)) return ("truck:" + code.ToLowerInvariant(), truck.Name);
            // Applied XML changes identify the named variant, not just its shared file.
            var winch = Regex.Match(change.Property, @"^/WinchVariants\[[^\]]+\]/Winch\[([^\]]+)\](?:/|$)");
            if (Regex.IsMatch(path, @"(?:^|/)classes/winches/[^/]+\.xml$", RegexOptions.IgnoreCase) && winch.Success)
                return ("winch:" + path.ToLowerInvariant() + "/" + winch.Groups[1].Value, winch.Groups[1].Value);
            return ("entry:" + index, "");
        }
        var groups = changes.Select((change, index) => (Change: change, Identity: Identity(change, index)))
            .GroupBy(item => (item.Change.State, item.Identity.Key))
            .Select(group => new PakChangeGroup(group.First().Identity.Name, group.Select(item => item.Change).ToArray()));
        foreach (var group in groups)
        {
            var first = group.Changes[0];
            var target = group.Name.Length > 0 ? group.Name : first.Target;
            var parts = group.Changes.Select(change => BriefPakChange(change, group.Name)).ToArray();
            var summary = string.Join("; ", parts.Take(3)) + (parts.Length > 3 ? $"; +{parts.Length - 3} more" : "");
            var area = first.SummaryGroup.StartsWith("shared:map:", StringComparison.Ordinal) ? "Maps"
                : first.SummaryGroup == "shared:planning" ? "Planning" : "Initial PAK";
            yield return new ChangeGroup(area, target, summary, group.Changes);
        }
    }

    private static string BriefPakChange(WorkspaceChange change, string truckName)
    {
        var property = change.Property;
        foreach (var separator in new[] { ": ", " / " })
            if (truckName.Length > 0 && property.StartsWith(truckName + separator, StringComparison.OrdinalIgnoreCase)) property = property[(truckName.Length + separator.Length)..];
        var attribute = Regex.Match(property, @"/@([^/]+)$").Groups[1].Value;
        if (change.SummaryGroup.StartsWith("winch:", StringComparison.OrdinalIgnoreCase))
        {
            property = change.Property[(change.Property.LastIndexOf(" / ", StringComparison.Ordinal) + 3)..];
            attribute = property;
        }
        property = attribute switch
        {
            "FuelCapacity" => "Fuel capacity (L)", "Price" => "Price", "UnlockByRank" => "Unlock rank",
            "UnlockByExploration" => "Unlock by exploration", "UiName" => "Name",
            "Length" => "Length (m)", "StrengthMult" or "StrengthMultiplier" => "Strength multiplier",
            "IsEngineIgnitionRequired" or "IgnitionRequired" => "Ignition required",
            "Type" when property.Contains("EngineSocket", StringComparison.Ordinal) => "Engine compatibility",
            "Type" when property.Contains("WinchUpgradeSocket", StringComparison.Ordinal) => "Winch compatibility",
            "Default" when property.Contains("EngineSocket", StringComparison.Ordinal) => "Default engine",
            "Default" when property.Contains("WinchUpgradeSocket", StringComparison.Ordinal) => "Default winch",
            _ when attribute.Length > 0 => Regex.Replace(attribute, @"([a-z])([A-Z])", "$1 $2"),
            _ => property
        };
        var before = Regex.Replace(change.Before, @"\s+", " ").Trim();
        var after = Regex.Replace(change.After, @"\s+", " ").Trim();
        return before.Length + after.Length > 64 || before.Contains("<", StringComparison.Ordinal) || after.Contains("<", StringComparison.Ordinal)
            ? property + " updated" : $"{property}: {before} → {after}";
    }

    private static IEnumerable<ChangeGroup> MapGroups(IReadOnlyList<WorkspaceChange> changes, IReadOnlyDictionary<string, string> regionNames)
    {
        static string Region(WorkspaceChange change) => Regex.Match(change.Target, @"level_((?:us|ru)_\d{2})_", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
        static bool TruckChange(WorkspaceChange change) => Region(change).Length > 0 && (change.Property is "Truck randomization" or "Obtainable randomization" or "Reset to original"
            || change.Property.EndsWith(" / Truck", StringComparison.Ordinal) || change.Property.EndsWith(" / Installed equipment", StringComparison.Ordinal));
        var groups = changes.Where(TruckChange).GroupBy(change => (Region: Region(change), change.State))
            .Select(group => new MapChangeGroup(group.Key.Region, group.ToArray()))
            .Concat(changes.Where(change => !TruckChange(change)).Select(change => new MapChangeGroup(Region(change), [change])))
            .OrderBy(group => regionNames.GetValueOrDefault(group.RegionCode, group.RegionCode), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.Changes[0].State == WorkspaceChange.Pending ? 1 : 0).ToArray();
        foreach (var group in groups)
        {
            var first = group.Changes[0];
            var region = regionNames.GetValueOrDefault(group.RegionCode, group.RegionCode.Length > 0 ? group.RegionCode : first.Target);
            if (TruckChange(first))
            {
                var maps = group.Changes.Select(change => change.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                var resets = group.Changes.Count(change => change.Property == "Reset to original");
                var title = resets == group.Changes.Count ? "Reset to original" : "Truck / equipment changes";
                yield return new ChangeGroup("Maps", region, title + $": {group.Changes.Count} change(s) across {maps} map(s)"
                    + (resets > 0 ? $"; {resets} reset(s)" : "") + ". Details in Map trucks.", group.Changes, true);
            }
            else yield return new ChangeGroup("Maps", region, first.Target + " / " + first.Property + $": {first.Before} → {first.After}", group.Changes, true);
        }
    }

    private void BindChanges(IEnumerable<ChangeGroup> groups)
    {
        var data = new DataTable();
        foreach (var column in new[] { "State", "Area", "File / target", "Change" }) data.Columns.Add(column);
        data.Columns.Add("Entry", typeof(ChangeGroup));
        foreach (var group in groups) data.Rows.Add(group.Changes[0].State, group.Area, group.Target, group.Summary, group);
        TableHeaderBehavior.Attach(_changes);
        _changes.DataSource = data; _changes.Columns["Entry"]!.Visible = false;
        _changes.Columns["State"]!.FillWeight = 35; _changes.Columns["Area"]!.FillWeight = 35;
        _changes.Columns["File / target"]!.HeaderText = "Truck / winch / region / target";
        _changes.Columns["File / target"]!.FillWeight = 100;
        _changes.Columns["Change"]!.HeaderText = "Summary"; _changes.Columns["Change"]!.FillWeight = 220;
        PaintRows(_changes); UpdateDiscardButton();
    }

    private static void PaintRows(DataGridView grid)
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            var color = AppColors.Get(row.Cells["State"].Value?.ToString() == WorkspaceChange.Pending ? "EngineMatch" : "DAF8E0");
            row.DefaultCellStyle.BackColor = row.DefaultCellStyle.SelectionBackColor = color;
            row.DefaultCellStyle.ForeColor = row.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        }
    }
    private void ColorsChanged(object? sender, EventArgs e)
    {
        PaintRows(_changes);
        _changes.BackgroundColor = AppColors.Get("Surface");
        for (var i = 0; i < _legend.Count; i++) _legend[i].BackColor = AppColors.Get(i == 0 ? "DAF8E0" : "EngineMatch");
    }
}
