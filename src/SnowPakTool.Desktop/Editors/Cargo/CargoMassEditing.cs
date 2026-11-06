using System.Data;
using System.Globalization;

namespace SnowPakTool.Desktop;

internal sealed partial class TrucksCargoTab
{
    private Dictionary<string, CargoMassRow> _massRows = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CargoMassEdit> _stagedMass = new(StringComparer.OrdinalIgnoreCase);
    private long _loadedVersion;
    internal event Action? StagingChanged;
    private static string? MassRole(string column) => column == "Loose weight (kg)" ? "Loose" : column == "Packed weight (kg)" ? "Packed" : null;
    private void EditMassBodies(int rowIndex, int columnIndex)
    {
        if (_bindingCargo || rowIndex < 0 || columnIndex < 0 || _loadedWorkspace is null || _loadedWorkspace != _getWorkspacePath().Trim()
            || PresetBrowseMode.IsReadOnly(this) || SharedPreset.HasPending(_loadedWorkspace)
            || MassRole(_cargo.Columns[columnIndex].Name) is not { } role
            || _cargo.Rows[rowIndex].DataBoundItem is not DataRowView data || data["Entry"] is not CargoEntry entry) return;
        var targets = entry.MassTargets.Where(target => target.Role == role).ToArray();
        if (targets.Any(target => !_massRows.ContainsKey(CargoMassStore.Key(target))))
        { _status.Text = "Cargo details are being refreshed. Reload cargo before editing body masses."; return; }
        var rows = targets.Select(target => _massRows[CargoMassStore.Key(target)])
            .DistinctBy(row => CargoMassStore.Key(row.Target), StringComparer.OrdinalIgnoreCase).ToArray();
        if (rows.Length == 0) { _status.Text = $"No editable {role.ToLowerInvariant()} body definitions are available for {entry.Name}."; return; }
        using var dialog = new CargoMassDialog(entry.Name, role, rows);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        foreach (var (key, value) in dialog.Values) _massRows[key].Draft = value;
        UpdateMassCells();
    }
    private void InitializeMassEditing(FlowLayoutPanel toolbar)
    {
        void Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (_, _) =>
            {
                if (_loadedWorkspace is null || _loadedWorkspace != _getWorkspacePath().Trim() || PresetBrowseMode.IsReadOnly(this)) return;
                try { action(); }
                catch (Exception ex) { _status.Text = "Could not update cargo masses: " + ex.Message; }
            };
            toolbar.Controls.Add(button);
        }
        Button("Stage changes", () =>
        {
            var edits = MassEdits();
            CargoMassStore.Save(_loadedWorkspace!, edits); RestoreStagedMass(); UpdateMassCells(); StagingChanged?.Invoke();
            _status.Text = $"Staged {edits.Count} raw cargo mass changes. Apply pending changes from Start.";
        });
        Button("Revert unstaged edits", () => { RestoreStagedMass(); UpdateMassCells(); });
        Button("Revert staged changes", () => { CargoMassStore.Save(_loadedWorkspace!, []); RestoreStagedMass(); UpdateMassCells(); StagingChanged?.Invoke(); });
        Button("Reset selected to original", () =>
        {
            if (_cargo.CurrentRow?.DataBoundItem is DataRowView data && data["Entry"] is CargoEntry entry)
                foreach (var target in entry.MassTargets)
                    if (_massRows.TryGetValue(CargoMassStore.Key(target), out var row) && row.HasOriginal) row.Draft = row.Original;
            UpdateMassCells();
        });
        Button("Reset to original values", () => { foreach (var row in _massRows.Values.Where(row => row.HasOriginal)) row.Draft = row.Original; UpdateMassCells(); });
        _cargo.CellDoubleClick += (_, e) => EditMassBodies(e.RowIndex, e.ColumnIndex);
        _cargo.KeyDown += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Enter or Keys.F2) || _cargo.CurrentCell is not { OwningColumn: { } column } cell || MassRole(column.Name) is null) return;
            e.Handled = e.SuppressKeyPress = true;
            EditMassBodies(cell.RowIndex, cell.ColumnIndex);
        };
        _cargo.CellFormatting += FormatMass;
        _cargo.DataError += (_, e) => { e.ThrowException = false; _status.Text = "Enter a valid mass in kg."; };
    }
    private List<CargoMassEdit> MassEdits() => _massRows.Values.Where(row => !CargoMassStore.Equal(row.Draft, row.Current))
        .Select(row => new CargoMassEdit(row.Target.RelativeFile, row.Target.BodyPath, row.Target.ModelFrame, row.Structure,
            row.Target.Role + " cargo mass", row.Current, row.Draft)).ToList();
    private void RestoreStagedMass()
    {
        _stagedMass = CargoMassStore.Load(_loadedWorkspace!).ToDictionary(edit => CargoMassStore.Key(edit.RelativeFile, edit.BodyPath), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, row) in _massRows)
            row.Draft = _stagedMass.TryGetValue(key, out var edit) ? edit.After : row.Current;
    }
    private decimal? PreviewTotal(CargoEntry entry, string role, bool original = false)
    {
        var groups = entry.MassTargets.Where(target => target.Role == role).GroupBy(target => target.RelativeFile).ToArray();
        var totals = new List<decimal>();
        foreach (var group in groups)
        {
            decimal total = 0;
            foreach (var target in group)
            {
                if (!_massRows.TryGetValue(CargoMassStore.Key(target), out var row)) return null;
                if (original && !row.HasOriginal) return null;
                var value = original ? row.Original : row.Draft;
                if (value is not null) total += decimal.Parse(value, CultureInfo.InvariantCulture);
                else if (target.InheritedMass.HasValue) total += target.InheritedMass.Value;
                else return null;
            }
            totals.Add(total);
        }
        return totals.Count > 0 && totals.Distinct().Count() == 1 ? totals[0] : null;
    }
    private void UpdateMassCells()
    {
        if (_cargo.DataSource is not DataTable table) return;
        foreach (DataRow data in table.Rows)
        {
            if (data["Entry"] is not CargoEntry entry) continue;
            data["Loose weight (kg)"] = (object?)PreviewTotal(entry, "Loose") ?? DBNull.Value;
            data["Packed weight (kg)"] = (object?)PreviewTotal(entry, "Packed") ?? DBNull.Value;
        }
        _cargo.Invalidate(); ShowProperties();
    }
    private void FormatMass(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (_bindingCargo || e.RowIndex < 0 || e.ColumnIndex < 0 || MassRole(_cargo.Columns[e.ColumnIndex].Name) is not { } role
            || _cargo.Rows[e.RowIndex].DataBoundItem is not DataRowView data || data["Entry"] is not CargoEntry entry) return;
        var targets = entry.MassTargets.Where(target => target.Role == role).ToArray();
        if (targets.Any(target => !_massRows.ContainsKey(CargoMassStore.Key(target)))) return;
        var rows = targets.Select(target => _massRows[CargoMassStore.Key(target)]).ToArray();
        var changed = rows.Any(row => !CargoMassStore.Equal(row.Draft, row.Current));
        var applied = rows.Any(row => row.HasOriginal && !CargoMassStore.Equal(row.Current, row.Original));
        e.CellStyle!.BackColor = changed ? Color.LightGoldenrodYellow : applied ? Color.Honeydew : AppColors.Get("Surface");
        var preview = PreviewTotal(entry, role);
        var original = PreviewTotal(entry, role, original: true);
        string Display(decimal? value) => value?.ToString("0.############################", CultureInfo.CurrentCulture) ?? "Unresolved";
        e.Value = preview.HasValue ? Display(preview) : "";
        if (original.HasValue && preview != original) e.Value = $"{Display(preview)} ({Display(original)})";
        e.FormattingApplied = true;
        _cargo[e.ColumnIndex, e.RowIndex].ToolTipText = $"Double-click, or press Enter/F2, to edit {rows.Length} raw body mass value(s).\nPreview total: {Display(preview)} kg\nOriginal total: {Display(original)} kg\nEach popup row identifies its exact body and source file. OK changes the preview; Stage and Apply are still required.";
    }
    internal void InvalidateCatalog() { _loadedWorkspace = null; }
    internal void ExportPreview(string destination)
    {
        if (_loadedWorkspace != _getWorkspacePath().Trim()) return;
        CargoMassStore.Save(destination, MassEdits());
    }
    internal IEnumerable<WorkspaceChange> PresetPreviewChanges() => _loadedWorkspace is null || _loadedWorkspace != _getWorkspacePath().Trim() ? [] : MassEdits().Select(edit =>
        new WorkspaceChange(WorkspaceChange.Pending, WorkspaceOriginals.GetCurrentPath(_loadedWorkspace, edit.RelativeFile),
            edit.Name + " / " + edit.BodyPath + "/@Mass", edit.Before ?? "Not present", edit.After ?? "Not present"));
}
