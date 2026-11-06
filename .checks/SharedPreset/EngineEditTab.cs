using System.Diagnostics;
using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Windows.Forms.VisualStyles;

namespace SnowPakTool.Desktop;

internal sealed class EngineEditTab : UserControl
{
    private static readonly Regex EngineSocket = new(@"<EngineSocket\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EngineDefinitionTag = new(@"<Engine\b(?<attrs>[^>]*?)(?:/\s*>|>(?<body>[\s\S]*?)</Engine\s*>)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex XmlAttribute = new(@"(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)\s*=\s*""(?<value>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex XmlElementTag = new(@"<(?<tag>[A-Za-z_][A-Za-z0-9_.:-]*)\b(?<attrs>[^>]*)>", RegexOptions.Compiled);
    private static readonly Regex DlcSection = new(@"^   (?<section>[A-Za-z0-9_]+)\s*=\s*\{", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex DlcNameKey = new(@"\bname\s*=\s*""(?<key>UI_DLC_NAME_[^""]+)""", RegexOptions.Compiled);
    private static readonly string[] EngineProperties =
    [
        "Torque", "FuelConsumption", "EngineResponsiveness", "CriticalDamageThreshold", "DamageCapacity",
        "DamagedConsumptionModifier", "DamagedMinTorqueMultiplier", "DamagedMaxTorqueMultiplier", "MaxDeltaAngVel",
        "Price", "UnlockByRank", "UnlockByExploration"
    ];
    private static readonly string[] InitiallyHiddenEngineProperties =
    [
        "EngineResponsiveness", "CriticalDamageThreshold", "DamageCapacity", "DamagedConsumptionModifier",
        "DamagedMinTorqueMultiplier", "DamagedMaxTorqueMultiplier", "MaxDeltaAngVel"
    ];

    private readonly Func<string> _getWorkspacePath;
    private readonly OwnedContentTab _ownedContent;
    private sealed class BufferedDataGridView : DataGridView
    {
        public BufferedDataGridView()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    private readonly BufferedDataGridView _compatibilityGrid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeColumns = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        ScrollBars = ScrollBars.Both,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
        EditMode = DataGridViewEditMode.EditOnEnter,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f),
        ColumnHeadersDefaultCellStyle = { Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10.5f, FontStyle.Bold) },
        ColumnHeadersHeight = 112,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        DefaultCellStyle = { BackColor = AppColors.Get("Surface"), SelectionBackColor = AppColors.Get("Surface"), SelectionForeColor = SystemColors.ControlText },
        RowTemplate = { Height = 26 }
    };
    private readonly DataGridView _originalPropertiesGrid = CreatePropertiesGrid(readOnly: true);
    private readonly DataGridView _updatedPropertiesGrid = CreatePropertiesGrid(readOnly: false);
    private readonly Button _saveCompatibilityButton = new() { Text = "Save compatibility changes", AutoSize = true, Enabled = false, Margin = new Padding(4) };
    private readonly CheckBox _selectAllTrucks = new() { Text = "Select all visible", AutoSize = true, ThreeState = true, CheckState = CheckState.Checked, Margin = new Padding(4, 7, 10, 4) };
    private readonly CheckBox _allowCompatibilityEditing = new() { Text = "Allow editing", AutoSize = true, Checked = false, Margin = new Padding(8, 7, 4, 4) };
    private readonly TableHeaderBehavior _compatibilityHeaders;
    private readonly CheckedFilterDropdown _propertyColumnsFilter = new("Show columns", 300);
    private readonly Button _confirmPropertiesButton = new() { Text = "Confirm and save", AutoSize = true, Enabled = false, Margin = new Padding(4) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4, 6, 4, 4) };
    private List<EngineClass> _engineClasses = new();
    private List<EngineClass> _matrixEngineClasses = new();
    private readonly Dictionary<string, EngineDefinition> _updatedDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private string? _loadedWorkspace;
    private bool _updatingGrid;
    private bool _syncingSelection;
    private bool _syncingPropertySelection;
    private int _sortedCompatibilityColumnIndex = -1;
    private ListSortDirection _sortedCompatibilityDirection;
    private Dictionary<string, string> _dlcNames = new(StringComparer.OrdinalIgnoreCase);

    public EngineEditTab(Func<string> getWorkspacePath, OwnedContentTab ownedContent)
    {
        _getWorkspacePath = getWorkspacePath;
        _ownedContent = ownedContent;
        BuildLayout();
        AppColors.Changed += (_, _) => RefreshPaletteColors();
        _ownedContent.OwnedTrucksChanged += RefreshOwnedTrucks;
        _compatibilityGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_compatibilityGrid.IsCurrentCellDirty) _compatibilityGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _compatibilityGrid.CellValueChanged += CompatibilityCellValueChanged;
        _compatibilityGrid.CellMouseClick += CompatibilityCellMouseClick;
        _compatibilityGrid.CellPainting += CompatibilityCellPainting;
        _compatibilityGrid.SelectionChanged += (_, _) => RefreshPropertyTablesForTruckSelection();
        _compatibilityHeaders = TableHeaderBehavior.Attach(_compatibilityGrid,
            () => SortCompatibility(null, ListSortDirection.Ascending),
            (column, direction) => SortCompatibility(column, direction), column => column.Index >= 4);
        _compatibilityHeaders.AddFilter("TruckClass");
        _compatibilityHeaders.AddFilter("Dlc");
        _compatibilityHeaders.Changed += () => { SyncSelectAllCheckbox(); RefreshPropertyTablesForTruckSelection(); };
        _compatibilityGrid.RowPostPaint += DrawTenthRowSeparator;
        _propertyColumnsFilter.SelectionChanged += (_, _) => ApplyPropertyColumnVisibility();
        _originalPropertiesGrid.SelectionChanged += (_, _) => PropertyGridSelectionChanged(_originalPropertiesGrid, _updatedPropertiesGrid);
        _updatedPropertiesGrid.SelectionChanged += (_, _) => PropertyGridSelectionChanged(_updatedPropertiesGrid, _originalPropertiesGrid);
        _originalPropertiesGrid.CellPainting += PropertiesCellPainting;
        _updatedPropertiesGrid.CellPainting += PropertiesCellPainting;
        _originalPropertiesGrid.RowPostPaint += DrawTenthRowSeparator;
        _updatedPropertiesGrid.RowPostPaint += DrawTenthRowSeparator;
        _originalPropertiesGrid.CellMouseClick += PropertiesCellMouseClick;
        _updatedPropertiesGrid.CellMouseClick += PropertiesCellMouseClick;
        _selectAllTrucks.CheckedChanged += (_, _) =>
        {
            if (_syncingSelection || _selectAllTrucks.CheckState == CheckState.Indeterminate) return;
            SetVisibleTruckSelection(_selectAllTrucks.Checked);
        };
        _saveCompatibilityButton.Click += SaveCompatibility;
        _allowCompatibilityEditing.CheckedChanged += (_, _) => UpdateCompatibilityEditingMode();
        _updatedPropertiesGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_updatedPropertiesGrid.IsCurrentCellDirty) _updatedPropertiesGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _updatedPropertiesGrid.CellValueChanged += UpdatedPropertyValueChanged;
        _updatedPropertiesGrid.CellFormatting += UpdatedPropertyCellFormatting;
        _confirmPropertiesButton.Click += ConfirmProperties;
    }

    private void RefreshPaletteColors()
    {
        var surface = AppColors.Get("Surface");
        var selected = AppColors.Get("SelectionBlue");
        _compatibilityGrid.DefaultCellStyle.BackColor = surface;
        _compatibilityGrid.DefaultCellStyle.SelectionBackColor = surface;
        foreach (var grid in new[] { _originalPropertiesGrid, _updatedPropertiesGrid })
        {
            grid.DefaultCellStyle.BackColor = surface;
            grid.DefaultCellStyle.SelectionBackColor = selected;
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Tag is string) row.DefaultCellStyle.BackColor = AppColors.Get("GroupRow");
        }
        var source = _updatedPropertiesGrid.CurrentRow?.Tag is EngineTableRow ? _updatedPropertiesGrid : _originalPropertiesGrid;
        HighlightTrucksForSelectedEngine(source);
        _compatibilityGrid.Invalidate();
        _originalPropertiesGrid.Invalidate();
        _updatedPropertiesGrid.Invalidate();
    }

    private void BuildLayout()
    {
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Controls.Add(new Label
        {
            Text = "Edit truck engine compatibility and engine properties. Confirm changes to save.",
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 8)
        }, 0, 0);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        split.SizeChanged += (_, _) =>
        {
            const int minLeftWidth = 220;
            const int minRightWidth = 360;
            var maxDistance = split.Width - split.SplitterWidth - minRightWidth;
            if (maxDistance >= minLeftWidth)
                split.SplitterDistance = Math.Clamp((int)(split.Width * 0.60), minLeftWidth, maxDistance);
        };

        var compatibilitySection = new GroupBox { Text = "Truck engine compatibility", Dock = DockStyle.Fill, Padding = new Padding(6) };
        var compatibilityLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        compatibilityLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        compatibilityLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        compatibilityLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        filters.Controls.Add(_selectAllTrucks);
        filters.Controls.Add(_allowCompatibilityEditing);
        compatibilityLayout.Controls.Add(filters, 0, 0);
        compatibilityLayout.Controls.Add(_compatibilityGrid, 0, 1);
        var savePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        savePanel.Controls.Add(_saveCompatibilityButton);
        compatibilityLayout.Controls.Add(savePanel, 0, 2);
        compatibilitySection.Controls.Add(compatibilityLayout);
        split.Panel1.Controls.Add(compatibilitySection);

        var propertyLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(2) };
        propertyLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        propertyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        propertyLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var propertyColumnPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        propertyColumnPanel.Controls.Add(_propertyColumnsFilter);
        propertyLayout.Controls.Add(propertyColumnPanel, 0, 0);
        var originalSection = new GroupBox { Text = "Original engine properties", Dock = DockStyle.Fill, Padding = new Padding(6) };
        originalSection.Controls.Add(_originalPropertiesGrid);
        var updatedSection = new GroupBox { Text = "Updated engine properties", Dock = DockStyle.Fill, Padding = new Padding(6) };
        var updatedLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        updatedLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        updatedLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actionPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        actionPanel.Controls.Add(_confirmPropertiesButton);
        actionPanel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            Margin = new Padding(8, 8, 4, 4),
            Text = "Edit engine parameters below. Unlock by exploration is a checkbox; the other values are saved as XML parameters."
        });
        updatedLayout.Controls.Add(actionPanel, 0, 0);
        updatedLayout.Controls.Add(_updatedPropertiesGrid, 0, 1);
        updatedSection.Controls.Add(updatedLayout);
        propertyLayout.Controls.Add(originalSection, 0, 1);
        propertyLayout.Controls.Add(updatedSection, 0, 2);
        split.Panel2.Controls.Add(propertyLayout);

        content.Controls.Add(split, 0, 1);
        content.Controls.Add(_status, 0, 2);
        Controls.Add(content);
    }

    private static DataGridView CreatePropertiesGrid(bool readOnly) => new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeColumns = false,
        AllowUserToResizeRows = false,
        ReadOnly = readOnly,
        RowHeadersVisible = false,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        ScrollBars = ScrollBars.Both,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f),
        ColumnHeadersDefaultCellStyle = { Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f, FontStyle.Bold) },
        DefaultCellStyle = { BackColor = AppColors.Get("Surface"), SelectionBackColor = AppColors.Get("SelectionBlue"), SelectionForeColor = SystemColors.ControlText },
        RowTemplate = { Height = 23 }
    };

    public void EnsureLoaded()
    {
        var workspace = _getWorkspacePath().Trim();
        if (string.Equals(_loadedWorkspace, workspace, StringComparison.OrdinalIgnoreCase))
        {
            RefreshOwnedTrucks();
            return;
        }
        _loadedWorkspace = workspace;
        _compatibilityGrid.Rows.Clear();
        _compatibilityGrid.Columns.Clear();
        _originalPropertiesGrid.Rows.Clear();
        _updatedPropertiesGrid.Rows.Clear();
        _engineClasses.Clear();
        _matrixEngineClasses.Clear();
        _updatedDefinitions.Clear();
        _saveCompatibilityButton.Enabled = false;
        _confirmPropertiesButton.Enabled = false;

        if (!WorkspaceOriginals.EnumerateCurrentFiles(workspace, "[media]", "*.xml").Any())
        {
            _status.Text = "Unpack a workspace to load truck and engine definitions.";
            return;
        }
        try
        {
            var prepared = (PreparedWorkspaceData)WorkspaceDataCache.GetEngineData(workspace);
            _dlcNames = prepared.DlcNames;
            _engineClasses = prepared.EngineClasses;
            _matrixEngineClasses = prepared.MatrixEngineClasses;
            foreach (var engineClass in _engineClasses)
                foreach (var definition in engineClass.Definitions)
                    _updatedDefinitions[DefinitionKey(engineClass.Code, definition.Name)] = definition;
            ConfigureCompatibilityColumns();
            ConfigurePropertyColumns(_originalPropertiesGrid, readOnly: true);
            ConfigurePropertyColumns(_updatedPropertiesGrid, readOnly: false);
            var hiddenColumnNames = InitiallyHiddenEngineProperties
                .Select(name => _updatedPropertiesGrid.Columns[name].HeaderText);
            _propertyColumnsFilter.SetOptions(hiddenColumnNames, Array.Empty<string>());
            ApplyPropertyColumnVisibility();
            PopulatePropertyTables();
            RefreshOwnedTrucks();
            _status.Text = $"Loaded {_engineClasses.Count} engine classes.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _status.Text = $"Could not load engine data: {ex.Message}";
        }
    }

    public void WorkspaceUpdated()
    {
        _loadedWorkspace = null;
        if (Visible)
            EnsureLoaded();
    }

    internal static object PrepareWorkspaceData(string workspace, IReadOnlyDictionary<string, string> uiNames)
    {
        var dlcNames = LoadDlcNames(workspace, uiNames);
        var engineClasses = LoadEngineClasses(workspace, uiNames);
        var matrixClasses = OrderEngineColumnsBaseThenDlc(workspace, engineClasses, dlcNames);
        return new PreparedWorkspaceData(dlcNames, engineClasses, matrixClasses);
    }

    private sealed record PreparedWorkspaceData(
        Dictionary<string, string> DlcNames,
        List<EngineClass> EngineClasses,
        List<EngineClass> MatrixEngineClasses);
    private static List<EngineClass> LoadEngineClasses(string workspace, IReadOnlyDictionary<string, string> uiNames)
    {
        var classes = new Dictionary<string, EngineClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in WorkspaceOriginals.EnumerateCurrentFiles(workspace, Path.Combine("[media]", "classes", "engines"), "*.xml")
                     .Concat(WorkspaceOriginals.EnumerateCurrentFiles(workspace, Path.Combine("[media]", "_dlc"), "*.xml"))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(Directory.GetParent(file)?.Name, "engines", StringComparison.OrdinalIgnoreCase)) continue;
            var xml = WorkspaceDataCache.ReadAllText(file, Encoding.UTF8);
            var definitions = ReadEngineDefinitions(xml, uiNames);
            if (definitions.Count == 0) continue;
            var code = Path.GetFileNameWithoutExtension(file);
            var label = code.StartsWith("e_", StringComparison.OrdinalIgnoreCase) ? code[2..] : code;
            label = string.Join(" ", label.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
            var relativePath = WorkspaceOriginals.GetRelativePath(workspace, file);
            var originalPath = WorkspaceOriginals.GetOriginalReadPath(workspace, relativePath);
            originalPath = WorkspaceOriginals.GetOriginalReadPath(workspace, relativePath);
            var originalDefinitions = ReadEngineDefinitions(File.Exists(originalPath) ? WorkspaceDataCache.ReadAllText(originalPath, Encoding.UTF8) : xml, uiNames);
            classes.TryAdd(code, new EngineClass(code, label, file, definitions[0].Name, definitions, originalDefinitions));
        }
        return classes.Values.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static List<EngineDefinition> ReadEngineDefinitions(string xml, IReadOnlyDictionary<string, string> uiNames)
    {
        var definitions = new List<EngineDefinition>();
        foreach (Match match in EngineDefinitionTag.Matches(xml))
        {
            var attrs = match.Groups["attrs"].Value;
            var body = match.Groups["body"].Value;
            var name = GetAttribute(attrs, "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var properties = EngineProperties.ToDictionary(property => property, _ => "—", StringComparer.OrdinalIgnoreCase);
            foreach (Match attribute in XmlAttribute.Matches(attrs))
                if (properties.ContainsKey(attribute.Groups["name"].Value)) properties[attribute.Groups["name"].Value] = DisplayValue(attribute.Groups["value"].Value);

            var uiNameKey = string.Empty;
            foreach (Match element in XmlElementTag.Matches(body))
            {
                var tagName = element.Groups["tag"].Value;
                var elementAttrs = element.Groups["attrs"].Value;
                if (string.Equals(tagName, "GameData", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (Match attribute in XmlAttribute.Matches(elementAttrs))
                        if (properties.ContainsKey(attribute.Groups["name"].Value)) properties[attribute.Groups["name"].Value] = DisplayValue(attribute.Groups["value"].Value);
                }
                else if (string.Equals(tagName, "UiDesc", StringComparison.OrdinalIgnoreCase))
                    uiNameKey = GetAttribute(elementAttrs, "UiName");
            }
            var uiName = uiNames.TryGetValue(uiNameKey, out var localizedName) ? localizedName : name;
            definitions.Add(new EngineDefinition(name, uiName, uiNameKey, properties));
        }
        return definitions;
    }

    private void ConfigureCompatibilityColumns()
    {
        _compatibilityGrid.Columns.Clear();
        _compatibilityGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "SelectTruck", HeaderText = "", Width = 32, MinimumWidth = 32, Frozen = true, ThreeState = false, SortMode = DataGridViewColumnSortMode.NotSortable });
        _compatibilityGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Truck", HeaderText = "Truck", ReadOnly = true, Frozen = true, Width = 190, MinimumWidth = 150, SortMode = DataGridViewColumnSortMode.NotSortable });
        _compatibilityGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TruckClass", HeaderText = "Truck class", ReadOnly = true, Width = 140, MinimumWidth = 110, SortMode = DataGridViewColumnSortMode.Programmatic });
        _compatibilityGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Dlc", HeaderText = "DLC", ReadOnly = true, Width = 90, MinimumWidth = 75, SortMode = DataGridViewColumnSortMode.Programmatic });
        foreach (var engineClass in _matrixEngineClasses)
        {
            var shortenedHeader = Regex.Replace(
                engineClass.DisplayName,
                @"\s*(?:Us Truck Old|Ru Truck Old|Ru Truck Modern|Us Truck Modern|Us Scout Old|Ru Scout Old|Us Truck Military)\s*",
                " ",
                RegexOptions.IgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(shortenedHeader)) shortenedHeader = engineClass.DisplayName;
            var column = new DataGridViewCheckBoxColumn
            {
                Name = engineClass.Code,
                HeaderText = shortenedHeader,
                Width = 46,
                MinimumWidth = 42,
                SortMode = DataGridViewColumnSortMode.Programmatic,
                ThreeState = false,
                ReadOnly = true
            };
            column.HeaderCell.ToolTipText = $"{engineClass.DisplayName}\n{engineClass.Code}";
            _compatibilityGrid.Columns.Add(column);
        }
        UpdateCompatibilityEditingMode();
    }

    private void UpdateCompatibilityEditingMode()
    {
        var editing = _allowCompatibilityEditing.Checked;
        foreach (DataGridViewColumn column in _compatibilityGrid.Columns.Cast<DataGridViewColumn>().Skip(4))
            column.ReadOnly = !editing;
        foreach (DataGridViewRow row in _compatibilityGrid.Rows)
        {
            var hasSocket = row.Tag is TruckRow truck && truck.HasEngineSocket;
            foreach (DataGridViewColumn column in _compatibilityGrid.Columns.Cast<DataGridViewColumn>().Skip(4))
                row.Cells[column.Index].ReadOnly = !editing || !hasSocket;
        }
        _compatibilityGrid.Invalidate();
    }

    private static void DrawTenthRowSeparator(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        if ((e.RowIndex + 1) % 10 != 0) return;
        var grid = (DataGridView)sender!;
        using var pen = new Pen(AppColors.Get("CompatibilitySeparator"), 2f);
        var y = e.RowBounds.Bottom - 1;
        e.Graphics.DrawLine(pen, e.RowBounds.Left, y, Math.Min(e.RowBounds.Right, grid.ClientSize.Width), y);
    }

    private static void ConfigurePropertyColumns(DataGridView grid, bool readOnly)
    {
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "EngineType", HeaderText = "Engine class", Width = 145, MinimumWidth = 120, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Engine", HeaderText = "Engine", Width = 180, MinimumWidth = 145, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var property in EngineProperties)
        {
            var header = property switch
            {
                "FuelConsumption" => "Fuel consumption",
                "EngineResponsiveness" => "Responsiveness",
                "CriticalDamageThreshold" => "Critical damage threshold",
                "DamageCapacity" => "Damage capacity",
                "DamagedConsumptionModifier" => "Damaged consumption",
                "DamagedMinTorqueMultiplier" => "Damaged min torque multiplier",
                "DamagedMaxTorqueMultiplier" => "Damaged max torque multiplier",
                "MaxDeltaAngVel" => "Max delta angular velocity",
                "UnlockByRank" => "Rank",
                "UnlockByExploration" => "Unlock by exploration",
                _ => property
            };
            if (property == "UnlockByExploration")
                grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = property, HeaderText = header, Width = 145, MinimumWidth = 125, ReadOnly = readOnly, ThreeState = false, TrueValue = true, FalseValue = false, ValueType = typeof(bool), SortMode = DataGridViewColumnSortMode.NotSortable, Visible = true });
            else
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = property, HeaderText = header, Width = 105, MinimumWidth = 85, ReadOnly = readOnly, SortMode = DataGridViewColumnSortMode.NotSortable,
                    Visible = property is not ("CriticalDamageThreshold" or "DamageCapacity" or "DamagedMinTorqueMultiplier" or "DamagedMaxTorqueMultiplier" or "MaxDeltaAngVel") });
        }
        foreach (DataGridViewColumn column in grid.Columns)
        {
            if (column.Name == "Engine") continue;
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.ColumnHeader;
            column.MinimumWidth = Math.Min(column.MinimumWidth, 42);
        }
        grid.ReadOnly = readOnly;
    }

    private void PopulatePropertyTables()
    {
        PopulatePropertyTables(null);
    }

    private void PopulatePropertyTables(HashSet<string>? classCodes)
    {
        _originalPropertiesGrid.Rows.Clear();
        _updatedPropertiesGrid.Rows.Clear();
        foreach (var engineClass in _engineClasses.Where(item => classCodes is null || classCodes.Contains(item.Code)))
        {
            AddGroupRow(_originalPropertiesGrid, engineClass);
            AddGroupRow(_updatedPropertiesGrid, engineClass);
            foreach (var definition in engineClass.OriginalDefinitions)
                AddDefinitionRow(_originalPropertiesGrid, engineClass, definition);
            foreach (var definition in engineClass.Definitions)
                AddDefinitionRow(_updatedPropertiesGrid, engineClass, _updatedDefinitions[DefinitionKey(engineClass.Code, definition.Name)]);
        }
        UpdateConfirmButton();
    }

    private void PopulateTruckFilters() => _compatibilityHeaders.Refresh();

    private void ApplyTruckFilters()
    {
        if (_updatingGrid) return;
        _compatibilityHeaders.Refresh();
        SyncSelectAllCheckbox();
        RefreshPropertyTablesForTruckSelection();
    }
    private void RefreshPropertyTablesForTruckSelection()
    {
        if (_updatingGrid || _compatibilityGrid.Columns.Count == 0 || _engineClasses.Count == 0) return;
        var selectedRows = _compatibilityGrid.Rows.Cast<DataGridViewRow>().Where(row => row.Visible && row.Tag is TruckRow truck && truck.IsSelected).ToArray();
        var classes = selectedRows.SelectMany(row => ((TruckRow)row.Tag!).Types).ToHashSet(StringComparer.OrdinalIgnoreCase);
        PopulatePropertyTables(classes);
        _status.Text = $"Showing engines for {selectedRows.Length} selected visible trucks across {classes.Count} engine classes.";
    }

    private void PropertyGridSelectionChanged(DataGridView source, DataGridView target)
    {
        if (!_syncingPropertySelection)
        {
            _syncingPropertySelection = true;
            try
            {
                var selected = source.CurrentRow?.Tag as EngineTableRow;
                var match = selected is null ? null : target.Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(row => row.Tag is EngineTableRow item &&
                        string.Equals(item.ClassCode, selected.ClassCode, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.Name, selected.Name, StringComparison.OrdinalIgnoreCase));
                target.ClearSelection();
                if (match is not null) target.CurrentCell = match.Cells["Engine"];
            }
            finally { _syncingPropertySelection = false; }
        }
        HighlightTrucksForSelectedEngine(source);
    }

    private void ApplyPropertyColumnVisibility()
    {
        var visibleHeaders = _propertyColumnsFilter.SelectedValues.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        foreach (var name in InitiallyHiddenEngineProperties)
        {
            var header = _updatedPropertiesGrid.Columns[name].HeaderText;
            var visible = visibleHeaders.Contains(header);
            _originalPropertiesGrid.Columns[name].Visible = visible;
            _updatedPropertiesGrid.Columns[name].Visible = visible;
        }
    }

    private void PropertiesCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        var grid = (DataGridView)sender!;
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        var row = grid.Rows[e.RowIndex];
        var columnName = grid.Columns[e.ColumnIndex].Name;
        if (row.Tag is string)
        {
            e.PaintBackground(e.CellBounds, true);
            if (columnName == "Engine")
            {
                var firstCell = grid.GetCellDisplayRectangle(grid.Columns["EngineType"].Index, e.RowIndex, false);
                var labelBounds = Rectangle.FromLTRB(firstCell.Left + 4, e.CellBounds.Top, e.CellBounds.Right - 4, e.CellBounds.Bottom);
                var label = Convert.ToString(row.Cells["EngineType"].Value, CultureInfo.CurrentCulture) ?? string.Empty;
                TextRenderer.DrawText(e.Graphics, label, e.CellStyle.Font, labelBounds, e.CellStyle.ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
            using (var border = new Pen(grid.GridColor))
            {
                e.Graphics.DrawLine(border, e.CellBounds.Left, e.CellBounds.Top, e.CellBounds.Right - 1, e.CellBounds.Top);
                e.Graphics.DrawLine(border, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right - 1, e.CellBounds.Bottom - 1);
            }
            e.Handled = true;
            return;
        }
        if (columnName != "Engine" || row.Tag is not EngineTableRow)
            return;
        e.PaintBackground(e.CellBounds, true);
        e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
        var buttonBounds = new Rectangle(e.CellBounds.Right - 28, e.CellBounds.Top + 2, 25, e.CellBounds.Height - 4);
        var textBounds = new Rectangle(e.CellBounds.Left + 4, e.CellBounds.Top, buttonBounds.Left - e.CellBounds.Left - 7, e.CellBounds.Height);
        TextRenderer.DrawText(e.Graphics, e.FormattedValue?.ToString() ?? string.Empty, e.CellStyle.Font, textBounds, e.CellStyle.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        ButtonRenderer.DrawButton(e.Graphics, buttonBounds, "…", e.CellStyle.Font, false, PushButtonState.Normal);
        e.Handled = true;
    }

    private void PropertiesCellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        var grid = (DataGridView)sender!;
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "Engine" || grid.Rows[e.RowIndex].Tag is not EngineTableRow row) return;
        var bounds = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
        if (e.X >= bounds.Width - 30) OpenRawFile(row.FilePath);
    }

    private void SetVisibleTruckSelection(bool selected)
    {
        _syncingSelection = true;
        foreach (DataGridViewRow row in _compatibilityGrid.Rows)
        {
            if (!row.Visible || row.Tag is not TruckRow truck) continue;
            truck.IsSelected = selected;
            row.Cells["SelectTruck"].Value = selected;
        }
        _syncingSelection = false;
        SyncSelectAllCheckbox();
        RefreshPropertyTablesForTruckSelection();
    }

    private void SyncSelectAllCheckbox()
    {
        var visible = _compatibilityGrid.Rows.Cast<DataGridViewRow>().Where(row => row.Visible && row.Tag is TruckRow).ToArray();
        var selected = visible.Count(row => ((TruckRow)row.Tag!).IsSelected);
        _syncingSelection = true;
        _selectAllTrucks.CheckState = visible.Length == 0 || selected == 0 ? CheckState.Unchecked
            : selected == visible.Length ? CheckState.Checked : CheckState.Indeterminate;
        _syncingSelection = false;
    }

    private void HighlightTrucksForSelectedEngine(DataGridView sourceGrid)
    {
        if (_compatibilityGrid.Rows.Count == 0) return;
        var classCode = sourceGrid.CurrentRow?.Tag is EngineTableRow engine ? engine.ClassCode : null;
        foreach (DataGridViewRow row in _compatibilityGrid.Rows)
        {
            if (row.Tag is not TruckRow truck) continue;
            var highlight = classCode is not null && truck.Types.Contains(classCode);
            var color = highlight ? AppColors.Get("EngineMatch") : AppColors.Get("Surface");
            row.DefaultCellStyle.Font = new Font(_compatibilityGrid.Font, highlight ? FontStyle.Bold : FontStyle.Regular);
            for (var column = 0; column < 4; column++)
            {
                row.Cells[column].Style.BackColor = color;
                row.Cells[column].Style.SelectionBackColor = color;
            }
        }
    }

    private void SortCompatibility(DataGridViewColumn? column, ListSortDirection direction)
    {
        _sortedCompatibilityColumnIndex = column?.Index ?? -1;
        _sortedCompatibilityDirection = direction;
        var wasUpdating = _updatingGrid;
        _updatingGrid = true;
        _compatibilityGrid.SuspendLayout();
        try
        {
            if (column is null) _compatibilityGrid.Sort(new TruckOriginalOrderComparer());
            else if (column.Name == "Dlc") _compatibilityGrid.Sort(new TruckDlcComparer(direction));
            else _compatibilityGrid.Sort(column, direction);
        }
        finally { _compatibilityGrid.ResumeLayout(false); _updatingGrid = wasUpdating; }
        _compatibilityGrid.Invalidate();
    }
    private static string GetTruckDlc(string path, string workspace, IReadOnlyDictionary<string, string> dlcNames)
    {
        var relative = WorkspaceOriginals.GetRelativePath(workspace, path);
        var mediaPrefix = Path.Combine("[media]") + Path.DirectorySeparatorChar;
        if (relative.StartsWith(mediaPrefix, StringComparison.OrdinalIgnoreCase)) relative = relative[mediaPrefix.Length..];
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dlcIndex = Array.FindIndex(parts, part => string.Equals(part, "_dlc", StringComparison.OrdinalIgnoreCase));
        if (dlcIndex < 0 || dlcIndex + 1 >= parts.Length) return "Base game";
        var dlcFolder = parts[dlcIndex + 1];
        var season = Regex.Match(dlcFolder, @"season[_ -]?(?<number>\d+)", RegexOptions.IgnoreCase);
        if (season.Success) return int.Parse(season.Groups["number"].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            return dlcNames.TryGetValue(dlcFolder, out var displayName) ? displayName : Regex.Replace(dlcFolder, "[_-]+", " ").ToUpperInvariant();
    }

    private static string FormatTruckClass(string truckClass)
    {
        if (string.IsNullOrWhiteSpace(truckClass) || truckClass == "—") return "—";
        var words = truckClass.Replace('-', '_').Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
    }

    private static Dictionary<string, string> LoadDlcNames(string workspace, IReadOnlyDictionary<string, string> localizedNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var library = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[ps]", "dlc_library.sso"));
        if (!File.Exists(library)) return result;
        var source = WorkspaceDataCache.ReadAllText(library, Encoding.UTF8);
        var sections = DlcSection.Matches(source).Cast<Match>().ToArray();
        var truckNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < sections.Length; index++)
        {
            var start = sections[index].Index;
            var end = index + 1 < sections.Length ? sections[index + 1].Index : source.Length;
            var body = source.Substring(start, end - start);
            var key = DlcNameKey.Match(body);
            if (!key.Success || !localizedNames.TryGetValue(key.Groups["key"].Value, out var name)) continue;
            foreach (var property in new[] { "trucksToAdd", "dlcTruck" })
            {
                var propertyMatch = Regex.Match(body, $@"\b{property}\s*=\s*\{{", RegexOptions.IgnoreCase);
                if (!propertyMatch.Success) continue;
                var opening = propertyMatch.Index + propertyMatch.Length - 1;
                var depth = 0;
                var blockEnd = opening;
                for (; blockEnd < body.Length; blockEnd++)
                {
                    if (body[blockEnd] == '{') depth++;
                    else if (body[blockEnd] == '}' && --depth == 0) break;
                }
                var block = body[opening..Math.Min(body.Length, blockEnd + 1)];
                foreach (Match truck in Regex.Matches(block, @"^\s*(?<truck>[A-Za-z0-9_]+)\s*=\s*\{", RegexOptions.Multiline))
                    truckNames[truck.Groups["truck"].Value] = name;
            }
        }
        foreach (var truckFile in WorkspaceOriginals.EnumerateCurrentFiles(workspace, Path.Combine("[media]", "_dlc"), "*.xml")
                     .Where(file => string.Equals(Directory.GetParent(file)?.Name, "trucks", StringComparison.OrdinalIgnoreCase)))
            if (truckNames.TryGetValue(Path.GetFileNameWithoutExtension(truckFile), out var dlcName))
            {
                var parts = WorkspaceOriginals.GetRelativePath(workspace, truckFile).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var dlcIndex = Array.FindIndex(parts, part => string.Equals(part, "_dlc", StringComparison.OrdinalIgnoreCase));
                if (dlcIndex >= 0 && dlcIndex + 1 < parts.Length) result.TryAdd(parts[dlcIndex + 1], dlcName);
            }
        return result;
    }

    private static List<EngineClass> OrderEngineColumnsBaseThenDlc(string workspace, List<EngineClass> classes, IReadOnlyDictionary<string, string> dlcNames)
    {
        string DlcName(EngineClass engineClass)
        {
            var relative = WorkspaceOriginals.GetRelativePath(workspace, engineClass.FilePath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dlcIndex = Array.FindIndex(relative, part => string.Equals(part, "_dlc", StringComparison.OrdinalIgnoreCase));
            if (dlcIndex < 0 || dlcIndex + 1 >= relative.Length) return string.Empty;
            var code = relative[dlcIndex + 1];
            return dlcNames.TryGetValue(code, out var name) ? name : code;
        }
        return classes.OrderBy(item => DlcName(item).Length == 0 ? 0 : 1)
            .ThenBy(DlcName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void AddGroupRow(DataGridView grid, EngineClass engineClass)
    {
        var values = new object[grid.Columns.Count];
        values[0] = engineClass.DisplayName;
        var rowIndex = grid.Rows.Add(values);
        var row = grid.Rows[rowIndex];
        row.Cells["UnlockByExploration"] = new DataGridViewTextBoxCell { Value = string.Empty };
        row.Tag = engineClass.Code;
        row.DefaultCellStyle.BackColor = AppColors.Get("GroupRow");
        row.DefaultCellStyle.SelectionBackColor = row.DefaultCellStyle.BackColor;
        row.DefaultCellStyle.Font = new Font(grid.Font, FontStyle.Bold);
    }

    private static void AddDefinitionRow(DataGridView grid, EngineClass engineClass, EngineDefinition definition)
    {
        var values = new List<object> { string.Empty, definition.UiName };
        foreach (var property in EngineProperties)
        {
            var value = definition.Properties[property];
            values.Add(property == "UnlockByExploration" ? bool.TryParse(value, out var unlocked) && unlocked : (object)value);
        }
        var rowIndex = grid.Rows.Add(values.ToArray());
        var row = grid.Rows[rowIndex];
        row.Tag = new EngineTableRow(engineClass.Code, engineClass.FilePath, definition.Name);
        row.Cells["Engine"].ToolTipText = $"Open the raw file\nEngine key: {definition.Name}";
    }

    private void RefreshOwnedTrucks()
    {
        if (_compatibilityGrid.Columns.Count == 0 || IsDisposed) return;
        var workspace = _getWorkspacePath().Trim();
        var files = FindTruckFiles(workspace);
        _updatingGrid = true;
        _saveCompatibilityButton.Enabled = false;
        _compatibilityGrid.Rows.Clear();
        foreach (var truck in _ownedContent.GetOwnedTrucks().OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!files.TryGetValue(truck.Code, out var path)) continue;
            var xml = WorkspaceDataCache.ReadAllText(path, Encoding.UTF8);
            var socket = EngineSocket.Match(xml);
            var currentTypes = ReadEngineTypes(socket.Success ? socket.Value : string.Empty);
            var relativePath = WorkspaceOriginals.GetRelativePath(workspace, path);
            var originalPath = WorkspaceOriginals.GetOriginalReadPath(workspace, relativePath);
            var originalXml = File.Exists(originalPath) ? WorkspaceDataCache.ReadAllText(originalPath, Encoding.UTF8) : xml;
            var originalSocket = EngineSocket.Match(originalXml);
            var originalTypes = ReadEngineTypes(originalSocket.Success ? originalSocket.Value : string.Empty);
            var truckData = Regex.Match(xml, @"<TruckData\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase);
            var truckClass = TruckClasses.ForTruck(truck.Code, FormatTruckClass(GetAttribute(truckData.Success ? truckData.Groups["attrs"].Value : string.Empty, "TruckType")));
            var rowData = new TruckRow(path, truck.Name, socket.Success, currentTypes, originalTypes, GetAttribute(socket.Success ? socket.Value : string.Empty, "Default"))
            {
                IsSelected = true,
                OriginalOrder = _compatibilityGrid.Rows.Count
            };
            var rowValues = new object[] { rowData.IsSelected, truck.Name, string.IsNullOrWhiteSpace(truckClass) ? "—" : truckClass, GetTruckDlc(path, workspace, _dlcNames) }
                .Concat(_matrixEngineClasses.Select(engine => (object)currentTypes.Contains(engine.Code))).ToArray();
            var rowIndex = _compatibilityGrid.Rows.Add(rowValues);
            var row = _compatibilityGrid.Rows[rowIndex];
            row.Tag = rowData;
            row.Cells["Truck"].ToolTipText = $"Open the raw file\nTruck key: {truck.Code}";
            for (var column = 4; column < row.Cells.Count; column++)
                row.Cells[column].ReadOnly = !_allowCompatibilityEditing.Checked || !socket.Success;
            UpdateCompatibilityCellStyles(row);
        }
        PopulateTruckFilters();
        _updatingGrid = false;
        SyncSelectAllCheckbox();
        ApplyTruckFilters();
        _status.Text = $"Showing {_compatibilityGrid.Rows.Cast<DataGridViewRow>().Count(row => row.Visible && row.Tag is TruckRow)} owned trucks. Green adds and red removes engine classes compared with the originals.";
    }

    private void UpdateCompatibilityCellStyles(DataGridViewRow row)
    {
        if (row.Tag is not TruckRow data) return;
        foreach (var engineClass in _matrixEngineClasses)
        {
            var cell = row.Cells[engineClass.Code];
            var selected = data.Types.Contains(engineClass.Code);
            var original = data.OriginalTypes.Contains(engineClass.Code);
            var color = selected == original ? AppColors.Get("Surface") : selected ? AppColors.Get("ChangedPositive") : AppColors.Get("ChangedNegative");
            cell.Style.BackColor = color;
            cell.Style.SelectionBackColor = color;
            cell.Style.ForeColor = SystemColors.ControlText;
            cell.Style.SelectionForeColor = SystemColors.ControlText;
        }
    }

    private void UpdateSaveButton()
    {
        _saveCompatibilityButton.Enabled = _compatibilityGrid.Rows.Cast<DataGridViewRow>()
            .Select(row => row.Tag).OfType<TruckRow>().Any(row => !row.Types.SetEquals(row.PersistedTypes));
    }

    private void CompatibilityCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_updatingGrid || _syncingSelection || (e.ColumnIndex >= 4 && !_allowCompatibilityEditing.Checked) || e.RowIndex < 0 || _compatibilityGrid.Rows[e.RowIndex].Tag is not TruckRow data) return;
        if (e.ColumnIndex == 0)
        {
            data.IsSelected = _compatibilityGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value is true;
            SyncSelectAllCheckbox();
            RefreshPropertyTablesForTruckSelection();
            return;
        }
        if (e.ColumnIndex < 4) return;
        var engineClass = _matrixEngineClasses[e.ColumnIndex - 4];
        var updated = new HashSet<string>(data.Types, StringComparer.OrdinalIgnoreCase);
        if (_compatibilityGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value is true) updated.Add(engineClass.Code);
        else updated.Remove(engineClass.Code);
        if (updated.Count == 0)
        {
            _updatingGrid = true;
            _compatibilityGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = true;
            _updatingGrid = false;
            MessageBox.Show(this, "A truck must keep at least one engine class.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        data.Types = updated;
        UpdateCompatibilityCellStyles(_compatibilityGrid.Rows[e.RowIndex]);
        UpdateSaveButton();
        RefreshPropertyTablesForTruckSelection();
        _status.Text = data.Types.SetEquals(data.PersistedTypes)
            ? $"Engine compatibility changes for {data.Name} were reverted."
            : $"Engine compatibility changes for {data.Name} are pending save.";
    }

    public IEnumerable<WorkspaceChange> PendingChanges(string workspace)
    {
        if (!string.Equals(_loadedWorkspace, workspace, StringComparison.OrdinalIgnoreCase)) yield break;
        foreach (var row in _compatibilityGrid.Rows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<TruckRow>().Where(row => !row.Types.SetEquals(row.PersistedTypes)))
            yield return new(WorkspaceChange.Pending, row.FilePath, row.Name + " / Engine compatibility", string.Join(", ", row.PersistedTypes.Order()), string.Join(", ", row.Types.Order()));
        foreach (var engineClass in _engineClasses)
            foreach (var original in engineClass.Definitions)
                if (_updatedDefinitions.TryGetValue(DefinitionKey(engineClass.Code, original.Name), out var updated))
                    foreach (var key in original.Properties.Keys.Concat(updated.Properties.Keys).Distinct())
                        if (original.Properties.GetValueOrDefault(key) != updated.Properties.GetValueOrDefault(key))
                            yield return new(WorkspaceChange.Pending, engineClass.FilePath, original.UiName + " / " + key,
                                original.Properties.GetValueOrDefault(key, ""), updated.Properties.GetValueOrDefault(key, ""));
    }

    public void SavePendingChanges()
    {
        _compatibilityGrid.EndEdit(); _updatedPropertiesGrid.EndEdit();
        SaveCompatibility(this, EventArgs.Empty); ConfirmProperties(this, EventArgs.Empty);
    }

    private void SaveCompatibility(object? sender, EventArgs e)
    {
        var pending = _compatibilityGrid.Rows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<TruckRow>()
            .Where(row => !row.Types.SetEquals(row.PersistedTypes)).ToList();
        try
        {
            foreach (var row in pending)
            {
                WriteTruckCompatibility(row, row.Types);
                row.PersistedTypes = new HashSet<string>(row.Types, StringComparer.OrdinalIgnoreCase);
                row.DefaultEngine = GetCurrentDefault(row.FilePath);
            }
            UpdateSaveButton();
            _status.Text = $"Saved engine compatibility for {pending.Count} truck{(pending.Count == 1 ? string.Empty : "s")} to the workspace.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            UpdateSaveButton();
            MessageBox.Show(this, $"Could not save engine compatibility: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CompatibilityCellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _compatibilityGrid.Columns[e.ColumnIndex].Name != "Truck" || _compatibilityGrid.Rows[e.RowIndex].Tag is not TruckRow row) return;
        var bounds = _compatibilityGrid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
        if (e.X >= bounds.Width - 30) OpenRawFile(row.FilePath);
    }

    private void CompatibilityCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {

        if (e.RowIndex < 0 && e.ColumnIndex >= 4)
        {
            e.PaintBackground(e.CellBounds, false);
            e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
            var state = e.Graphics.Save();
            e.Graphics.TranslateTransform(e.CellBounds.Left + e.CellBounds.Width / 2f, e.CellBounds.Top + e.CellBounds.Height / 2f);
            e.Graphics.RotateTransform(-90);
            var hasSortIcon = e.ColumnIndex == _sortedCompatibilityColumnIndex;
            var iconSpace = hasSortIcon ? 13f : 0f;
            var rotatedBounds = new RectangleF(-e.CellBounds.Height / 2f + iconSpace, -e.CellBounds.Width / 2f, e.CellBounds.Height - iconSpace, e.CellBounds.Width);
            using var brush = new SolidBrush(e.CellStyle.ForeColor);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            e.Graphics.DrawString(e.FormattedValue?.ToString() ?? string.Empty, e.CellStyle.Font, brush, rotatedBounds, format);
            e.Graphics.Restore(state);
            if (hasSortIcon)
            {
                // Filled caret icon, matching the compact Bootstrap Icons sort direction indicators.
                var centerX = e.CellBounds.Right - 8f;
                var centerY = e.CellBounds.Top + e.CellBounds.Height / 2f;
                PointF[] points = _sortedCompatibilityDirection == ListSortDirection.Ascending
                    ? [new PointF(centerX - 5f, centerY + 2.5f), new PointF(centerX + 5f, centerY + 2.5f), new PointF(centerX, centerY - 4f)]
                    : [new PointF(centerX - 5f, centerY - 2.5f), new PointF(centerX + 5f, centerY - 2.5f), new PointF(centerX, centerY + 4f)];
                using var iconBrush = new SolidBrush(e.CellStyle.ForeColor);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillPolygon(iconBrush, points);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
            }
            e.Handled = true;
            return;
        }
        if (e.RowIndex >= 0 && e.ColumnIndex >= 4 && !_allowCompatibilityEditing.Checked)
        {
            e.PaintBackground(e.CellBounds, true);
            e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
            if (e.FormattedValue is true)
            {
                // Follows Bootstrap Icons' check-lg silhouette. See the included third-party notice.
                var size = Math.Min(16f, Math.Min(e.CellBounds.Width * 0.58f, e.CellBounds.Height * 0.62f));
                var left = e.CellBounds.Left + (e.CellBounds.Width - size) / 2f;
                var top = e.CellBounds.Top + (e.CellBounds.Height - size) / 2f;
                using var pen = new Pen(e.CellStyle.ForeColor, Math.Max(1.8f, size / 6f))
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round
                };
                var oldSmoothing = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.DrawLines(pen, [new PointF(left + size * 0.12f, top + size * 0.53f), new PointF(left + size * 0.39f, top + size * 0.79f), new PointF(left + size * 0.9f, top + size * 0.22f)]);
                e.Graphics.SmoothingMode = oldSmoothing;
            }
            e.Handled = true;
            return;
        }
        if (e.RowIndex < 0 || e.ColumnIndex != 1 || _compatibilityGrid.Rows[e.RowIndex].Tag is not TruckRow)
            return;

        e.PaintBackground(e.CellBounds, true);
        e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
        var buttonBounds = new Rectangle(e.CellBounds.Right - 28, e.CellBounds.Top + 2, 25, e.CellBounds.Height - 4);
        var textBounds = new Rectangle(e.CellBounds.Left + 4, e.CellBounds.Top, buttonBounds.Left - e.CellBounds.Left - 7, e.CellBounds.Height);
        TextRenderer.DrawText(e.Graphics, e.FormattedValue?.ToString() ?? string.Empty, e.CellStyle.Font, textBounds, e.CellStyle.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        ButtonRenderer.DrawButton(e.Graphics, buttonBounds, "…", e.CellStyle.Font, false, PushButtonState.Normal);
        e.Handled = true;
    }

    private void UpdatedPropertyValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _updatedPropertiesGrid.Rows[e.RowIndex].Tag is not EngineTableRow row) return;
        var key = DefinitionKey(row.ClassCode, row.Name);
        if (!_updatedDefinitions.TryGetValue(key, out var definition)) return;
        var column = _updatedPropertiesGrid.Columns[e.ColumnIndex].Name;
        if (!EngineProperties.Contains(column, StringComparer.OrdinalIgnoreCase)) return;
        var value = column == "UnlockByExploration"
            ? (_updatedPropertiesGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value is true ? "true" : "false")
            : Convert.ToString(_updatedPropertiesGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        var properties = new Dictionary<string, string>(definition.Properties, StringComparer.OrdinalIgnoreCase) { [column] = value };
        _updatedDefinitions[key] = definition with { Properties = properties };
        UpdateConfirmButton();
        _status.Text = "Engine property edits are previewed. Confirm and save to write them to the workspace.";
    }

    private void UpdatedPropertyCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        e.CellStyle.BackColor = AppColors.Get("Surface");
        e.CellStyle.SelectionBackColor = AppColors.Get("SelectionBlue");
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _updatedPropertiesGrid.Rows[e.RowIndex].Tag is not EngineTableRow row) return;
        var property = _updatedPropertiesGrid.Columns[e.ColumnIndex].Name;
        if (!EngineProperties.Contains(property, StringComparer.OrdinalIgnoreCase)) return;
        var engineClass = _engineClasses.FirstOrDefault(item => string.Equals(item.Code, row.ClassCode, StringComparison.OrdinalIgnoreCase));
        var original = engineClass?.OriginalDefinitions.FirstOrDefault(item => string.Equals(item.Name, row.Name, StringComparison.OrdinalIgnoreCase));
        var updated = _updatedDefinitions.GetValueOrDefault(DefinitionKey(row.ClassCode, row.Name));
        if (original is null || updated is null || !double.TryParse(original.Properties[property], NumberStyles.Float, CultureInfo.InvariantCulture, out var originalValue) ||
            !double.TryParse(Convert.ToString(e.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var updatedValue) || updatedValue == originalValue)
            return;
        var color = updatedValue > originalValue ? AppColors.Get("ChangedPositive") : AppColors.Get("ChangedNegative");
        e.CellStyle.BackColor = color;
        e.CellStyle.SelectionBackColor = color;
    }

    private void UpdateConfirmButton()
    {
        _confirmPropertiesButton.Enabled = _engineClasses.Any(engineClass => engineClass.Definitions.Any(original =>
            !EngineDefinitionsEqual(original, _updatedDefinitions[DefinitionKey(engineClass.Code, original.Name)])));
    }

    private static bool EngineDefinitionsEqual(EngineDefinition first, EngineDefinition second)
    {
        return string.Equals(first.Name, second.Name, StringComparison.OrdinalIgnoreCase) &&
               first.Properties.Count == second.Properties.Count &&
               first.Properties.All(pair => second.Properties.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private void ConfirmProperties(object? sender, EventArgs e)
    {
        _updatedPropertiesGrid.EndEdit();
        var pending = _engineClasses.SelectMany(engineClass => engineClass.Definitions
            .Select(definition => (Class: engineClass, Original: definition, Updated: _updatedDefinitions[DefinitionKey(engineClass.Code, definition.Name)])))
            .Where(item => !EngineDefinitionsEqual(item.Original, item.Updated)).ToList();
        if (pending.Count == 0)
        {
            UpdateConfirmButton();
            return;
        }
        foreach (var change in pending)
            if (!ValidProperties(change.Updated, out var error))
            {
                MessageBox.Show(this, $"{change.Updated.UiName}: {error}", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        try
        {
            foreach (var group in pending.GroupBy(item => item.Class.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                var changes = group.ToList();
                WriteEngineDefinitions(group.Key, changes.Select(item => (item.Original.Name, item.Updated)).ToList());
                foreach (var change in changes)
                {
                    var definitionIndex = change.Class.Definitions.FindIndex(item => string.Equals(item.Name, change.Original.Name, StringComparison.OrdinalIgnoreCase));
                    if (definitionIndex >= 0) change.Class.Definitions[definitionIndex] = change.Updated;
                }
            }
            UpdateConfirmButton();
            _status.Text = $"Saved updated properties for {pending.Count} engine variants to the workspace.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, $"Could not save engine properties: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool ValidProperties(EngineDefinition definition, out string error)
    {
        foreach (var property in EngineProperties.Where(item => item is not "UnlockByExploration" and not "UnlockByRank" and not "Price"))
        {
            var value = definition.Properties[property];
            if (value == "—") continue;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                error = $"{property} must be a number.";
                return false;
            }
        }
        var rank = definition.Properties["UnlockByRank"];
        if (rank != "—" && !string.IsNullOrWhiteSpace(rank) && (!int.TryParse(rank, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rankNumber) || rankNumber < 1))
        {
            error = "Rank must be a positive whole number.";
            return false;
        }
        var price = definition.Properties["Price"];
        if (price != "—" && !string.IsNullOrWhiteSpace(price) && (!int.TryParse(price, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priceNumber) || priceNumber < 0))
        {
            error = "Price must be a non-negative whole number.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    private void WriteEngineDefinitions(string path, IReadOnlyList<(string Name, EngineDefinition Updated)> definitions)
    {
        path = WorkspaceOriginals.PrepareForEdit(_getWorkspacePath(), path);
        var bytes = File.ReadAllBytes(path);
        var bom = Encoding.UTF8.GetPreamble();
        var hasBom = bytes.AsSpan().StartsWith(bom);
        var xml = Encoding.UTF8.GetString(bytes, hasBom ? bom.Length : 0, bytes.Length - (hasBom ? bom.Length : 0));
        var matches = EngineDefinitionTag.Matches(xml).Cast<Match>().ToArray();
        var replacements = new List<(int Index, int Length, string Text)>();
        foreach (var (name, updated) in definitions)
        {
            var match = matches.FirstOrDefault(item => string.Equals(GetAttribute(item.Groups["attrs"].Value, "Name"), name, StringComparison.OrdinalIgnoreCase));
            if (match is null) throw new InvalidDataException($"Engine definition '{name}' was not found in {Path.GetFileName(path)}.");
            var openEnd = match.Value.IndexOf('>') + 1;
            if (openEnd <= 0) throw new InvalidDataException($"Engine definition '{name}' has an invalid opening tag.");
            var openTag = match.Value[..openEnd];
            foreach (var property in EngineProperties.Where(item => item is not "Price" and not "UnlockByRank" and not "UnlockByExploration"))
                if (updated.Properties[property] != "—") openTag = ReplaceAttribute(openTag, property, XmlValue(updated.Properties[property]));

            var definitionText = openTag + match.Value[openEnd..];
            var gameData = Regex.Match(definitionText, @"<GameData\b[^>]*>", RegexOptions.IgnoreCase);
            if (!gameData.Success) throw new InvalidDataException($"Engine definition '{name}' has no GameData element.");
            var gameDataTag = gameData.Value;
            foreach (var property in new[] { "Price", "UnlockByRank", "UnlockByExploration" })
                if (updated.Properties[property] != "—") gameDataTag = ReplaceAttribute(gameDataTag, property, XmlValue(updated.Properties[property]));
            definitionText = definitionText[..gameData.Index] + gameDataTag + definitionText[(gameData.Index + gameData.Length)..];
            replacements.Add((match.Index, match.Length, definitionText));
        }
        foreach (var replacement in replacements.OrderByDescending(item => item.Index))
            xml = xml[..replacement.Index] + replacement.Text + xml[(replacement.Index + replacement.Length)..];
        var output = new UTF8Encoding(false).GetBytes(xml);
        if (hasBom) output = bom.Concat(output).ToArray();
        File.WriteAllBytes(path, output);
        WorkspaceDataCache.InvalidateFile(path);
    }

    private void WriteTruckCompatibility(TruckRow row, HashSet<string> types)
    {
        var path = WorkspaceOriginals.PrepareForEdit(_getWorkspacePath(), row.FilePath);
        var bytes = File.ReadAllBytes(path);
        var bom = Encoding.UTF8.GetPreamble();
        var hasBom = bytes.AsSpan().StartsWith(bom);
        var xml = Encoding.UTF8.GetString(bytes, hasBom ? bom.Length : 0, bytes.Length - (hasBom ? bom.Length : 0));
        var socket = EngineSocket.Match(xml);
        if (!socket.Success) throw new InvalidDataException("The truck has no EngineSocket element.");
        var selected = _engineClasses.Where(engineClass => types.Contains(engineClass.Code)).ToArray();
        var defaultEngine = row.DefaultEngine;
        if (!selected.Any(engineClass => engineClass.Definitions.Any(definition => string.Equals(definition.Name, defaultEngine, StringComparison.OrdinalIgnoreCase))))
            defaultEngine = selected.Select(engineClass => engineClass.DefaultEngine).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? defaultEngine;
        var unknownTypes = types.Where(type => !_engineClasses.Any(engineClass => string.Equals(engineClass.Code, type, StringComparison.OrdinalIgnoreCase)));
        var updatedTag = ReplaceAttribute(socket.Value, "Type", string.Join(", ", selected.Select(item => item.Code).Concat(unknownTypes)));
        updatedTag = ReplaceAttribute(updatedTag, "Default", defaultEngine);
        xml = xml[..socket.Index] + updatedTag + xml[(socket.Index + socket.Length)..];
        var output = new UTF8Encoding(false).GetBytes(xml);
        if (hasBom) output = bom.Concat(output).ToArray();
        File.WriteAllBytes(path, output);
        WorkspaceDataCache.InvalidateFile(path);
    }

    private void OpenRawFile(string path)
    {
        try
        {
            path = WorkspaceOriginals.PrepareForEdit(_getWorkspacePath(), path);
            if (!File.Exists(path)) throw new FileNotFoundException("The source file no longer exists.", path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"Could not open the source file: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Dictionary<string, string> FindTruckFiles(string workspace)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in WorkspaceOriginals.EnumerateCurrentFiles(workspace, Path.Combine("[media]", "classes", "trucks"), "*.xml")
                     .Concat(WorkspaceOriginals.EnumerateCurrentFiles(workspace, Path.Combine("[media]", "_dlc"), "*.xml"))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            if (string.Equals(Directory.GetParent(file)?.Name, "trucks", StringComparison.OrdinalIgnoreCase))
                files.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        return files;
    }

    private static HashSet<string> ReadEngineTypes(string socket)
    {
        return GetAttribute(socket, "Type").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Path.GetFileNameWithoutExtension(value.Trim())).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string GetCurrentDefault(string path)
    {
        var socket = EngineSocket.Match(WorkspaceDataCache.ReadAllText(path, Encoding.UTF8));
        return GetAttribute(socket.Success ? socket.Value : string.Empty, "Default");
    }

    private static string ReplaceAttribute(string tag, string attribute, string value)
    {
        var escaped = System.Security.SecurityElement.Escape(value) ?? string.Empty;
        var pattern = $@"\b{Regex.Escape(attribute)}\s*=\s*""[^""]*""";
        var replacement = $"{attribute}=\"{escaped}\"";
        if (Regex.IsMatch(tag, pattern, RegexOptions.IgnoreCase)) return Regex.Replace(tag, pattern, replacement, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return tag.Insert(tag.Length - 1, $" {replacement}");
    }

    private static string GetAttribute(string tag, string name) => XmlAttribute.Matches(tag).Cast<Match>()
        .FirstOrDefault(match => string.Equals(match.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))?.Groups["value"].Value ?? string.Empty;

    private static string DisplayValue(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string XmlValue(string value) => value == "—" ? string.Empty : value;
    private static string DefinitionKey(string classCode, string name) => $"{classCode}\0{name}";

    private sealed class CheckedFilterDropdown : UserControl
    {
        private readonly string _allLabel;
        private readonly Button _button;
        private readonly CheckedListBox _list;
        private readonly ToolStripDropDown _dropDown;
        private bool _loading;

        public event EventHandler? SelectionChanged;
        public IEnumerable<string> SelectedValues => _list.CheckedItems.Cast<string>().ToArray();

        public CheckedFilterDropdown(string allLabel, int width)
        {
            _allLabel = allLabel;
            Width = width;
            Height = 30;
            Margin = new Padding(4);
            _button = new Button { Dock = DockStyle.Fill, Text = $"{allLabel} ▾", TextAlign = System.Drawing.ContentAlignment.MiddleLeft, FlatStyle = FlatStyle.System };
            Controls.Add(_button);
        _list = new CheckedListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, CheckOnClick = true, IntegralHeight = false };
        _dropDown = new ToolStripDropDown { AutoSize = false, Size = new Size(width, 422), Padding = Padding.Empty };
        _dropDown.Items.Add(new ToolStripControlHost(_list) { AutoSize = false, Size = new Size(width, 422), Padding = Padding.Empty, Margin = Padding.Empty });
            _button.Click += (_, _) => _dropDown.Show(_button, new Point(0, _button.Height));
            _list.ItemCheck += (_, _) =>
            {
                if (_loading || !IsHandleCreated) return;
                BeginInvoke((Action)(() =>
                {
                    UpdateButtonText();
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                }));
            };
        }

        public void SetOptions(IEnumerable<string> options, IEnumerable<string> selected)
        {
            var selectedSet = selected.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            _loading = true;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var option in options.Distinct(StringComparer.CurrentCultureIgnoreCase))
                _list.Items.Add(option, selectedSet.Contains(option));
            _list.EndUpdate();
            _loading = false;
            UpdateButtonText();
        }

        public bool Matches(string value)
        {
            var selected = _list.CheckedItems.Cast<string>().ToArray();
            return selected.Length == 0 || selected.Length == _list.Items.Count || selected.Contains(value, StringComparer.CurrentCultureIgnoreCase);
        }

        private void UpdateButtonText()
        {
            var selectedCount = _list.CheckedItems.Count;
            _button.Text = selectedCount == 0 || selectedCount == _list.Items.Count
                ? $"{_allLabel} ▾"
                : $"{_allLabel}: {selectedCount} selected ▾";
        }
    }

    private sealed record EngineDefinition(string Name, string UiName, string UiNameKey, Dictionary<string, string> Properties);
    private sealed record EngineClass(string Code, string DisplayName, string FilePath, string DefaultEngine, List<EngineDefinition> Definitions, List<EngineDefinition> OriginalDefinitions);
    private sealed record EngineTableRow(string ClassCode, string FilePath, string Name);

    private sealed class TruckRow(string filePath, string name, bool hasEngineSocket, HashSet<string> types, HashSet<string> originalTypes, string defaultEngine)
    {
        public string FilePath { get; } = filePath;
        public string Name { get; } = name;
        public bool HasEngineSocket { get; } = hasEngineSocket;
        public HashSet<string> Types { get; set; } = types;
        public HashSet<string> OriginalTypes { get; } = originalTypes;
        public HashSet<string> PersistedTypes { get; set; } = new(types, StringComparer.OrdinalIgnoreCase);
        public string DefaultEngine { get; set; } = defaultEngine;
        public bool IsSelected { get; set; }
        public int OriginalOrder { get; set; }
    }

    private sealed class TruckDlcComparer(ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var left = (x as DataGridViewRow)?.Tag as TruckRow;
            var right = (y as DataGridViewRow)?.Tag as TruckRow;
            var leftName = Convert.ToString((x as DataGridViewRow)?.Cells["Dlc"].Value, CultureInfo.InvariantCulture) ?? string.Empty;
            var rightName = Convert.ToString((y as DataGridViewRow)?.Cells["Dlc"].Value, CultureInfo.InvariantCulture) ?? string.Empty;

            var leftIsBaseGame = string.Equals(leftName, "Base game", StringComparison.OrdinalIgnoreCase);
            var rightIsBaseGame = string.Equals(rightName, "Base game", StringComparison.OrdinalIgnoreCase);
            var leftHasSeason = TryGetSeasonNumber(leftName, out var leftSeason);
            var rightHasSeason = TryGetSeasonNumber(rightName, out var rightSeason);
            var comparison = leftIsBaseGame != rightIsBaseGame
                ? (leftIsBaseGame ? -1 : 1)
                : leftHasSeason && rightHasSeason
                    ? leftSeason.CompareTo(rightSeason)
                    : string.Compare(leftName, rightName, StringComparison.CurrentCultureIgnoreCase);

            if (comparison == 0)
                return (left?.OriginalOrder ?? int.MaxValue).CompareTo(right?.OriginalOrder ?? int.MaxValue);
            return direction == ListSortDirection.Ascending ? comparison : -comparison;
        }

        private static bool TryGetSeasonNumber(string value, out int seasonNumber)
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out seasonNumber)) return true;
            seasonNumber = 0;
            var seasonMatch = Regex.Match(value, @"\bSeason[\s_-]*(?<number>\d+)", RegexOptions.IgnoreCase);
            return seasonMatch.Success && int.TryParse(seasonMatch.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out seasonNumber);
        }
    }

    private sealed class TruckOriginalOrderComparer : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var left = (x as DataGridViewRow)?.Tag as TruckRow;
            var right = (y as DataGridViewRow)?.Tag as TruckRow;
            return (left?.OriginalOrder ?? int.MaxValue).CompareTo(right?.OriginalOrder ?? int.MaxValue);
        }
    }
}
