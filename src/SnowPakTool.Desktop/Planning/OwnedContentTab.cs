using System.Text;
using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Collections;

namespace SnowPakTool.Desktop;

internal sealed class OwnedContentTab : UserControl
{
    private static readonly Regex DlcSection = new(@"^   (?<section>[A-Za-z0-9_]+)\s*=\s*\{", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex DlcName = new(@"\bname\s*=\s*""(?<key>UI_DLC_NAME_[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex RegionList = new(@"\bregions\s*=\s*\[(?<regions>.*?)\]", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RegionCode = new(@"""(?<code>(?:us|ru)_\d{2})""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TruckEntry = new(@"^\s*(?<truck>[A-Za-z0-9_]+)\s*=\s*\{", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex SeasonNumber = new(@"\bSeason\s+(?<number>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Func<string> _getWorkspacePath;
    private readonly OwnedRegionState _ownedRegionState;
    private readonly OwnedDlcState _ownedDlcState = new();
    private readonly DataGridView _regionGrid = CreateGrid();
    private readonly DataGridView _truckDlcGrid = CreateGrid();
    private readonly DataGridView _otherDlcGrid = CreateGrid();
    private readonly DataGridView _truckGrid = CreateGrid();
    private readonly List<MultiSelectFilter> _truckFilters = new();
    private readonly CheckBox _selectAllRegions = new() { Text = "Select all", AutoSize = true };
    private readonly CheckBox _selectAllTruckDlcs = new() { Text = "Select all", AutoSize = true };
    private readonly CheckBox _selectAllOtherDlcs = new() { Text = "Select all", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(4, 4, 4, 8), Text = "Select a workspace to load owned content." };
    private readonly TableLayoutPanel _dlcSections = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = Padding.Empty };
    private readonly HashSet<int> _expandedDlcPanes = [];
    private readonly ToolTip _paneTips = new();
    private bool _updatingSelectAll;
    private string? _loadedWorkspace;
    private ListSortDirection _regionSortDirection = ListSortDirection.Ascending;
    private string? _truckSortColumn;
    private ListSortDirection _truckSortDirection = ListSortDirection.Ascending;
    public event Action? OwnedTrucksChanged;

    public OwnedContentTab(Func<string> getWorkspacePath, OwnedRegionState ownedRegionState)
    {
        _getWorkspacePath = getWorkspacePath;
        _ownedRegionState = ownedRegionState;
        BuildLayout();
        Disposed += (_, _) => { _paneTips.Dispose(); foreach (var filter in _truckFilters) filter.Dispose(); };
        AppColors.Changed += (_, _) => RefreshPaletteColors();
        _ownedRegionState.Changed += UpdateOwnedRegion;
    }

    public void EnsureLoaded()
    {
        var workspace = _getWorkspacePath().Trim();
        if (string.Equals(_loadedWorkspace, workspace, StringComparison.OrdinalIgnoreCase)) return;
        _loadedWorkspace = workspace;
        _regionGrid.Rows.Clear();
        _truckDlcGrid.Rows.Clear();
        _otherDlcGrid.Rows.Clear();
        _truckGrid.Rows.Clear();
        _truckSortColumn = null;
        foreach (DataGridViewColumn column in _truckGrid.Columns) column.HeaderCell.SortGlyphDirection = SortOrder.None;
        foreach (var filter in _truckFilters) filter.SetOptions(Array.Empty<string>());
        OwnedTrucksChanged?.Invoke();

        var dlcLibraryFile = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[ps]", "dlc_library.sso"));
        var stringsFile = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[strings]", "strings_english.str"));
        var descriptionsFile = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[ps]", "zone_desc_storage.sso"));
        if (!Directory.Exists(workspace) || !File.Exists(dlcLibraryFile) || !File.Exists(stringsFile) || !File.Exists(descriptionsFile))
        {
            _status.Text = "Unpack a workspace containing [ps]/dlc_library.sso and [strings]/strings_english.str to load owned content.";
            return;
        }

        try
        {
            var localizedNames = WorkspaceDataCache.GetLocalizedNames(workspace);
            var prepared = (PreparedWorkspaceData)WorkspaceDataCache.GetOwnedData(workspace);
            var truckMetadata = prepared.TruckMetadata;
            var regionMetadata = WorkspaceDataCache.GetRegionMetadata(workspace);
            var dlcEntries = prepared.DlcEntries;
            var trucksByDlc = dlcEntries
                .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (
                        Names: string.Join(", ", group.SelectMany(entry => entry.Trucks).Distinct(StringComparer.CurrentCultureIgnoreCase)),
                        HasUnlocalizedTruck: group.Any(entry => entry.HasUnlocalizedTruck),
                        ReleaseDate: group.Select(entry => entry.ReleaseDate).FirstOrDefault(date => date.HasValue)),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var (code, metadata) in regionMetadata.Where(entry => !entry.Value.IsBaseGame))
            {
                var trucks = trucksByDlc.GetValueOrDefault(metadata.DlcName, (Names: string.Empty, HasUnlocalizedTruck: false, ReleaseDate: (DateOnly?)null));
                var rowIndex = _regionGrid.Rows.Add(_ownedRegionState.IsOwned(code), metadata.DlcName, ReleaseDateCellValue(trucks.ReleaseDate), metadata.Name, trucks.Names);
                _regionGrid.Rows[rowIndex].Tag = code;
                SetRowColor(_regionGrid.Rows[rowIndex], metadata.DlcName);
                if (trucks.HasUnlocalizedTruck) _regionGrid.Rows[rowIndex].Cells["Trucks"].Style.ForeColor = AppColors.Get("WarningText");
            }
            SortRegionRows();
            UpdateSelectAllState(_regionGrid, _selectAllRegions);

            foreach (var entry in dlcEntries.Where(entry => entry.Regions.Count == 0 && entry.Trucks.Count > 0).OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var rowIndex = _truckDlcGrid.Rows.Add(_ownedDlcState.IsOwned(entry.Code), entry.Name, ReleaseDateCellValue(entry.ReleaseDate), string.Join(", ", entry.Trucks));
                _truckDlcGrid.Rows[rowIndex].Tag = entry.Code;
                SetRowColor(_truckDlcGrid.Rows[rowIndex], entry.Name);
                if (entry.HasUnlocalizedTruck) _truckDlcGrid.Rows[rowIndex].Cells["Trucks"].Style.ForeColor = AppColors.Get("WarningText");
            }
            foreach (var entry in dlcEntries.Where(entry => entry.Regions.Count == 0 && entry.Trucks.Count == 0).OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var rowIndex = _otherDlcGrid.Rows.Add(_ownedDlcState.IsOwned(entry.Code), entry.Name, ReleaseDateCellValue(entry.ReleaseDate));
                _otherDlcGrid.Rows[rowIndex].Tag = entry.Code;
                SetRowColor(_otherDlcGrid.Rows[rowIndex], entry.Name);
            }

            var baseTruckFiles = prepared.BaseTruckFiles;
            var baseTruckCodes = baseTruckFiles.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in dlcEntries.Where(entry => entry.TruckCodes.Count > 0))
                foreach (var truckCode in entry.TruckCodes)
                {
                    if (baseTruckCodes.Contains(truckCode)) continue;
                    var truck = truckMetadata.GetValueOrDefault(truckCode, new TruckMetadata(truckCode, "—"));
                    var rowIndex = _truckGrid.Rows.Add(truck.Name, entry.Name, truck.Class);
                    _truckGrid.Rows[rowIndex].Tag = new TruckRowTag(entry.Code, entry.Name, truckCode, entry.ReleaseDate, IsBaseGame: false);
                }

            foreach (var file in baseTruckFiles)
            {
                var code = Path.GetFileNameWithoutExtension(file);
                if (!truckMetadata.TryGetValue(code, out var truck)) continue;
                var rowIndex = _truckGrid.Rows.Add(truck.Name, "Base game", truck.Class);
                _truckGrid.Rows[rowIndex].Tag = new TruckRowTag("base_game", "Base game", code, null, IsBaseGame: true);
            }
            _truckGrid.Sort(new TruckInitialComparer());
            UpdateTruckFilterOptions();
            RefreshTruckOwnershipAppearance();
            OwnedTrucksChanged?.Invoke();

            _status.Text = $"Loaded {_regionGrid.Rows.Count} regions, {_truckDlcGrid.Rows.Count} truck DLCs, {_otherDlcGrid.Rows.Count} other DLCs, and {_truckGrid.Rows.Count} trucks from the selected workspace.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not load owned content: {ex.Message}";
        }
    }

    public void WorkspaceUpdated()
    {
        _loadedWorkspace = null;
        if (Visible)
            EnsureLoaded();
    }

    private void BuildLayout()
    {
        ConfigureColumns();
        WireSelectAll(_regionGrid, _selectAllRegions);
        WireSelectAll(_truckDlcGrid, _selectAllTruckDlcs);
        WireSelectAll(_otherDlcGrid, _selectAllOtherDlcs);
        _dlcSections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var weight in new[] { 45f, 35f, 20f })
        {
            _dlcSections.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _dlcSections.RowStyles.Add(new RowStyle(SizeType.Percent, weight));
        }
        _dlcSections.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        AddOwnedSection("Region DLCs", _regionGrid, _selectAllRegions, 0, 45);
        AddOwnedSection("Truck DLCs", _truckDlcGrid, _selectAllTruckDlcs, 2, 35);
        AddOwnedSection("Other DLCs", _otherDlcGrid, _selectAllOtherDlcs, 4, 20);
        var truckSection = new GroupBox { Text = "Truck list", Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(6) };
        ConfigureTruckHeaderFilters();
        truckSection.Controls.Add(_truckGrid);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 69));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_status, 0, 0);
        layout.SetColumnSpan(_status, 2);
        layout.Controls.Add(_dlcSections, 0, 1);
        layout.Controls.Add(truckSection, 1, 1);
        Controls.Add(layout);
    }
    private void ConfigureColumns()
    {
        _regionGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Owned", HeaderText = "Owned", FillWeight = 14, MinimumWidth = 54 });
        _regionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DlcName", HeaderText = "DLC name", ReadOnly = true, FillWeight = 26, MinimumWidth = 100 });
        _regionGrid.Columns.Add(CreateReleaseDateColumn());
        _regionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RegionName", HeaderText = "Region name", ReadOnly = true, FillWeight = 24, MinimumWidth = 100 });
        _regionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Trucks", HeaderText = "Trucks added", ReadOnly = true, FillWeight = 36, MinimumWidth = 140 });

        _truckDlcGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Owned", HeaderText = "Owned", FillWeight = 18, MinimumWidth = 54 });
        _truckDlcGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DlcName", HeaderText = "DLC name", ReadOnly = true, FillWeight = 34, MinimumWidth = 110 });
        _truckDlcGrid.Columns.Add(CreateReleaseDateColumn());
        _truckDlcGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Trucks", HeaderText = "Trucks added", ReadOnly = true, FillWeight = 38, MinimumWidth = 130 });

        _otherDlcGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Owned", HeaderText = "Owned", FillWeight = 24, MinimumWidth = 54 });
        _otherDlcGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DlcName", HeaderText = "DLC name", ReadOnly = true, FillWeight = 56, MinimumWidth = 120 });
        _otherDlcGrid.Columns.Add(CreateReleaseDateColumn());

        _truckGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TruckName", HeaderText = "Truck", ReadOnly = true, FillWeight = 42, MinimumWidth = 120 });
        _truckGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DlcName", HeaderText = "DLC", ReadOnly = true, FillWeight = 38, MinimumWidth = 110 });
        _truckGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TruckClass", HeaderText = "Class", ReadOnly = true, FillWeight = 20, MinimumWidth = 100 });
        foreach (DataGridViewColumn column in _truckGrid.Columns)
            column.SortMode = DataGridViewColumnSortMode.Programmatic;
        _truckGrid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var column = _truckGrid.Columns[e.ColumnIndex];
            var cellBounds = _truckGrid.GetCellDisplayRectangle(e.ColumnIndex, -1, true);
            var filterBounds = TableHeaderCell.FilterBounds(new Rectangle(0, 0, cellBounds.Width, cellBounds.Height), _truckGrid.DeviceDpi / 96f);
            if (filterBounds.Contains(e.X, e.Y)) { ShowTruckFilter(column); return; }
            if (_truckSortColumn == column.Name && _truckSortDirection == ListSortDirection.Descending)
            {
                ClearTruckSort(); return;
            }
            SortTrucks(column.Name, _truckSortColumn == column.Name && _truckSortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending : ListSortDirection.Ascending);
        };

        TableHeaderBehavior.Attach(_regionGrid,
            () => _regionGrid.Sort(new RegionDlcComparer(ListSortDirection.Ascending)),
            (column, direction) =>
            {
                if (column.Name == "DlcName") _regionGrid.Sort(new RegionDlcComparer(direction));
                else if (column.Name == "ReleaseDate") _regionGrid.Sort(new ReleaseDateComparer(direction));
                else _regionGrid.Sort(column, direction);
            });
        foreach (var grid in new[] { _truckDlcGrid, _otherDlcGrid })
            TableHeaderBehavior.Attach(grid, sort: (column, direction) =>
            {
                if (column.Name == "ReleaseDate") grid.Sort(new ReleaseDateComparer(direction));
                else grid.Sort(column, direction);
            });
    }
    private void AddOwnedSection(string title, DataGridView grid, CheckBox selectAll, int headerRow, float weight)
    {
        var bodyRow = headerRow + 1;
        var body = new Panel { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(6, 0, 6, 6) };
        body.Controls.Add(grid);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, AutoSize = true, Margin = new Padding(8, 2, 8, 2) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        header.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9.5f, FontStyle.Bold), Padding = new Padding(2, 4, 4, 4) }, 0, 0);
        selectAll.Anchor = AnchorStyles.Right;
        header.Controls.Add(selectAll, 1, 0);
        var toggle = new LinkLabel { Text = "▾", Size = new Size(26, 26), TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.Right, Margin = Padding.Empty, LinkBehavior = LinkBehavior.NeverUnderline,
            LinkColor = SystemColors.ControlText, ActiveLinkColor = SystemColors.Highlight, VisitedLinkColor = SystemColors.ControlText,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 14f), AccessibleRole = AccessibleRole.PushButton, TabStop = true };
        var collapsed = false;
        void UpdateToggle()
        {
            toggle.Text = collapsed ? "▸" : "▾";
            toggle.AccessibleName = (collapsed ? "Expand " : "Collapse ") + title;
            toggle.AccessibleDescription = collapsed ? "Collapsed" : "Expanded";
            _paneTips.SetToolTip(toggle, toggle.AccessibleName);
        }
        toggle.LinkClicked += (_, _) =>
        {
            collapsed = !collapsed;
            _dlcSections.SuspendLayout();
            body.Visible = !collapsed;
            selectAll.Visible = !collapsed;
            _dlcSections.RowStyles[bodyRow].SizeType = collapsed ? SizeType.Absolute : SizeType.Percent;
            _dlcSections.RowStyles[bodyRow].Height = collapsed ? 0 : weight;
            if (collapsed) _expandedDlcPanes.Remove(bodyRow); else _expandedDlcPanes.Add(bodyRow);
            _dlcSections.RowStyles[6].SizeType = _expandedDlcPanes.Count == 0 ? SizeType.Percent : SizeType.Absolute;
            _dlcSections.RowStyles[6].Height = _expandedDlcPanes.Count == 0 ? 100 : 0;
            UpdateToggle();
            _dlcSections.ResumeLayout(true);
        };
        UpdateToggle();
        header.Controls.Add(toggle, 2, 0);
        _dlcSections.Controls.Add(header, 0, headerRow);
        _dlcSections.Controls.Add(body, 0, bodyRow);
        _expandedDlcPanes.Add(bodyRow);
    }
    private void WireSelectAll(DataGridView grid, CheckBox selectAll)
    {
        selectAll.CheckedChanged += (_, _) =>
        {
            if (_updatingSelectAll) return;
            _updatingSelectAll = true;
            foreach (DataGridViewRow row in grid.Rows) row.Cells["Owned"].Value = selectAll.Checked;
            _updatingSelectAll = false;
            if (grid == _regionGrid)
                foreach (DataGridViewRow row in grid.Rows) SetOwnedRegion(row, selectAll.Checked);
            else
                foreach (DataGridViewRow row in grid.Rows)
                    if (row.Tag is string code) _ownedDlcState.SetOwned(code, selectAll.Checked);
            UpdateSelectAllState(grid, selectAll);
            if (grid == _truckDlcGrid || grid == _regionGrid)
            {
                RefreshTruckOwnershipAppearance();
                OwnedTrucksChanged?.Invoke();
            }
        };
        grid.CellValueChanged += (_, e) =>
        {
            if (_updatingSelectAll || e.RowIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Owned") return;
            if (grid == _regionGrid) SetOwnedRegion(grid.Rows[e.RowIndex], grid.Rows[e.RowIndex].Cells["Owned"].Value is true);
            else if (grid.Rows[e.RowIndex].Tag is string code) _ownedDlcState.SetOwned(code, grid.Rows[e.RowIndex].Cells["Owned"].Value is true);
            UpdateSelectAllState(grid, selectAll);
            if (grid == _truckDlcGrid || grid == _regionGrid)
            {
                RefreshTruckOwnershipAppearance();
                OwnedTrucksChanged?.Invoke();
            }
        };
    }

    private void RefreshTruckOwnershipAppearance()
    {
        foreach (DataGridViewRow row in _truckGrid.Rows)
        {
            if (row.Tag is not TruckRowTag tag) continue;
            var owned = IsDlcOwned(tag.DlcCode, tag.DlcName);
            var background = tag.IsBaseGame ? AppColors.Get("BaseGameOwned") : AppColors.Get("Surface");
            row.DefaultCellStyle.ForeColor = owned ? SystemColors.ControlText : SystemColors.GrayText;
            row.DefaultCellStyle.BackColor = background;
            row.DefaultCellStyle.SelectionForeColor = owned ? SystemColors.ControlText : SystemColors.GrayText;
            row.DefaultCellStyle.SelectionBackColor = background;
        }
    }

    private void RefreshPaletteColors()
    {
        foreach (var grid in new[] { _regionGrid, _truckDlcGrid, _otherDlcGrid })
            foreach (DataGridViewRow row in grid.Rows)
                SetRowColor(row, Convert.ToString(row.Cells["DlcName"].Value) ?? string.Empty);
        RefreshTruckOwnershipAppearance();
        Invalidate(true);
    }

    private void ConfigureTruckHeaderFilters()
    {
        foreach (DataGridViewColumn column in _truckGrid.Columns)
        {
            var filter = new MultiSelectFilter(column.HeaderText, column.Name);
            filter.Changed += ApplyTruckFilters;
            _truckFilters.Add(filter);
            column.HeaderCell = new TableHeaderCell(() => filter.IsFiltered) { Value = column.HeaderText };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Sort ascending", null, (_, _) => SortTrucks(column.Name, ListSortDirection.Ascending));
            menu.Items.Add("Sort descending", null, (_, _) => SortTrucks(column.Name, ListSortDirection.Descending));
            menu.Items.Add("Sort off (default order)", null, (_, _) => ClearTruckSort());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Filter " + column.HeaderText + "…", null, (_, _) => ShowTruckFilter(column));
            menu.Items.Add("Clear filter", null, (_, _) => filter.Clear());
            column.HeaderCell.ContextMenuStrip = menu;
            Disposed += (_, _) => menu.Dispose();
        }
        UpdateTruckHeaderTips();
    }

    private void ShowTruckFilter(DataGridViewColumn column)
    {
        var bounds = _truckGrid.GetCellDisplayRectangle(column.Index, -1, true);
        _truckFilters.Single(filter => filter.ColumnName == column.Name)
            .Show(_truckGrid, new Point(bounds.Right - 280, bounds.Bottom));
    }

    private void SortTrucks(string columnName, ListSortDirection direction)
    {
        _truckSortColumn = columnName;
        _truckSortDirection = direction;
        _truckGrid.Sort(new TruckColumnComparer(columnName, direction));
        foreach (DataGridViewColumn column in _truckGrid.Columns)
            column.HeaderCell.SortGlyphDirection = column.Name != columnName ? SortOrder.None
                : direction == ListSortDirection.Ascending ? SortOrder.Ascending : SortOrder.Descending;
        _truckGrid.Invalidate();
    }

    private void UpdateTruckHeaderTips()
    {
        foreach (var filter in _truckFilters)
            _truckGrid.Columns[filter.ColumnName]!.HeaderCell.ToolTipText =
                "Click the heading to cycle ascending, descending, off; click the funnel to filter. Right-click for commands. " + filter.Description;
    }

    private void ClearTruckSort()
    {
        _truckSortColumn = null;
        _truckSortDirection = ListSortDirection.Ascending;
        _truckGrid.Sort(new TruckInitialComparer());
        foreach (DataGridViewColumn column in _truckGrid.Columns) column.HeaderCell.SortGlyphDirection = SortOrder.None;
        _truckGrid.Invalidate();
    }
    private void ApplyTruckFilters()
    {
        _truckGrid.CurrentCell = null;
        foreach (DataGridViewRow row in _truckGrid.Rows)
            row.Visible = _truckFilters.All(filter => filter.Includes(Convert.ToString(row.Cells[filter.ColumnName].Value) ?? string.Empty));
        UpdateTruckHeaderTips();
        _truckGrid.Invalidate();
    }

    private void UpdateTruckFilterOptions()
    {
        foreach (var filter in _truckFilters)
        {
            var options = _truckGrid.Rows.Cast<DataGridViewRow>()
                .Select(row => Convert.ToString(row.Cells[filter.ColumnName].Value) ?? string.Empty)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase);
            filter.SetOptions(options);
        }
        ApplyTruckFilters();
    }

    private void SetRowColor(DataGridViewRow row, string dlcName)
    {
        var color = string.Equals(dlcName, "Base game", StringComparison.OrdinalIgnoreCase)
            ? AppColors.Get("BaseGameOwned")
            : AppColors.Get("Surface");
        row.DefaultCellStyle.BackColor = color;
        row.DefaultCellStyle.SelectionBackColor = color;
        row.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
    }

    private sealed class TruckInitialComparer : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var first = (DataGridViewRow)x!;
            var second = (DataGridViewRow)y!;
            var firstTag = (TruckRowTag)first.Tag!;
            var secondTag = (TruckRowTag)second.Tag!;
            if (firstTag.IsBaseGame != secondTag.IsBaseGame) return firstTag.IsBaseGame ? -1 : 1;
            var firstDate = firstTag.ReleaseDate;
            var secondDate = secondTag.ReleaseDate;
            var dateComparison = firstDate.HasValue != secondDate.HasValue
                ? (firstDate.HasValue ? -1 : 1)
                : firstDate.GetValueOrDefault().CompareTo(secondDate.GetValueOrDefault());
            if (dateComparison != 0) return dateComparison;
            var dlcComparison = string.Compare(firstTag.DlcName, secondTag.DlcName, StringComparison.CurrentCultureIgnoreCase);
            return dlcComparison != 0 ? dlcComparison : string.Compare(Convert.ToString(first.Cells["TruckName"].Value), Convert.ToString(second.Cells["TruckName"].Value), StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private sealed class TruckColumnComparer(string columnName, ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var first = (DataGridViewRow)x!;
            var second = (DataGridViewRow)y!;
            var comparison = string.Compare(Convert.ToString(first.Cells[columnName].Value), Convert.ToString(second.Cells[columnName].Value), StringComparison.CurrentCultureIgnoreCase);
            return direction == ListSortDirection.Ascending ? comparison : -comparison;
        }
    }

    private bool IsDlcOwned(string code, string name)
    {
        if (string.Equals(code, "base_game", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (DataGridViewRow row in _truckDlcGrid.Rows)
            if (string.Equals(row.Tag as string, code, StringComparison.OrdinalIgnoreCase))
                return row.Cells["Owned"].Value is true;

        var regionRows = _regionGrid.Rows.Cast<DataGridViewRow>()
            .Where(row => string.Equals(Convert.ToString(row.Cells["DlcName"].Value), name, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        if (regionRows.Length > 0) return regionRows.Any(row => row.Cells["Owned"].Value is true);

        foreach (DataGridViewRow row in _otherDlcGrid.Rows)
            if (string.Equals(row.Tag as string, code, StringComparison.OrdinalIgnoreCase))
                return row.Cells["Owned"].Value is true;
        return false;
    }

    public IReadOnlyList<OwnedTruck> GetOwnedTrucks() => TruckCatalogService.GetOwned(GetTruckCatalogEntries());

    public IReadOnlySet<string> GetOwnedDlcCodes() => ((PreparedWorkspaceData)WorkspaceDataCache.GetOwnedData(_getWorkspacePath().Trim()))
        .DlcEntries.Where(entry => IsDlcOwned(entry.Code, entry.Name)).Select(entry => entry.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<CustomTruck> GetAvailableCustomGroupingTrucks() =>
        TruckCatalogService.GetAvailableForGrouping(GetTruckCatalogEntries());

    private IEnumerable<TruckCatalogEntry> GetTruckCatalogEntries() =>
        _truckGrid.Rows.Cast<DataGridViewRow>()
            .Where(row => row.Tag is TruckRowTag)
            .Select(row =>
            {
                var tag = (TruckRowTag)row.Tag!;
                return new TruckCatalogEntry(
                    tag.TruckCode,
                    Convert.ToString(row.Cells["TruckName"].Value) ?? tag.TruckCode,
                    Convert.ToString(row.Cells["TruckClass"].Value) ?? "—",
                    IsDlcOwned(tag.DlcCode, tag.DlcName),
                    tag.IsBaseGame);
            });
    private void UpdateSelectAllState(DataGridView grid, CheckBox selectAll)
    {
        _updatingSelectAll = true;
        selectAll.Checked = grid.Rows.Count > 0 && grid.Rows.Cast<DataGridViewRow>().All(row => row.Cells["Owned"].Value is true);
        _updatingSelectAll = false;
    }

    private static DataGridView CreateGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f),
            ColumnHeadersDefaultCellStyle = { Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f, FontStyle.Bold) },
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeColumns = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            EditMode = DataGridViewEditMode.EditOnEnter,
            ScrollBars = ScrollBars.Both,
            RowTemplate = { Height = 26 },
            DefaultCellStyle = { BackColor = AppColors.Get("Surface"), SelectionBackColor = AppColors.Get("Surface"), SelectionForeColor = SystemColors.ControlText },
            AlternatingRowsDefaultCellStyle = { BackColor = AppColors.Get("AlternateRow"), SelectionBackColor = AppColors.Get("AlternateRow"), SelectionForeColor = SystemColors.ControlText }
        };
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        return grid;
    }

    internal static object PrepareWorkspaceData(string workspace, IReadOnlyDictionary<string, string> localizedNames)
    {
        var truckMetadata = LoadTruckMetadataFromDlcXml(workspace, localizedNames);
        var dlcLibraryFile = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[ps]", "dlc_library.sso"));
        var dlcEntries = File.Exists(dlcLibraryFile)
            ? ReadDlcEntries(WorkspaceDataCache.ReadAllText(dlcLibraryFile, Encoding.UTF8), localizedNames, truckMetadata)
            : [];
        var baseTruckFiles = WorkspaceOriginals.EnumerateCurrentFiles(workspace, Path.Combine("[media]", "classes", "trucks"), "*.xml", SearchOption.TopDirectoryOnly).ToList();
        // ANK Civilian ships in a DLC folder without a truck entitlement entry.
        // Include it in the base catalog so ownership/grouping/tier consumers can see it.
        // Do not infer that every unlisted DLC-folder vehicle is a base-game truck.
        var civilianFile = WorkspaceOriginals.GetCurrentPath(workspace,
            Path.Combine("[media]", "_dlc", "dlc_6", "classes", "trucks", "ank_mk38_ht.xml"));
        if (truckMetadata.ContainsKey("ank_mk38_ht") && File.Exists(civilianFile)
            && !baseTruckFiles.Any(file => Path.GetFileNameWithoutExtension(file).Equals("ank_mk38_ht", StringComparison.OrdinalIgnoreCase)))
            baseTruckFiles.Add(civilianFile);
        return new PreparedWorkspaceData(truckMetadata, dlcEntries, baseTruckFiles.ToArray());
    }

    private sealed record PreparedWorkspaceData(
        Dictionary<string, TruckMetadata> TruckMetadata,
        List<DlcEntry> DlcEntries,
        string[] BaseTruckFiles);
    private static List<DlcEntry> ReadDlcEntries(string dlcLibrary, IReadOnlyDictionary<string, string> localizedDlcNames, IReadOnlyDictionary<string, TruckMetadata> truckMetadata)
    {
        var sections = DlcSection.Matches(dlcLibrary).Cast<Match>().ToArray();
        var entries = new List<DlcEntry>();
        for (var i = 0; i < sections.Length; i++)
        {
            var start = sections[i].Index;
            var end = i + 1 < sections.Length ? sections[i + 1].Index : dlcLibrary.Length;
            var body = dlcLibrary.Substring(start, end - start);
            var code = sections[i].Groups["section"].Value;
            var nameMatch = DlcName.Match(body);
            if (!nameMatch.Success || !localizedDlcNames.TryGetValue(nameMatch.Groups["key"].Value, out var dlcName)) continue;

            var regionMatch = RegionList.Match(body);
            var regions = regionMatch.Success
                ? RegionCode.Matches(regionMatch.Groups["regions"].Value).Cast<Match>().Select(match => match.Groups["code"].Value).ToList()
                : new List<string>();
            var truckCodes = GetTruckCodes(body);
            var hasUnlocalizedTruck = truckCodes.Any(code => !truckMetadata.ContainsKey(code));
            var truckNames = truckCodes
                .Select(code => truckMetadata.GetValueOrDefault(code, new TruckMetadata(code, "—")).Name)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            entries.Add(new DlcEntry(code, dlcName, regions, truckNames, truckCodes, hasUnlocalizedTruck, DlcReleaseDates.Get(code)));
        }
        return entries;
    }

    private static Dictionary<string, TruckMetadata> LoadTruckMetadataFromDlcXml(string workspace, IReadOnlyDictionary<string, string> localizedNames)
    {
        var result = new Dictionary<string, TruckMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in WorkspaceOriginals.EnumerateCurrentFiles(workspace, Path.Combine("[media]", "classes", "trucks"), "*.xml")
                     .Concat(WorkspaceOriginals.EnumerateCurrentFiles(workspace, Path.Combine("[media]", "_dlc"), "*.xml"))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // Vehicle definitions are directly in each DLC's classes/trucks folder.
            // Do not treat truck add-ons, engines, wheels, or suspension XMLs as vehicles.
            if (!string.Equals(Directory.GetParent(file)?.Name, "trucks", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                // SnowRunner truck files commonly concatenate a _templates root and a
                // separate Truck root, which is not a valid single-root XML document.
                // Parse just the Truck element so those files still provide UiDesc.
                var xml = WorkspaceDataCache.ReadAllText(file, Encoding.UTF8);
                var truckElement = Regex.Match(xml, @"<Truck(?=[\s>])[\s\S]*?</Truck\s*>", RegexOptions.IgnoreCase);
                if (!truckElement.Success) continue;
                // Truck XML can contain region-prefixed elements such as <region:default>
                // without declaring the prefix, so a strict XML parser rejects valid game data.
                // UiName is the first localized vehicle name in the truck's UiDesc block.
                var uiNameKey = Regex.Match(truckElement.Value, @"\bUiName\s*=\s*""(?<key>[^""]+)""", RegexOptions.IgnoreCase)
                    .Groups["key"].Value;
                var truckName = !string.IsNullOrWhiteSpace(uiNameKey) && localizedNames.TryGetValue(uiNameKey, out var localizedName)
                    ? localizedName
                    : Path.GetFileNameWithoutExtension(file);
                var truckClass = Regex.Match(truckElement.Value, @"\bTruckType\s*=\s*""(?<class>[^""]+)""", RegexOptions.IgnoreCase)
                    .Groups["class"].Value;

                var code = Path.GetFileNameWithoutExtension(file);
                result[code] = new TruckMetadata(truckName, TruckClasses.ForTruck(code, string.IsNullOrWhiteSpace(truckClass) ? "—" : truckClass));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // Skip a malformed vehicle definition and continue loading the remaining DLCs.
            }
        }

        return result;
    }

    private void SortRegionRows()
    {
        if (_regionGrid.Rows.Count == 0) return;
        _regionGrid.Sort(new RegionDlcComparer(_regionSortDirection));
        _regionGrid.Columns["DlcName"]!.HeaderCell.SortGlyphDirection = _regionSortDirection == ListSortDirection.Ascending
            ? SortOrder.Ascending
            : SortOrder.Descending;
    }

    private sealed class RegionDlcComparer(ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var first = (DataGridViewRow)x!;
            var second = (DataGridViewRow)y!;
            var firstName = Convert.ToString(first.Cells["DlcName"].Value) ?? string.Empty;
            var secondName = Convert.ToString(second.Cells["DlcName"].Value) ?? string.Empty;
            var firstSeason = SeasonNumber.Match(firstName);
            var secondSeason = SeasonNumber.Match(secondName);
            int comparison;
            if (firstSeason.Success && secondSeason.Success)
                comparison = int.Parse(firstSeason.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture)
                    .CompareTo(int.Parse(secondSeason.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture));
            else if (firstSeason.Success != secondSeason.Success)
                comparison = firstSeason.Success ? 1 : -1;
            else
                comparison = string.Compare(firstName, secondName, StringComparison.CurrentCultureIgnoreCase);

            if (comparison != 0) return direction == ListSortDirection.Ascending ? comparison : -comparison;
            return string.Compare(
                Convert.ToString(first.Cells["RegionName"].Value),
                Convert.ToString(second.Cells["RegionName"].Value),
                StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private static List<string> GetTruckCodes(string dlcBody)
    {
        var codes = new List<string>();
        foreach (var property in new[] { "trucksToAdd", "dlcTruck" })
        {
            var propertyMatch = Regex.Match(dlcBody, $@"\b{property}\s*=\s*\{{", RegexOptions.IgnoreCase);
            if (!propertyMatch.Success) continue;
            var block = ReadBraceBlock(dlcBody, propertyMatch.Index + propertyMatch.Length - 1);
            codes.AddRange(TruckEntry.Matches(block).Cast<Match>().Select(match => match.Groups["truck"].Value));
        }
        return codes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ReadBraceBlock(string text, int openingBrace)
    {
        var depth = 0;
        for (var i = openingBrace; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[openingBrace..(i + 1)];
        }
        return text[openingBrace..];
    }

    private static DataGridViewTextBoxColumn CreateReleaseDateColumn() => new()
    {
        Name = "ReleaseDate",
        HeaderText = "Release date",
        ReadOnly = true,
        FillWeight = 20,
        MinimumWidth = 88,
        SortMode = DataGridViewColumnSortMode.Programmatic,
        DefaultCellStyle = { Format = "yyyy-MMMM", FormatProvider = System.Globalization.CultureInfo.InvariantCulture, NullValue = "—" }
    };

    private static object ReleaseDateCellValue(DateOnly? date) => date.HasValue ? date.Value : DBNull.Value;

    private sealed class ReleaseDateComparer(ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var first = (DataGridViewRow)x!;
            var second = (DataGridViewRow)y!;
            var firstHasDate = first.Cells["ReleaseDate"].Value is DateOnly;
            var secondHasDate = second.Cells["ReleaseDate"].Value is DateOnly;
            if (firstHasDate != secondHasDate) return firstHasDate ? -1 : 1;
            if (!firstHasDate) return 0;
            var comparison = ((DateOnly)first.Cells["ReleaseDate"].Value!).CompareTo((DateOnly)second.Cells["ReleaseDate"].Value!);
            return direction == ListSortDirection.Ascending ? comparison : -comparison;
        }
    }

    private void UpdateOwnedRegion(string regionCode, bool isOwned)
    {
        foreach (DataGridViewRow row in _regionGrid.Rows)
            if (string.Equals(row.Tag as string, regionCode, StringComparison.OrdinalIgnoreCase))
                row.Cells["Owned"].Value = isOwned;
        UpdateSelectAllState(_regionGrid, _selectAllRegions);
    }

    private void SetOwnedRegion(DataGridViewRow row, bool isOwned)
    {
        if (row.Tag is string code) _ownedRegionState.SetOwned(code, isOwned);
    }

    private sealed class MultiSelectFilter : IDisposable
    {
        private readonly string _label;
        private readonly ToolStripDropDown _dropDown = new() { AutoClose = true };
        private readonly CheckedListBox _options = new()
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
            HorizontalScrollbar = true
        };
        private bool _suppressEvents;

        public MultiSelectFilter(string label, string columnName)
        {
            _label = label;
            ColumnName = columnName;
            _dropDown.AccessibleName = label + " filter";
            var selectAll = new Button { Text = "Select all", AutoSize = true, Margin = new Padding(2) };
            var clear = new Button { Text = "Select none", AutoSize = true, Margin = new Padding(2) };
            var commands = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, AutoSize = false, WrapContents = false };
            commands.Controls.Add(selectAll);
            commands.Controls.Add(clear);
            var content = new Panel { Width = 280, Height = 330, Padding = new Padding(4) };
            content.Controls.Add(_options);
            content.Controls.Add(commands);
            var host = new ToolStripControlHost(content)
            {
                AutoSize = false,
                Size = content.Size,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            _dropDown.Items.Add(host);
            selectAll.Click += (_, _) => SetAll(true);
            clear.Click += (_, _) => SetAll(false);
            _options.ItemCheck += (_, _) =>
            {
                if (_suppressEvents) return;
                if (_options.IsHandleCreated) _options.BeginInvoke((Action)NotifyChanged);
                else NotifyChanged();
            };
        }

        public event Action? Changed;
        public string ColumnName { get; }
        public bool IsFiltered => _options.CheckedItems.Count != _options.Items.Count;
        public string Description => $"{_label}: {_options.CheckedItems.Count} of {_options.Items.Count} selected.";
        public void Show(Control owner, Point location) => _dropDown.Show(owner, location);
        public void Clear() => SetAll(true);
        public void Dispose() => _dropDown.Dispose();

        public void SetOptions(IEnumerable<string> values)
        {
            _suppressEvents = true;
            _options.BeginUpdate();
            _options.Items.Clear();
            foreach (var value in values) _options.Items.Add(value, isChecked: true);
            _options.EndUpdate();
            _suppressEvents = false;
        }

        public bool Includes(string value) => _options.CheckedItems.Count == _options.Items.Count ||
            _options.CheckedItems.Cast<string>().Contains(value, StringComparer.CurrentCultureIgnoreCase);

        private void SetAll(bool selected)
        {
            _suppressEvents = true;
            for (var i = 0; i < _options.Items.Count; i++) _options.SetItemChecked(i, selected);
            _suppressEvents = false;
            NotifyChanged();
        }

        private void NotifyChanged()
        {
            Changed?.Invoke();
        }
    }

    private sealed record TruckMetadata(string Name, string Class);
    private sealed record TruckRowTag(string DlcCode, string DlcName, string TruckCode, DateOnly? ReleaseDate, bool IsBaseGame);
    private sealed record DlcEntry(string Code, string Name, List<string> Regions, List<string> Trucks, List<string> TruckCodes, bool HasUnlocalizedTruck, DateOnly? ReleaseDate);
}
