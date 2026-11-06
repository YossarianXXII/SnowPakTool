using System.Data;

namespace SnowPakTool.Desktop;

internal sealed partial class TrucksCargoTab : UserControl
{
    private readonly Func<string> _getWorkspacePath;
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(4, 6, 4, 4) };
    private readonly TextBox _description = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly DataGridView _cargo = CreateGrid();
    private readonly DataGridView _properties = CreateGrid();
    private CargoCatalog? _catalog;
    private string? _loadedWorkspace;
    private bool _loading;
    private bool _bindingCargo;

    public TrucksCargoTab(Func<string> getWorkspacePath)
    {
        _getWorkspacePath = getWorkspacePath;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        toolbar.Controls.Add(new Label { Text = "Cargo", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 5, 12, 0) });
        var reload = new Button { Text = "Reload cargo", AutoSize = true };
        toolbar.Controls.Add(reload);
        InitializeMassEditing(toolbar);
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_status, 0, 1);
        layout.Controls.Add(_cargo, 0, 2);
        layout.Controls.Add(_description, 0, 3);
        layout.Controls.Add(_properties, 0, 4);
        Controls.Add(layout);
        _properties.Columns.Add("Property", "Selected cargo property");
        _properties.Columns.Add("Value", "Value");
        _properties.Columns.Add("Source", "Source file");
        _properties.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        TableHeaderBehavior.Attach(_cargo);
        TableHeaderBehavior.Attach(_properties);
        _cargo.CurrentCellChanged += (_, _) => ShowProperties();
        _cargo.DataBindingComplete += (_, _) => ShowProperties();
        reload.Click += async (_, _) =>
        {
            if (_loading) return;
            _loading = true;
            reload.Enabled = false;
            try
            {
                await WorkspaceDataCache.PreloadAsync(_getWorkspacePath(), forceReload: true);
                _loadedWorkspace = null;
                LoadCargo();
            }
            catch (Exception ex) { _status.Text = $"Could not reload cargo: {ex.Message}"; }
            finally { _loading = false; reload.Enabled = true; }
        };
        AppColors.Changed += RefreshColors;
        Disposed += (_, _) => AppColors.Changed -= RefreshColors;
        RefreshColors(this, EventArgs.Empty);
    }

    public void EnsureLoaded()
    {
        if (!string.Equals(_loadedWorkspace, _getWorkspacePath().Trim(), StringComparison.OrdinalIgnoreCase)
            || _loadedVersion != WorkspaceDataCache.ContentVersion && !_massRows.Values.Any(row => !CargoMassStore.Equal(row.Draft, row.Current))) LoadCargo();
    }

    private void LoadCargo()
    {
        var workspace = _getWorkspacePath().Trim();
        _bindingCargo = true;
        try
        {
            // Detaching can synchronously format old rows. Keep their lookup intact
            // until they are detached, and suppress callbacks throughout the transition.
            _cargo.DataSource = null;
            _catalog = null;
            _massRows = new(StringComparer.OrdinalIgnoreCase);
            _loadedWorkspace = null;
            _properties.Rows.Clear();
            _description.Clear();
            if (workspace.Length == 0 || !Directory.Exists(workspace))
            {
                _status.Text = "Choose an extracted workspace on the Start tab to list cargo.";
                return;
            }
            var catalog = CargoCatalogService.Load(workspace, WorkspaceDataCache.GetLocalizedNames(workspace));
            var massRows = CargoMassStore.ReadRows(workspace, catalog);
            _catalog = catalog;
            _massRows = massRows;
            _loadedWorkspace = workspace;
            _loadedVersion = WorkspaceDataCache.ContentVersion;
            RestoreStagedMass();
            ApplyFilter();
        }
        catch (Exception ex) { _status.Text = $"Could not load cargo: {ex.Message}"; }
        finally
        {
            _bindingCargo = false;
            _cargo.Invalidate();
            ShowProperties();
        }
    }

    private void ApplyFilter()
    {
        if (_catalog is null) return;
        var visible = _catalog.Entries;
        var table = new DataTable();
        foreach (var column in new[] { "Cargo", "Cargo type", "Variant", "Content" }) table.Columns.Add(column);
        foreach (var column in new[] { "Cargo length (slots)", "Loose weight (kg)", "Packed weight (kg)", "Cargo value" }) table.Columns.Add(column, typeof(decimal));
        table.Columns.Add("Entry", typeof(CargoEntry));
        foreach (var entry in visible)
            table.Rows.Add(entry.Name, entry.Type, entry.Variant, entry.Content,
                (object?)entry.Slots ?? DBNull.Value, (object?)entry.LooseMassKg ?? DBNull.Value,
                (object?)entry.PackedMassKg ?? DBNull.Value, (object?)entry.LoadValue ?? DBNull.Value, entry);
        _cargo.DataSource = table;
        _cargo.ReadOnly = true;
        _cargo.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _cargo.Columns["Entry"]!.Visible = false;
        foreach (DataGridViewColumn column in _cargo.Columns)
        {
            column.Width = column.Index switch { 0 => 200, 1 => 190, 2 => 290, 3 => 90, _ => 125 };
            if (column.ValueType == typeof(decimal)) column.DefaultCellStyle.Format = "N0";
            column.ReadOnly = true;
        }
        _cargo.Columns["Cargo value"]!.DefaultCellStyle.Format = "0.###";
        foreach (var role in new[] { "Loose", "Packed" })
        {
            _cargo.Columns[role + " weight (kg)"]!.DefaultCellStyle.Format = "0.############################";
            _cargo.Columns[role + " weight (kg)"]!.HeaderText = role + " (kg)";
            _cargo.Columns[role + " weight (kg)"]!.Width = 155;
            _cargo.Columns[role + " weight (kg)"]!.ToolTipText = "Double-click, or press Enter/F2, to edit individual body masses in a popup.";
        }
        var massOrder = new[] { "Cargo", "Loose weight (kg)", "Packed weight (kg)" };
        for (var index = 0; index < massOrder.Length; index++) _cargo.Columns[massOrder[index]]!.DisplayIndex = index;
        _status.Text = $"{visible.Count} variants · {_catalog.Entries.Select(entry => entry.Type).Distinct(StringComparer.OrdinalIgnoreCase).Count()} cargo types. "
            + "Blank = missing, unresolved or differing loose variants. Log cargo length uses carrier slots.";
        if (_catalog.Warnings.Count > 0) _status.Text += $" {_catalog.Warnings.Count} file(s) could not be read (see details).";
        UpdateMassCells();
    }

    private void ShowProperties()
    {
        if (_bindingCargo) return;
        _properties.Rows.Clear();
        _description.Clear();
        if (_cargo.CurrentRow?.DataBoundItem is not DataRowView row || row["Entry"] is not CargoEntry entry) return;
        _description.Text = entry.Description + Environment.NewLine
            + "Loose weight is per loose cargo unit (one individual log for logs). Packed weight sums the cargo addon's physics bodies for the complete load; it can include carrier parts."
            + Environment.NewLine + "Double-click Loose or Packed (or press Enter/F2) to edit individual body masses in a popup. OK updates the preview; Stage changes, then Apply pending changes from Start. Blank raw mass restores inheritance."
            + (_catalog?.Warnings.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, _catalog.Warnings) : "");
        foreach (var property in entry.Properties) _properties.Rows.Add(property.Property, property.Value, property.Source);
        foreach (var target in entry.MassTargets)
            if (_massRows.TryGetValue(CargoMassStore.Key(target), out var mass))
                _properties.Rows.Add(target.Role + " raw " + target.BodyPath + "/@Mass (preview)", mass.Draft ?? "Inherited / not present", target.RelativeFile);
        TableHeaderBehavior.Refresh(_properties);
    }

    private void RefreshColors(object? sender, EventArgs e)
    {
        foreach (var grid in new[] { _cargo, _properties })
        {
            grid.BackgroundColor = grid.DefaultCellStyle.BackColor = AppColors.Get("Surface");
            grid.AlternatingRowsDefaultCellStyle.BackColor = AppColors.Get("AlternateRow");
            grid.DefaultCellStyle.SelectionBackColor = AppColors.Get("SelectionBlue");
            grid.DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        }
    }

    private static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f),
        ColumnHeadersDefaultCellStyle = { Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f, FontStyle.Bold) },
        RowTemplate = { Height = 26 }
    };
}
