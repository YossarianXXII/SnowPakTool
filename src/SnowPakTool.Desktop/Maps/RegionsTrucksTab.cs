using System.Data;

namespace SnowPakTool.Desktop;

internal sealed partial class RegionsTrucksTab : UserControl
{
    private readonly Func<string> _getWorkspacePath;
    private readonly Func<string> _getPakPath;
    private readonly RegionTruckAvailabilityStore _availabilityStore;
    private readonly Func<IReadOnlyList<CustomTruck>> _getEnabledTrucks;
    internal string PresetMapFolder => string.IsNullOrWhiteSpace(_mapFolder.Text) ? RegionTruckCatalogService.FindMapDirectory(_getPakPath()) : _mapFolder.Text.Trim();
    private readonly TextBox _mapFolder = new() { Dock = DockStyle.Fill, PlaceholderText = "Folder containing level_*.pak or extracted map folders" };
    private readonly Button _reload = new() { Text = "Reload maps", AutoSize = true };
    private readonly Button _browse = new() { Text = "Browse…", AutoSize = true };
    private readonly ToolTip _resetTips = new();
    private readonly Button _resetTruck = new() { Text = "Reset selected to original", AutoSize = true, Enabled = false };
    private readonly Button _resetRegions = new() { Text = "Reset selected regions to original", AutoSize = true, Enabled = false };
    private readonly Button _resetAll = new() { Text = "Reset all to original", AutoSize = true, Enabled = false };
    private readonly Button _stageChanges = new() { Text = "Stage changes", AutoSize = true, Enabled = false };
    public event Action? ChangesStaged;
    private readonly FlowLayoutPanel _regions = new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
        AutoScroll = true, Padding = new Padding(2), Margin = Padding.Empty
    };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(4, 5, 4, 5) };
    private readonly TextBox _details = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private readonly DataGridView _trucks = new()
    {
        Dock = DockStyle.Fill, ReadOnly = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        EditMode = DataGridViewEditMode.EditOnEnter,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f),
        ColumnHeadersDefaultCellStyle = { Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f, FontStyle.Bold) },
        RowTemplate = { Height = 26 }
    };
    private readonly HashSet<string> _selectedRegions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private RegionTruckCatalog? _catalog;
    private string? _loadedWorkspace;
    private string? _loadedMapFolder;
    private string? _loadedSourceStamp;
    private bool _loading;
    private bool _updatingTable;
    private bool _savingAvailability;
    private bool _editingPlan;

    public RegionsTrucksTab(Func<string> getWorkspacePath, Func<string> getPakPath, RegionTruckAvailabilityStore? availabilityStore = null,
        Func<IReadOnlyList<CustomTruck>>? getEnabledTrucks = null)
    {
        _getWorkspacePath = getWorkspacePath;
        _getPakPath = getPakPath;
        _availabilityStore = availabilityStore ?? new RegionTruckAvailabilityStore();
        _getEnabledTrucks = getEnabledTrucks ?? (() => []);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 238));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_regions, 0, 0);
        var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4 };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderRow.Controls.Add(new Label { Text = "Map folder", AutoSize = true, Padding = new Padding(0, 5, 8, 0) }, 0, 0);
        folderRow.Controls.Add(_mapFolder, 1, 0);
        folderRow.Controls.Add(_browse, 2, 0);
        folderRow.Controls.Add(_reload, 3, 0);
        layout.Controls.Add(folderRow, 0, 1);
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var tableHeaders = TableHeaderBehavior.Attach(_trucks);
        tableHeaders.AddFilter("Availability", value =>
        {
            var text = Convert.ToString(value) ?? "";
            return text.StartsWith("Obtainable", StringComparison.Ordinal) ? "Obtainable"
                : text.StartsWith("Mission", StringComparison.Ordinal) ? "Mission" : "Unverified";
        }, "Unverified");
        tableHeaders.Changed += () => { HideTruckHover(); ShowDetails(); };
        var all = new Button { Text = "Select all regions", AutoSize = true };
        var clear = new Button { Text = "Clear selection", AutoSize = true };
        filters.Controls.Add(all);
        filters.Controls.Add(clear);
        filters.Controls.Add(_resetTruck);
        filters.Controls.Add(_resetRegions);
        filters.Controls.Add(_resetAll);
        filters.Controls.Add(_stageChanges);
        layout.Controls.Add(filters, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        var detailsSplit = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(900, 500),
            SplitterDistance = 285, Panel1MinSize = 60, Panel2MinSize = 60, SplitterWidth = 6
        };
        detailsSplit.Panel1.Controls.Add(_trucks);
        detailsSplit.Panel2.Controls.Add(_details);
        layout.Controls.Add(detailsSplit, 0, 4);
        Controls.Add(layout);
        _status.Text = "Open this tab with an extracted workspace to scan installed maps. Click region tiles to select multiple regions.";
        _browse.Click += async (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose the folder containing level_*.pak archives or extracted map folders." };
            if (Directory.Exists(_mapFolder.Text)) dialog.SelectedPath = _mapFolder.Text;
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _mapFolder.Text = dialog.SelectedPath;
            await LoadAsync(force: true);
        };
        _reload.Click += async (_, _) => await LoadAsync(force: true);
        _resetTruck.Click += async (_, _) =>
        {
            await ResetOriginalAsync(CurrentFilteredEntries());
        };
        _resetTips.SetToolTip(_resetTruck, "Reset all trucks matching the selected regions and current table filters, including rows outside the visible scroll area.");
        _resetTruck.AccessibleDescription = "Reset all currently filtered trucks to their preserved original, regardless of row selection.";
        _resetRegions.Click += async (_, _) =>
        {
            if (_catalog is not null)
                await ResetOriginalAsync(_catalog.Entries.Where(entry => _selectedRegions.Contains(entry.RegionCode)).ToArray());
        };
        _resetAll.Click += async (_, _) =>
        {
            if (_catalog is not null) await ResetOriginalAsync(_catalog.Entries);
        };
        _stageChanges.Click += async (_, _) =>
        {
            try { await StageChangesAsync(); }
            catch (Exception ex) { _status.Text = "Could not stage map changes: " + ex.Message; }
        };
        all.Click += (_, _) => { _selectedRegions.UnionWith(_tiles.Keys); RefreshTiles(); ApplyFilter(); };
        clear.Click += (_, _) => { _selectedRegions.Clear(); RefreshTiles(); ApplyFilter(); };
        _trucks.CurrentCellChanged += (_, _) => ShowDetails();
        _trucks.DataBindingComplete += (_, _) => { ConfigureAvailabilityCells(); ShowDetails(); };
        _trucks.Sorted += (_, _) => ConfigureAvailabilityCells();
        _trucks.CellBeginEdit += (_, e) =>
        {
            e.Cancel = _trucks.Columns[e.ColumnIndex].Name != "Availability"
                || _trucks.Rows[e.RowIndex].DataBoundItem is not DataRowView row
                || row["Entry"] is not RegionTruckEntry { AvailabilityEditable: true };
        };
        _trucks.CellToolTipTextNeeded += EquipmentToolTipNeeded;
        InitializeTruckHover();
        _trucks.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_trucks.IsCurrentCellDirty && _trucks.CurrentCell is DataGridViewComboBoxCell)
                _trucks.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _trucks.CellValueChanged += AvailabilityChanged;
        AppColors.Changed += PaletteChanged;
        Disposed += (_, _) => { AppColors.Changed -= PaletteChanged; _resetTips.Dispose(); };
        PaletteChanged(this, EventArgs.Empty);
    }

    public Task EnsureLoadedAsync() => LoadAsync(force: false);
    public Task RefreshChangesAsync() { _loadedMapFolder = null; return LoadAsync(force: false); }

    public async Task StageChangesAsync()
    {
        if (_editingPlan || _loading) throw new InvalidOperationException("Wait for map loading or editing to finish before staging.");
        var workspace = _getWorkspacePath().Trim();
        if (!Directory.Exists(workspace)) throw new InvalidOperationException("Choose an extracted workspace on Start first.");
        _editingPlan = true;
        ShowDetails();
        try
        {
            var count = PendingTruckReplacements.Stage(workspace);
            await RefreshChangesAsync();
            var reloadError = _status.Text.StartsWith("Could not", StringComparison.Ordinal) ? " " + _status.Text : "";
            _status.Text = $"Staged {count} map changes. Apply them from Start → Apply pending changes. Editing a staged placement returns it to Draft." + reloadError;
        }
        finally { _editingPlan = false; ShowDetails(); }
        ChangesStaged?.Invoke();
    }

    public async Task<RegionTruckCatalog> GetRandomizationCatalogAsync()
    {
        while (_loading && !IsDisposed) await Task.Delay(50);
        await LoadAsync(force: false);
        return _catalog ?? throw new InvalidOperationException("No map data is loaded. Choose a workspace and map folder in Randomizer → Map trucks.");
    }

    private async Task ResetOriginalAsync(IReadOnlyList<RegionTruckEntry> entries)
    {
        if (_loadedWorkspace is null || entries.Count == 0) return;
        if (_editingPlan || _loading) return;
        _editingPlan = true;
        _resetTruck.Enabled = _resetRegions.Enabled = _resetAll.Enabled = _stageChanges.Enabled = false;
        try
        {
            PendingTruckReplacements.Reset(_loadedWorkspace, entries);
            await RefreshChangesAsync();
            _status.Text = "Original trucks and equipment restored in the draft; randomization assignments cleared. Stage changes, then apply restorations from Start → Apply pending changes.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { _status.Text = "Could not reset trucks: " + ex.Message; }
        finally { _editingPlan = false; ShowDetails(); }
    }

    private async Task LoadAsync(bool force)
    {
        if (_loading) return;
        var workspace = _getWorkspacePath().Trim();
        if (_mapFolder.Text.Length == 0) _mapFolder.Text = RegionTruckCatalogService.FindMapDirectory(_getPakPath());
        var mapFolder = _mapFolder.Text.Trim();
        if (workspace.Length == 0 || !Directory.Exists(workspace))
        { ClearCatalog(); _status.Text = "Choose an extracted workspace on the Start tab."; return; }
        string sourceStamp;
        try { sourceStamp = RegionTruckCatalogService.SourceStamp(workspace, mapFolder); }
        catch (Exception ex) { ClearCatalog(); _status.Text = "Could not inspect map sources: " + ex.Message; return; }
        if (!force && workspace.Equals(_loadedWorkspace, StringComparison.OrdinalIgnoreCase)
            && mapFolder.Equals(_loadedMapFolder, StringComparison.OrdinalIgnoreCase)
            && sourceStamp == _loadedSourceStamp
            && _catalog?.EquipmentCatalog?.IsCurrent(workspace) == true) return;
        ClearCatalog();
        if (!Directory.Exists(mapFolder) && RegionTruckCatalogService.MapSources(workspace, mapFolder).Count == 0)
        { _status.Text = "Choose a map folder or provide backed-up maps in the workspace."; return; }
        _loading = true;
        _reload.Enabled = _browse.Enabled = false;
        _mapFolder.ReadOnly = true;
        _status.Text = "Reading map placements and matching task rewards/removal rules…";
        try
        {
            // Region names and workspace text are ready before unrelated engine,
            // winch and experience editor warm-up has finished.
            if (force) _ = WorkspaceDataCache.PreloadAsync(workspace, forceReload: true);
            await WorkspaceDataCache.PreloadOwnedAsync(workspace);
            var names = WorkspaceDataCache.GetLocalizedNames(workspace);
            var regions = WorkspaceDataCache.GetRegionMetadata(workspace).ToDictionary(pair => pair.Key, pair => pair.Value.Name, StringComparer.OrdinalIgnoreCase);
            var catalog = await Task.Run(() => RegionTruckCatalogService.Load(workspace, mapFolder, names, regions, _availabilityStore));
            if (IsDisposed || !workspace.Equals(_getWorkspacePath().Trim(), StringComparison.OrdinalIgnoreCase)
                || !mapFolder.Equals(_mapFolder.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return;
            _catalog = catalog;
            _loadedWorkspace = workspace;
            _loadedMapFolder = mapFolder;
            _loadedSourceStamp = sourceStamp;
            BuildRegionTiles();
            ApplyFilter();
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = $"Could not load map trucks: {ex.Message}"; }
        finally
        {
            _loading = false;
            if (!IsDisposed) ShowDetails();
            if (!IsDisposed) { _reload.Enabled = _browse.Enabled = true; _mapFolder.ReadOnly = false; }
        }
    }

    private void ClearCatalog()
    {
        HideTruckHover();
        _catalog = null;
        _loadedWorkspace = _loadedMapFolder = _loadedSourceStamp = null;
        _trucks.DataSource = null;
        _details.Clear();
        foreach (var tile in _tiles.Values) tile.Dispose();
        _tiles.Clear();
        _regions.Controls.Clear();
    }

    private void BuildRegionTiles()
    {
        if (_catalog is null) return;
        _regions.SuspendLayout();
        foreach (var group in _catalog.Entries.GroupBy(entry => entry.RegionCode)
                     .OrderBy(group => RegionTileAssets.GetReleaseOrder(group.Key))
                     .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var code = group.Key;
            var rows = group.ToArray();
            var tile = new RegionTileButton
            {
                Width = 150, Height = 210, Margin = new Padding(0, 0, 1, 1), FlatStyle = FlatStyle.Flat,
                RegionName = rows[0].Region,
                ReleaseLabel = RegionTileAssets.GetReleaseLabel(code) + " · " + code.ToUpperInvariant(),
                Summary = TileSummary(rows),
                Thumbnail = RegionTileAssets.GetThumbnail(code),
                Text = rows[0].Region,
                Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter, UseVisualStyleBackColor = false,
                AccessibleName = rows[0].Region, AccessibleDescription = "Toggle this region in the truck table", Tag = code
            };
            tile.Click += (_, _) =>
            {
                if (!_selectedRegions.Add(code)) _selectedRegions.Remove(code);
                RefreshTiles();
                ApplyFilter();
            };
            _tiles[code] = tile;
            _regions.Controls.Add(tile);
        }
        _selectedRegions.IntersectWith(_tiles.Keys);
        if (_selectedRegions.Count == 0 && _tiles.Count > 0) _selectedRegions.Add(_tiles.Keys.First());
        RefreshTiles();
        _regions.ResumeLayout();
    }

    private void RefreshTiles()
    {
        foreach (var (code, tile) in _tiles)
        {
            var selected = _selectedRegions.Contains(code);
            tile.BackColor = selected ? AppColors.Get("SelectionBlue") : Color.FromArgb(32, 38, 45);
            tile.ForeColor = tile.BackColor.GetBrightness() < 0.55f ? Color.White : Color.Black;
            tile.FlatAppearance.BorderSize = selected ? 3 : 1;
            tile.FlatAppearance.BorderColor = selected ? SystemColors.Highlight : Color.FromArgb(70, 80, 90);
            tile.AccessibleDescription = selected ? "Selected region. Activate to remove from the table." : "Activate to add this region to the table.";
        }
    }

    private void ApplyFilter()
    {
        HideTruckHover();
        if (_catalog is null) return;
        var rows = _catalog.Entries.Where(entry => _selectedRegions.Contains(entry.RegionCode))
            .OrderBy(entry => entry.Region).ThenBy(entry => entry.Map).ThenBy(entry => entry.Truck).ToArray();
        var table = new DataTable();
        var changes = _loadedWorkspace is not null ? PendingTruckReplacements.Load(_loadedWorkspace)
            .ToDictionary(change => change.Key, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, PendingTruckReplacement>();
        foreach (var name in new[] { "Region", "Map", "Truck", "Class", "Tires", "Frame addons", "Addons", "Availability", "Task / contract", "Placement ID", "Truck ID", "Change state" }) table.Columns.Add(name);
        table.Columns.Add("Entry", typeof(RegionTruckEntry));
        foreach (var entry in rows) table.Rows.Add(entry.Region, entry.Map, entry.Truck, entry.Class,
            entry.Tires, entry.FrameAddons, entry.Addons, entry.Availability, entry.Task, entry.PlacementId, entry.TruckCode,
            entry.FileEntry is null ? "In files" : changes.TryGetValue(entry.MapCode + "|" + entry.PlacementId + "|" + entry.FileEntry.BinaryOffset, out var change) && change.Staged ? "Staged" : "Draft", entry);
        _updatingTable = true;
        try
        {
            _trucks.DataSource = table;
            var truckColumn = _trucks.Columns["Truck"]!;
            truckColumn.CellTemplate = new TruckReplacementCell { Open = OpenReplacement };
            foreach (DataGridViewRow row in _trucks.Rows)
                row.Cells[truckColumn.Index] = new TruckReplacementCell { Open = OpenReplacement, Value = row.Cells[truckColumn.Index].Value };
            _trucks.Columns["Entry"]!.Visible = false;
            foreach (DataGridViewColumn column in _trucks.Columns)
            {
                column.ReadOnly = column.Name != "Availability";
                column.Width = column.Name switch { "Truck" => 210, "Tires" => 95, "Frame addons" => 220, "Addons" => 250, "Availability" => 220, "Task / contract" => 260, "Class" => 85, _ => 160 };
            }
            ConfigureAvailabilityCells();
        }
        finally { _updatingTable = false; }
        _status.Text = $"{rows.Length} placements in {_selectedRegions.Count} selected regions · {_catalog.MapsRead} maps read · {_catalog.Warnings.Count} warnings. "
            + "Double-tap Shift to search. Unverified rows are always visible; use their Availability dropdown to resolve them.";
        if (_catalog.MapsRead == 0) _status.Text = "No supported map files found. Choose the folder containing level_*.pak or extracted level folders.";
        ShowDetails();
    }

    private void ConfigureAvailabilityCells()
    {
        var updating = _updatingTable;
        _updatingTable = true;
        try
        {
            foreach (DataGridViewRow row in _trucks.Rows)
            {
                if (row.DataBoundItem is not DataRowView view || view["Entry"] is not RegionTruckEntry entry) continue;
                var truckCell = row.Cells["Truck"];
                var truckColor = AppColors.Get(entry.TruckChanged ? entry.FileEntry is null ? "DAF8E0" : "EngineMatch" : "Surface");
                truckCell.Style.BackColor = truckCell.Style.SelectionBackColor = truckColor;
                truckCell.Style.ForeColor = truckCell.Style.SelectionForeColor = SystemColors.ControlText;
                if (entry.AvailabilityEditable && row.Cells["Availability"] is not DataGridViewComboBoxCell)
                {
                    var choice = new DataGridViewComboBoxCell { DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton };
                    choice.Items.AddRange(RegionTruckAvailabilityStore.Choices);
                    choice.Value = entry.Availability;
                    row.Cells["Availability"] = choice;
                }
                else if (!entry.AvailabilityEditable && row.Cells["Availability"] is DataGridViewComboBoxCell)
                    row.Cells["Availability"] = new DataGridViewTextBoxCell { Value = entry.Availability };
                row.Cells["Availability"].ReadOnly = !entry.AvailabilityEditable;
                row.Cells["Availability"].Style.BackColor = AppColors.Get(entry.Availability.StartsWith("Obtainable", StringComparison.Ordinal)
                    ? "DAF8E0" : entry.Availability == "Mission/task only" ? "FFDCDC" : "EngineMatch");
            }
        }
        finally { _updatingTable = updating; }
    }

    private static string TileSummary(IEnumerable<RegionTruckEntry> entries)
    {
        var rows = entries.ToArray();
        var unverified = rows.Count(row => row.Availability == "Unverified");
        return $"{rows.Select(row => row.MapCode).Distinct().Count()} maps · {rows.Length} placements"
            + (unverified > 0 ? $"\n{unverified} unverified" : "");
    }

    private void UpdateTileSummaries()
    {
        if (_catalog is null) return;
        foreach (var group in _catalog.Entries.GroupBy(entry => entry.RegionCode))
            if (_tiles.TryGetValue(group.Key, out var button) && button is RegionTileButton tile)
            { tile.Summary = TileSummary(group); tile.Invalidate(); }
    }

    private void AvailabilityChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_updatingTable || _savingAvailability || _catalog is null || e.RowIndex < 0 || e.ColumnIndex < 0
            || _trucks.Columns[e.ColumnIndex].Name != "Availability"
            || _trucks.Rows[e.RowIndex].DataBoundItem is not DataRowView row || row["Entry"] is not RegionTruckEntry entry
            || !entry.AvailabilityEditable) return;
        var value = Convert.ToString(_trucks.Rows[e.RowIndex].Cells[e.ColumnIndex].Value) ?? "";
        if (value == entry.Availability || !RegionTruckAvailabilityStore.Choices.Contains(value, StringComparer.Ordinal)) return;
        _savingAvailability = true;
        try
        {
            _availabilityStore.Set(entry.MapCode, entry.PlacementId, value);
            _catalog = _catalog with { Entries = _catalog.Entries.Select(item =>
                item.MapCode.Equals(entry.MapCode, StringComparison.OrdinalIgnoreCase)
                && item.PlacementId.Equals(entry.PlacementId, StringComparison.OrdinalIgnoreCase)
                    ? _availabilityStore.Apply(item) : item).ToArray() };
            row["Entry"] = _availabilityStore.Apply(entry);
            // Finish committing the combo box before rebinding the filtered table.
            if (IsHandleCreated) BeginInvoke(new Action(RefreshAfterEdit));
            else RefreshAfterEdit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _trucks.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = entry.Availability;
            _status.Text = $"Could not save availability: {ex.Message}";
        }
        finally { _savingAvailability = false; }

        void RefreshAfterEdit()
        {
            if (IsDisposed) return;
            UpdateTileSummaries();
            ApplyFilter();
        }
    }

    private RegionTruckEntry[] CurrentFilteredEntries()
    {
        // The filtered DataView is authoritative, even while the grid is rebinding after a header filter.
        var view = _trucks.DataSource switch { DataTable table => table.DefaultView, DataView filtered => filtered, _ => null };
        return view is null ? [] : view.Cast<DataRowView>().Select(row => row["Entry"]).OfType<RegionTruckEntry>().ToArray();
    }

    private void ShowDetails()
    {
        var filtered = CurrentFilteredEntries();
        _resetTruck.Enabled = !_loading && !_editingPlan && filtered.Any(entry => entry.OriginalEquipment is { Parsed: true });
        _resetRegions.Enabled = !_loading && !_editingPlan && _catalog is not null && _selectedRegions.Count > 0;
        _resetAll.Enabled = !_loading && !_editingPlan && _catalog?.Entries.Count > 0;
        try { _stageChanges.Enabled = !_loading && !_editingPlan && _loadedWorkspace is not null && PendingTruckReplacements.HasUnstagedChanges(_loadedWorkspace); }
        catch (Exception) { _stageChanges.Enabled = false; }
        _details.Clear();
        if (_trucks.CurrentRow is { Index: >= 0 } current && current.Index < filtered.Length)
            _details.Text = TruckDetailsFormatter.Format(filtered[current.Index]);
        if (_catalog?.Warnings.Count > 0) _details.AppendText("\r\nRead warnings:\r\n" + string.Join("\r\n", _catalog.Warnings));
    }

    private async void OpenReplacement(int rowIndex)
    {
        if (PresetBrowseMode.IsReadOnly(this)) return;
        if (rowIndex < 0 || rowIndex >= _trucks.Rows.Count || _loadedWorkspace is null
            || _trucks.Rows[rowIndex].DataBoundItem is not DataRowView row || row["Entry"] is not RegionTruckEntry entry) return;
        using var dialog = new TruckReplacementDialog(_loadedWorkspace, entry, _getEnabledTrucks, _catalog?.EquipmentCatalog);
        dialog.ShowDialog(this);
        if (dialog.QueuedReplacement is { } queued)
        {
            _loadedMapFolder = null;
            await LoadAsync(force: false);
            _status.Text += $" Draft replacement: {queued.Original.Truck} → {queued.Target.Name}. Stage changes before applying map files from Start.";
        }
    }

    private void EquipmentToolTipNeeded(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _trucks.Rows[e.RowIndex].DataBoundItem is not DataRowView row
            || row["Entry"] is not RegionTruckEntry entry) return;
        // Resolve from the bound entry so tooltips remain correct after sorting.
        if (_trucks.Columns[e.ColumnIndex].Name == "Truck")
        {
            e.ToolTipText = $"Original truck: {entry.BaselineTruck} ({entry.BaselineTruckCode}).";
            if (entry.FileEntry is not null)
                e.ToolTipText += $"\r\nPreview: {entry.FileEntry.Truck} → {entry.Truck}. Stage changes, then apply from Start → Apply pending changes.";
            else if (entry.TruckChanged)
                e.ToolTipText += $"\r\nSaved replacement: {entry.Truck}.";
            e.ToolTipText += "\r\nCompare replacement trucks (⇄ button, F4 or Alt+Down).";
        }
        if (_trucks.Columns[e.ColumnIndex].Name == "Tires") e.ToolTipText = entry.TireDetails;
        if (_trucks.Columns[e.ColumnIndex].Name == "Addons") e.ToolTipText = entry.AddonDetails;
        if (_trucks.Columns[e.ColumnIndex].Name == "Frame addons") e.ToolTipText = entry.FrameAddonDetails;
    }

    private void PaletteChanged(object? sender, EventArgs e)
    {
        _trucks.BackgroundColor = _trucks.DefaultCellStyle.BackColor = AppColors.Get("Surface");
        _trucks.AlternatingRowsDefaultCellStyle.BackColor = AppColors.Get("AlternateRow");
        _trucks.DefaultCellStyle.SelectionBackColor = AppColors.Get("SelectionBlue");
        _trucks.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        RefreshTiles();
        ApplyFilter();
    }
}
