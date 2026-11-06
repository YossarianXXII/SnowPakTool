using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Globalization;

namespace SnowPakTool.Desktop;

internal sealed class TruckEditTab : UserControl
{
    private static readonly Regex WinchSocket = new(@"<WinchUpgradeSocket\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TruckDataTag = new(@"<TruckData\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex XmlAttribute = new(@"(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)\s*=\s*""(?<value>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex WinchDefinitionTag = new(@"<Winch\b(?<attrs>[^>]*?)(?:/\s*>|>(?<body>[\s\S]*?)</Winch\s*>)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex XmlElementTag = new(@"<(?<tag>[A-Za-z_][A-Za-z0-9_.:-]*)\b(?<attrs>[^>]*)>", RegexOptions.Compiled);
    private readonly Func<string> _getWorkspacePath;
    private readonly OwnedContentTab _ownedContent;
    private readonly EngineEditTab _engineEditTab;
    private readonly AddonsTab _addonsTab;
    private readonly AddonsTab _frameAddonsTab;
    private bool _pagesHostedExternally;
    private readonly TabControl _subTabs = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _winchGrid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeColumns = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        ScrollBars = ScrollBars.Both,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        MultiSelect = false,
        EditMode = DataGridViewEditMode.EditOnEnter,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f),
        ColumnHeadersDefaultCellStyle = { Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f, FontStyle.Bold) },
        DefaultCellStyle = { BackColor = AppColors.Get("Surface"), SelectionBackColor = AppColors.Get("Surface"), SelectionForeColor = SystemColors.ControlText },
        RowTemplate = { Height = 26 }
    };
    private readonly DataGridView _winchPropertiesGrid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeColumns = false,
        AllowUserToResizeRows = false,
        ReadOnly = true,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        ScrollBars = ScrollBars.Vertical,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f),
        ColumnHeadersDefaultCellStyle = { Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f, FontStyle.Bold) },
        DefaultCellStyle = { BackColor = AppColors.Get("Surface"), SelectionBackColor = AppColors.Get("Surface"), SelectionForeColor = SystemColors.ControlText },
        RowTemplate = { Height = 23 }
    };
    private readonly DataGridView _updatedWinchPropertiesGrid = CreatePropertiesGrid();
    private readonly FlowLayoutPanel _presetPanel = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
    private readonly ComboBox _customWinchPresetCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(WinchCustomPreset.Name), Width = 180, Margin = new Padding(4), Enabled = false };
    private readonly Button _applyCustomWinchPresetButton = new() { Text = "Apply saved preset", AutoSize = true, Margin = new Padding(4), Enabled = false };
    private readonly Button _saveWinchCompatibilityButton = new() { Text = "Stage compatibility changes", AutoSize = true, Enabled = false, Margin = new Padding(4) };
    private Button? _confirmPresetButton;
    private readonly Label _winchStatus = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4, 6, 4, 4) };
    private List<WinchClass> _winchClasses = new();
    private List<WinchClass> _matrixWinchClasses = new();
    private readonly Dictionary<string, WinchDefinition> _updatedDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WinchDefinition> _stagedDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private PendingWinchChanges _stagedWinchChanges = PendingWinchChanges.Empty;
    private WinchCustomPresetCollection _customWinchPresets = WinchCustomPresetCollection.Empty;
    private string? _loadedWorkspace;
    private bool _updatingGrid;
    private bool _syncingWinchNames;
    private readonly TableHeaderBehavior _winchHeaders;

    public TruckEditTab(Func<string> getWorkspacePath, OwnedContentTab ownedContent)
    {
        _getWorkspacePath = getWorkspacePath;
        _ownedContent = ownedContent;
        _engineEditTab = new EngineEditTab(getWorkspacePath, ownedContent);
        _addonsTab = new AddonsTab(getWorkspacePath) { Dock = DockStyle.Fill };
        _frameAddonsTab = new AddonsTab(getWorkspacePath, frameAddonsOnly: true) { Dock = DockStyle.Fill };
        BuildLayout();
        _winchHeaders = TableHeaderBehavior.Attach(_winchGrid);
        _winchHeaders.Changed += UpdateWinchVisibleCount;
        AppColors.Changed += (_, _) => RefreshPaletteColors();
        _ownedContent.OwnedTrucksChanged += RefreshOwnedTrucks;
        _winchGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_winchGrid.IsCurrentCellDirty) _winchGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _winchGrid.CellValueChanged += WinchCellValueChanged;
        _winchGrid.CellPainting += WinchGridCellPainting;
        _winchGrid.CellMouseClick += WinchGridCellMouseClick;
        _updatedWinchPropertiesGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_updatedWinchPropertiesGrid.IsCurrentCellDirty) _updatedWinchPropertiesGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _updatedWinchPropertiesGrid.CellValueChanged += UpdatedWinchPropertyValueChanged;
        _saveWinchCompatibilityButton.Click += SaveWinchCompatibility;
        _winchPropertiesGrid.CellPainting += WinchPropertiesCellPainting;
        _updatedWinchPropertiesGrid.CellPainting += WinchPropertiesCellPainting;
        _winchPropertiesGrid.CellMouseClick += WinchPropertiesCellMouseClick;
        _updatedWinchPropertiesGrid.CellMouseClick += WinchPropertiesCellMouseClick;
    }

    private void RefreshPaletteColors()
    {
        foreach (var grid in new[] { _winchGrid, _winchPropertiesGrid, _updatedWinchPropertiesGrid })
        {
            grid.DefaultCellStyle.BackColor = AppColors.Get("Surface");
            grid.DefaultCellStyle.SelectionBackColor = grid == _updatedWinchPropertiesGrid
                ? AppColors.Get("SelectionBlue") : AppColors.Get("Surface");
            foreach (DataGridViewRow row in grid.Rows)
                if (row.Tag is string) row.DefaultCellStyle.BackColor = AppColors.Get("GroupRow");
            grid.Invalidate();
        }
    }

    private void BuildLayout()
    {
        var winchesPage = new TabPage("Winches") { Name = "Winches" };
        var enginesPage = new TabPage("Engines") { Name = "Engines" };
        var addonsPage = new TabPage("Addons") { Name = "Addons" };
        var frameAddonsPage = new TabPage("Frame addons") { Name = "Frame addons" };
        var winchContent = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
        winchContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        winchContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        winchContent.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        winchContent.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        winchContent.Controls.Add(new Label
        {
            Text = "Rows show owned trucks and their truck class. Green cells add a winch class and red cells remove one compared with the original; stage compatibility edits here, then apply pending PAK edits from Start. The properties tables below show winch specifications by class.",
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 8)
        }, 0, 0);
        var tables = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        tables.SizeChanged += (_, _) =>
        {
            const int minLeftWidth = 220;
            const int minRightWidth = 320;
            var maxDistance = tables.Width - tables.SplitterWidth - minRightWidth;
            if (maxDistance >= minLeftWidth)
                tables.SplitterDistance = Math.Clamp((int)(tables.Width * 0.42), minLeftWidth, maxDistance);
        };
        var matrices = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(2) };
        matrices.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var truckSection = new GroupBox { Text = "Truck winch compatibility", Dock = DockStyle.Fill, Padding = new Padding(6) };
        var truckGridLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        truckGridLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        truckGridLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        truckGridLayout.Controls.Add(_winchGrid, 0, 0);
        var saveCompatibilityPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        saveCompatibilityPanel.Controls.Add(_saveWinchCompatibilityButton);
        truckGridLayout.Controls.Add(saveCompatibilityPanel, 0, 1);
        truckSection.Controls.Add(truckGridLayout);
        matrices.Controls.Add(truckSection, 0, 0);
        tables.Panel1.Controls.Add(matrices);
        var propertiesLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(2) };
        propertiesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        propertiesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var propertiesSection = new GroupBox { Text = "Original winch properties", Dock = DockStyle.Fill, Padding = new Padding(6) };
        propertiesSection.Controls.Add(_winchPropertiesGrid);
        var updatedSection = new GroupBox { Text = "Updated winch properties", Dock = DockStyle.Fill, Padding = new Padding(6) };
        var updatedLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        updatedLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        updatedLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var applyPresetButton = new Button { Text = "Tow rope/Rescue", AutoSize = true, Margin = new Padding(4), Anchor = AnchorStyles.Top };
        applyPresetButton.Click += ApplyGlobalWinchPreset;
        _presetPanel.Controls.Add(applyPresetButton);
        var resetOriginalButton = new Button { Text = "Reset to original values", AutoSize = true, Margin = new Padding(4), Anchor = AnchorStyles.Top };
        resetOriginalButton.Click += ResetWinchPropertiesToOriginal;
        _presetPanel.Controls.Add(resetOriginalButton);
        var saveCustomPresetButton = new Button { Text = "Save custom preset", AutoSize = true, Margin = new Padding(4), Anchor = AnchorStyles.Top };
        saveCustomPresetButton.Click += SaveCustomWinchPreset;
        _presetPanel.Controls.Add(saveCustomPresetButton);
        _presetPanel.Controls.Add(_customWinchPresetCombo);
        _applyCustomWinchPresetButton.Click += ApplyCustomWinchPreset;
        _presetPanel.Controls.Add(_applyCustomWinchPresetButton);
        _confirmPresetButton = new Button { Text = "Stage winch changes", AutoSize = true, Margin = new Padding(4), Anchor = AnchorStyles.Top, Enabled = false };
        _confirmPresetButton.Click += ConfirmWinchPreset;
        _presetPanel.Controls.Add(_confirmPresetButton);
        _presetPanel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            Margin = new Padding(8, 8, 4, 4),
            Text = "Edit the Updated values, reset them, or apply a saved preset. Save custom presets for this workspace. Stage changes here, then use Start → Apply pending changes to write them to the workspace."
        });
        updatedLayout.Controls.Add(_presetPanel, 0, 0);
        updatedLayout.Controls.Add(_updatedWinchPropertiesGrid, 0, 1);
        updatedSection.Controls.Add(updatedLayout);
        propertiesLayout.Controls.Add(propertiesSection, 0, 0);
        propertiesLayout.Controls.Add(updatedSection, 0, 1);
        var propertiesHost = new Panel { Dock = DockStyle.Fill };
        propertiesHost.Controls.Add(propertiesLayout);
        tables.Panel2.Controls.Add(propertiesHost);
        winchContent.Controls.Add(tables, 0, 1);
        winchContent.Controls.Add(_winchStatus, 0, 2);
        winchesPage.Controls.Add(winchContent);
        _engineEditTab.Dock = DockStyle.Fill;
        enginesPage.Controls.Add(_engineEditTab);
        addonsPage.Controls.Add(_addonsTab);
        frameAddonsPage.Controls.Add(_frameAddonsTab);
        _subTabs.TabPages.Add(winchesPage);
        _subTabs.TabPages.Add(enginesPage);
        _subTabs.TabPages.Add(addonsPage);
        _subTabs.TabPages.Add(frameAddonsPage);
        _subTabs.SelectedIndexChanged += (_, _) =>
        {
            if (_pagesHostedExternally) return;
            if (_subTabs.SelectedTab == winchesPage) EnsureLoaded();
            if (_subTabs.SelectedTab == enginesPage) _engineEditTab.EnsureLoaded();
            if (_subTabs.SelectedTab == addonsPage) _addonsTab.EnsureLoaded();
            if (_subTabs.SelectedTab == frameAddonsPage) _frameAddonsTab.EnsureLoaded();
        };
        Controls.Add(_subTabs);
    }

    // Keep one editor controller for staged changes while hosting its pages directly under Editors.
    internal TabPage[] TakeEditorPages()
    {
        _pagesHostedExternally = true;
        var pages = _subTabs.TabPages.Cast<TabPage>().ToArray();
        _subTabs.TabPages.Clear();
        return pages;
    }

    internal void EnsureEditorLoaded(TabPage page)
    {
        if (page.Name == "Winches") EnsureLoaded();
        else if (page.Name == "Engines") _engineEditTab.EnsureLoaded();
        else if (page.Name == "Addons") _addonsTab.EnsureLoaded();
        else if (page.Name == "Frame addons") _frameAddonsTab.EnsureLoaded();
    }

    internal void ApplyFrameAddonFuelChanges(string workspace)
    {
        FrameAddonFuelStore.Apply(workspace);
        _frameAddonsTab.InvalidateCatalog();
        _addonsTab.InvalidateCatalog();
    }

    private Control CreatePlaceholder(string text)
    {
        return new Label { Dock = DockStyle.Fill, Text = text, TextAlign = ContentAlignment.MiddleCenter, ForeColor = SystemColors.GrayText };
    }

    private static DataGridView CreatePropertiesGrid() => new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeColumns = false,
        AllowUserToResizeRows = false,
        ReadOnly = true,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        ScrollBars = ScrollBars.Vertical,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f),
        ColumnHeadersDefaultCellStyle = { Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f, FontStyle.Bold) },
        DefaultCellStyle = { BackColor = AppColors.Get("Surface"), SelectionBackColor = AppColors.Get("Surface"), SelectionForeColor = SystemColors.ControlText },
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
        if (_confirmPresetButton is not null) _confirmPresetButton.Enabled = false;
        _winchGrid.Rows.Clear();
        _winchGrid.Columns.Clear();
        _winchClasses.Clear();
        _customWinchPresets = WinchCustomPresetCollection.Empty;
        RefreshCustomWinchPresetList();

        if (!WorkspaceOriginals.EnumerateCurrentFiles(workspace, "[media]", "*.xml").Any())
        {
            _winchStatus.Text = "Unpack a workspace to load truck and winch definitions.";
            return;
        }

        try
        {
            var prepared = (PreparedWinchData)WorkspaceDataCache.GetWinchData(workspace);
            _winchClasses = prepared.WinchClasses;
            _matrixWinchClasses = prepared.MatrixWinchClasses;
            _stagedWinchChanges = PendingWinchChangesStore.Load(workspace);
            _customWinchPresets = WinchCustomPresetStore.Load(workspace);
            RefreshCustomWinchPresetList();
            _updatedDefinitions.Clear();
            _stagedDefinitions.Clear();
            foreach (var winchClass in _winchClasses)
                foreach (var definition in winchClass.Definitions)
                    _updatedDefinitions[DefinitionKey(winchClass.Code, definition.Name)] = definition;
            foreach (var staged in _stagedWinchChanges.Definitions)
            {
                var key = DefinitionKey(staged.ClassCode, staged.WinchName);
                if (!_updatedDefinitions.TryGetValue(key, out var current)) continue;
                var definition = new WinchDefinition(staged.WinchName, staged.UiNameKey, staged.UiName,
                    string.IsNullOrWhiteSpace(staged.UiDescriptionKey) ? current.UiDescriptionKey : staged.UiDescriptionKey,
                    string.IsNullOrWhiteSpace(staged.UiDescriptionKey) ? current.Description : staged.Description,
                    staged.Length, staged.StrengthMultiplier, staged.IgnitionRequired, staged.UnlockByRank);
                _updatedDefinitions[key] = definition;
                _stagedDefinitions[key] = definition;
            }
            ConfigureWinchColumns(_winchGrid);
            ConfigureWinchPropertiesColumns();
            PopulateWinchProperties();
            RefreshOwnedTrucks();
            UpdateWinchPropertyStageButton();
            _winchStatus.Text = $"Loaded {_winchClasses.Count} winch classes. Stage edits here, then apply pending PAK edits from Start.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _winchStatus.Text = $"Could not load winch data: {ex.Message}";
        }
    }

    public void WorkspaceUpdated()
    {
        _loadedWorkspace = null;
        if (Visible)
            EnsureLoaded();
        _engineEditTab.WorkspaceUpdated();
    }

    internal static object PrepareWinchData(string workspace, IReadOnlyDictionary<string, string> uiNames)
    {
        var classes = LoadWinchClasses(workspace, uiNames);
        var matrixClasses = classes.Where(item => !string.Equals(item.Code, "winches_default", StringComparison.OrdinalIgnoreCase)).ToList();
        return new PreparedWinchData(classes, matrixClasses);
    }

    private sealed record PreparedWinchData(List<WinchClass> WinchClasses, List<WinchClass> MatrixWinchClasses);
    private static List<WinchClass> LoadWinchClasses(string workspace, IReadOnlyDictionary<string, string> uiNames)
    {
        var originalStringsPath = WorkspaceOriginals.GetOriginalReadPath(workspace, Path.Combine("[strings]", "strings_english.str"));
        var originalUiNames = File.Exists(originalStringsPath) ? LocalizedStrings.Load(originalStringsPath) : uiNames;
        var classes = new Dictionary<string, WinchClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in WorkspaceOriginals.EnumerateCurrentFiles(workspace, Path.Combine("[media]", "classes", "winches"), "*.xml")
                     .Concat(WorkspaceOriginals.EnumerateCurrentFiles(workspace, Path.Combine("[media]", "_dlc"), "*.xml"))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(Directory.GetParent(file)?.Name, "winches", StringComparison.OrdinalIgnoreCase)) continue;
                var code = Path.GetFileNameWithoutExtension(file);
                var xml = WorkspaceDataCache.ReadAllText(file, Encoding.UTF8);
                var winchNames = Regex.Matches(xml, @"<Winch\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase)
                    .Cast<Match>()
                    .Select(match => GetAttribute(match.Groups["attrs"].Value, "Name"))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (winchNames.Length == 0) continue;
                var display = code.StartsWith("winches_", StringComparison.OrdinalIgnoreCase) ? code[8..] : code;
                display = string.Join(" ", display.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
                display += " winches";
                var definitions = ReadWinchDefinitions(xml, uiNames);
                var relativePath = WorkspaceOriginals.GetRelativePath(workspace, file);
                var originalPath = WorkspaceOriginals.GetOriginalPath(workspace, relativePath);
                originalPath = WorkspaceOriginals.GetOriginalReadPath(workspace, relativePath);
                var originalDefinitions = ReadWinchDefinitions(File.Exists(originalPath) ? WorkspaceDataCache.ReadAllText(originalPath, Encoding.UTF8) : xml, originalUiNames);
                classes.TryAdd(code, new WinchClass(code, display, file, winchNames[0], winchNames, definitions, originalDefinitions));
            }
        return classes.Values
            .OrderBy(item => item.Code.Contains("actaeon", StringComparison.OrdinalIgnoreCase) || item.DisplayName.Contains("actaeon", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<WinchDefinition> ReadWinchDefinitions(string xml, IReadOnlyDictionary<string, string> uiNames)
    {
        var definitions = new List<WinchDefinition>();
        foreach (Match definition in WinchDefinitionTag.Matches(xml))
        {
            var name = GetAttribute(definition.Groups["attrs"].Value, "Name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var attrs = definition.Groups["attrs"].Value;
            var body = definition.Groups["body"].Value;
            string Value(string attr) => GetAttribute(attrs, attr);
            var uiNameKey = Value("UiName");
            var uiDescriptionKey = string.Empty;
            foreach (Match element in XmlElementTag.Matches(body))
            {
                if (!string.Equals(element.Groups["tag"].Value, "UiDesc", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(uiNameKey)) uiNameKey = GetAttribute(element.Groups["attrs"].Value, "UiName");
                uiDescriptionKey = GetAttribute(element.Groups["attrs"].Value, "UiDesc");
                if (!string.IsNullOrWhiteSpace(uiNameKey) && !string.IsNullOrWhiteSpace(uiDescriptionKey)) break;
            }
            var uiName = !string.IsNullOrWhiteSpace(uiNameKey) && uiNames.TryGetValue(uiNameKey, out var localizedName)
                ? localizedName
                : ReadableVariant(name);
            var description = !string.IsNullOrWhiteSpace(uiDescriptionKey) && uiNames.TryGetValue(uiDescriptionKey, out var localizedDescription)
                ? localizedDescription
                : System.Net.WebUtility.HtmlDecode(uiDescriptionKey);
            if (!string.IsNullOrWhiteSpace(uiDescriptionKey) && !uiNames.ContainsKey(uiDescriptionKey))
                uiDescriptionKey = string.Empty;
            string IgnitionRequired()
            {
                var value = Value("IsEngineIgnitionRequired");
                if (!string.IsNullOrWhiteSpace(value)) return value;
                var element = Regex.Match(body, @"<IsEngineIgnitionRequired\b[^>]*>(?<value>[^<]*)</IsEngineIgnitionRequired\s*>", RegexOptions.IgnoreCase);
                return element.Success ? element.Groups["value"].Value.Trim() : "—";
            }
            var unlock = Value("UnlockByRank");
            if (string.IsNullOrWhiteSpace(unlock))
            {
                // UnlockByRank is stored on the nested <GameData> element, not the <Winch> element.
                foreach (Match element in XmlElementTag.Matches(body))
                {
                    if (!string.Equals(element.Groups["tag"].Value, "GameData", StringComparison.OrdinalIgnoreCase)) continue;
                    unlock = GetAttribute(element.Groups["attrs"].Value, "UnlockByRank");
                    if (!string.IsNullOrWhiteSpace(unlock)) break;
                }
            }
            definitions.Add(new WinchDefinition(
                name,
                uiNameKey,
                uiName,
                uiDescriptionKey,
                description,
                DisplayValue(Value("Length")),
                DisplayValue(Value("StrengthMult")),
                DisplayValue(IgnitionRequired()),
                DisplayValue(unlock)));
        }
        return definitions;
    }

    private void ConfigureWinchColumns(DataGridView grid)
    {
        grid.Columns.Clear();
        grid.Rows.Clear();
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "TruckName",
            HeaderText = "Truck",
            ReadOnly = true,
            Frozen = true,
            Width = 220,
            MinimumWidth = 160,
            SortMode = DataGridViewColumnSortMode.Programmatic
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "TruckClass",
            HeaderText = "Truck class",
            ReadOnly = true,
            Width = 115,
            MinimumWidth = 90,
            SortMode = DataGridViewColumnSortMode.Programmatic
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "TruckFile",
            HeaderText = "File",
            Width = 2,
            MinimumWidth = 2,
            Visible = false,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        foreach (var winch in _matrixWinchClasses)
        {
            var column = new DataGridViewCheckBoxColumn
            {
                Name = winch.Code,
                HeaderText = winch.DisplayName,
                Width = 120,
                MinimumWidth = 90,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                ThreeState = false,
                ReadOnly = false
            };
            column.HeaderCell.ToolTipText = winch.Code;
            grid.Columns.Add(column);
            _winchHeaders.AddFilter(winch.Code, value => value is true ? "Compatible" : "Not compatible");
        }
    }

    private void ConfigureWinchPropertiesColumns()
    {
        ConfigurePropertiesGridColumns(_winchPropertiesGrid, editable: false);
        ConfigurePropertiesGridColumns(_updatedWinchPropertiesGrid, editable: true);
    }

    private static void ConfigurePropertiesGridColumns(DataGridView grid, bool editable)
    {
        grid.Columns.Clear();
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "WinchType", HeaderText = "Winch type", FillWeight = 20, MinimumWidth = 110, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Winch", HeaderText = "Winch", FillWeight = 24, MinimumWidth = 130, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = !editable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Description", HeaderText = "Description", FillWeight = 12, MinimumWidth = 95, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = !editable, Visible = editable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Length", HeaderText = "Length", FillWeight = 12, MinimumWidth = 70, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = !editable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "StrengthMultiplier", HeaderText = "Strength multiplier", FillWeight = 18, MinimumWidth = 105, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = !editable });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "IgnitionRequired", HeaderText = "Is ignition required", FillWeight = 16, MinimumWidth = 115, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = !editable, ThreeState = false, TrueValue = true, FalseValue = false, ValueType = typeof(bool) });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "UnlockByRank", HeaderText = "Rank", FillWeight = 10, MinimumWidth = 65, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = !editable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "WinchFile", HeaderText = "File", FillWeight = 8, MinimumWidth = 42, Width = 42, SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = true });
        grid.ReadOnly = !editable;
    }

    private void PopulateWinchProperties()
    {
        _winchPropertiesGrid.Rows.Clear();
        _updatedWinchPropertiesGrid.Rows.Clear();
        foreach (var winchClass in _winchClasses)
        {
            if (string.Equals(winchClass.Code, "winches_default", StringComparison.OrdinalIgnoreCase)) continue;
            AddWinchGroupRow(_winchPropertiesGrid, winchClass);
            AddWinchGroupRow(_updatedWinchPropertiesGrid, winchClass);
            foreach (var winch in winchClass.OriginalDefinitions)
                AddWinchDefinitionRow(_winchPropertiesGrid, winchClass, winch);
            foreach (var winch in winchClass.Definitions)
                AddWinchDefinitionRow(_updatedWinchPropertiesGrid, winchClass, _updatedDefinitions[DefinitionKey(winchClass.Code, winch.Name)]);
        }
    }

    private static void AddWinchGroupRow(DataGridView grid, WinchClass winchClass)
    {
        var groupRowIndex = grid.Rows.Add(winchClass.DisplayName, string.Empty, string.Empty, string.Empty, string.Empty, false, string.Empty, string.Empty);
        var groupRow = grid.Rows[groupRowIndex];
        groupRow.Cells["WinchFile"] = new DataGridViewTextBoxCell { Value = string.Empty };
        foreach (DataGridViewCell cell in groupRow.Cells) cell.ReadOnly = true;
        groupRow.Tag = winchClass.Code;
        groupRow.DefaultCellStyle.BackColor = AppColors.Get("GroupRow");
        groupRow.DefaultCellStyle.SelectionBackColor = groupRow.DefaultCellStyle.BackColor;
        groupRow.DefaultCellStyle.Font = new Font(grid.Font, FontStyle.Bold);
    }

    private void AddWinchDefinitionRow(DataGridView grid, WinchClass winchClass, WinchDefinition winch)
    {
        var ignitionRequired = bool.TryParse(winch.IgnitionRequired, out var required) && required;
        var rowIndex = grid.Rows.Add(string.Empty, winch.UiName, winch.Description, winch.Length, winch.StrengthMultiplier, ignitionRequired, winch.UnlockByRank, "Open");
        var row = grid.Rows[rowIndex];
        row.Tag = new WinchTableRow(winchClass.Code, winchClass.FilePath, winch.Name);
        row.Cells["Winch"].ToolTipText = $"{winch.Name}\nUI key: {winch.UiNameKey}";
        row.Cells["Winch"].ReadOnly = grid == _winchPropertiesGrid || string.IsNullOrWhiteSpace(winch.UiNameKey);
        row.Cells["Description"].ToolTipText = winch.Description;
        row.Cells["Description"].ReadOnly = grid == _winchPropertiesGrid;
        row.Cells["WinchFile"].ToolTipText = "Open the raw file";
    }

    private void RefreshOwnedTrucks()
    {
        if (_winchGrid.Columns.Count == 0 || IsDisposed) return;
        var workspace = _getWorkspacePath().Trim();
        var truckFiles = FindTruckFiles(workspace);
        _updatingGrid = true;
        _saveWinchCompatibilityButton.Enabled = false;
        _winchGrid.Rows.Clear();
        foreach (var truck in _ownedContent.GetOwnedTrucks().OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!truckFiles.TryGetValue(truck.Code, out var path)) continue;
            var xml = WorkspaceDataCache.ReadAllText(path, Encoding.UTF8);
            var socket = WinchSocket.Match(xml);
            var socketAttributes = socket.Success ? socket.Value : string.Empty;
            var typeList = ReadWinchTypes(xml);
            var relativePath = WorkspaceOriginals.GetRelativePath(workspace, path);
            var originalPath = WorkspaceOriginals.GetOriginalPath(workspace, relativePath);
            originalPath = WorkspaceOriginals.GetOriginalReadPath(workspace, relativePath);
            var originalXml = File.Exists(originalPath) ? WorkspaceDataCache.ReadAllText(originalPath, Encoding.UTF8) : xml;
            var originalTypes = ReadWinchTypes(originalXml);
            var truckData = TruckDataTag.Match(xml);
            var truckClass = GetAttribute(truckData.Success ? truckData.Groups["attrs"].Value : string.Empty, "TruckType");
            truckClass = TruckClasses.ForTruck(truck.Code, truckClass);
            var relativeTruckFile = WorkspaceOriginals.GetRelativePath(workspace, path);
            var stagedTruck = _stagedWinchChanges.Trucks.FirstOrDefault(change => string.Equals(change.RelativeFile, relativeTruckFile, StringComparison.OrdinalIgnoreCase));
            var displayedTypes = stagedTruck is null
                ? typeList
                : stagedTruck.Types.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rowValues = new object[] { truck.Name, string.IsNullOrWhiteSpace(truckClass) ? "—" : truckClass, "Open" }
                .Concat(_matrixWinchClasses.Select(winch => (object)displayedTypes.Contains(winch.Code))).ToArray();
            var rowData = new TruckRow(path, truck.Name, socket.Success, displayedTypes, originalTypes, GetAttribute(socketAttributes, "Default"))
            {
                PersistedTypes = new HashSet<string>(typeList, StringComparer.OrdinalIgnoreCase),
                StagedTypes = new HashSet<string>(displayedTypes, StringComparer.OrdinalIgnoreCase)
            };
            var index = _winchGrid.Rows.Add(rowValues);
            var row = _winchGrid.Rows[index];
            row.Tag = rowData;
            row.Cells["TruckName"].ToolTipText = $"Open the raw file\nTruck key: {truck.Code}";
            for (var column = 3; column < row.Cells.Count; column++) row.Cells[column].ReadOnly = !socket.Success;
            UpdateCompatibilityCellStyles(row);
        }
        _updatingGrid = false;
        _winchHeaders.Refresh();
        UpdateSaveCompatibilityButton();
        UpdateWinchVisibleCount();
    }

    private void UpdateWinchVisibleCount() => _winchStatus.Text = $"Showing {_winchGrid.Rows.Cast<DataGridViewRow>().Count(row => row.Visible)} of {_winchGrid.Rows.Count} owned trucks. Green cells add and red cells remove winch classes compared with the original files.";

    private static HashSet<string> ReadWinchTypes(string xml)
    {
        var socket = WinchSocket.Match(xml);
        return GetAttribute(socket.Success ? socket.Value : string.Empty, "Type")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeWinchType)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void UpdateCompatibilityCellStyles(DataGridViewRow row)
    {
        if (row.Tag is not TruckRow data) return;
        foreach (var winch in _matrixWinchClasses)
        {
            var cell = row.Cells[winch.Code];
            var isSelected = data.Types.Contains(winch.Code);
            var wasSelected = data.OriginalTypes.Contains(winch.Code);
            var color = isSelected == wasSelected
                ? AppColors.Get("Surface")
                : isSelected ? AppColors.Get("ChangedPositive") : AppColors.Get("ChangedNegative");
            cell.Style.BackColor = color;
            cell.Style.SelectionBackColor = color;
            cell.Style.ForeColor = SystemColors.ControlText;
            cell.Style.SelectionForeColor = SystemColors.ControlText;
        }
    }

    private void UpdateSaveCompatibilityButton()
    {
        _saveWinchCompatibilityButton.Enabled = _winchGrid.Rows
            .Cast<DataGridViewRow>()
            .Select(row => row.Tag)
            .OfType<TruckRow>()
            .Any(row => !row.Types.SetEquals(row.StagedTypes));
    }

    public IEnumerable<WorkspaceChange> PendingChanges(string workspace)
    {
        foreach (var change in _engineEditTab.PendingChanges(workspace)) yield return change;
        if (!string.Equals(_loadedWorkspace, workspace, StringComparison.OrdinalIgnoreCase)) EnsureLoaded();
        if (!string.Equals(_loadedWorkspace, workspace, StringComparison.OrdinalIgnoreCase)) yield break;
        foreach (var row in _winchGrid.Rows.Cast<DataGridViewRow>().Select(row => row.Tag).OfType<TruckRow>().Where(row => !row.Types.SetEquals(row.PersistedTypes)))
            yield return new(WorkspaceChange.Pending, row.FilePath, row.Name + " / Winch compatibility", string.Join(", ", row.PersistedTypes.Order()), string.Join(", ", row.Types.Order()));
        var displayedUiKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var winchClass in _winchClasses)
            foreach (var original in winchClass.Definitions)
                if (_updatedDefinitions.TryGetValue(DefinitionKey(winchClass.Code, original.Name), out var updated) && original != updated)
                {
                    var summaryGroup = "winch:" + winchClass.Code + "/" + original.Name;
                    foreach (var field in new[] { ("Length", original.Length, updated.Length), ("StrengthMultiplier", original.StrengthMultiplier, updated.StrengthMultiplier),
                                 ("IgnitionRequired", original.IgnitionRequired, updated.IgnitionRequired), ("UnlockByRank", original.UnlockByRank, updated.UnlockByRank) })
                        if (field.Item2 != field.Item3) yield return new(WorkspaceChange.Pending, winchClass.FilePath, original.UiName + " / " + field.Item1, field.Item2, field.Item3,
                            SummaryGroup: summaryGroup, SummaryName: original.UiName);
                    if (original.UiName != updated.UiName && displayedUiKeys.Add(original.UiNameKey))
                        yield return new(WorkspaceChange.Pending, WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[strings]", "strings_english.str")),
                            original.UiNameKey + " / Winch display name", original.UiName, updated.UiName,
                            SummaryGroup: summaryGroup, SummaryName: original.UiName);
                    var descriptionKey = string.IsNullOrWhiteSpace(original.UiDescriptionKey)
                        ? DefinitionKey(winchClass.Code, original.Name)
                        : original.UiDescriptionKey;
                    if (original.Description != updated.Description && displayedUiKeys.Add(descriptionKey))
                        yield return new(WorkspaceChange.Pending, winchClass.FilePath,
                            original.Name + " / Winch description", original.Description, updated.Description,
                            SummaryGroup: summaryGroup, SummaryName: original.UiName);
                }
    }

    public void SavePendingChanges()
    {
        _engineEditTab.SavePendingChanges(); _winchGrid.EndEdit(); _updatedWinchPropertiesGrid.EndEdit();
        if (!StageWinchCompatibility(out var compatibilityError)) throw new InvalidOperationException(compatibilityError);
        if (!StageWinchProperties(out var propertyError)) throw new InvalidOperationException(propertyError);
        ApplyStagedWinchChanges();
    }

    private void SaveWinchCompatibility(object? sender, EventArgs e)
    {
        if (!StageWinchCompatibility(out var error))
        {
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
    }

    private bool StageWinchCompatibility(out string error)
    {
        try
        {
            var stagedTrucks = _stagedWinchChanges.Trucks.ToDictionary(item => item.RelativeFile, StringComparer.OrdinalIgnoreCase);
            foreach (var row in _winchGrid.Rows.Cast<DataGridViewRow>().Select(item => item.Tag).OfType<TruckRow>())
            {
                var relativeFile = WorkspaceOriginals.GetRelativePath(_getWorkspacePath().Trim(), row.FilePath);
                if (row.Types.SetEquals(row.PersistedTypes)) stagedTrucks.Remove(relativeFile);
                else stagedTrucks[relativeFile] = new PendingWinchTruckChange(relativeFile, row.Name, row.Types.Order(StringComparer.OrdinalIgnoreCase).ToArray());
            }
            _stagedWinchChanges = _stagedWinchChanges with { Trucks = stagedTrucks.Values.ToList() };
            PendingWinchChangesStore.Save(_getWorkspacePath().Trim(), _stagedWinchChanges);
            foreach (var row in _winchGrid.Rows.Cast<DataGridViewRow>().Select(item => item.Tag).OfType<TruckRow>())
                row.StagedTypes = new HashSet<string>(row.Types, StringComparer.OrdinalIgnoreCase);
            UpdateSaveCompatibilityButton();
            error = string.Empty;
            _winchStatus.Text = $"Staged winch compatibility changes. Apply pending changes from Start to write them to the workspace.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            UpdateSaveCompatibilityButton();
            error = $"Could not stage winch compatibility changes: {ex.Message}";
            return false;
        }
    }

    private void WinchGridCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 0 || _winchGrid.Rows[e.RowIndex].Tag is not TruckRow) return;
        e.PaintBackground(e.CellBounds, true);
        e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
        var button = new Rectangle(e.CellBounds.Right - 28, e.CellBounds.Top + 2, 25, e.CellBounds.Height - 4);
        var text = new Rectangle(e.CellBounds.Left + 4, e.CellBounds.Top, button.Left - e.CellBounds.Left - 7, e.CellBounds.Height);
        TextRenderer.DrawText(e.Graphics, e.FormattedValue?.ToString() ?? string.Empty, e.CellStyle.Font, text, e.CellStyle.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        ButtonRenderer.DrawButton(e.Graphics, button, "…", e.CellStyle.Font, false, System.Windows.Forms.VisualStyles.PushButtonState.Normal);
        e.Handled = true;
    }

    private void WinchGridCellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != 0 || _winchGrid.Rows[e.RowIndex].Tag is not TruckRow row) return;
        var bounds = _winchGrid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
        if (e.X >= bounds.Width - 30) OpenRawFile(row.FilePath);
    }

    private void WinchPropertiesCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        var grid = (DataGridView)sender!;
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "WinchFile" || grid.Rows[e.RowIndex].Tag is not WinchTableRow) return;
        e.PaintBackground(e.CellBounds, true);
        e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
        var button = new Rectangle(e.CellBounds.Right - 28, e.CellBounds.Top + 2, 25, e.CellBounds.Height - 4);
        ButtonRenderer.DrawButton(e.Graphics, button, "…", e.CellStyle.Font, false, System.Windows.Forms.VisualStyles.PushButtonState.Normal);
        e.Handled = true;
    }

    private void WinchPropertiesCellMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        var grid = (DataGridView)sender!;
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || grid.Columns[e.ColumnIndex].Name != "WinchFile" || grid.Rows[e.RowIndex].Tag is not WinchTableRow row) return;
        var bounds = grid.GetCellDisplayRectangle(e.ColumnIndex, e.RowIndex, false);
        if (e.X >= bounds.Width - 30) OpenRawFile(row.FilePath);
    }

    private void UpdatedWinchPropertyValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_syncingWinchNames || e.RowIndex < 0 || e.ColumnIndex < 0 || _updatedWinchPropertiesGrid.Rows[e.RowIndex].Tag is not WinchTableRow row) return;
        var definitionKey = DefinitionKey(row.ClassCode, row.Name);
        if (!_updatedDefinitions.TryGetValue(definitionKey, out var current)) return;
        var cell = _updatedWinchPropertiesGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
        var text = Convert.ToString(cell.Value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        var columnName = _updatedWinchPropertiesGrid.Columns[e.ColumnIndex].Name;
        var updated = _updatedWinchPropertiesGrid.Columns[e.ColumnIndex].Name switch
        {
            "Winch" => current with { UiName = text },
            "Description" => current with { Description = text },
            "Length" => current with { Length = text },
            "StrengthMultiplier" => current with { StrengthMultiplier = text },
            "IgnitionRequired" => current with { IgnitionRequired = cell.Value is true ? "true" : "false" },
            "UnlockByRank" => current with { UnlockByRank = text },
            _ => current
        };
        if (columnName is "Winch" or "Description")
        {
            var isName = columnName == "Winch";
            var localizationKey = isName ? current.UiNameKey : current.UiDescriptionKey;
            if (isName && string.IsNullOrWhiteSpace(localizationKey)) return;
            if (!isName && string.IsNullOrWhiteSpace(localizationKey))
            {
                _updatedDefinitions[definitionKey] = updated;
            }
            else
            {
                _syncingWinchNames = true;
                try
                {
                    foreach (var winchClass in _winchClasses)
                    foreach (var definition in winchClass.Definitions.Where(item => string.Equals(isName ? item.UiNameKey : item.UiDescriptionKey, localizationKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        var key = DefinitionKey(winchClass.Code, definition.Name);
                        if (!_updatedDefinitions.TryGetValue(key, out var linked)) continue;
                        _updatedDefinitions[key] = isName ? linked with { UiName = text } : linked with { Description = text };
                        var matchingRow = _updatedWinchPropertiesGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(gridRow =>
                            gridRow.Tag is WinchTableRow tableRow && string.Equals(tableRow.ClassCode, winchClass.Code, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(tableRow.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
                        if (matchingRow is not null) matchingRow.Cells[columnName].Value = text;
                    }
                }
                finally { _syncingWinchNames = false; }
            }
        }
        else
            _updatedDefinitions[definitionKey] = updated;
        UpdateWinchPropertyStageButton();
        _winchStatus.Text = "Winch property edits are previewed. Stage them here, then apply pending PAK edits from Start.";
    }

    private void UpdateWinchPropertyStageButton()
    {
        if (_confirmPresetButton is null) return;
        _confirmPresetButton.Enabled = _winchClasses
            .Where(item => !string.Equals(item.Code, "winches_default", StringComparison.OrdinalIgnoreCase))
            .Any(winchClass => winchClass.Definitions.Any(definition =>
            {
                var key = DefinitionKey(winchClass.Code, definition.Name);
                var staged = _stagedDefinitions.GetValueOrDefault(key, definition);
                return _updatedDefinitions.TryGetValue(key, out var updated) && updated != staged;
            }));
    }

    private void ApplyGlobalWinchPreset(object? sender, EventArgs e)
    {
        var profiles = new Dictionary<string, WinchProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["default"] = new("8", "0.1", "1"),
            ["ext"] = new("17", "0.8", "10"),
            ["pow"] = new("17", "1.2", "1"),
            ["ext_pow"] = new("34", "2.2", "20"),
            ["offline"] = new("20", "0.8", "10")
        };
        var changes = new List<(WinchClass Class, string Name, WinchDefinition Updated)>();
        foreach (var winchClass in _winchClasses.Where(item => !string.Equals(item.Code, "winches_default", StringComparison.OrdinalIgnoreCase)))
        {
            changes.AddRange(winchClass.Definitions.Select(item =>
            {
                var current = _updatedDefinitions[DefinitionKey(winchClass.Code, item.Name)];
                var suffix = VariantSuffix(item.Name);
                var profile = suffix is not null && profiles.TryGetValue(suffix, out var matchingProfile)
                    ? matchingProfile
                    : profiles["default"];
                if (string.Equals(suffix, "offline", StringComparison.OrdinalIgnoreCase) &&
                    (winchClass.Code.Contains("actaeon", StringComparison.OrdinalIgnoreCase) || winchClass.DisplayName.Contains("actaeon", StringComparison.OrdinalIgnoreCase) || item.Name.Contains("actaeon", StringComparison.OrdinalIgnoreCase)))
                    profile = profile with { StrengthMultiplier = "0.9" };
                return (winchClass, item.Name, current with
                {
                    UiName = PresetWinchDisplayName(suffix) ?? current.UiName,
                    Description = PresetWinchDescription(suffix, profile.Length) ?? current.Description,
                    Length = profile.Length,
                    StrengthMultiplier = profile.StrengthMultiplier,
                    UnlockByRank = profile.UnlockByRank
                });
            }));
        }
        if (changes.Count == 0) return;
        try
        {
            foreach (var change in changes)
                _updatedDefinitions[DefinitionKey(change.Class.Code, change.Name)] = change.Updated;
            PopulateWinchProperties();
            UpdateWinchPropertyStageButton();
            _winchStatus.Text = $"Previewed the preset for {changes.Count} winch definitions. Stage changes here, then apply pending PAK edits from Start.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, $"Could not apply the preset to all winches: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResetWinchPropertiesToOriginal(object? sender, EventArgs e)
    {
        _updatedWinchPropertiesGrid.EndEdit();
        var resetCount = 0;
        foreach (var winchClass in _winchClasses.Where(item => !string.Equals(item.Code, "winches_default", StringComparison.OrdinalIgnoreCase)))
        foreach (var original in winchClass.OriginalDefinitions)
        {
            var key = DefinitionKey(winchClass.Code, original.Name);
            if (!_updatedDefinitions.ContainsKey(key)) continue;
            _updatedDefinitions[key] = original;
            resetCount++;
        }
        if (resetCount == 0) return;
        PopulateWinchProperties();
        UpdateWinchPropertyStageButton();
        _winchStatus.Text = $"Previewed original values for {resetCount} winch definitions. Stage changes here, then apply pending PAK edits from Start.";
    }

    private void SaveCustomWinchPreset(object? sender, EventArgs e)
    {
        _updatedWinchPropertiesGrid.EndEdit();
        var name = PromptForCustomWinchPresetName();
        if (string.IsNullOrWhiteSpace(name)) return;
        var existingIndex = _customWinchPresets.Presets.FindIndex(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0 && MessageBox.Show(this, $"A custom preset named '{name}' already exists. Replace it?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        var definitions = _winchClasses
            .Where(item => !string.Equals(item.Code, "winches_default", StringComparison.OrdinalIgnoreCase))
            .SelectMany(winchClass => winchClass.Definitions.Select(definition =>
            {
                var updated = _updatedDefinitions[DefinitionKey(winchClass.Code, definition.Name)];
                return new WinchCustomPresetDefinition(winchClass.Code, definition.Name, updated.UiName, updated.Description,
                    updated.Length, updated.StrengthMultiplier, updated.IgnitionRequired, updated.UnlockByRank);
            }))
            .ToList();
        if (definitions.Count == 0) return;

        var preset = new WinchCustomPreset(name, definitions);
        var presets = _customWinchPresets.Presets.ToList();
        if (existingIndex >= 0) presets[existingIndex] = preset;
        else presets.Add(preset);
        try
        {
            var collection = new WinchCustomPresetCollection(presets);
            WinchCustomPresetStore.Save(_getWorkspacePath().Trim(), collection);
            _customWinchPresets = collection;
            RefreshCustomWinchPresetList(preset.Name);
            _winchStatus.Text = $"Saved custom winch preset '{preset.Name}' for this workspace.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(this, $"Could not save the custom winch preset: {ex.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ApplyCustomWinchPreset(object? sender, EventArgs e)
    {
        if (_customWinchPresetCombo.SelectedItem is not WinchCustomPreset preset) return;
        _updatedWinchPropertiesGrid.EndEdit();
        var applied = 0;
        foreach (var saved in preset.Definitions)
        {
            var key = DefinitionKey(saved.ClassCode, saved.WinchName);
            if (!_updatedDefinitions.TryGetValue(key, out var current)) continue;
            _updatedDefinitions[key] = current with
            {
                UiName = saved.UiName,
                Description = saved.Description,
                Length = saved.Length,
                StrengthMultiplier = saved.StrengthMultiplier,
                IgnitionRequired = saved.IgnitionRequired,
                UnlockByRank = saved.UnlockByRank
            };
            applied++;
        }
        if (applied == 0)
        {
            _winchStatus.Text = $"Custom preset '{preset.Name}' has no winch definitions matching this workspace.";
            return;
        }
        PopulateWinchProperties();
        UpdateWinchPropertyStageButton();
        _winchStatus.Text = $"Previewed custom preset '{preset.Name}' for {applied} winch definitions. Stage changes here, then apply pending PAK edits from Start.";
    }

    private void RefreshCustomWinchPresetList(string? selectedName = null)
    {
        _customWinchPresetCombo.BeginUpdate();
        try
        {
            _customWinchPresetCombo.Items.Clear();
            foreach (var preset in _customWinchPresets.Presets.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
                _customWinchPresetCombo.Items.Add(preset);
            var selected = _customWinchPresetCombo.Items.Cast<WinchCustomPreset>()
                .FirstOrDefault(item => string.Equals(item.Name, selectedName, StringComparison.OrdinalIgnoreCase));
            if (selected is not null) _customWinchPresetCombo.SelectedItem = selected;
            else if (_customWinchPresetCombo.Items.Count > 0) _customWinchPresetCombo.SelectedIndex = 0;
        }
        finally { _customWinchPresetCombo.EndUpdate(); }
        _customWinchPresetCombo.Enabled = _customWinchPresetCombo.Items.Count > 0;
        _applyCustomWinchPresetButton.Enabled = _customWinchPresetCombo.Items.Count > 0;
    }

    private string? PromptForCustomWinchPresetName()
    {
        using var dialog = new Form
        {
            Text = "Save custom winch preset",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(380, 120)
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label { Text = "Preset name:", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 4) };
        var nameBox = new TextBox { Dock = DockStyle.Top };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(nameBox, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => nameBox.Focus();
        return dialog.ShowDialog(this) == DialogResult.OK ? nameBox.Text.Trim() : null;
    }

    private void ConfirmWinchPreset(object? sender, EventArgs e)
    {
        _updatedWinchPropertiesGrid.EndEdit();
        if (!StageWinchProperties(out var error))
        {
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private bool StageWinchProperties(out string error)
    {
        var pending = _winchClasses
            .Where(item => !string.Equals(item.Code, "winches_default", StringComparison.OrdinalIgnoreCase))
            .SelectMany(winchClass => winchClass.Definitions.Select(definition =>
                (Class: winchClass, Original: definition, Updated: _updatedDefinitions[DefinitionKey(winchClass.Code, definition.Name)])))
            .Where(item => item.Original != item.Updated)
            .ToList();
        var changedLocalizationKeys = pending
            .SelectMany(change => new[]
            {
                change.Original.UiName != change.Updated.UiName ? change.Updated.UiNameKey : string.Empty,
                change.Original.Description != change.Updated.Description ? change.Updated.UiDescriptionKey : string.Empty
            })
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (changedLocalizationKeys.Length > 0)
        {
            try
            {
                var stringsPath = WorkspaceOriginals.GetCurrentPath(_getWorkspacePath().Trim(), Path.Combine("[strings]", "strings_english.str"));
                if (!File.Exists(stringsPath))
                {
                    error = "The workspace English strings file is required to stage winch localization changes.";
                    return false;
                }
                var availableKeys = LocalizedStrings.Load(stringsPath);
                var missingKeys = changedLocalizationKeys.Where(key => !availableKeys.ContainsKey(key)).ToArray();
                if (missingKeys.Length > 0)
                {
                    error = "The existing localization key was not found in strings_english.str: " + string.Join(", ", missingKeys);
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                error = $"Could not read the workspace English strings file: {ex.Message}";
                return false;
            }
        }
        foreach (var change in pending)
        {
            var updated = change.Updated;
            if (!double.TryParse(updated.Length, NumberStyles.Float, CultureInfo.InvariantCulture, out var length) || length <= 0 ||
                !double.TryParse(updated.StrengthMultiplier, NumberStyles.Float, CultureInfo.InvariantCulture, out var strength) || strength <= 0 ||
                (updated.UnlockByRank != "—" && !string.IsNullOrWhiteSpace(updated.UnlockByRank) &&
                 (!int.TryParse(updated.UnlockByRank, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank) || rank < 1)))
            {
                error = $"Check the Length, Strength multiplier, and Rank values for '{updated.UiName}'. Length and strength must be positive numbers; Rank must be a positive whole number.";
                return false;
            }
            if (change.Original.UiName != updated.UiName && (string.IsNullOrWhiteSpace(updated.UiName) || string.IsNullOrWhiteSpace(updated.UiNameKey)))
            {
                error = $"A winch display name and its existing UI key are required for '{change.Original.Name}'.";
                return false;
            }
            if (change.Original.Description != updated.Description && string.IsNullOrWhiteSpace(updated.Description))
            {
                error = $"A winch description is required for '{change.Original.Name}'.";
                return false;
            }
            if (change.Original.Description != updated.Description && string.IsNullOrWhiteSpace(updated.UiDescriptionKey))
            {
                error = $"An existing localization key is required for the description of '{change.Original.Name}'.";
                return false;
            }
        }
        try
        {
            var staged = _stagedWinchChanges.Definitions.ToDictionary(item => DefinitionKey(item.ClassCode, item.WinchName), StringComparer.OrdinalIgnoreCase);
            foreach (var winchClass in _winchClasses.Where(item => !string.Equals(item.Code, "winches_default", StringComparison.OrdinalIgnoreCase)))
            foreach (var original in winchClass.Definitions)
            {
                var key = DefinitionKey(winchClass.Code, original.Name);
                var updated = _updatedDefinitions[key];
                if (updated == original) staged.Remove(key);
                else staged[key] = ToPendingDefinition(winchClass.Code, updated);
            }
            _stagedWinchChanges = _stagedWinchChanges with { Definitions = staged.Values.ToList() };
            PendingWinchChangesStore.Save(_getWorkspacePath().Trim(), _stagedWinchChanges);
            _stagedDefinitions.Clear();
            foreach (var change in _stagedWinchChanges.Definitions)
                _stagedDefinitions[DefinitionKey(change.ClassCode, change.WinchName)] = FromPendingDefinition(change);
            UpdateWinchPropertyStageButton();
            error = string.Empty;
            _winchStatus.Text = pending.Count == 0
                ? "There are no unstaged winch property edits. Existing staged changes remain pending in Start."
                : $"Staged property changes for {pending.Count} winch definition{(pending.Count == 1 ? string.Empty : "s")}. Apply pending changes from Start to write them to the workspace.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            error = $"Could not stage winch property changes: {ex.Message}";
            return false;
        }
    }

    private static PendingWinchDefinitionChange ToPendingDefinition(string classCode, WinchDefinition definition) =>
        new(classCode, definition.Name, definition.UiNameKey, definition.UiName, definition.Length,
            definition.StrengthMultiplier, definition.IgnitionRequired, definition.UnlockByRank,
            definition.UiDescriptionKey, definition.Description);

    private static WinchDefinition FromPendingDefinition(PendingWinchDefinitionChange definition) =>
        new(definition.WinchName, definition.UiNameKey, definition.UiName,
            definition.UiDescriptionKey, definition.Description, definition.Length,
            definition.StrengthMultiplier, definition.IgnitionRequired, definition.UnlockByRank);

    private void ApplyStagedWinchChanges()
    {
        var workspace = _getWorkspacePath().Trim();
        _stagedWinchChanges = PendingWinchChangesStore.Load(workspace);
        if (_stagedWinchChanges.Trucks.Count == 0 && _stagedWinchChanges.Definitions.Count == 0) return;

        var stagedDefinitions = _stagedWinchChanges.Definitions.ToDictionary(
            item => DefinitionKey(item.ClassCode, item.WinchName), FromPendingDefinition, StringComparer.OrdinalIgnoreCase);
        var currentDefinitions = _winchClasses.SelectMany(winchClass => winchClass.Definitions
                .Select(definition => (Key: DefinitionKey(winchClass.Code, definition.Name), Definition: definition)))
            .ToDictionary(item => item.Key, item => item.Definition, StringComparer.OrdinalIgnoreCase);
        var unknownDefinitions = stagedDefinitions.Keys.Where(key => !currentDefinitions.ContainsKey(key)).ToArray();
        if (unknownDefinitions.Length > 0)
            throw new InvalidDataException("Some staged winch definitions are no longer present in the workspace. Reload the workspace and review the pending changes.");
        foreach (var group in _winchClasses
                     .Where(winchClass => winchClass.Definitions.Any(definition => stagedDefinitions.ContainsKey(DefinitionKey(winchClass.Code, definition.Name))))
                     .Select(winchClass => (Class: winchClass, Changes: winchClass.Definitions
                         .Where(definition => stagedDefinitions.ContainsKey(DefinitionKey(winchClass.Code, definition.Name)))
                         .Select(definition => (Original: definition, Updated: stagedDefinitions[DefinitionKey(winchClass.Code, definition.Name)]))
                         .Where(change => change.Original.Length != change.Updated.Length || change.Original.StrengthMultiplier != change.Updated.StrengthMultiplier
                             || change.Original.IgnitionRequired != change.Updated.IgnitionRequired || change.Original.UnlockByRank != change.Updated.UnlockByRank
                             || change.Original.Description != change.Updated.Description)
                         .Select(change => (change.Original.Name, change.Original, change.Updated)).ToArray()))
                     .Where(item => item.Changes.Length > 0)
                     .GroupBy(item => item.Class.FilePath, StringComparer.OrdinalIgnoreCase))
            WriteWinchDefinitions(group.Key, group.SelectMany(item => item.Changes).ToArray());

        var truckRows = _winchGrid.Rows.Cast<DataGridViewRow>().Select(item => item.Tag).OfType<TruckRow>()
            .ToDictionary(row => WorkspaceOriginals.GetRelativePath(workspace, row.FilePath), StringComparer.OrdinalIgnoreCase);
        foreach (var change in _stagedWinchChanges.Trucks)
        {
            if (!truckRows.TryGetValue(change.RelativeFile, out var row))
            {
                var file = WorkspaceOriginals.GetCurrentPath(workspace, change.RelativeFile);
                if (!File.Exists(file)) throw new InvalidDataException($"Truck '{change.TruckName}' is no longer present in the workspace.");
                var xml = WorkspaceDataCache.ReadAllText(file, Encoding.UTF8);
                var socket = WinchSocket.Match(xml);
                row = new TruckRow(file, change.TruckName, socket.Success, ReadWinchTypes(xml), ReadWinchTypes(xml), GetAttribute(socket.Success ? socket.Value : string.Empty, "Default"));
            }
            row.Types = change.Types.ToHashSet(StringComparer.OrdinalIgnoreCase);
            UpdateTruckXml(row, row.Types, _winchClasses);
            row.PersistedTypes = new HashSet<string>(row.Types, StringComparer.OrdinalIgnoreCase);
            row.StagedTypes = new HashSet<string>(row.Types, StringComparer.OrdinalIgnoreCase);
            row.DefaultWinch = GetCurrentDefault(row.FilePath);
        }

        var localizedNames = stagedDefinitions
            .Where(pair => currentDefinitions.TryGetValue(pair.Key, out var current)
                && current.UiName != pair.Value.UiName && !string.IsNullOrWhiteSpace(pair.Value.UiNameKey))
            .Select(pair => (Key: pair.Value.UiNameKey, Value: pair.Value.UiName))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Value).Distinct(StringComparer.Ordinal).First(), StringComparer.OrdinalIgnoreCase);
        var localizedDescriptions = stagedDefinitions
            .Where(pair => currentDefinitions.TryGetValue(pair.Key, out var current)
                && current.Description != pair.Value.Description && !string.IsNullOrWhiteSpace(pair.Value.UiDescriptionKey))
            .Select(pair => (Key: pair.Value.UiDescriptionKey, Value: pair.Value.Description))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Value).Distinct(StringComparer.Ordinal).First(), StringComparer.OrdinalIgnoreCase);
        var localizedStrings = localizedNames.Concat(localizedDescriptions)
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Value).Distinct(StringComparer.Ordinal).First(), StringComparer.OrdinalIgnoreCase);
        if (localizedStrings.Count > 0) WriteLocalizedWinchStrings(workspace, localizedStrings);

        _winchClasses = _winchClasses.Select(winchClass => winchClass with
        {
            Definitions = winchClass.Definitions.Select(definition =>
            {
                var updated = stagedDefinitions.GetValueOrDefault(DefinitionKey(winchClass.Code, definition.Name), definition);
                return updated;
            }).ToList()
        }).ToList();
        var classesByCode = _winchClasses.ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);
        _matrixWinchClasses = _matrixWinchClasses.Select(item => classesByCode[item.Code]).ToList();
        _updatedDefinitions.Clear();
        _stagedDefinitions.Clear();
        foreach (var winchClass in _winchClasses)
            foreach (var definition in winchClass.Definitions)
                _updatedDefinitions[DefinitionKey(winchClass.Code, definition.Name)] = definition;
        _stagedWinchChanges = PendingWinchChanges.Empty;
        PendingWinchChangesStore.Save(workspace, _stagedWinchChanges);
        PopulateWinchProperties();
        UpdateSaveCompatibilityButton();
        UpdateWinchPropertyStageButton();
        _winchStatus.Text = "Applied staged winch changes to the workspace updated files. Review the file changes on Start.";
    }

    private static void WriteLocalizedWinchStrings(string workspace, IReadOnlyDictionary<string, string> strings)
    {
        var relativePath = Path.Combine("[strings]", "strings_english.str");
        var source = WorkspaceOriginals.GetCurrentPath(workspace, relativePath);
        if (!File.Exists(source)) throw new FileNotFoundException("The workspace English strings file is required to apply winch localization changes.", source);
        var path = WorkspaceOriginals.PrepareForEdit(workspace, source);
        var bytes = File.ReadAllBytes(path);
        var (encoding, preamble) = DetectTextEncoding(bytes);
        var hasBom = preamble.Length > 0;
        var text = encoding.GetString(bytes, hasBom ? preamble.Length : 0, bytes.Length - (hasBom ? preamble.Length : 0));
        foreach (var (key, value) in strings)
        {
            var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
            var lines = text.Split('\n');
            var replaced = false;
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                var tokenStart = 0;
                while (tokenStart < line.Length && char.IsWhiteSpace(line[tokenStart])) tokenStart++;
                var tokenEnd = tokenStart;
                while (tokenEnd < line.Length && !char.IsWhiteSpace(line[tokenEnd])) tokenEnd++;
                if (!string.Equals(line[tokenStart..tokenEnd], key, StringComparison.OrdinalIgnoreCase)) continue;

                var valueStart = tokenEnd;
                while (valueStart < line.Length && char.IsWhiteSpace(line[valueStart])) valueStart++;
                if (valueStart >= line.Length || line[valueStart] != '"') continue;
                var valueEnd = valueStart + 1;
                while (valueEnd < line.Length)
                {
                    if (line[valueEnd] == '\\') { valueEnd += 2; continue; }
                    if (line[valueEnd] == '"') break;
                    valueEnd++;
                }
                if (valueEnd >= line.Length) continue;
                lines[lineIndex] = line[..(valueStart + 1)] + escaped + line[valueEnd..];
                replaced = true;
            }
            if (!replaced) throw new InvalidDataException($"The existing localization key '{key}' was not found in strings_english.str.");
            text = string.Join("\n", lines);
        }
        var output = encoding.GetBytes(text);
        if (hasBom) output = preamble.Concat(output).ToArray();
        File.WriteAllBytes(path, output);
        WorkspaceDataCache.InvalidateFile(path);
        WorkspaceDataCache.RefreshLocalizedNames(workspace);
    }

    private static (Encoding Encoding, byte[] Preamble) DetectTextEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())) return (new UTF8Encoding(false), Encoding.UTF8.GetPreamble());
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble())) return (Encoding.Unicode, Encoding.Unicode.GetPreamble());
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble())) return (Encoding.BigEndianUnicode, Encoding.BigEndianUnicode.GetPreamble());
        return (new UTF8Encoding(false), Array.Empty<byte>());
    }

    private void WriteWinchDefinitions(string path, IReadOnlyList<(string Name, WinchDefinition Original, WinchDefinition Updated)> definitions)
    {
        path = WorkspaceOriginals.PrepareForEdit(_getWorkspacePath(), path);
        var bytes = File.ReadAllBytes(path);
        var bom = Encoding.UTF8.GetPreamble();
        var hasBom = bytes.AsSpan().StartsWith(bom);
        var xml = Encoding.UTF8.GetString(bytes, hasBom ? bom.Length : 0, bytes.Length - (hasBom ? bom.Length : 0));
        var matches = WinchDefinitionTag.Matches(xml).Cast<Match>().ToArray();
        var replacements = new List<(int Index, int Length, string Text)>();
        foreach (var (winchName, original, updated) in definitions)
        {
            var match = matches.FirstOrDefault(item => string.Equals(GetAttribute(item.Groups["attrs"].Value, "Name"), winchName, StringComparison.OrdinalIgnoreCase));
            if (match is null) throw new InvalidDataException($"Winch definition '{winchName}' was not found in {Path.GetFileName(path)}.");
            var openEnd = match.Value.IndexOf('>') + 1;
            if (openEnd <= 0) throw new InvalidDataException($"Winch definition '{winchName}' has an invalid opening tag.");
            var openTag = match.Value[..openEnd];
            openTag = ReplaceAttribute(openTag, "Length", XmlValue(updated.Length));
            openTag = ReplaceAttribute(openTag, "StrengthMult", XmlValue(updated.StrengthMultiplier));
            if (updated.IgnitionRequired != "—")
                openTag = ReplaceAttribute(openTag, "IsEngineIgnitionRequired", XmlValue(updated.IgnitionRequired));
            var definitionText = openTag + match.Value[openEnd..];
            if (original.Description != updated.Description)
            {
                var uiDescription = Regex.Match(definitionText, @"<UiDesc\b[^>]*>", RegexOptions.IgnoreCase);
                if (!uiDescription.Success) throw new InvalidDataException($"Winch definition '{winchName}' has no UiDesc element.");
                if (string.IsNullOrWhiteSpace(updated.UiDescriptionKey))
                    throw new InvalidDataException($"Winch definition '{winchName}' has no existing description localization key.");
                var updatedUiDescription = ReplaceAttribute(uiDescription.Value, "UiDesc", EscapeXmlAttributeValue(updated.UiDescriptionKey));
                definitionText = definitionText[..uiDescription.Index] + updatedUiDescription + definitionText[(uiDescription.Index + uiDescription.Length)..];
            }
            var gameData = Regex.Match(definitionText, @"<GameData\b[^>]*>", RegexOptions.IgnoreCase);
            if (!gameData.Success) throw new InvalidDataException($"Winch definition '{winchName}' has no GameData element.");
            var updatedGameData = ReplaceAttribute(gameData.Value, "UnlockByRank", XmlValue(updated.UnlockByRank));
            definitionText = definitionText[..gameData.Index] + updatedGameData + definitionText[(gameData.Index + gameData.Length)..];
            replacements.Add((match.Index, match.Length, definitionText));
        }
        foreach (var replacement in replacements.OrderByDescending(item => item.Index))
            xml = xml[..replacement.Index] + replacement.Text + xml[(replacement.Index + replacement.Length)..];
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
            {
                if (!string.Equals(Directory.GetParent(file)?.Name, "trucks", StringComparison.OrdinalIgnoreCase)) continue;
                files.TryAdd(Path.GetFileNameWithoutExtension(file), file);
            }
        return files;
    }

    private void WinchCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_updatingGrid || e.RowIndex < 0 || e.ColumnIndex < 3 || _winchGrid.Rows[e.RowIndex].Tag is not TruckRow rowData) return;
        var winchClass = _matrixWinchClasses[e.ColumnIndex - 3];
        var isChecked = _winchGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value is true;
        var updatedTypes = new HashSet<string>(rowData.Types, StringComparer.OrdinalIgnoreCase);
        if (isChecked) updatedTypes.Add(winchClass.Code);
        else updatedTypes.Remove(winchClass.Code);

        if (updatedTypes.Count == 0)
        {
            _updatingGrid = true;
            _winchGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = true;
            _updatingGrid = false;
            MessageBox.Show(this, "A truck must keep at least one winch class.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        rowData.Types = updatedTypes;
        UpdateCompatibilityCellStyles(_winchGrid.Rows[e.RowIndex]);
        UpdateSaveCompatibilityButton();
        _winchStatus.Text = rowData.Types.SetEquals(rowData.StagedTypes)
            ? $"No unstaged winch compatibility changes remain for {rowData.Name}."
            : $"Winch compatibility changes for {rowData.Name} are ready to stage.";
    }

    private void UpdateTruckXml(TruckRow row, HashSet<string> types, IReadOnlyList<WinchClass> classes)
    {
        var path = WorkspaceOriginals.PrepareForEdit(_getWorkspacePath(), row.FilePath);
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
        var xml = Encoding.UTF8.GetString(bytes, hasBom ? Encoding.UTF8.GetPreamble().Length : 0, bytes.Length - (hasBom ? Encoding.UTF8.GetPreamble().Length : 0));
        var socket = WinchSocket.Match(xml);
        if (!socket.Success) throw new InvalidDataException("The truck has no WinchUpgradeSocket element.");

        var selectedClasses = classes.Where(item => types.Contains(item.Code)).ToArray();
        var defaultWinch = row.DefaultWinch;
        if (!selectedClasses.Any(item => item.WinchNames.Contains(defaultWinch, StringComparer.OrdinalIgnoreCase)))
            defaultWinch = selectedClasses.Select(item => item.DefaultWinch).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? defaultWinch;

        var tag = socket.Value;
        var unknownTypes = types.Where(type => !classes.Any(item => string.Equals(item.Code, type, StringComparison.OrdinalIgnoreCase)));
        var selectedTypeNames = selectedClasses.Select(item => item.Code).Concat(unknownTypes);
        tag = ReplaceAttribute(tag, "Type", string.Join(", ", selectedTypeNames));
        tag = ReplaceAttribute(tag, "Default", defaultWinch);
        xml = xml[..socket.Index] + tag + xml[(socket.Index + socket.Length)..];
        var encoded = new UTF8Encoding(false).GetBytes(xml);
        if (hasBom) encoded = Encoding.UTF8.GetPreamble().Concat(encoded).ToArray();
        File.WriteAllBytes(path, encoded);
        WorkspaceDataCache.InvalidateFile(path);
    }

    private static string ReplaceAttribute(string tag, string attribute, string value)
    {
        var pattern = $@"\b{Regex.Escape(attribute)}\s*=\s*""[^""]*""";
        var replacement = $"{attribute}=\"{value}\"";
        if (Regex.IsMatch(tag, pattern, RegexOptions.IgnoreCase)) return Regex.Replace(tag, pattern, replacement, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return tag.Insert(tag.Length - 1, $" {replacement}");
    }

    private static string EscapeXmlAttributeValue(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\r\n", "&#10;", StringComparison.Ordinal)
        .Replace("\r", "&#10;", StringComparison.Ordinal)
        .Replace("\n", "&#10;", StringComparison.Ordinal)
        .Replace("\t", "&#9;", StringComparison.Ordinal);

    private static string GetCurrentDefault(string filePath)
    {
        var socket = WinchSocket.Match(WorkspaceDataCache.ReadAllText(filePath, Encoding.UTF8));
        return GetAttribute(socket.Success ? socket.Value : string.Empty, "Default");
    }

    private static string GetAttribute(string tag, string name) => XmlAttribute.Matches(tag).Cast<Match>()
        .FirstOrDefault(match => string.Equals(match.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))?.Groups["value"].Value ?? string.Empty;

    private static string DisplayValue(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
    private static string XmlValue(string value) => value == "—" ? string.Empty : value;

    private static string ReadableVariant(string winchName)
    {
        return VariantLabel(VariantSuffix(winchName) ?? winchName[(winchName.LastIndexOf('_') + 1)..]);
    }

    private static string? VariantSuffix(string name) => new[] { "ext_pow", "default", "offline", "ext", "pow" }
        .FirstOrDefault(suffix => name.EndsWith("_" + suffix, StringComparison.OrdinalIgnoreCase));

    private static string VariantLabel(string suffix) => suffix.ToLowerInvariant() switch
    {
        "default" => "Tow rope",
        "ext" => "Winch",
        "pow" => "Rescue winch",
        "ext_pow" => "Advanced rescue",
        "offline" => "Offline",
        _ => suffix
    };

    private static string? PresetWinchDisplayName(string? suffix) => suffix?.ToLowerInvariant() switch
    {
        "default" => "Tow rope",
        "ext" => "Winch",
        "pow" => "Rescue winch",
        "ext_pow" => "Advanced rescue",
        _ => null
    };

    private static string? PresetWinchDescription(string? suffix, string length) => suffix?.ToLowerInvariant() switch
    {
        "default" => $"A standard tow rope ({length} m).",
        "ext" => $"Basic winch ({length} m).",
        "pow" => $"A high-power rescue winch ({length} m).",
        "ext_pow" => $"An advanced rescue winch ({length} m).",
        "offline" => $"An autonomous winch ({length} m).",
        _ => null
    };

    private static string DefinitionKey(string classCode, string winchName) => $"{classCode}\0{winchName}";

    private static string NormalizeWinchType(string value) => Path.GetFileNameWithoutExtension(value.Trim());

    private sealed record WinchDefinition(string Name, string UiNameKey, string UiName, string UiDescriptionKey, string Description, string Length, string StrengthMultiplier, string IgnitionRequired, string UnlockByRank);
    private sealed record WinchProfile(string Length, string StrengthMultiplier, string UnlockByRank);
    private sealed record WinchTableRow(string ClassCode, string FilePath, string Name);
    private sealed record WinchClass(string Code, string DisplayName, string FilePath, string DefaultWinch, string[] WinchNames, List<WinchDefinition> Definitions, List<WinchDefinition> OriginalDefinitions);

    private sealed class TruckRow(string filePath, string name, bool hasWinchSocket, HashSet<string> types, HashSet<string> originalTypes, string defaultWinch)
    {
        public string FilePath { get; } = filePath;
        public string Name { get; } = name;
        public bool HasWinchSocket { get; } = hasWinchSocket;
        public HashSet<string> Types { get; set; } = types;
        public HashSet<string> OriginalTypes { get; } = originalTypes;
        public HashSet<string> PersistedTypes { get; set; } = new(types, StringComparer.OrdinalIgnoreCase);
        public HashSet<string> StagedTypes { get; set; } = new(types, StringComparer.OrdinalIgnoreCase);
        public string DefaultWinch { get; set; } = defaultWinch;
    }
}
