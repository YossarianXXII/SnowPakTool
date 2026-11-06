using System.Data;
using System.Globalization;

namespace SnowPakTool.Desktop;

internal sealed partial class AddonsTab
{
    private Dictionary<string, AddonExplorationRow> _explorationRows = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string?> _stagedExploration = new(StringComparer.OrdinalIgnoreCase);
    private static string? PropertyAttribute(string column) => AddonExplorationStore.Attributes.FirstOrDefault(a => AddonExplorationStore.Column(a) == column);
    private static string EditKey(AddonExplorationEdit edit) => AddonExplorationStore.Key(edit.Source, edit.Element, edit.Variant, edit.Attribute);

    private void InitializeExplorationTools(TableLayoutPanel layout)
    {
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        void Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (_, _) =>
            {
                if (_loading || _loadedWorkspace is null || _loadedWorkspace != _getWorkspacePath().Trim()) return;
                try { action(); } catch (Exception ex) { _status.Text = "Could not update addon properties: " + ex.Message; }
            };
            tools.Controls.Add(button);
        }
        void Reset(IEnumerable<AddonExplorationRow> rows)
        {
            _addons.CancelEdit();
            foreach (var row in rows.Where(r => r.HasOriginal)) row.Draft = row.Original;
            UpdateExplorationCells();
        }
        Button("Reset selected to original", () =>
        {
            if (_addons.CurrentRow?.DataBoundItem is not DataRowView view || view["Entry"] is not AddonCatalogEntry entry) return;
            Reset(AddonExplorationStore.Attributes.Select(a => _explorationRows.GetValueOrDefault(AddonExplorationStore.Key(entry, a))).OfType<AddonExplorationRow>());
        });
        Button("Reset to original values", () => Reset(_explorationRows.Values));
        Button("Stage changes", () =>
        {
            if (!CommitAddonProperties()) return;
            var edits = ExplorationEdits(); AddonExplorationStore.Save(_loadedWorkspace!, edits);
            _stagedExploration = edits.ToDictionary(EditKey, e => e.After, StringComparer.OrdinalIgnoreCase);
            UpdateExplorationCells(); _status.Text = $"Staged {edits.Count} addon property changes across all views and filters. Apply pending changes from Start.";
        });
        Button("Revert unstaged edits", () => { _addons.CancelEdit(); RestoreExploration(); UpdateExplorationCells(); });
        Button("Revert staged changes", () => { _addons.CancelEdit(); AddonExplorationStore.Save(_loadedWorkspace!, []); RestoreExploration(); UpdateExplorationCells(); });
        tools.Controls.Add(new Label { AutoSize = true, Padding = new Padding(4, 5, 0, 0), Text = "Edit Price, Unlock rank or Exploration unlock in the table. Blank restores inheritance." });
        layout.Controls.Add(tools, 0, 1);
        _addons.CellValidating += (_, e) =>
        {
            if (_binding || e.RowIndex < 0 || _addons[e.ColumnIndex, e.RowIndex].ReadOnly || PropertyAttribute(_addons.Columns[e.ColumnIndex].Name) is not { } attribute) return;
            if (!TryPropertyValue(attribute, e.FormattedValue, out var parsed))
            { e.Cancel = true; _status.Text = attribute == "UnlockByExploration" ? "Enter true, false, or leave blank for inheritance." : $"Enter a whole number from 0 to {(attribute == "Price" ? "100,000,000" : "30")}, or leave blank for inheritance."; }
        };
        _addons.CellParsing += (_, e) =>
        {
            if (e.ColumnIndex < 0 || PropertyAttribute(_addons.Columns[e.ColumnIndex].Name) is not { } attribute) return;
            if (!TryPropertyValue(attribute, e.Value, out var value))
            {
                if (e.RowIndex < 0 || _addons.Rows[e.RowIndex].DataBoundItem is not DataRowView view || view["Entry"] is not AddonCatalogEntry entry
                    || !_explorationRows.TryGetValue(AddonExplorationStore.Key(entry, attribute), out var previous)) return;
                value = previous.Draft;
                _status.Text = "Invalid addon property value; the last valid preview was retained.";
            }
            e.Value = value is null ? DBNull.Value : attribute == "UnlockByExploration" ? value : decimal.Parse(value, CultureInfo.InvariantCulture);
            e.ParsingApplied = true;
        };
        _addons.CellValueChanged += (_, e) =>
        {
            if (_binding || e.RowIndex < 0 || e.ColumnIndex < 0 || PropertyAttribute(_addons.Columns[e.ColumnIndex].Name) is not { } attribute
                || _addons.Rows[e.RowIndex].DataBoundItem is not DataRowView view || view["Entry"] is not AddonCatalogEntry entry
                || !_explorationRows.TryGetValue(AddonExplorationStore.Key(entry, attribute), out var row)
                || !TryPropertyValue(attribute, _addons[e.ColumnIndex, e.RowIndex].Value, out var value)) return;
            row.Draft = value; UpdateExplorationCells();
        };
        _addons.EditingControlShowing += (_, e) =>
        {
            if (_addons.CurrentCell is not { OwningColumn: { } column, OwningRow: { } gridRow } cell || PropertyAttribute(column.Name) is not { } attribute
                || gridRow.DataBoundItem is not DataRowView view || view["Entry"] is not AddonCatalogEntry entry
                || !_explorationRows.TryGetValue(AddonExplorationStore.Key(entry, attribute), out var row) || e.Control is not TextBox text) return;
            text.Text = row.Draft is null ? "" : attribute == "UnlockByExploration" ? row.Draft
                : decimal.Parse(row.Draft, CultureInfo.InvariantCulture).ToString(CultureInfo.CurrentCulture);
            text.SelectAll();
        };
        _addons.DataError += (_, e) => { e.ThrowException = false; _status.Text = "Enter a valid addon property value."; };
    }
    private static bool TryPropertyValue(string attribute, object? input, out string? value)
    {
        var text = input is null or DBNull ? "" : Convert.ToString(input, CultureInfo.CurrentCulture)?.Trim() ?? "";
        value = text.Length == 0 ? null : text;
        if (attribute == "UnlockByExploration") value = value?.ToLowerInvariant();
        else if (value is not null && decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var number)) value = number.ToString(CultureInfo.InvariantCulture);
        return AddonExplorationStore.ValidValue(attribute, value);
    }
    private bool CommitAddonProperties()
    {
        if (_addons.IsCurrentCellInEditMode && _addons.CurrentCell is { OwningColumn: { } column }
            && PropertyAttribute(column.Name) is { } attribute && _addons.EditingControl is TextBox text
            && !TryPropertyValue(attribute, text.Text, out var value))
        { _status.Text = "Correct the active addon property before staging: price 0–100,000,000; rank 0–30; exploration true/false. Blank restores inheritance."; return false; }
        return _addons.EndEdit();
    }
    private List<AddonExplorationEdit> ExplorationEdits() => _explorationRows.Values.Where(r => r.Draft != r.Current)
        .Select(r => r.Identity with { After = r.Draft }).ToList();
    private void RestoreExploration()
    {
        var edits = AddonExplorationStore.Load(_loadedWorkspace!);
        var byKey = edits.ToDictionary(EditKey, StringComparer.OrdinalIgnoreCase);
        _stagedExploration = edits.ToDictionary(EditKey, e => e.After, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, row) in _explorationRows) row.Draft = byKey.TryGetValue(key, out var edit) ? edit.After : row.Current;
    }
    private string ExplorationState(AddonCatalogEntry entry)
    {
        var rows = AddonExplorationStore.Attributes.Select(a => _explorationRows.GetValueOrDefault(AddonExplorationStore.Key(entry, a))).OfType<AddonExplorationRow>().ToArray();
        if (rows.Length == 0) return "Read-only";
        var changes = rows.Where(r => r.Draft != r.Current).ToArray();
        if (changes.Length > 0) return changes.All(r => _stagedExploration.TryGetValue(EditKey(r.Identity), out var staged) && staged == r.Draft) ? "Staged" : "Draft";
        return rows.Any(r => r.HasOriginal && r.Current != r.Original) ? "In files" : "Original";
    }
    private void UpdateExplorationCells()
    {
        if (_addons.DataSource is not DataTable table) return;
        _binding = true;
        try
        {
            foreach (DataRow data in table.Rows)
                if (data["Entry"] is AddonCatalogEntry entry)
                {
                    foreach (var attribute in AddonExplorationStore.Attributes)
                        if (_explorationRows.TryGetValue(AddonExplorationStore.Key(entry, attribute), out var row))
                            data[AddonExplorationStore.Column(attribute)] = row.Draft is null ? DBNull.Value : attribute == "UnlockByExploration" ? row.Draft : decimal.Parse(row.Draft, CultureInfo.InvariantCulture);
                    data["Change state"] = ExplorationState(entry);
                    data["__Changed"] = HasAddonChanges(entry);
                }
        }
        finally { _binding = false; }
        _addons.Invalidate(); ShowProperties();
        _status.Text = $"{_explorationRows.Values.Count(r => r.Draft != r.Current)} addon property changes in preview. Reset covers all addon views, including filtered rows. Stage, then Apply from Start.";
    }
    private void FormatExploration(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || PropertyAttribute(_addons.Columns[e.ColumnIndex].Name) is not { } attribute
            || _addons.Rows[e.RowIndex].DataBoundItem is not DataRowView view || view["Entry"] is not AddonCatalogEntry entry
            || !_explorationRows.TryGetValue(AddonExplorationStore.Key(entry, attribute), out var row)) return;
        string Display(string? value) => value is null ? "Inherited / unspecified" : attribute == "UnlockByExploration" ? value : decimal.Parse(value, CultureInfo.InvariantCulture).ToString("N0");
        var current = Display(row.Draft);
        e.CellStyle!.BackColor = row.Draft != row.Current ? Color.LightGoldenrodYellow
            : row.HasOriginal && row.Current != row.Original ? Color.Honeydew : AppColors.Get("Surface");
        if (!_addons[e.ColumnIndex, e.RowIndex].IsInEditMode)
        { e.Value = row.HasOriginal && row.Draft != row.Original ? $"{current} ({Display(row.Original)})" : current; e.FormattingApplied = true; }
        _addons.Rows[e.RowIndex].Cells[e.ColumnIndex].ToolTipText = $"Original: {(row.HasOriginal ? Display(row.Original) : "Unavailable")}\nIn files: {Display(row.Current)}\nPreview: {current}\nBlank removes the local attribute; parent/template rules may still apply.";
    }
    internal void ExportExplorationPreview(string destination)
    {
        if (!_frameAddonsOnly && _loadedWorkspace == _getWorkspacePath().Trim())
        { if (!CommitAddonProperties()) throw new InvalidDataException("Finish editing addon properties before sharing."); AddonExplorationStore.Save(destination, ExplorationEdits()); }
    }
}
