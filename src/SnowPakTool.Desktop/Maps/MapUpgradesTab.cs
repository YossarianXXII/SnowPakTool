using System.Data;

namespace SnowPakTool.Desktop;

internal sealed partial class MapUpgradesTab : UserControl
{
    private readonly Func<string> _getWorkspacePath;
    private readonly Func<bool, Task<UpgradeTrialCatalog>>? _getCatalog;
    private readonly TextBox _source = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Button _reload = new() { Text = "Reload upgrades", AutoSize = true };
    private readonly Button _resetSelected = new() { Text = "Reset selected to original", AutoSize = true };
    private readonly Button _resetRegions = new() { Text = "Reset selected regions to original", AutoSize = true };
    private readonly Button _resetAll = new() { Text = "Reset all to original", AutoSize = true };
    private readonly FlowLayoutPanel _regions = new() { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Padding = new Padding(2), Margin = Padding.Empty };
    private readonly HashSet<string> _selectedRegions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RegionTileButton> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(4, 5, 4, 5) };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private readonly DataGridView _upgrades = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f), RowTemplate = { Height = 26 },
        ColumnHeadersDefaultCellStyle = { Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f, FontStyle.Bold) }
    };
    private readonly ToolTip _tips = new();
    private UpgradeTrialCatalog? _catalog;
    private UpgradeTrialDraft? _draft;
    private string? _selectionWorkspace;
    private bool _loading;
    private Func<Task>? _stageChanges;
    private Func<Task>? _applyChanges;
    private readonly Button _stage = new() { Text = "Stage changes", AutoSize = true, Enabled = false };
    private readonly Button _apply = new() { Text = "Apply staged upgrades", AutoSize = true, Enabled = false };
    private IReadOnlyDictionary<string, string> _truckNames = new Dictionary<string, string>();

    public MapUpgradesTab(Func<string> getWorkspacePath, Func<string> getPakPath,
        Func<bool, Task<UpgradeTrialCatalog>>? getCatalog = null)
    {
        _getWorkspacePath = getWorkspacePath; _getCatalog = getCatalog;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 238));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_regions, 0, 0);
        var sourceRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceRow.Controls.Add(new Label { Text = "Upgrade settings", AutoSize = true, Padding = new Padding(0, 5, 8, 0) }, 0, 0);
        sourceRow.Controls.Add(_source, 1, 0); sourceRow.Controls.Add(_reload, 2, 0); layout.Controls.Add(sourceRow, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var all = new Button { Text = "Select all regions", AutoSize = true };
        var clear = new Button { Text = "Clear selection", AutoSize = true };
        _tips.SetToolTip(_stage, "Stage all upgrade drafts and exploration flags, then use Apply staged upgrades.");
        _tips.SetToolTip(_apply, "Apply the staged pickup replacements and exploration flags to the workspace. Then pack and install the updated initial.pak.");
        _tips.SetToolTip(_resetSelected, "Reset every pickup matching selected regions and current column filters, regardless of highlighted row.");
        actions.Controls.AddRange([all, clear, _resetSelected, _resetRegions, _resetAll, _stage, _apply]); layout.Controls.Add(actions, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(900, 500), SplitterDistance = 285, Panel1MinSize = 60, Panel2MinSize = 60, SplitterWidth = 6 };
        var headers = TableHeaderBehavior.Attach(_upgrades); headers.Changed += () => { UpdateButtons(); ShowDetails(); };
        foreach (var name in new[] { "Region", "Map", "Compatible trucks", "Upgrade", "Type", "Placement ID", "Upgrade ID", "Change state" })
            _upgrades.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = name, DataPropertyName = name,
                Width = name == "Compatible trucks" ? 420 : name == "Upgrade" ? 280 : name is "Placement ID" or "Upgrade ID" ? 260 : 145,
                SortMode = DataGridViewColumnSortMode.Automatic });
        split.Panel1.Controls.Add(_upgrades); split.Panel2.Controls.Add(_details); layout.Controls.Add(split, 0, 4); Controls.Add(layout);
        _status.Text = "Review upgrade pickups, Stage changes, then Apply staged upgrades.";
        _stage.Click += async (_, _) =>
        {
            if (_stageChanges is null || _loading) return;
            _loading = true; UpdateButtons();
            string message;
            try { await _stageChanges(); message = "Upgrades and exploration flags staged. Use Apply staged upgrades to write them to the workspace."; }
            catch (Exception ex) { message = "Could not stage upgrades: " + ex.Message; }
            finally { _loading = false; await LoadAsync(false); UpdateButtons(); }
            _status.Text = message;
        };
        _apply.Click += async (_, _) =>
        {
            if (_applyChanges is null || _loading) return;
            _loading = true; UpdateButtons();
            string message;
            try
            {
                await _applyChanges();
                message = "Staged pickup replacements and exploration flags applied to the workspace. Pack to a new PAK from Start and install it for the game to use these upgrades.";
            }
            catch (Exception ex) { message = "Could not apply upgrades: " + ex.Message; }
            finally { _loading = false; await LoadAsync(false); UpdateButtons(); }
            _status.Text = message;
        };
        all.Click += (_, _) => { _selectedRegions.UnionWith(_tiles.Keys); RenderRows(); };
        clear.Click += (_, _) => { _selectedRegions.Clear(); RenderRows(); };
        _reload.Click += async (_, _) => await LoadAsync(true);
        _resetSelected.Click += (_, _) => Reset(VisibleSlots());
        _resetRegions.Click += (_, _) => Reset(_catalog?.AllSlots.Where(s => _selectedRegions.Contains(s.Region)) ?? []);
        _resetAll.Click += (_, _) => Reset(_catalog?.AllSlots ?? []);
        _upgrades.CurrentCellChanged += (_, _) => ShowDetails();
        InitializeCompatibilityHover();
        AppColors.Changed += PaletteChanged;
        Disposed += (_, _) => { AppColors.Changed -= PaletteChanged; _tips.Dispose(); };
        TextAreaStyle.Mark(_details); UpdateButtons();
    }

    public Task EnsureLoadedAsync() => LoadAsync(false);
    public void ConfigureStaging(Func<Task> stage) { _stageChanges = stage; UpdateButtons(); }
    public void ConfigureApplying(Func<Task> apply) { _applyChanges = apply; UpdateButtons(); }
    public Task RefreshDraftAsync() => LoadAsync(false);

    private async Task LoadAsync(bool force)
    {
        if (_loading || _getCatalog is null) return;
        var workspace = _getWorkspacePath().Trim();
        if (!Directory.Exists(workspace))
        {
            _catalog = null; _draft = null; _upgrades.DataSource = null;
            ClearTiles(); _details.Clear(); _status.Text = "Choose an extracted workspace on Start."; UpdateButtons(); return;
        }
        _loading = true; _reload.Enabled = false; UpdateButtons();
        _status.Text = "Reading original pickup settings, upgrade compatibility and the current truck preview…";
        try
        {
            var catalog = await _getCatalog(force);
            var truckNames = await Task.Run(() => RegionTruckCatalogService.LoadTrucks(workspace,
                WorkspaceDataCache.GetLocalizedNames(workspace), new List<string>())
                .ToDictionary(t => t.Key, t => t.Value.Name, StringComparer.OrdinalIgnoreCase));
            if (IsDisposed || !workspace.Equals(_getWorkspacePath().Trim(), StringComparison.OrdinalIgnoreCase)) return;
            _catalog = catalog; _draft = UpgradeTrialStore.Load(workspace); _truckNames = truckNames;
            _source.Text = Path.Combine(WorkspaceOriginals.GetOriginalRoot(workspace), "[ps]");
            if (!workspace.Equals(_selectionWorkspace, StringComparison.OrdinalIgnoreCase))
            { _selectedRegions.Clear(); _selectedRegions.UnionWith(catalog.AllSlots.Select(s => s.Region)); _selectionWorkspace = workspace; }
            BuildTiles(); RenderRows();
        }
        catch (Exception ex) { if (!IsDisposed) { _catalog = null; _draft = null; _upgrades.DataSource = null; ClearTiles(); _details.Clear(); _status.Text = "Could not load upgrades: " + ex.Message; } }
        finally { _loading = false; if (!IsDisposed) { _reload.Enabled = true; UpdateButtons(); } }
    }

    private void ClearTiles() { foreach (var tile in _tiles.Values) tile.Dispose(); _tiles.Clear(); _regions.Controls.Clear(); }
    private void BuildTiles()
    {
        ClearTiles(); if (_catalog is null) return;
        var names = WorkspaceDataCache.GetRegionMetadata(_catalog.Workspace);
        foreach (var group in _catalog.AllSlots.GroupBy(s => s.Region).OrderBy(g => RegionTileAssets.GetReleaseOrder(g.Key)))
        {
            var code = group.Key; var name = names.TryGetValue(code, out var region) ? region.Name : code;
            var changes = group.Count(slot => DesiredId(slot, _draft?.Assignments.FirstOrDefault(a => UpgradeTrialService.Key(a.Slot) == UpgradeTrialService.Key(slot))) != slot.OriginalUpgradeId);
            var tile = new RegionTileButton { Width = 150, Height = 210, Margin = new Padding(0, 0, 1, 1), FlatStyle = FlatStyle.Flat,
                RegionName = name, ReleaseLabel = RegionTileAssets.GetReleaseLabel(code) + " · " + code.ToUpperInvariant(), Summary = $"{group.Count()} pickups\n{changes} changed pickups",
                Thumbnail = RegionTileAssets.GetThumbnail(code), Text = name, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f, FontStyle.Bold),
                UseVisualStyleBackColor = false, AccessibleName = name, Tag = code };
            tile.Click += (_, _) => { if (!_selectedRegions.Add(code)) _selectedRegions.Remove(code); RenderRows(); };
            _tiles.Add(code, tile); _regions.Controls.Add(tile);
        }
    }

    private void RenderRows()
    {
        RefreshTiles(); if (_catalog is null) return;
        var draft = _draft?.Assignments.ToDictionary(a => UpgradeTrialService.Key(a.Slot), StringComparer.OrdinalIgnoreCase) ?? new();
        var regions = WorkspaceDataCache.GetRegionMetadata(_catalog.Workspace);
        var table = new DataTable();
        foreach (DataGridViewColumn column in _upgrades.Columns) table.Columns.Add(column.Name);
        table.Columns.Add("Slot", typeof(UpgradeDistributionSlot));
        foreach (var slot in _catalog.AllSlots.Where(s => _selectedRegions.Contains(s.Region)))
        {
            draft.TryGetValue(UpgradeTrialService.Key(slot), out var assignment);
            var id = DesiredId(slot, assignment);
            _catalog.Parts.TryGetValue(id, out var part); _catalog.Parts.TryGetValue(slot.OriginalUpgradeId, out var original);
            var state = UpgradeChanges.State(_catalog.Workspace, slot, id);
            if (state == "Draft" && _draft?.InputStamp != _catalog.Stamp) state = "Draft (inputs changed)";
            table.Rows.Add(regions.TryGetValue(slot.Region, out var region) ? region.Name : slot.Region, slot.Map,
                string.Join(", ", TruckNames(part)), $"{part?.Name ?? id} ({original?.Name ?? slot.OriginalUpgradeId})",
                part?.Definition.Kind.ToString() ?? "Unresolved", slot.Zone, id, state, slot);
        }
        _upgrades.DataSource = table;
        foreach (DataGridViewRow row in _upgrades.Rows)
            if (row.DataBoundItem is DataRowView data)
            {
                var state = Convert.ToString(data["Change state"]);
                if (state?.StartsWith("Draft", StringComparison.Ordinal) == true || state == "Staged") row.DefaultCellStyle.BackColor = AppColors.Get("EngineMatch");
                else if (state == "In files") row.DefaultCellStyle.BackColor = AppColors.Get("DAF8E0");
            }
        _status.Text = $"{table.Rows.Count} pickups in {_selectedRegions.Count} selected regions ({_catalog.Input.Slots.Count} in owned regions)"
            + (_draft is null ? "" : $" · seed {_draft.Seed}") + ". Stage here, then Apply staged upgrades. Pack and install the updated PAK to use them in game.";
        if (_draft is not null && _draft.Assignments.Count > 0 && _draft.InputStamp != _catalog.Stamp) _status.Text += " Inputs changed; randomize again before staging.";
        UpdateButtons(); ShowDetails();
    }

    private IEnumerable<UpgradeDistributionSlot> VisibleSlots() => _upgrades.DataSource switch
    {
        DataTable table => table.DefaultView.Cast<DataRowView>().Select(r => r["Slot"]).OfType<UpgradeDistributionSlot>(),
        DataView view => view.Cast<DataRowView>().Select(r => r["Slot"]).OfType<UpgradeDistributionSlot>(),
        _ => []
    };
    private void Reset(IEnumerable<UpgradeDistributionSlot> slots)
    {
        if (_catalog is null || _loading) return;
        try { UpgradeTrialStore.Reset(_catalog.Workspace, slots.ToArray()); _draft = UpgradeTrialStore.Load(_catalog.Workspace); BuildTiles(); RenderRows(); }
        catch (Exception ex) { _status.Text = "Could not reset upgrades: " + ex.Message; }
    }
    private void UpdateButtons()
    {
        _apply.Enabled = !_loading && _applyChanges is not null && _catalog is not null && UpgradeChanges.Pending(_catalog.Workspace).Count > 0;
        _stage.Enabled = !_loading && _stageChanges is not null && _catalog is not null && _draft is not null;
        var keys = _draft?.Assignments.Select(a => UpgradeTrialService.Key(a.Slot)).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        if (_catalog is not null) keys.UnionWith(_catalog.AllSlots.Where(s => UpgradeChanges.CurrentId(_catalog.Workspace, s) != s.OriginalUpgradeId).Select(UpgradeTrialService.Key));
        _resetSelected.Enabled = !_loading && VisibleSlots().Any(s => keys.Contains(UpgradeTrialService.Key(s)));
        _resetRegions.Enabled = !_loading && _catalog?.AllSlots.Any(s => _selectedRegions.Contains(s.Region) && keys.Contains(UpgradeTrialService.Key(s))) == true;
        _resetAll.Enabled = !_loading && keys.Count > 0;
    }
    private void ShowDetails()
    {
        _details.Clear();
        var index = _upgrades.CurrentRow?.Index ?? -1;
        if (_catalog is null || index < 0 || VisibleSlots().ElementAtOrDefault(index) is not UpgradeDistributionSlot slot) return;
        var assignment = _draft?.Assignments.FirstOrDefault(a => UpgradeTrialService.Key(a.Slot).Equals(UpgradeTrialService.Key(slot), StringComparison.OrdinalIgnoreCase));
        var id = DesiredId(slot, assignment);
        _catalog.Parts.TryGetValue(id, out var part);
        _catalog.Parts.TryGetValue(slot.OriginalUpgradeId, out var original);
        _details.Text = $"Source: {slot.Source}\r\nZone: {slot.Zone} / upgrades[{slot.RewardIndex}]\r\nOriginal ID: {slot.OriginalUpgradeId}\r\nSelected ID: {id}\r\nDefinition: {part?.Source}\r\nNew compatible trucks: {string.Join(", ", TruckNames(part))}\r\nOriginal compatible trucks: {string.Join(", ", TruckNames(original))}"
            + (assignment is null ? "" : $"\r\nSelection: {assignment.Path}; {assignment.Relation}; fallback={assignment.Fallback}; reused={assignment.Reused}")
            + "\r\nStage changes, then Apply pending changes from Start. Exploration locks follow the resulting map pickups."
            + (_catalog.Warnings.Count == 0 ? "" : "\r\nSource warnings:\r\n" + string.Join("\r\n", _catalog.Warnings));
    }
    private string[] TruckNames(UpgradeTrialPart? part) => (part?.Definition.CompatibleTrucks ?? [])
        .Select(code => _truckNames.GetValueOrDefault(code, code)).Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    private string DesiredId(UpgradeDistributionSlot slot, UpgradeDistributionAssignment? assignment) => assignment?.UpgradeId
        ?? (_draft?.ResetKeys?.Contains(UpgradeTrialService.Key(slot), StringComparer.OrdinalIgnoreCase) == true
            ? slot.OriginalUpgradeId : UpgradeChanges.CurrentId(_catalog!.Workspace, slot));
    private void PaletteChanged(object? sender, EventArgs e) => RefreshTiles();
    private void RefreshTiles()
    {
        foreach (var (code, tile) in _tiles)
        {
            var selected = _selectedRegions.Contains(code);
            tile.BackColor = selected ? AppColors.Get("SelectionBlue") : Color.FromArgb(32, 38, 45);
            tile.ForeColor = tile.BackColor.GetBrightness() < .55f ? Color.White : Color.Black;
            tile.FlatAppearance.BorderSize = selected ? 3 : 1;
            tile.FlatAppearance.BorderColor = selected ? SystemColors.Highlight : Color.FromArgb(70, 80, 90);
        }
    }
}
