using System.Data;

namespace SnowPakTool.Desktop;

internal sealed record ReplacementTruck(CustomTruck Truck, TruckEquipmentSummary Equipment, IReadOnlyDictionary<string, string> Properties,
    MapTruckEquipment? Configuration = null);
internal sealed record TruckComparisonRow(string Property, string Current, string Replacement);

internal static class TruckReplacementComparison
{
    private static readonly HashSet<string> HiddenProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "EngineMarkerOffset", "ShadowHideDistance", "HideDistance", "ExhaustStartTime", "EngineStartDelay",
        "EngineIconScale", "EngineIconMesh", "SteerSpeed", "TruckImage"
    };

    private static bool HiddenCategory(string category)
    {
        var compact = category.Replace("_", "").Replace("-", "").Replace(" ", "").ToLowerInvariant();
        return compact is "hoodornament" or "hoodornaments" or "miscellenious" or "miscellaneous";
    }

    private static bool HiddenProperty(string key)
    {
        var parts = key.Split('/', StringSplitOptions.TrimEntries);
        if (parts[0].Equals("Wheels", StringComparison.OrdinalIgnoreCase))
            return !key.Equals("Wheels / Compatible families and scale", StringComparison.OrdinalIgnoreCase);
        return HiddenProperties.Contains(parts[^1])
            || (parts.Length > 1 && parts[0] == "Addon socket options" && HiddenCategory(parts[1]));
    }
    public static IReadOnlyList<TruckComparisonRow> Build(RegionTruckEntry current, ReplacementTruck original, ReplacementTruck candidate)
    {
        var left = Values(new TruckEquipmentSummary(current.Tires, current.TireDetails, current.Addons, current.AddonDetails,
            current.InstalledAddons, current.FrameAddons, current.FrameAddonDetails, current.InternalEquipment), original.Properties);
        var right = Values(candidate.Equipment, candidate.Properties);
        return left.Keys.Concat(right.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key => new TruckComparisonRow(key, left.GetValueOrDefault(key, "Not specified"), right.GetValueOrDefault(key, "Not specified")))
            .Where(row => !row.Property.StartsWith("Addon socket options / ", StringComparison.Ordinal) || row.Current != row.Replacement).ToArray();
    }

    private static Dictionary<string, string> Values(TruckEquipmentSummary equipment, IReadOnlyDictionary<string, string> properties)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in equipment.InternalEquipment ?? [])
        {
            if (item.Slot == "Vehicle definition") continue;
            if (equipment.InstalledAddons?.Any(addon => addon.IsVisual && addon.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase)
                && addon.Category is not ("awd" or "diff_lock")) == true) continue;
            values["Internal / " + item.Slot] = item.Name;
        }
        if (equipment.InstalledAddons is null) values["Installed addons"] = "Unknown — map equipment could not be read";
        else foreach (var group in equipment.InstalledAddons.Where(addon => !addon.IsVisual && !HiddenCategory(addon.Category)).GroupBy(addon => addon.Category.Length == 0 ? "Uncategorized" : addon.Category))
            values["Installed addons / " + group.Key.Replace('_', ' ')] = string.Join("\r\n", group.Select(addon => addon.Name).Order(StringComparer.CurrentCultureIgnoreCase));
        foreach (var (key, value) in properties)
            if (!HiddenProperty(key)) values[key] = value.Length == 0 ? "None specified" : value;
        return values;
    }
}

/// <summary>Compare and save a replacement using the candidate's default equipment.</summary>
internal sealed class TruckReplacementDialog : Form
{
    private readonly RegionTruckEntry _current;
    private readonly Func<IReadOnlyList<CustomTruck>> _getEnabled;
    private readonly string _workspace;
    private readonly CheckBox _differences = new() { Text = "Differences only", AutoSize = true, Margin = new Padding(12, 7, 4, 4) };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(4, 6, 4, 6) };
    private readonly DataGridView _list = Grid();
    private readonly DataGridView _comparison = new SmoothComparisonGrid();
    private readonly SmoothComparisonPanel _comparisonScroll;
    private readonly CurrentTabSearch _search;
    private IReadOnlyList<ReplacementTruck> _trucks = [];
    private ReplacementTruck? _original;
    private bool _updating;
    private TruckEquipmentCatalog? _equipmentCatalog;
    private int _comparisonRequest;
    private readonly Button _apply = new() { Text = "Apply replacement", AutoSize = true, Enabled = false };
    private bool _saving;
    public PendingTruckReplacement? QueuedReplacement { get; private set; }

    public TruckReplacementDialog(string workspace, RegionTruckEntry current, Func<IReadOnlyList<CustomTruck>> getEnabled,
        TruckEquipmentCatalog? equipmentCatalog = null)
    {
        _workspace = workspace; _current = current; _getEnabled = getEnabled;
        _equipmentCatalog = equipmentCatalog;
        ConfigureGrid(_comparison);
        var listHeaders = TableHeaderBehavior.Attach(_list);
        listHeaders.AddFilter("Class");
        listHeaders.AddFilter("Default tires");
        listHeaders.Changed += () => Compare();
        TableHeaderBehavior.Attach(_comparison);
        _comparisonScroll = new SmoothComparisonPanel(_comparison) { Dock = DockStyle.Fill };
        Text = "Replace truck — " + current.Truck;
        StartPosition = FormStartPosition.CenterParent; Size = new Size(1150, 850); MinimumSize = new Size(800, 600);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        filters.Controls.Add(_differences);
        layout.Controls.Add(filters, 0, 0); layout.Controls.Add(_status, 0, 1);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1100, 650), SplitterDistance = 270, Panel1MinSize = 100, Panel2MinSize = 100, SplitterWidth = 6 };
        split.Panel1.Controls.Add(_list); split.Panel2.Controls.Add(_comparisonScroll); layout.Controls.Add(split, 0, 2);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel }; footer.Controls.Add(close);
        footer.Controls.Add(_apply);
        footer.Controls.Add(new Label { Text = "Saves a draft • stage in Map trucks or Randomizer, then apply from Start", AutoSize = true, Padding = new Padding(4, 7, 12, 0) });
        layout.Controls.Add(footer, 0, 3); Controls.Add(layout); CancelButton = close;
        _comparison.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _comparison.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _comparison.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _differences.CheckedChanged += (_, _) => Compare();
        _list.CurrentCellChanged += (_, _) => Compare();
        _apply.Click += async (_, _) => await ApplyReplacementAsync();
        FormClosing += (_, e) => { if (_saving) e.Cancel = true; };
        _search = new CurrentTabSearch(() => layout); _search.Attach(this);
        Shown += async (_, _) => await LoadTrucksAsync();
        AppColors.Changed += PaletteChanged; Disposed += (_, _) => { _search.Dispose(); AppColors.Changed -= PaletteChanged; };
        PaletteChanged(this, EventArgs.Empty);
    }

    private async Task LoadTrucksAsync()
    {
        _status.Text = "Loading enabled trucks and equipment…";
        try
        {
            var enabled = _getEnabled().DistinctBy(truck => truck.Code, StringComparer.OrdinalIgnoreCase).ToArray();
            var names = WorkspaceDataCache.GetLocalizedNames(_workspace);
            var loaded = await Task.Run(() =>
            {
                var catalog = _equipmentCatalog?.IsCurrent(_workspace) == true ? _equipmentCatalog : new TruckEquipmentCatalog(_workspace, names, []);
                ReplacementTruck Profile(CustomTruck truck) => new(truck, catalog.ResolveDefault(truck.Code), new Dictionary<string, string>());
                var trucks = enabled.Select(Profile).OrderBy(item => item.Truck.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                var original = trucks.FirstOrDefault(item => item.Truck.Code.Equals(_current.TruckCode, StringComparison.OrdinalIgnoreCase))
                    ?? Profile(new CustomTruck(_current.TruckCode, _current.Truck, _current.Class));
                original = original with { Properties = catalog.ComparisonProperties(original.Truck.Code) };
                if (trucks.Length > 0) catalog.ComparisonProperties(trucks[0].Truck.Code);
                return (trucks, original, catalog);
            });
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke((Action)(() => FinishLoading(loaded.trucks, loaded.original, loaded.catalog)));
            else FinishLoading(loaded.trucks, loaded.original, loaded.catalog);
        }
        catch (Exception ex) { if (!IsDisposed) _status.Text = "Could not load replacement trucks: " + ex.Message; }
    }

    private void FinishLoading(IReadOnlyList<ReplacementTruck> trucks, ReplacementTruck original, TruckEquipmentCatalog catalog)
    {
        if (IsDisposed) return;
        _trucks = trucks; _original = original; _equipmentCatalog = catalog;
        _updating = false; ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_updating) return;
        var previous = Selected()?.Truck.Code;
        var table = new DataTable();
        foreach (var column in new[] { "Truck", "Class", "Default tires", "Default frame addons", "Truck ID" }) table.Columns.Add(column);
        table.Columns.Add("Profile", typeof(ReplacementTruck));
        foreach (var truck in _trucks)
            table.Rows.Add(truck.Truck.Name, truck.Truck.Class, truck.Equipment.Tires, truck.Equipment.FrameAddons, truck.Truck.Code, truck);
        var sort = (_list.DataSource as DataTable)?.DefaultView.Sort;
        if (!string.IsNullOrEmpty(sort)) table.DefaultView.Sort = sort;
        _list.DataSource = table; _list.Columns["Profile"]!.Visible = false;
        foreach (DataGridViewRow row in _list.Rows)
            if (row.DataBoundItem is DataRowView view && ((ReplacementTruck)view["Profile"]).Truck.Code == previous)
            { _list.CurrentCell = row.Cells[0]; break; }
        _status.Text = $"{table.Rows.Count} of {_trucks.Count} enabled trucks • Double-tap Shift to search any text (3+ characters) • Click a column header to sort";
        Compare();
    }

    private ReplacementTruck? Selected() => _list.CurrentRow?.DataBoundItem is DataRowView row ? row["Profile"] as ReplacementTruck : null;

    private void Compare()
    {
        _apply.Enabled = !_saving && QueuedReplacement is null && _current.MapDataHash.Length > 0 && Selected() is { } choice
            && !choice.Truck.Code.Equals(_current.TruckCode, StringComparison.OrdinalIgnoreCase);
        var request = ++_comparisonRequest;
        var selected = Selected();
        var table = new DataTable(); table.Columns.Add("Property");
        table.Columns.Add("Current — " + _current.Truck + " (map equipment)");
        table.Columns.Add("Replacement — " + (Selected()?.Truck.Name ?? "select a truck") + " (defaults)");
        if (_original is not null && selected is not null && _equipmentCatalog is { } catalog)
        {
            if (!catalog.TryGetComparisonProperties(selected.Truck.Code, out var properties))
            {
                _comparison.DataSource = table;
                var code = selected.Truck.Code;
                _ = Task.Run(() => catalog.ComparisonProperties(code)).ContinueWith(task =>
                {
                    var error = task.Exception?.GetBaseException().Message;
                    if (IsDisposed || !IsHandleCreated) return;
                    try
                    {
                        BeginInvoke((Action)(() =>
                        {
                            if (IsDisposed || request != _comparisonRequest) return;
                            if (error is not null) _status.Text = "Could not compare this truck: " + error;
                            else Compare();
                        }));
                    }
                    catch (InvalidOperationException) when (IsDisposed || !IsHandleCreated) { }
                }, TaskScheduler.Default);
                return;
            }
            selected = selected with { Properties = properties };
            foreach (var item in TruckReplacementComparison.Build(_current, _original, selected))
                if (!_differences.Checked || item.Current != item.Replacement) table.Rows.Add(item.Property, item.Current, item.Replacement);
        }
        _comparison.DataSource = table;
        foreach (DataGridViewRow row in _comparison.Rows)
            if (!Equals(row.Cells[1].Value, row.Cells[2].Value)) row.Cells[2].Style.BackColor = AppColors.Get("EngineMatch");
        _comparison.Columns[0].FillWeight = 60;
    }

    private async Task ApplyReplacementAsync()
    {
        if (_saving || QueuedReplacement is not null || Selected() is not { } selected) return;
        var enabled = _getEnabled();
        if (!enabled.Any(truck => truck.Code.Equals(selected.Truck.Code, StringComparison.OrdinalIgnoreCase)))
        { _status.Text = "This truck is no longer enabled. Close and reopen the replacement window."; return; }
        _saving = true; _apply.Enabled = false; _apply.Text = "Saving…";
        _list.Enabled = false;
        _status.Text = "Saving the pending replacement…";
        try
        {
            var queued = await Task.Run(() => PendingTruckReplacements.Queue(_workspace, _current, selected.Truck));
            if (IsDisposed) return;
            await InvokeAsync(() =>
            {
                QueuedReplacement = queued;
                _status.Text = $"Draft saved: {selected.Truck.Name}. Stage changes in Map trucks or Randomizer, then apply from Start → Apply pending changes.";
            });
        }
        catch (Exception ex) { if (!IsDisposed) await InvokeAsync(() => _status.Text = "Replacement was not saved: " + ex.Message); }
        finally
        {
            if (!IsDisposed)
            {
                await InvokeAsync(() =>
                {
                    _saving = false; _apply.Text = QueuedReplacement is null ? "Apply replacement" : "Draft saved";
                    _list.Enabled = true;
                    _apply.Enabled = QueuedReplacement is null && _current.MapDataHash.Length > 0 && Selected() is { } choice
                        && !choice.Truck.Code.Equals(_current.TruckCode, StringComparison.OrdinalIgnoreCase);
                });
            }
        }
    }

    private static DataGridView Grid()
    {
        var grid = new DataGridView(); ConfigureGrid(grid); return grid;
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = grid.AllowUserToDeleteRows = grid.RowHeadersVisible = false;
        grid.MultiSelect = false; grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.DefaultCellStyle.SelectionBackColor = AppColors.Get("SelectionBlue"); grid.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold); grid.RowTemplate.Height = 26;
    }

    private void PaletteChanged(object? sender, EventArgs e)
    {
        foreach (var grid in new[] { _list, _comparison })
        { grid.BackgroundColor = grid.DefaultCellStyle.BackColor = AppColors.Get("Surface"); grid.AlternatingRowsDefaultCellStyle.BackColor = AppColors.Get("AlternateRow"); }
        Compare();
    }
}
