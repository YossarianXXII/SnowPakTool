using System.Data;
using System.Globalization;

namespace SnowPakTool.Desktop;

internal sealed partial class AddonsTab : UserControl
{
    private readonly Func<string> _getWorkspacePath;
    private readonly bool _frameAddonsOnly;
    private readonly TrackBar _fuelMultiplier = new() { Minimum = 1, Maximum = 6, Value = 2, TickFrequency = 1, SmallChange = 1, LargeChange = 1, Width = 150, Height = 38, AutoSize = false, AccessibleName = "Frame addon fuel capacity multiplier" };
    private readonly Label _fuelMultiplierValue = new() { Text = "×1.0", AutoSize = true, Margin = new Padding(4, 8, 8, 0) };
    private readonly FlowLayoutPanel _fuelTools = new() { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty, Enabled = false };
    private Dictionary<string, FrameAddonFuelRow> _fuelRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(4, 6, 4, 4) };
    private readonly TextBox _description = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly DataGridView _addons = CreateGrid(new BoundCatalogGrid());
    private readonly DataGridView _properties = CreateGrid();
    private AddonCatalog? _catalog;
    private string? _loadedWorkspace;
    private bool _loading;
    private bool _showInternal = true;
    private bool _binding;
    private long _loadedContentVersion;
    private readonly Button _reload = new() { Text = "Reload addons", AutoSize = true };
    private readonly Button _switchAddons = new() { Text = "Show external addons", AutoSize = true, Enabled = false };
    private readonly CheckBox _changedOnly = new() { Text = "Show changed only", Appearance = Appearance.Button, AutoSize = true };
    private bool _changingChangedFilter;

    public AddonsTab(Func<string> getWorkspacePath, bool frameAddonsOnly = false)
    {
        _getWorkspacePath = getWorkspacePath;
        _frameAddonsOnly = frameAddonsOnly;
        if (_frameAddonsOnly) _reload.Text = "Reload frame addons";
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        toolbar.Controls.Add(new Label { Text = _frameAddonsOnly ? "Frame addons" : "Addons", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 5, 12, 0) });
        toolbar.Controls.Add(_reload);
        toolbar.Controls.Add(_changedOnly);
        if (!_frameAddonsOnly) toolbar.Controls.Add(_switchAddons);
        if (!_frameAddonsOnly) InitializeExplorationTools(layout);
        if (_frameAddonsOnly)
        {
            _fuelTools.Controls.Add(new Label { Text = "Fuel multiplier", AutoSize = true, Margin = new Padding(4, 8, 0, 4) });
            _fuelTools.Controls.Add(_fuelMultiplier);
            _fuelTools.Controls.Add(_fuelMultiplierValue);
            _fuelMultiplier.ValueChanged += (_, _) => _fuelMultiplierValue.Text = "×" + (_fuelMultiplier.Value / 2m).ToString("0.0", CultureInfo.CurrentCulture);
            void Button(string text, Action action)
            {
                var button = new Button { Text = text, AutoSize = true };
                button.Click += (_, _) =>
                {
                    if (!FuelReady()) return;
                    try { action(); }
                    catch (Exception ex) { _status.Text = "Could not update frame addon fuel: " + ex.Message; }
                };
                _fuelTools.Controls.Add(button);
            }
            Button("Multiply all fuel", MultiplyFuel);
            Button("Stage changes", StageFuel);
            Button("Revert unstaged edits", () => { RestoreStagedFuel(); UpdateFuelCells(); });
            Button("Revert staged changes", () => { FrameAddonFuelStore.Save(_loadedWorkspace!, []); RestoreStagedFuel(); UpdateFuelCells(); });
            Button("Reset to original values", () =>
            {
                foreach (var row in _fuelRows.Values) row.Draft = row.Original;
                UpdateFuelCells();
            });
            layout.Controls.Add(_fuelTools, 0, 1);
            _addons.CellFormatting += FormatFuel;
        }
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_status, 0, 2);
        layout.Controls.Add(_addons, 0, 3);
        layout.Controls.Add(_description, 0, 4);
        layout.Controls.Add(_properties, 0, 5);
        Controls.Add(layout);
        _properties.Columns.Add("Property", _frameAddonsOnly ? "Selected frame addon property" : "Selected addon / upgrade property");
        _properties.Columns.Add("Value", "Value");
        _properties.Columns.Add("Source", "Source file");
        _properties.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        TableHeaderBehavior.Attach(_addons);
        _changedOnly.CheckedChanged += (_, _) =>
        {
            if (_changingChangedFilter) return;
            if (!_frameAddonsOnly && !CommitAddonProperties())
            { _changingChangedFilter = true; _changedOnly.Checked = !_changedOnly.Checked; _changingChangedFilter = false; return; }
            TableHeaderBehavior.Attach(_addons).SetAdditionalFilter(expression: _changedOnly.Checked ? "[__Changed] = true" : null);
            ShowProperties();
        };
        TableHeaderBehavior.Attach(_properties);
        _addons.CurrentCellChanged += (_, _) => ShowProperties();
        _addons.DataBindingComplete += (_, _) => ShowProperties();
        _addons.CellFormatting += FormatExploration;
        _reload.Click += async (_, _) => await LoadAsync(force: true);
        _switchAddons.Click += (_, _) =>
        {
            _showInternal = !_showInternal;
            _switchAddons.Text = _showInternal ? "Show external addons" : "Show internal addons";
            ApplyFilter();
        };
        AppColors.Changed += RefreshColors;
        Disposed += (_, _) => AppColors.Changed -= RefreshColors;
        RefreshColors(this, EventArgs.Empty);
    }

    public void EnsureLoaded() => _ = EnsureLoadedAsync();

    public Task EnsureLoadedAsync() => LoadAsync(force: false);
    internal void InvalidateCatalog() => _loadedWorkspace = null;

    private async Task LoadAsync(bool force)
    {
        if (_loading || IsDisposed) return;
        var workspace = _getWorkspacePath().Trim();
        if (!force && string.Equals(_loadedWorkspace, workspace, StringComparison.OrdinalIgnoreCase)
            && (_loadedContentVersion == WorkspaceDataCache.ContentVersion
                || _explorationRows.Values.Any(r => r.Draft != r.Current) || _fuelRows.Values.Any(r => r.Draft != r.Current))) return;
        _catalog = null;
        _fuelTools.Enabled = false;
        _fuelRows.Clear();
        _explorationRows.Clear();
        _switchAddons.Enabled = false;
        _loadedWorkspace = null;
        _addons.DataSource = null;
        _properties.Rows.Clear();
        _description.Clear();
        if (workspace.Length == 0 || !Directory.Exists(workspace))
        {
            _status.Text = _frameAddonsOnly ? "Choose an extracted workspace on the Start tab to list frame addons."
                : "Choose an extracted workspace on the Start tab to list addons and upgrades.";
            return;
        }
        _loading = true;
        _reload.Enabled = false;
        _status.Text = "Reading addon/component definitions and level upgrade rewards…";
        try
        {
            if (force) await WorkspaceDataCache.PreloadAsync(workspace, forceReload: true);
            else await WorkspaceDataCache.PreloadOwnedAsync(workspace);
            var names = WorkspaceDataCache.GetLocalizedNames(workspace);
            var catalog = await Task.Run(() => AddonCatalogService.Load(workspace, names));
            var exploration = await Task.Run(() => AddonExplorationStore.ReadRows(workspace, _frameAddonsOnly
                ? catalog with { Entries = catalog.Entries.Where(e => e.Type == "Addon" && e.Category.Equals("frame_addons", StringComparison.OrdinalIgnoreCase)).ToArray() }
                : catalog));
            var fuelRows = _frameAddonsOnly ? await Task.Run(() => LoadFuelRows(workspace, catalog)) : new Dictionary<string, FrameAddonFuelRow>();
            if (IsDisposed || !string.Equals(workspace, _getWorkspacePath().Trim(), StringComparison.OrdinalIgnoreCase)) return;
            _catalog = catalog;
            _loadedWorkspace = workspace;
            _loadedContentVersion = WorkspaceDataCache.ContentVersion;
            _fuelRows = fuelRows;
            _explorationRows = exploration;
            RestoreExploration();
            if (_frameAddonsOnly) RestoreStagedFuel();
            ApplyFilter();
            _fuelTools.Enabled = _frameAddonsOnly;
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = $"Could not load addons: {ex.Message}"; }
        finally
        {
            _loading = false;
            if (!IsDisposed)
            {
                _reload.Enabled = true;
                _switchAddons.Enabled = _catalog is not null;
                if (!string.Equals(workspace, _getWorkspacePath().Trim(), StringComparison.OrdinalIgnoreCase)) await LoadAsync(force: false);
            }
        }
    }

    private void ApplyFilter()
    {
        if (_catalog is null) return;
        var visible = _catalog.Entries.Where(entry => _frameAddonsOnly
            ? entry.Type == "Addon" && entry.Category.Equals("frame_addons", StringComparison.OrdinalIgnoreCase)
            : IsInternal(entry) == _showInternal).ToArray();
        if (_frameAddonsOnly)
        {
            BindFrameAddons(visible);
            return;
        }
        var table = new DataTable();
        foreach (var column in new[] { "Addon / upgrade", "Type", "Category", "ID", "ID scope", "Socket / family", "Content", "Exploration unlock", "Referenced maps", "Source file", "Change state" }) table.Columns.Add(column);
        foreach (var column in new[] { "Price", "Unlock rank", "Map reward entries" }) table.Columns.Add(column, typeof(decimal));
        table.Columns.Add("Entry", typeof(AddonCatalogEntry));
        foreach (var entry in visible)
            table.Rows.Add(entry.Name, entry.Type, entry.Category, entry.Id, entry.Type == "Tire" ? "Local tire name" : entry.Type == "Unresolved map upgrade" ? "Unresolved reward ID" : "Global upgrade ID", entry.Family, entry.Content,
                _explorationRows.TryGetValue(AddonExplorationStore.Key(entry), out var flag) ? AddonExplorationStore.Display(flag.Draft) : entry.Exploration,
                string.Join(", ", entry.MapReferences.Select(item => item.Map).Distinct(StringComparer.OrdinalIgnoreCase)), entry.Source,
                ExplorationState(entry), (object?)entry.Price ?? DBNull.Value, (object?)entry.UnlockRank ?? DBNull.Value, (decimal)entry.MapReferences.Count, entry);
        AddChangedColumn(table);
        _binding = true;
        try
        {
            _addons.DataSource = table;
            _addons.ReadOnly = false;
            _addons.EditMode = DataGridViewEditMode.EditOnEnter;
            _addons.SelectionMode = DataGridViewSelectionMode.CellSelect;
            _addons.Columns["Entry"]!.Visible = false;
            _addons.Columns["__Changed"]!.Visible = false;
            foreach (DataGridViewColumn column in _addons.Columns)
            {
                column.Width = column.Name switch { "Addon / upgrade" => 210, "Exploration unlock" => 230, "ID" or "Socket / family" => 260, "Referenced maps" or "Source file" => 300, _ => 125 };
                if (column.ValueType == typeof(decimal)) column.DefaultCellStyle.Format = "N0";
                column.ReadOnly = PropertyAttribute(column.Name) is null;
            }
            var order = new[] { "Addon / upgrade", "Type", "Price", "Unlock rank", "Exploration unlock", "Change state" };
            for (var i = 0; i < order.Length; i++) _addons.Columns[order[i]]!.DisplayIndex = i;
            foreach (DataGridViewRow row in _addons.Rows)
                if (row.DataBoundItem is DataRowView data && data["Entry"] is AddonCatalogEntry entry)
                    foreach (var attribute in AddonExplorationStore.Attributes)
                        row.Cells[AddonExplorationStore.Column(attribute)].ReadOnly = !_explorationRows.ContainsKey(AddonExplorationStore.Key(entry, attribute));
        }
        finally { _binding = false; }
        UpdateExplorationCells();
        _status.Text = $"{(_showInternal ? "Internal" : "External")} addons · {visible.Length} definitions · {_catalog.MapReferences.Count} level-setting reward entries. "
            + "Edit price, unlock rank and exploration flags, or reset to originals. Stage changes, then Apply pending changes from Start. Other metadata is read-only.";
        if (_catalog.Warnings.Count > 0) _status.Text += $" {_catalog.Warnings.Count} warnings (see details).";
        ShowProperties();
    }

    private void BindFrameAddons(IReadOnlyList<AddonCatalogEntry> entries)
    {
        var table = new DataTable();
        foreach (var column in new[] { "Frame addon", "ID", "Install socket", "Content", "Exploration unlock" }) table.Columns.Add(column);
        foreach (var column in new[] { "Cargo slots", "Fuel (L)", "Water (L)", "Repair points", "Spare wheels", "Price", "Unlock rank" })
            table.Columns.Add(column, typeof(decimal));
        table.Columns.Add("Entry", typeof(AddonCatalogEntry));
        static object Value(decimal? value) => (object?)value ?? DBNull.Value;
        foreach (var entry in entries)
            table.Rows.Add(entry.Name, entry.Id, entry.Family, entry.Content, entry.Exploration,
                Value(entry.Frame?.CargoSlots), Value(_fuelRows.TryGetValue(entry.Source, out var fuel) ? fuel.Draft : entry.Frame?.Fuel), Value(entry.Frame?.Water),
                Value(entry.Frame?.Repairs), Value(entry.Frame?.SpareWheels), Value(entry.Price), Value(entry.UnlockRank), entry);
        AddChangedColumn(table);
        _binding = true;
        try
        {
            _addons.DataSource = table;
            _addons.Columns["Entry"]!.Visible = false;
            _addons.Columns["__Changed"]!.Visible = false;
            foreach (DataGridViewColumn column in _addons.Columns)
            {
                column.Width = column.Name switch { "Frame addon" => 210, "ID" or "Install socket" => 230, _ => 125 };
                if (column.ValueType == typeof(decimal)) column.DefaultCellStyle.Format = "#,0.##";
            }
            // Keep the most useful capacities next to the name; identifiers follow the summary.
            var order = new[] { "Frame addon", "Cargo slots", "Fuel (L)", "Water (L)", "Repair points", "Spare wheels",
                "Price", "Unlock rank", "Exploration unlock", "Install socket", "Content", "ID" };
            for (var i = 0; i < order.Length; i++) _addons.Columns[order[i]]!.DisplayIndex = i;
        }
        finally { _binding = false; }
        _status.Text = $"Frame addons · {entries.Count} definitions · Fuel multiplier previews explicit capacities; stage changes, then apply from Start. Header funnels filter columns; select a row for all source properties."
            + (_catalog!.Warnings.Count > 0 ? $" {_catalog.Warnings.Count} catalog warnings (see details)." : "");
        ShowProperties();
    }

    private void ShowProperties()
    {
        if (_binding) return;
        _properties.Rows.Clear();
        _description.Clear();
        if (_addons.CurrentRow?.DataBoundItem is not DataRowView row || row["Entry"] is not AddonCatalogEntry entry) return;
        _description.Text = entry.Description + Environment.NewLine
            + (_frameAddonsOnly
                ? "Metadata and capacities follow explicit file parents; templates are shown without expansion. Blank = not resolved. Install socket does not guarantee truck compatibility. Source: " + entry.Source
                : "Metadata follows explicit file parents; shared/local templates are shown below without expansion. Blank = not resolved. Socket/family identifies definitions, not guaranteed truck compatibility. Tire names are local to their wheel family and are not automatically treated as global reward IDs.")
            + (_catalog?.Warnings.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, _catalog.Warnings) : "");
        foreach (var reference in entry.MapReferences)
            _properties.Rows.Add($"Map reward: {reference.Map} / {reference.Zone} / upgrades[{reference.Index}]", reference.UpgradeId, reference.Source);
        foreach (var property in entry.Properties) _properties.Rows.Add(property.Property, property.Value, property.Source);
        if (!_frameAddonsOnly)
            foreach (var attribute in AddonExplorationStore.Attributes)
                if (_explorationRows.TryGetValue(AddonExplorationStore.Key(entry, attribute), out var value))
                {
                    var label = AddonExplorationStore.Column(attribute);
                    _properties.Rows.Add(label + " — original", value.HasOriginal ? AddonExplorationStore.Display(value.Original) : "Original unavailable", entry.Source);
                    _properties.Rows.Add(label + " — in files", AddonExplorationStore.Display(value.Current), entry.Source);
                    _properties.Rows.Add(label + " — preview", AddonExplorationStore.Display(value.Draft), entry.Source);
                }
        if (_frameAddonsOnly && _fuelRows.TryGetValue(entry.Source, out var fuel))
            _properties.Rows.Add("Fuel capacity preview (L)", fuel.Draft.ToString(CultureInfo.InvariantCulture), entry.Source);
        TableHeaderBehavior.Refresh(_properties);
    }

    private static bool IsInternal(AddonCatalogEntry entry) => entry.Type != "Addon"
        || entry.Category is "awd" or "diff_lock" or "snorkel";

    private bool FuelReady() => _frameAddonsOnly && !_loading && _catalog is not null && _loadedWorkspace is not null
        && string.Equals(_loadedWorkspace, _getWorkspacePath().Trim(), StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, FrameAddonFuelRow> LoadFuelRows(string workspace, AddonCatalog catalog)
    {
        var result = new Dictionary<string, FrameAddonFuelRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Entries.Where(entry => entry.Type == "Addon" && entry.Category.Equals("frame_addons", StringComparison.OrdinalIgnoreCase)))
        {
            // Only locally explicit capacities/category are writable. Never multiply a shared parent on behalf of its children.
            if (!entry.Properties.Any(property => property.Source == entry.Source && property.Property == "TruckAddon/GameData/@Category")) continue;
            var current = FrameAddonFuelStore.Read(File.ReadAllText(WorkspaceOriginals.GetCurrentPath(workspace, entry.Source)));
            if (current is null) continue;
            var originalPath = WorkspaceOriginals.GetOriginalReadPath(workspace, entry.Source);
            var original = File.Exists(originalPath) ? FrameAddonFuelStore.Read(File.ReadAllText(originalPath)) ?? current.Value : current.Value;
            result[entry.Source] = new(entry.Name, original, current.Value);
        }
        return result;
    }

    private void RestoreStagedFuel()
    {
        var staged = FrameAddonFuelStore.Load(_loadedWorkspace!);
        foreach (var pair in _fuelRows)
            pair.Value.Draft = staged.FirstOrDefault(edit => edit.RelativeFile.Equals(pair.Key, StringComparison.OrdinalIgnoreCase))?.After ?? pair.Value.Current;
    }

    private void MultiplyFuel()
    {
        if (!FuelReady()) return;
        foreach (var row in _fuelRows.Values.Where(row => row.Draft != 0)) row.Draft = FrameAddonFuelStore.RoundFuel(row.Draft * (_fuelMultiplier.Value / 2m));
        UpdateFuelCells();
    }

    internal bool HasPresetPreview => _loadedWorkspace == _getWorkspacePath().Trim()
        && (_fuelRows.Values.Any(r => r.Draft != r.Current) || _explorationRows.Values.Any(r => r.Draft != r.Current));

    internal IEnumerable<WorkspaceChange> PresetPreviewChanges()
    {
        if (_loadedWorkspace != _getWorkspacePath().Trim()) yield break;
        foreach (var (source, row) in _fuelRows.Where(p => p.Value.Draft != p.Value.Current))
            yield return new("Preview", WorkspaceOriginals.GetCurrentPath(_loadedWorkspace!, source), row.Name + " / Fuel capacity",
                row.Current.ToString(CultureInfo.InvariantCulture), row.Draft.ToString(CultureInfo.InvariantCulture));
        foreach (var row in _explorationRows.Values.Where(r => r.Draft != r.Current))
            yield return new("Preview", WorkspaceOriginals.GetCurrentPath(_loadedWorkspace!, row.Identity.Source),
                row.Identity.Name + " / " + row.Identity.Attribute, row.Current ?? "Inherited", row.Draft ?? "Inherited");
    }

    internal void ExportFuelPreview(string destination)
    {
        if (_loadedWorkspace != _getWorkspacePath().Trim() || !_frameAddonsOnly) return;
        var edits = _fuelRows.Where(pair => pair.Value.Draft != pair.Value.Current)
            .Select(pair => new FrameAddonFuelEdit(pair.Key, pair.Value.Name, pair.Value.Current, pair.Value.Draft)).ToList();
        FrameAddonFuelStore.Save(destination, edits);
    }

    private void StageFuel()
    {
        var edits = _fuelRows.Where(pair => pair.Value.Draft != pair.Value.Current)
            .Select(pair => new FrameAddonFuelEdit(pair.Key, pair.Value.Name, pair.Value.Current, pair.Value.Draft)).ToList();
        FrameAddonFuelStore.Save(_loadedWorkspace!, edits);
        _status.Text = $"Staged {edits.Count} frame addon fuel changes. Apply pending changes from Start.";
    }

    private void UpdateFuelCells()
    {
        if (_addons.DataSource is not DataTable table) return;
        foreach (DataRow row in table.Rows)
            if (row["Entry"] is AddonCatalogEntry entry)
            {
                if (_fuelRows.TryGetValue(entry.Source, out var fuel)) row["Fuel (L)"] = fuel.Draft;
                row["__Changed"] = HasAddonChanges(entry);
            }
        _addons.Invalidate();
        ShowProperties();
        _status.Text = $"{_fuelRows.Count} explicit fuel capacities · {_fuelRows.Values.Count(row => row.Draft != row.Current)} changed values. Preview only; Stage changes, then apply from Start. Blank/inherited capacities stay unchanged.";
    }

    private void FormatFuel(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _addons.Columns[e.ColumnIndex].Name != "Fuel (L)"
            || _addons.Rows[e.RowIndex].DataBoundItem is not DataRowView view || view["Entry"] is not AddonCatalogEntry entry
            || !_fuelRows.TryGetValue(entry.Source, out var row)) return;
        e.CellStyle!.BackColor = row.Draft != row.Current ? Color.LightGoldenrodYellow
            : row.Draft != row.Original ? Color.Honeydew : AppColors.Get("Surface");
        e.Value = row.Draft != row.Original ? $"{row.Draft:N0} ({row.Original:N0})" : row.Draft.ToString("N0");
        e.FormattingApplied = true;
        _addons.Rows[e.RowIndex].Cells[e.ColumnIndex].ToolTipText = $"Original: {row.Original:N0} L\nIn files: {row.Current:N0} L\nPreview: {row.Draft:N0} L\nMultiplier: 0.5×–3×; zero capacities are skipped; round to 5 L; range 0–10,000 L. Reset restores the exact original capacity.";
    }

    private bool HasAddonChanges(AddonCatalogEntry entry) =>
        _frameAddonsOnly && _fuelRows.TryGetValue(entry.Source, out var fuel) && (fuel.Draft != fuel.Current || fuel.Draft != fuel.Original)
        || AddonExplorationStore.Attributes.Any(attribute => _explorationRows.TryGetValue(AddonExplorationStore.Key(entry, attribute), out var row)
            && (row.Draft != row.Current || row.HasOriginal && row.Draft != row.Original));

    private void AddChangedColumn(DataTable table)
    {
        table.Columns.Add("__Changed", typeof(bool));
        foreach (DataRow row in table.Rows)
            if (row["Entry"] is AddonCatalogEntry entry) row["__Changed"] = HasAddonChanges(entry);
    }

    private void RefreshColors(object? sender, EventArgs e)
    {
        foreach (var grid in new[] { _addons, _properties })
        {
            grid.BackgroundColor = grid.DefaultCellStyle.BackColor = AppColors.Get("Surface");
            grid.AlternatingRowsDefaultCellStyle.BackColor = AppColors.Get("AlternateRow");
            grid.DefaultCellStyle.SelectionBackColor = AppColors.Get("SelectionBlue");
            grid.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        }
    }

    private static DataGridView CreateGrid(DataGridView? grid = null)
    {
        grid ??= new DataGridView();
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.AllowUserToResizeRows = grid.RowHeadersVisible = grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f, FontStyle.Bold);
        grid.RowTemplate.Height = 26;
        return grid;
    }
}
