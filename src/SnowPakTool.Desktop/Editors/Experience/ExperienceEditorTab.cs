using System.Globalization;
using System.ComponentModel;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace SnowPakTool.Desktop;

internal sealed class ExperienceEditorTab : UserControl
{
    private const int SeventeenSeasonExperienceTotal = 1_163_997;
    private const int LeftColumnWidth = 580;
    private const int RegionSectionHeight = 700;
    private const int ControlsSectionHeight = 200;
    private const int GraphsSectionHeight = 900;
    private const int GraphsSectionWidth = 1_200;
    private const int ColumnDividerWidth = 4;
    private const int RankSummaryHeight = 42;
    private static readonly Encoding GameFileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex RankExperienceArray = new(@"(?<head>\brankExp\s*=\s*\[)(?<body>.*?)(?<tail>\])", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RegionCodeFromFileName = new(@"^(?:level_)?(?<country>us|ru)_(?<region>\d{2})_", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RegionUiNames = new(@"uiName\s*=\s*""(?<key>UI_REGION_[^""]+_NAME)""(?<details>[\s\S]{0,300}?)name\s*=\s*""(?<code>(?:us|ru)_\d{2})""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DlcRegionList = new(@"regions\s*=\s*\[(?<regions>.*?)\]", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex QuotedRegionCode = new(@"""(?<code>(?:us|ru)_\d{2})""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DlcNameKey = new(@"\bname\s*=\s*""(?<key>UI_DLC_NAME_[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex DlcTopLevelSection = new(@"^   (?<section>[A-Za-z0-9_]+)\s*=\s*\{", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex SeasonNumberFromDlcName = new(@"\bSeason\s+(?<number>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RewardExperience = new(@"ObjectiveRewardExperience\s*=\s*\{\s*amount\s*=\s*(?<amount>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex IntegerValue = new(@"\b\d+\b", RegexOptions.Compiled);

    private readonly Func<string> _getWorkspacePath;
    private readonly RegionProgressionStore _regionProgressionStore;
    private readonly OwnedRegionState _ownedRegionState;
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Text = "Choose a workspace and unpack a PAK to load rank settings." };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        BackgroundColor = AppColors.Get("Surface"),
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f),
        ColumnHeadersDefaultCellStyle = { Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f, FontStyle.Bold) },
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false,
        ScrollBars = ScrollBars.Vertical,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        MultiSelect = false,
        EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
        RowTemplate = { Height = 22 },
        DefaultCellStyle = { BackColor = AppColors.Get("Surface"), SelectionBackColor = AppColors.Get("Surface"), SelectionForeColor = SystemColors.ControlText },
        AlternatingRowsDefaultCellStyle = { BackColor = AppColors.Get("AlternateRow"), SelectionBackColor = AppColors.Get("AlternateRow"), SelectionForeColor = SystemColors.ControlText }
    };
    private readonly DataGridView _regionGrid = new()
    {
        Dock = DockStyle.Fill,
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f),
        ColumnHeadersDefaultCellStyle = { Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 8.5f, FontStyle.Bold) },
        Height = 190,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeColumns = false,
        AllowUserToResizeRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        EditMode = DataGridViewEditMode.EditOnEnter,
        AllowDrop = false,
        ScrollBars = ScrollBars.Vertical,
        RowTemplate = { Height = 28 },
        ColumnHeadersHeight = 28,
        DefaultCellStyle = { BackColor = AppColors.Get("Surface"), SelectionBackColor = AppColors.Get("Surface"), SelectionForeColor = SystemColors.ControlText },
        AlternatingRowsDefaultCellStyle = { BackColor = AppColors.Get("AlternateRow"), SelectionBackColor = AppColors.Get("AlternateRow"), SelectionForeColor = SystemColors.ControlText }
    };
    private readonly CheckBox _selectAllRegions = new() { Text = "Select all regions", AutoSize = true, Checked = true, Padding = new Padding(8, 6, 2, 0) };
    private readonly Button _selectOwnedRegions = new() { Text = "Select owned regions", AutoSize = true, Margin = new Padding(8, 3, 0, 0) };
    private readonly Label _selectedRegionTotal = new() { AutoSize = true, Padding = new Padding(12, 6, 4, 0) };
    private readonly Label _level30Total = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), Padding = new Padding(0, 6, 0, 6) };
    private readonly Label _updatedXpTotal = new() { AutoSize = true, Padding = new Padding(0, 6, 0, 6) };
    private readonly Label _xpBudgetLabel = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 4, 0) };
    private readonly Label _steepnessValueLabel = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 4, 0) };
    private readonly TrackBar _xpReductionSlider = new()
    {
        Minimum = 0,
        Maximum = 0,
        Value = 0,
        TickFrequency = 1,
        SmallChange = 1,
        LargeChange = 1,
        AutoSize = false,
        Height = 46,
        Width = 160,
        TickStyle = TickStyle.BottomRight
    };
    private readonly TrackBar _steepnessSlider = new()
    {
        Minimum = 5,
        Maximum = 80,
        Value = 30,
        TickFrequency = 5,
        SmallChange = 1,
        LargeChange = 5,
        AutoSize = false,
        Height = 46,
        TickStyle = TickStyle.BottomRight
    };
    private readonly Panel _regionBody = new() { Dock = DockStyle.Fill, Height = 190 };
    private readonly BufferedPanel _experienceGraph = new() { Dock = DockStyle.Top, Height = 320, BackColor = AppColors.Get("Surface"), BorderStyle = BorderStyle.FixedSingle };
    private TableLayoutPanel? _regionPanel;
    private TableLayoutPanel? _curveButtonsPanel;
    private TableLayoutPanel? _leftColumn;
    private GroupBox? _regionSection;
    private GroupBox? _controlsSection;
    private GroupBox? _graphsSection;
    private TableLayoutPanel? _rankPanel;
    private TableLayoutPanel? _progressionGraphPanel;
    private TableLayoutPanel? _rightColumn;
    private TableLayoutPanel? _contentLayout;
    private Panel? _contentScrollHost;
    private bool _updatingRegionChecks;
    private bool _updatingOwnedRegion;
    private bool _flipExperienceGraphAxes;
    private bool _flipExperienceGraphYAxis = true;
    private string _selectedXpCurve = "Exponential";
    private readonly Dictionary<string, double> _curveMagnitudes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Exponential"] = 3d
    };
    private readonly Dictionary<string, Label> _curveExpressionLabels = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingXpSlider;
    private bool _loadingProgressionSettings;
    private static Color BaseGameRegionColor => AppColors.Get("BaseGameRegion");
    private readonly Button _reloadButton = new() { Text = "Reload", AutoSize = true };
    private readonly Button _saveButton = new() { Text = "Stage changes", AutoSize = true, Enabled = false };
    private readonly Button _revertUnstaged = new() { Text = "Revert unstaged edits", AutoSize = true, Enabled = false };
    private readonly Button _revertStaged = new() { Text = "Revert staged changes", AutoSize = true, Enabled = false };
    private string? _loadedWorkspace;
    private string? _rankSettingsPath;
    private string? _loadedFileText;
    private List<int> _loadedValues = new();
    private readonly RankXpStageStore _rankStageStore;
    private StagedRankXp? _stagedRank;
    private bool _isLoading;
    internal event Action? StagingChanged;

    public ExperienceEditorTab(Func<string> getWorkspacePath, OwnedRegionState ownedRegionState)
        : this(getWorkspacePath, ownedRegionState, new RankXpStageStore()) { }

    internal ExperienceEditorTab(Func<string> getWorkspacePath, OwnedRegionState ownedRegionState, RankXpStageStore rankStageStore)
        : this(getWorkspacePath, ownedRegionState, rankStageStore, new RegionProgressionStore()) { }

    internal ExperienceEditorTab(Func<string> getWorkspacePath, OwnedRegionState ownedRegionState, RankXpStageStore rankStageStore, RegionProgressionStore regionProgressionStore)
    {
        _regionProgressionStore = regionProgressionStore;
        _rankStageStore = rankStageStore;
        _getWorkspacePath = getWorkspacePath;
        _ownedRegionState = ownedRegionState;
        _ownedRegionState.Changed += UpdateOwnedRegion;
        BuildLayout();
        AppColors.Changed += (_, _) => RefreshPaletteColors();
        _xpReductionSlider.ValueChanged += (_, _) =>
        {
            if (!_updatingXpSlider)
            {
                UpdateLinearProgression();
                SaveProgressionSettings();
            }
        };
        _steepnessSlider.ValueChanged += (_, _) =>
        {
            _curveMagnitudes["Exponential"] = _steepnessSlider.Value / 10d;
            if (_curveExpressionLabels.TryGetValue("Exponential", out var formula))
                formula.Text = CurveExpression("Exponential", _curveMagnitudes["Exponential"]);
            if (_steepnessValueLabel is not null)
                _steepnessValueLabel.Text = $"Steepness: {_curveMagnitudes["Exponential"]:0.0}";
            UpdateLinearProgression();
            SaveProgressionSettings();
        };
        _reloadButton.Click += (_, _) => LoadWorkspaceSettings(confirmDiscard: true);
        _saveButton.Click += (_, _) => SaveSettings();
        _revertUnstaged.Click += (_, _) => RestoreRankPreview();
        _revertStaged.Click += (_, _) => RevertStagedRankChanges();
        _grid.CellValueChanged += (_, _) => UpdateDirtyState();
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValidating += ValidateExperienceCell;
        _grid.DataError += (_, e) =>
        {
            e.ThrowException = false;
            _status.Text = "XP values must be non-negative whole numbers.";
        };
        var actionTips = new ToolTip();
        actionTips.SetToolTip(_reloadButton, "Reload rank XP and region data from the selected workspace. Unsaved edits require confirmation.");
        actionTips.SetToolTip(_saveButton, "Stage the Updated XP values in saved preferences. Start → Apply pending changes writes them to the workspace.");
        actionTips.SetToolTip(_revertUnstaged, "Restore the staged XP snapshot, or current file values when nothing is staged.");
        actionTips.SetToolTip(_revertStaged, "Clear staged XP changes and restore the preview to current file values. Game files are not changed.");
        actionTips.SetToolTip(_selectAllRegions, "Include or exclude every region in the XP budget and progression graphs.");
        actionTips.SetToolTip(_selectOwnedRegions, "Include only regions marked as owned in the Owned Content tab.");
        _selectOwnedRegions.Click += (_, _) => SelectOwnedRegions();
        _regionGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_regionGrid.IsCurrentCellDirty) _regionGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _regionGrid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && _regionGrid.Columns[e.ColumnIndex].Name == "Owned" && !_updatingOwnedRegion)
                _ownedRegionState.SetOwned(Convert.ToString(_regionGrid.Rows[e.RowIndex].Cells["RegionCode"].Value) ?? string.Empty, _regionGrid.Rows[e.RowIndex].Cells["Owned"].Value is true);
            if (e.RowIndex >= 0 && _regionGrid.Columns[e.ColumnIndex].Name == "Include")
            {
                UpdateSelectedRegionTotal();
                UpdateExperienceGraphLayout();
                _experienceGraph.Invalidate();
                UpdateLinearProgression();
                SaveProgressionSettings();
            }
        };
        _regionGrid.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 ||
                _regionGrid.Columns[e.ColumnIndex].Name != "Owned" ||
                _regionGrid.Rows[e.RowIndex].Tag is not RegionMetadata { IsBaseGame: true }) return;

            e.PaintBackground(e.CellBounds, true);
            e.Paint(e.CellBounds, DataGridViewPaintParts.Border);
            e.Handled = true;
        };
        _selectAllRegions.CheckedChanged += (_, _) =>
        {
            if (!_updatingRegionChecks) SetAllRegionsIncluded(_selectAllRegions.Checked);
        };
        _experienceGraph.Paint += DrawExperienceGraph;
        AddGraphFlipButton(_experienceGraph, () =>
        {
            _flipExperienceGraphAxes = !_flipExperienceGraphAxes;
            _experienceGraph.Invalidate();
            SaveProgressionSettings();
        });
    }

    private DataGridViewRow[] RankRows => _grid.Rows.Cast<DataGridViewRow>().OrderBy(row => Convert.ToInt32(row.Cells["Step"].Value, CultureInfo.InvariantCulture)).ToArray();

    public void EnsureLoaded()
    {
        if (!string.Equals(_loadedWorkspace, _getWorkspacePath(), StringComparison.OrdinalIgnoreCase))
            LoadWorkspaceSettings(confirmDiscard: true);
        else if (_loadedWorkspace is null)
            LoadWorkspaceSettings(confirmDiscard: false);
        else
            ApplyRegionProgressionOrder();
    }

    private void RefreshPaletteColors()
    {
        foreach (var grid in new[] { _grid, _regionGrid })
        {
            grid.BackgroundColor = AppColors.Get("Surface");
            grid.DefaultCellStyle.BackColor = AppColors.Get("Surface");
            grid.DefaultCellStyle.SelectionBackColor = AppColors.Get("Surface");
            grid.AlternatingRowsDefaultCellStyle.BackColor = AppColors.Get("AlternateRow");
            grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = AppColors.Get("AlternateRow");
        }
        foreach (DataGridViewRow row in _regionGrid.Rows)
        {
            if (row.Tag is not RegionMetadata { IsBaseGame: true }) continue;
            row.DefaultCellStyle.BackColor = BaseGameRegionColor;
            row.DefaultCellStyle.SelectionBackColor = BaseGameRegionColor;
        }
        _experienceGraph.Invalidate();
        RefreshRankChangeColors();
        Invalidate(true);
    }

    public void WorkspaceUpdated()
    {
        if (_loadedWorkspace is null || !string.Equals(_loadedWorkspace, _getWorkspacePath(), StringComparison.OrdinalIgnoreCase)) return;
        if (IsDirty && MessageBox.Show(this, "The workspace was updated. Reload rank settings and discard the unsaved table edits?", "Workspace updated", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        LoadWorkspaceSettings(confirmDiscard: false);
    }

    private void BuildLayout()
    {
        var explanation = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1450, 0),
            Text = "In this tab you can edit the desired experience needed for each rank. Graphs show which rank will be reached in each region, assuming 100% completion of previous regions.\r\n\r\nThe rank table shows original and updated XP values side by side. Updated XP uses an exponential progression; adjust its steepness to change how difficult the earlier stages feel.\r\n\r\nRegions follow the saved order from Randomizer → Region progression. Select the regions to include in your planned route. You can also lower the maximum XP target, which means later regions may be played at maximum level.",
            Padding = new Padding(0, 0, 0, 12)
        };
        var seasonTotal = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = $"Current maximum across all regions / 17 seasons: {SeventeenSeasonExperienceTotal:N0} XP",
            Padding = new Padding(0, 0, 0, 8)
        };
        var futureSeasons = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Text = "Region totals come from mission reward entries in the workspace.",
            Padding = new Padding(0, 0, 0, 10)
        };

        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Step", HeaderText = "Rank", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 23, MinimumWidth = 26 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "OriginalXP", HeaderText = "Original XP", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 38.5f, MinimumWidth = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "UpdatedXP", HeaderText = "Updated XP", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 38.5f, MinimumWidth = 55 });
        TableHeaderBehavior.Attach(_grid);
        _grid.Columns["Step"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns["OriginalXP"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns["UpdatedXP"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns["OriginalXP"]!.DefaultCellStyle.Format = "N0";
        _grid.Columns["UpdatedXP"]!.DefaultCellStyle.Format = "N0";

        _regionGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Include", ReadOnly = false, ThreeState = false, SortMode = DataGridViewColumnSortMode.NotSortable, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 14, MinimumWidth = 48 });
        _regionGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Owned", HeaderText = "Owned", ReadOnly = false, ThreeState = false, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 12, MinimumWidth = 48 });
        _regionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RegionName", HeaderText = "Region name", ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 28, MinimumWidth = 96 });
        _regionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RegionCode", HeaderText = "Code", ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        _regionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DlcName", HeaderText = "DLC name", ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 32, MinimumWidth = 96 });
        _regionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "RegionXP", HeaderText = "Possible XP", ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 26, MinimumWidth = 90 });
        _regionGrid.Columns["RegionCode"]!.Visible = false;
        _regionGrid.Columns["Include"]!.HeaderCell.ToolTipText = "Include this region in the XP budget and progression graphs.";
        _regionGrid.Columns["Owned"]!.HeaderCell.ToolTipText = "Mark this region as owned. This stays synchronized with the Owned Content tab.";
        _regionGrid.Columns["RegionName"]!.HeaderCell.ToolTipText = "Human-readable region name.";
        _regionGrid.Columns["DlcName"]!.HeaderCell.ToolTipText = "DLC or season containing this region. Click to sort.";
        _regionGrid.Columns["RegionXP"]!.HeaderCell.ToolTipText = "Experience available from mission rewards in this region. Click to sort.";
        _regionGrid.Columns["RegionXP"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _regionGrid.Columns["RegionXP"]!.DefaultCellStyle.Format = "N0";

        var regionHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 4) };
        regionHeader.Controls.Add(_selectAllRegions);
        regionHeader.Controls.Add(_selectOwnedRegions);
        regionHeader.Controls.Add(_selectedRegionTotal);
        _regionBody.Controls.Add(_regionGrid);
        _curveButtonsPanel = CreateCurveButtonsPanel();
        _regionPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, Width = 700, Height = 220, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        _regionPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _regionPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _regionPanel.Controls.Add(regionHeader, 0, 0);
        _regionPanel.Controls.Add(_regionBody, 0, 1);

        _regionSection = new GroupBox { Text = "1. Region list", AutoSize = false, Dock = DockStyle.Fill, Width = LeftColumnWidth, Height = RegionSectionHeight, Padding = new Padding(10), Margin = new Padding(0), Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10.5f, FontStyle.Bold) };
        _regionSection.Controls.Add(_regionPanel);
        _controlsSection = new GroupBox { Text = "2. Progression controls", AutoSize = false, Dock = DockStyle.Fill, Width = LeftColumnWidth, Height = ControlsSectionHeight, Padding = new Padding(10), Margin = new Padding(0), Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10.5f, FontStyle.Bold) };
        _curveButtonsPanel.Dock = DockStyle.Fill;
        _curveButtonsPanel.AutoScroll = true;
        _controlsSection.Controls.Add(_curveButtonsPanel);
        _leftColumn = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Width = LeftColumnWidth, Height = GraphsSectionHeight, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
        _leftColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LeftColumnWidth));
        _leftColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, RegionSectionHeight));
        _leftColumn.RowStyles.Add(new RowStyle(SizeType.Absolute, ControlsSectionHeight));
        _leftColumn.Controls.Add(_regionSection, 0, 0);
        _leftColumn.Controls.Add(_controlsSection, 0, 1);

        var rankSummary = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0), Padding = new Padding(0) };
        rankSummary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rankSummary.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        rankSummary.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _level30Total.Dock = DockStyle.Fill;
        _level30Total.Padding = new Padding(0, 2, 0, 0);
        _updatedXpTotal.Dock = DockStyle.Fill;
        _updatedXpTotal.Padding = new Padding(0, 2, 0, 0);
        rankSummary.Controls.Add(_level30Total, 0, 0);
        rankSummary.Controls.Add(_updatedXpTotal, 0, 1);

        _rankPanel = new TableLayoutPanel { Dock = DockStyle.Fill, Width = 360, Height = 640, ColumnCount = 1, RowCount = 2, Margin = new Padding(4, 0, 8, 0) };
        _rankPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, RankSummaryHeight));
        _rankPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _rankPanel.Controls.Add(rankSummary, 0, 0);
        _rankPanel.Controls.Add(_grid, 0, 1);

        _progressionGraphPanel = CreateGraphPanel(_experienceGraph, "Original vs modified XP progression by region");
        _rightColumn = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        _rightColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        _rightColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _rightColumn.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _rightColumn.Controls.Add(_rankPanel, 0, 0);
        _rightColumn.Controls.Add(_progressionGraphPanel, 1, 0);
        _graphsSection = new GroupBox { Text = "3. Rank tables and graphs", AutoSize = false, Dock = DockStyle.Fill, Width = GraphsSectionWidth, Height = GraphsSectionHeight, Padding = new Padding(10), Margin = new Padding(0), Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10.5f, FontStyle.Bold) };
        _rightColumn.Dock = DockStyle.Fill;
        _graphsSection.Controls.Add(_rightColumn);

        var columnDivider = new Panel { Width = ColumnDividerWidth, Dock = DockStyle.Fill, BackColor = SystemColors.ControlDark, Margin = new Padding(0) };

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(12, 6, 12, 6) };
        actions.Controls.Add(_reloadButton);
        actions.Controls.Add(_saveButton);
        actions.Controls.Add(_revertUnstaged);
        actions.Controls.Add(_revertStaged);
        actions.Controls.Add(_status);
        actions.SizeChanged += (_, _) => _status.MaximumSize = new Size(Math.Max(240, actions.ClientSize.Width - actions.Padding.Horizontal), 0);

        _contentLayout = new TableLayoutPanel { Dock = DockStyle.None, AutoSize = false, Padding = new Padding(18), ColumnCount = 3, RowCount = 5 };
        _contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LeftColumnWidth));
        _contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ColumnDividerWidth));
        _contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, GraphsSectionWidth));
        for (var row = 0; row < 5; row++) _contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _contentLayout.RowStyles[3].SizeType = SizeType.Percent;
        _contentLayout.RowStyles[3].Height = 100;
        _contentLayout.Controls.Add(explanation, 0, 0);
        _contentLayout.SetColumnSpan(explanation, 3);
        _contentLayout.Controls.Add(seasonTotal, 0, 1);
        _contentLayout.SetColumnSpan(seasonTotal, 3);
        _contentLayout.Controls.Add(futureSeasons, 0, 2);
        _contentLayout.SetColumnSpan(futureSeasons, 3);
        _contentLayout.Controls.Add(_leftColumn, 0, 3);
        _contentLayout.Controls.Add(columnDivider, 1, 3);
        _contentLayout.Controls.Add(_graphsSection, 2, 3);
        _contentScrollHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _contentScrollHost.Controls.Add(_contentLayout);
        _contentScrollHost.Resize += (_, _) => ResizeContentLayout();
        var editorLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        editorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        editorLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editorLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        editorLayout.Controls.Add(actions, 0, 0);
        editorLayout.Controls.Add(_contentScrollHost, 0, 1);
        Controls.Add(editorLayout);
        ResizeContentLayout();
        SetEditorEnabled(false);
    }

    private static TableLayoutPanel CreateGraphPanel(BufferedPanel graph, string title)
    {
        var titleLabel = new Label { Text = title, AutoSize = false, Height = RankSummaryHeight, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 14f, FontStyle.Bold), Padding = new Padding(0, 6, 0, 8) };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 4, 0, 14), Padding = new Padding(4) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, RankSummaryHeight));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(titleLabel, 0, 0);
        panel.Controls.Add(graph, 0, 1);
        graph.Dock = DockStyle.Fill;
        return panel;
    }

    private TableLayoutPanel CreateCurveButtonsPanel()
    {
        var curves = new[] { "Exponential" };
        var tips = new ToolTip();
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = curves.Length,
            Padding = new Padding(4, 3, 4, 3),
            Margin = new Padding(0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < curves.Length; row++) panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        for (var i = 0; i < curves.Length; i++)
        {
            var name = curves[i];
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 6,
                Margin = new Padding(2, 4, 2, 4),
                Padding = new Padding(0)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));

            var functionPreview = new Panel
            {
                AccessibleName = $"{name} curve preview",
                Width = 64,
                Height = 64,
                Anchor = AnchorStyles.None,
                Margin = new Padding(2),
                BackColor = SystemColors.Control
            };
            var expression = CurveExpression(name, _curveMagnitudes[name]);
            functionPreview.Paint += (_, e) => DrawCurvePreview(e.Graphics, functionPreview.ClientRectangle, name);

            var functionLabel = new Label
            {
                Text = expression,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9.5f, FontStyle.Bold),
                Margin = new Padding(4, 2, 4, 0)
            };
            _curveExpressionLabels[name] = functionLabel;
            _steepnessValueLabel.Text = $"Steepness: {_curveMagnitudes[name]:0.0}";
            _steepnessValueLabel.Dock = DockStyle.Fill;
            _steepnessValueLabel.TextAlign = ContentAlignment.MiddleLeft;
            _steepnessValueLabel.Margin = new Padding(4, 0, 4, 0);
            var reductionLabel = new Label
            {
                Text = "Reduce total XP (5,000 per step)",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(4, 0, 4, 0)
            };
            _steepnessSlider.Dock = DockStyle.Top;
            _steepnessSlider.Width = 164;
            _steepnessSlider.Height = 36;
            _steepnessSlider.TickFrequency = 10;
            _steepnessSlider.Margin = new Padding(4, 0, 8, 0);
            tips.SetToolTip(_steepnessSlider, "Adjust the exponential curve steepness.");
            _xpReductionSlider.Dock = DockStyle.Top;
            _xpReductionSlider.Width = 240;
            _xpReductionSlider.Height = 32;
            _xpReductionSlider.Margin = new Padding(0);
            tips.SetToolTip(_xpReductionSlider, "Reduce the XP budget by 5,000 XP per slider step.");
            _xpBudgetLabel.Dock = DockStyle.Top;
            _xpBudgetLabel.Height = 20;
            _xpBudgetLabel.TextAlign = ContentAlignment.MiddleCenter;
            row.Controls.Add(functionPreview, 0, 0);
            row.SetRowSpan(functionPreview, 3);
            row.Controls.Add(functionLabel, 1, 0);
            row.Controls.Add(_steepnessValueLabel, 1, 1);
            row.Controls.Add(_steepnessSlider, 1, 2);
            row.Controls.Add(reductionLabel, 0, 3);
            row.SetColumnSpan(reductionLabel, 2);
            row.Controls.Add(_xpReductionSlider, 0, 4);
            row.SetColumnSpan(_xpReductionSlider, 2);
            row.Controls.Add(_xpBudgetLabel, 0, 5);
            row.SetColumnSpan(_xpBudgetLabel, 2);
            panel.Controls.Add(row, 0, i);
        }
        return panel;
    }

    private Button CreateMagnitudeButton(string symbol, string curveName, int direction, ToolTip tips)
    {
        var button = new Button
        {
            Text = symbol,
            AccessibleName = $"{symbol} {curveName} magnitude",
            Size = new Size(36, 36),
            Anchor = AnchorStyles.None,
            Margin = new Padding(0),
            Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 12f, FontStyle.Bold)
        };
        tips.SetToolTip(button, $"{symbol} {curveName} curve steepness");
        button.Click += (_, _) => AdjustCurveMagnitude(curveName, direction);
        return button;
    }

    private static void AddGraphFlipButton(BufferedPanel graph, Action flip)
    {
        var tips = new ToolTip();
        var button = new Button
        {
            Text = "⇄",
            AccessibleName = "Flip graph axes",
            Size = new Size(34, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Font = new Font("Segoe UI Symbol", 12f, FontStyle.Bold),
            Location = new Point(Math.Max(0, graph.ClientSize.Width - 40), 3),
            Padding = new Padding(0)
        };
        tips.SetToolTip(button, "Flip the graph axes");
        button.Click += (_, _) => flip();
        graph.Controls.Add(button);
        button.BringToFront();
        graph.Resize += (_, _) => button.Location = new Point(Math.Max(0, graph.ClientSize.Width - button.Width - 4), 3);
    }

    private static void DrawCurvePreview(Graphics graphics, Rectangle bounds, string curveName)
    {
        if (bounds.Width < 40 || bounds.Height < 24) return;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var left = 18f;
        var right = bounds.Width - 18f;
        var top = 9f;
        var bottom = bounds.Height - 10f;
        using var axisPen = new Pen(AppColors.Get("GraphPreviewAxis"), 1f);
        using var curvePen = new Pen(AppColors.Get("GraphCurve"), 2.5f);
        graphics.DrawLine(axisPen, left, top, left, bottom);
        graphics.DrawLine(axisPen, left, bottom, right, bottom);

        double Weight(double x) => curveName switch
        {
            "Exponential" => Math.Exp(4d * x),
            _ => 1d
        };

        const int sampleCount = 32;
        var weights = Enumerable.Range(0, sampleCount).Select(index => Weight(index / (double)(sampleCount - 1))).ToArray();
        var min = weights.Min();
        var max = weights.Max();
        var points = new PointF[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var x = i / (double)(sampleCount - 1);
            var normalized = max - min < 1e-9 ? 0.5d : (weights[i] - min) / (max - min);
            points[i] = new PointF(left + (float)(x * (right - left)), bottom - (float)(normalized * (bottom - top)));
        }
        graphics.DrawLines(curvePen, points);
        using var borderPen = new Pen(AppColors.Get("GraphBorder"), 1f);
        graphics.DrawRectangle(borderPen, 1, 1, bounds.Width - 3, bounds.Height - 3);
    }

    private static string CurveExpression(string curveName, double magnitude)
    {
        var parameter = magnitude.ToString("0.##", CultureInfo.InvariantCulture);
        return curveName switch
        {
            "Exponential" => $"w(x) = exp({parameter}x)",
            _ => curveName
        };
    }

    private void RestoreProgressionSettings(string workspace)
    {
        var settingsPath = WorkspaceOriginals.GetMetadataPath(workspace, "progression-settings.json");
        ProgressionSettings? settings = null;
        try
        {
            if (File.Exists(settingsPath))
                settings = JsonSerializer.Deserialize<ProgressionSettings>(File.ReadAllText(settingsPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _status.Text = $"Could not load saved progression controls: {ex.Message}";
        }

        if (settings is null) return;
        _loadingProgressionSettings = true;
        try
        {
            if (settings.IncludedRegions is not null)
            {
                var included = settings.IncludedRegions.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (DataGridViewRow row in _regionGrid.Rows)
                    row.Cells["Include"].Value = included.Contains(Convert.ToString(row.Cells["RegionCode"].Value) ?? string.Empty);
            }

            _selectedXpCurve = string.Equals(settings.SelectedCurve, "Exponential", StringComparison.OrdinalIgnoreCase)
                ? "Exponential"
                : "Exponential";
            if (settings.CurveMagnitudes is not null && settings.CurveMagnitudes.TryGetValue("Exponential", out var magnitude))
                _curveMagnitudes["Exponential"] = Math.Clamp(magnitude, 0.5d, 8d);
            _steepnessSlider.Value = Math.Clamp((int)Math.Round(_curveMagnitudes["Exponential"] * 10d), _steepnessSlider.Minimum, _steepnessSlider.Maximum);
            _flipExperienceGraphAxes = settings.FlipExperienceGraphAxes;
            UpdateSelectedRegionTotal();
            _xpReductionSlider.Value = Math.Clamp(settings.XpReductionSteps, _xpReductionSlider.Minimum, _xpReductionSlider.Maximum);
        }
        finally
        {
            _loadingProgressionSettings = false;
        }
        UpdateLinearProgression();
        UpdateExperienceGraphLayout();
        _experienceGraph.Invalidate();
    }

    private void SaveProgressionSettings()
    {
        if (PresetBrowseMode.IsReadOnly(this)) return;
        if (_loadingProgressionSettings || _rankSettingsPath is null || string.IsNullOrWhiteSpace(_loadedWorkspace)) return;
        var settingsPath = WorkspaceOriginals.GetMetadataPath(_loadedWorkspace, "progression-settings.json");
        var settings = new ProgressionSettings
        {
            XpReductionSteps = _xpReductionSlider.Value,
            SelectedCurve = _selectedXpCurve,
            CurveMagnitudes = new Dictionary<string, double>(_curveMagnitudes, StringComparer.OrdinalIgnoreCase),
            FlipExperienceGraphAxes = _flipExperienceGraphAxes,
            IncludedRegions = _regionGrid.Rows.Cast<DataGridViewRow>()
                .Where(row => row.Cells["Include"].Value is true)
                .Select(row => Convert.ToString(row.Cells["RegionCode"].Value) ?? string.Empty).ToList()
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Could not save progression controls: {ex.Message}";
        }
    }

    private sealed class ProgressionSettings
    {
        public int XpReductionSteps { get; set; }
        public string SelectedCurve { get; set; } = "Exponential";
        public Dictionary<string, double>? CurveMagnitudes { get; set; }
        public bool FlipExperienceGraphAxes { get; set; }
        public List<string>? IncludedRegions { get; set; }
    }

    private void ApplyCurve(string name)
    {
        _selectedXpCurve = name;
        UpdateLinearProgression();
        SaveProgressionSettings();
    }

    private void AdjustCurveMagnitude(string name, int direction)
    {
        var limits = CurveMagnitudeLimits(name);
        var magnitude = _curveMagnitudes.GetValueOrDefault(name, 1d);
        _curveMagnitudes[name] = Math.Clamp(magnitude + limits.Step * direction, limits.Minimum, limits.Maximum);
        if (_curveExpressionLabels.TryGetValue(name, out var expressionLabel))
            expressionLabel.Text = CurveExpression(name, _curveMagnitudes[name]);
        ApplyCurve(name);
    }

    private static (double Minimum, double Maximum, double Step) CurveMagnitudeLimits(string name) => name switch
    {
        "Exponential" => (0.5d, 8d, 0.5d),
        _ => (0.5d, 5d, 0.5d)
    };

    private bool IsDirty
    {
        get
        {
            if (_isLoading || _loadedValues.Count != _grid.Rows.Count) return false;
            if (_stagedRank is not null && _stagedRank.SourceText != _loadedFileText) return true;
            var baseline = _stagedRank?.Values ?? _loadedValues;
            if (baseline.Count != _grid.Rows.Count) return true;
            for (var i = 0; i < baseline.Count; i++)
                if (!int.TryParse(Convert.ToString(RankRows[i].Cells["UpdatedXP"].Value, CultureInfo.InvariantCulture), out var value) || value != baseline[i])
                    return true;
            return false;
        }
    }

    private void LoadWorkspaceSettings(bool confirmDiscard)
    {
        if (confirmDiscard && IsDirty && MessageBox.Show(this, "Discard unsaved XP edits and load the workspace file?", "Unsaved changes", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _loadedWorkspace = null;
        _rankSettingsPath = null;
        _stagedRank = null;

        var workspace = _getWorkspacePath();
        if (string.IsNullOrWhiteSpace(workspace))
        {
            _loadedWorkspace = null;
            _regionGrid.Rows.Clear();
            _grid.Rows.Clear();
            UpdateRegionGridLayout();
            UpdateSelectedRegionTotal();
            UpdateLinearProgression();
            SetEditorEnabled(false);
            _status.Text = "Choose a workspace and unpack a PAK to load rank settings.";
            return;
        }

        LoadRegionValues(workspace);

        var path = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[ps]", "rank_settings.sso"));
        if (!File.Exists(path))
        {
            _loadedWorkspace = workspace;
            _rankSettingsPath = null;
            _loadedFileText = null;
            _loadedValues.Clear();
            _grid.Rows.Clear();
            UpdateRankGridHeight();
            UpdateLevel30Total();
            UpdateLinearProgression();
            ApplySectionHeights();
            UpdateEditorContentLayout();
            SetEditorEnabled(false);
            _status.Text = "rank_settings.sso was not found. Unpack initial.pak into this workspace first.";
            return;
        }

        try
        {
            var prepared = (PreparedRankData)WorkspaceDataCache.GetRankData(workspace);
            // An Apply may have created an updated file since the shared snapshot was prepared.
            if (!string.Equals(prepared.Path, path, StringComparison.OrdinalIgnoreCase) ||
                prepared.Text != File.ReadAllText(path, Encoding.UTF8))
            {
                WorkspaceDataCache.InvalidateFile(path);
                prepared = (PreparedRankData)PrepareRankData(workspace)!;
            }
            var text = prepared.Text;
            var values = prepared.Values;
            var originalValues = prepared.OriginalValues;
            _isLoading = true;
            _grid.Rows.Clear();
            for (var i = 0; i < values.Count; i++)
            {
                var originalValue = i < originalValues.Count ? originalValues[i] : values[i];
                _grid.Rows.Add(i + 2, originalValue, values[i]);
            }
            _isLoading = false;
            UpdateRankGridHeight();

            _loadedWorkspace = workspace;
            _rankSettingsPath = prepared.Path;
            _loadedFileText = text;
            _loadedValues = values;
            _stagedRank = _rankStageStore.Load(workspace);
            RestoreProgressionSettings(workspace);
            ApplyRegionProgressionOrder();
            UpdateLevel30Total();
            UpdateLinearProgression();
            if (_stagedRank?.Values.Count == _grid.Rows.Count)
            {
                _isLoading = true;
                for (var i = 0; i < _grid.Rows.Count; i++) RankRows[i].Cells["UpdatedXP"].Value = _stagedRank.Values[i];
                _isLoading = false;
            }
            ApplySectionHeights();
            UpdateEditorContentLayout();
            SetEditorEnabled(true);
            UpdateDirtyState();
            if (_stagedRank is null) _status.Text = $"Loaded {values.Count} rank steps. Preview changes, then Stage changes → Start → Apply pending changes.";
        }
        catch (Exception ex)
        {
            _isLoading = false;
            SetEditorEnabled(false);
            _status.Text = $"Could not load rank settings: {ex.Message}";
        }
    }

    public IEnumerable<WorkspaceChange> PendingChanges(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)) return [];
        var path = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[ps]", "rank_settings.sso"));
        try
        {
            var staged = _rankStageStore.Load(workspace);
            if (staged is null) return [];
            if (!RankProgressionChanges.TryRead(File.ReadAllText(path, Encoding.UTF8), out var before))
                throw new InvalidDataException("Could not read rankExp from the current workspace file.");
            var change = RankProgressionChanges.Create(WorkspaceChange.Pending, path, before, staged.Values);
            return change is null ? [] : [change];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [new(WorkspaceChange.Pending, path, RankProgressionChanges.Property, "Saved rank XP", "Could not read staged changes: " + ex.Message)];
        }
    }

    internal bool HasPresetPreview => _loadedWorkspace == _getWorkspacePath().Trim() && IsDirty;

    internal IEnumerable<WorkspaceChange> PresetPreviewChanges()
    {
        if (!HasPresetPreview || _rankSettingsPath is null) return [];
        if (!TryReadGridValues(out var preview))
            return [new("Preview", _rankSettingsPath, "Rank XP", "Current rank XP", "Unfinished / invalid preview values")];
        return [new("Preview", _rankSettingsPath, "Rank XP", string.Join(", ", _loadedValues), string.Join(", ", preview))];
    }

    internal void ExportPreview(string destination)
    {
        var workspace = _getWorkspacePath();
        var staged = _rankStageStore.Load(workspace);
        IReadOnlyList<int>? values = staged?.Values;
        if (_loadedWorkspace == workspace && _rankSettingsPath is not null)
        {
            if (!_grid.EndEdit() || !TryReadGridValues(out var preview)) throw new InvalidDataException("Every XP value must be a non-negative whole number.");
            values = preview;
        }
        if (values is null) return;
        var path = WorkspaceOriginals.GetCurrentPath(destination, Path.Combine("[ps]", "rank_settings.sso"));
        var text = File.ReadAllText(path, Encoding.UTF8);
        path = WorkspaceOriginals.PrepareForEdit(destination, path);
        File.WriteAllText(path, ReplaceIntegerArray(text, RankExperienceArray, values, "rankExp"), GameFileEncoding);
    }

    public void SavePendingChanges()
    {
        var workspace = _getWorkspacePath();
        var staged = _rankStageStore.Load(workspace);
        if (staged is null) return;
        var path = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[ps]", "rank_settings.sso"));
        var currentText = File.ReadAllText(path, Encoding.UTF8);
        if (!RankProgressionChanges.TryRead(currentText, out var currentValues))
            throw new InvalidDataException("Could not read rankExp from the workspace file.");
        if (!currentValues.SequenceEqual(staged.Values) && currentText != staged.SourceText)
            throw new InvalidOperationException("rank_settings.sso changed since rank XP was staged. Reload Experience / Ranks and stage the values again before applying.");
        var updatedText = currentText;
        if (!currentValues.SequenceEqual(staged.Values))
        {
            updatedText = ReplaceIntegerArray(currentText, RankExperienceArray, staged.Values, "rankExp");
            path = WorkspaceOriginals.PrepareForEdit(workspace, path);
            File.WriteAllText(path, updatedText, GameFileEncoding);
            WorkspaceDataCache.InvalidateFile(path);
        }
        _rankStageStore.Clear(workspace);
        if (string.Equals(_loadedWorkspace, workspace, StringComparison.OrdinalIgnoreCase))
        {
            _rankSettingsPath = path;
            _loadedFileText = updatedText;
            _loadedValues = [.. staged.Values];
            _stagedRank = null;
            UpdateDirtyState();
            _status.Text = IsDirty ? "Saved rank XP applied. New preview edits have not been staged." : "Saved rank XP applied to the workspace.";
        }
    }

    private void SaveSettings()
    {
        if (PresetBrowseMode.IsReadOnly(this)) return;
        if (_rankSettingsPath is null || _loadedFileText is null) return;
        if (!_grid.EndEdit()) return;
        if (!TryReadGridValues(out var values))
        {
            _status.Text = "Every XP value must be a non-negative whole number.";
            return;
        }

        try
        {
            var currentText = File.ReadAllText(_rankSettingsPath, Encoding.UTF8);
            if (!string.Equals(currentText, _loadedFileText, StringComparison.Ordinal))
            {
                MessageBox.Show(this, "rank_settings.sso changed on disk since it was loaded. Reload it before staging to avoid overwriting those changes.", "File changed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _rankStageStore.Stage(_loadedWorkspace!, currentText, values);
            _stagedRank = _rankStageStore.Load(_loadedWorkspace!);
            UpdateDirtyState();
            _status.Text = _stagedRank is null ? "Rank XP matches the workspace; no changes are staged." :
                $"Staged {values.Count} rank XP values. Use Start → Apply pending changes to write them to the workspace.";
            StagingChanged?.Invoke();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not stage rank XP: {ex.Message}", "Stage failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestoreRankPreview()
    {
        if (PresetBrowseMode.IsReadOnly(this) || _rankSettingsPath is null) return;
        var values = _stagedRank?.Values ?? _loadedValues;
        if (values.Count != _grid.Rows.Count) { _status.Text = "Reload rank XP before reverting the preview."; return; }
        _grid.CancelEdit(); _isLoading = true;
        try
        {
            var rows = RankRows;
            for (var i = 0; i < values.Count; i++) { rows[i].Cells["UpdatedXP"].Value = values[i]; rows[i].ErrorText = ""; }
        }
        finally { _isLoading = false; }
        UpdateDirtyState();
    }

    private void RevertStagedRankChanges()
    {
        if (PresetBrowseMode.IsReadOnly(this) || _loadedWorkspace is null || _rankSettingsPath is null) return;
        try
        {
            _rankStageStore.Clear(_loadedWorkspace); _stagedRank = null;
            RestoreRankPreview();
            _status.Text = "Staged rank XP cleared. Preview restored to current file values; workspace files were not changed.";
            StagingChanged?.Invoke();
        }
        catch (Exception ex) { MessageBox.Show(this, "Could not revert staged rank XP: " + ex.Message, "Revert failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ValidateExperienceCell(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (_isLoading || e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "UpdatedXP") return;
        if (!int.TryParse(Convert.ToString(e.FormattedValue, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            e.Cancel = true;
            _grid.Rows[e.RowIndex].ErrorText = "Enter a non-negative whole number.";
        }
        else
        {
            _grid.Rows[e.RowIndex].ErrorText = string.Empty;
        }
    }

    private void UpdateDirtyState()
    {
        UpdateLevel30Total();
        _experienceGraph.Invalidate();
        var dirty = IsDirty;
        _saveButton.Enabled = _revertUnstaged.Enabled = _rankSettingsPath is not null && dirty;
        _revertStaged.Enabled = _rankSettingsPath is not null && _stagedRank is not null;
        RefreshRankChangeColors();
        if (dirty && _rankSettingsPath is not null) _status.Text = _stagedRank is null
            ? "Unstaged XP preview. Stage changes, then use Start → Apply pending changes."
            : "Rank XP is staged. New preview edits are unstaged until you stage again.";
        else if (_stagedRank is not null) _status.Text = "Rank XP staged. Use Start → Apply pending changes.";
        else if (_rankSettingsPath is not null) _status.Text = "No staged XP changes. Edit the preview, then Stage changes.";
    }

    private void RefreshRankChangeColors()
    {
        var rows = RankRows;
        for (var i = 0; i < rows.Length && i < _loadedValues.Count; i++)
        {
            var row = rows[i]; var cell = row.Cells["UpdatedXP"];
            var valid = int.TryParse(Convert.ToString(cell.Value, CultureInfo.InvariantCulture), out var value);
            var staged = _stagedRank is not null && i < _stagedRank.Values.Count ? (int?)_stagedRank.Values[i] : null;
            var state = !valid ? "Invalid preview" : value != _loadedValues[i] ? staged == value ? "Staged" : "Unstaged preview"
                : Convert.ToInt32(row.Cells["OriginalXP"].Value, CultureInfo.InvariantCulture) != value ? "In files" : "Unchanged";
            var color = !valid || value != _loadedValues[i] ? AppColors.Get("EngineMatch") : state == "In files" ? AppColors.Get("DAF8E0")
                : AppColors.Get(row.Index % 2 == 0 ? "Surface" : "AlternateRow");
            cell.Style.BackColor = cell.Style.SelectionBackColor = color;
            cell.ToolTipText = $"{state}. In files: {_loadedValues[i]:N0} XP" + (staged is null ? "" : $"; staged: {staged:N0} XP");
        }
    }

    private void UpdateLevel30Total()
    {
        if (!TryReadGridValues(out var values))
        {
            _level30Total.Text = "Total XP needed to reach level 30: —";
            return;
        }
        _level30Total.Text = $"Total XP needed to reach level 30: {values.Sum(value => (long)value):N0} XP";
    }

    internal static object? PrepareRankData(string workspace)
    {
        var path = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[ps]", "rank_settings.sso"));
        if (!File.Exists(path)) return null;
        var text = WorkspaceDataCache.ReadAllText(path, Encoding.UTF8);
        var values = ParseIntegerArray(text, RankExperienceArray, "rankExp");
        var originalPath = WorkspaceOriginals.GetOriginalReadPath(workspace, Path.Combine("[ps]", "rank_settings.sso"));
        if (!File.Exists(originalPath)) originalPath = path;
        var originalText = WorkspaceDataCache.ReadAllText(originalPath, Encoding.UTF8);
        var originalValues = ParseIntegerArray(originalText, RankExperienceArray, "rankExp");
        return new PreparedRankData(path, text, values, originalValues);
    }

    private sealed record PreparedRankData(string Path, string Text, List<int> Values, List<int> OriginalValues);
    private void LoadRegionValues(string workspace)
    {
        _regionGrid.Rows.Clear();
        var regions = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var regionMetadata = WorkspaceDataCache.GetRegionMetadata(workspace);
            var preparedRegionXp = (SortedDictionary<string, long>)WorkspaceDataCache.GetRegionXpData(workspace);
            foreach (var (region, xp) in preparedRegionXp)
                regions[region] = xp;

            _updatingRegionChecks = true;
            foreach (var (region, xp) in regions)
            {
                var code = region.Replace(' ', '_').ToLowerInvariant();
                var metadata = regionMetadata.GetValueOrDefault(code, new RegionMetadata("Unknown region", "DLC", false));
                var rowIndex = _regionGrid.Rows.Add(true, _ownedRegionState.IsOwned(code), metadata.Name, code, metadata.DlcName, xp);
                _regionGrid.Rows[rowIndex].Tag = metadata;
                if (metadata.IsBaseGame)
                {
                    _regionGrid.Rows[rowIndex].Cells["Owned"].ReadOnly = true;
                    _regionGrid.Rows[rowIndex].DefaultCellStyle.BackColor = BaseGameRegionColor;
                    _regionGrid.Rows[rowIndex].DefaultCellStyle.SelectionBackColor = BaseGameRegionColor;
                    _regionGrid.Rows[rowIndex].DefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
                }
            }

            _updatingRegionChecks = false;
            UpdateRegionGridLayout();
            ApplyRegionProgressionOrder();
            _selectAllRegions.Checked = _regionGrid.Rows.Count > 0;
            UpdateSelectedRegionTotal();
        }
        catch (Exception ex)
        {
            _updatingRegionChecks = false;
            _selectedRegionTotal.Text = "Region totals could not be loaded.";
            _status.Text = $"Could not read region XP rewards: {ex.Message}";
        }
    }

    internal static object PrepareRegionXpData(string workspace)
    {
        var regions = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in WorkspaceOriginals.EnumerateCurrentFiles(workspace, "[ps]", "*.sso"))
        {
            var fileMatch = RegionCodeFromFileName.Match(Path.GetFileName(file));
            if (!fileMatch.Success) continue;
            var region = $"{fileMatch.Groups["country"].Value.ToUpperInvariant()} {fileMatch.Groups["region"].Value}";
            var content = WorkspaceDataCache.ReadAllText(file, Encoding.UTF8);
            foreach (Match reward in RewardExperience.Matches(content))
            {
                if (long.TryParse(reward.Groups["amount"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
                    regions[region] = regions.GetValueOrDefault(region) + amount;
            }
        }
        return regions;
    }
    internal static Dictionary<string, RegionMetadata> LoadRegionMetadata(string workspace, IReadOnlyDictionary<string, string> localizedNames)
    {
        var result = new Dictionary<string, RegionMetadata>(StringComparer.OrdinalIgnoreCase);
        var stringsFile = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[strings]", "strings_english.str"));
        var descriptionsFile = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[ps]", "zone_desc_storage.sso"));
        var dlcLibraryFile = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[ps]", "dlc_library.sso"));
        if (!File.Exists(stringsFile) || !File.Exists(descriptionsFile)) return result;

        var localizedDlcNames = localizedNames;
        var dlcByRegion = File.Exists(dlcLibraryFile)
            ? LoadDlcNamesByRegion(WorkspaceDataCache.ReadAllText(dlcLibraryFile, Encoding.UTF8), localizedDlcNames)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match region in RegionUiNames.Matches(WorkspaceDataCache.ReadAllText(descriptionsFile, Encoding.UTF8)))
        {
            if (localizedNames.TryGetValue(region.Groups["key"].Value, out var name))
            {
                var code = region.Groups["code"].Value;
                var isBaseGame = !region.Groups["details"].Value.Contains("dlcStoreImage", StringComparison.OrdinalIgnoreCase);
                var dlcName = isBaseGame ? "Base game" : dlcByRegion.GetValueOrDefault(code, "DLC");
                result[code] = new RegionMetadata(name, dlcName, isBaseGame);
            }
        }
        return result;
    }

    private static Dictionary<string, string> LoadDlcNamesByRegion(string dlcLibrary, IReadOnlyDictionary<string, string> localizedDlcNames)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sections = DlcTopLevelSection.Matches(dlcLibrary).Cast<Match>().ToArray();
        for (var i = 0; i < sections.Length; i++)
        {
            var start = sections[i].Index;
            var end = i + 1 < sections.Length ? sections[i + 1].Index : dlcLibrary.Length;
            var body = dlcLibrary.Substring(start, end - start);
            var nameMatch = DlcNameKey.Match(body);
            if (!nameMatch.Success || !localizedDlcNames.TryGetValue(nameMatch.Groups["key"].Value, out var dlcName)) continue;
            foreach (Match region in DlcRegionList.Matches(body))
                foreach (Match code in QuotedRegionCode.Matches(region.Groups["regions"].Value))
                    result[code.Groups["code"].Value] = dlcName;
        }
        return result;
    }

    internal sealed record RegionMetadata(string Name, string DlcName, bool IsBaseGame);
    private sealed record RegionXpPoint(string Name, long Xp);

    private void UpdateOwnedRegion(string regionCode, bool isOwned)
    {
        _updatingOwnedRegion = true;
        foreach (DataGridViewRow row in _regionGrid.Rows)
        {
            if (row.Tag is RegionMetadata { IsBaseGame: true }) continue;
            if (string.Equals(Convert.ToString(row.Cells["RegionCode"].Value), regionCode, StringComparison.OrdinalIgnoreCase))
                row.Cells["Owned"].Value = isOwned;
        }
        _updatingOwnedRegion = false;
    }

    private void UpdateSelectedRegionTotal()
    {
        long total = 0;
        foreach (DataGridViewRow row in _regionGrid.Rows)
        {
            if (row.Cells["Include"].Value is true &&
                long.TryParse(Convert.ToString(row.Cells["RegionXP"].Value, CultureInfo.CurrentCulture), NumberStyles.Number, CultureInfo.CurrentCulture, out var amount))
                total += amount;
        }
        _selectedRegionTotal.Text = $"Selected region total: {total:N0} XP";
        UpdateLinearProgression();
        if (!_updatingRegionChecks)
        {
            _updatingRegionChecks = true;
            _selectAllRegions.Checked = _regionGrid.Rows.Count > 0 && _regionGrid.Rows.Cast<DataGridViewRow>().All(row => row.Cells["Include"].Value is true);
            _updatingRegionChecks = false;
        }
    }

    private void SelectOwnedRegions()
    {
        _updatingRegionChecks = true;
        foreach (DataGridViewRow row in _regionGrid.Rows)
            row.Cells["Include"].Value = row.Cells["Owned"].Value is true;
        _updatingRegionChecks = false;

        UpdateSelectedRegionTotal();
        UpdateExperienceGraphLayout();
        _experienceGraph.Invalidate();
        UpdateLinearProgression();
    }

    private void ApplyRegionProgressionOrder()
    {
        if (_regionGrid.Rows.Count == 0) return;
        try
        {
            var order = _regionProgressionStore.Load().Order;
            var positions = order.Select((code, index) => (code, index))
                .ToDictionary(pair => pair.code, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            var fallback = new RegionRowComparer("DlcName", ListSortDirection.Ascending);
            var rows = _regionGrid.Rows.Cast<DataGridViewRow>()
                .OrderBy(row => positions.GetValueOrDefault(Convert.ToString(row.Cells["RegionCode"].Value) ?? "", int.MaxValue))
                .ThenBy(row => row, Comparer<DataGridViewRow>.Create((first, second) => fallback.Compare(first, second))).ToArray();
            for (var index = 0; index < rows.Length; index++)
            {
                if (_regionGrid.Rows[index] == rows[index]) continue;
                _regionGrid.Rows.Remove(rows[index]);
                _regionGrid.Rows.Insert(index, rows[index]);
            }
            _experienceGraph.Invalidate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _status.Text = "Could not read Region progression order: " + ex.Message;
        }
    }
    private sealed class RegionRowComparer(string columnName, ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var first = (DataGridViewRow)x!;
            var second = (DataGridViewRow)y!;
            var firstValue = first.Cells[columnName].Value;
            var secondValue = second.Cells[columnName].Value;
            int comparison;
            if (columnName == "DlcName")
            {
                if (first.Tag is RegionMetadata { IsBaseGame: true } && second.Tag is RegionMetadata { IsBaseGame: true })
                {
                    var firstBaseOrder = BaseGameRegionOrder(Convert.ToString(first.Cells["RegionName"].Value, CultureInfo.CurrentCulture));
                    var secondBaseOrder = BaseGameRegionOrder(Convert.ToString(second.Cells["RegionName"].Value, CultureInfo.CurrentCulture));
                    if (firstBaseOrder != secondBaseOrder) return firstBaseOrder.CompareTo(secondBaseOrder);
                }
                var firstText = Convert.ToString(firstValue, CultureInfo.CurrentCulture) ?? string.Empty;
                var secondText = Convert.ToString(secondValue, CultureInfo.CurrentCulture) ?? string.Empty;
                var firstSeason = SeasonNumberFromDlcName.Match(firstText);
                var secondSeason = SeasonNumberFromDlcName.Match(secondText);
                if (firstSeason.Success && secondSeason.Success)
                    comparison = int.Parse(firstSeason.Groups["number"].Value, CultureInfo.InvariantCulture).CompareTo(int.Parse(secondSeason.Groups["number"].Value, CultureInfo.InvariantCulture));
                else if (firstSeason.Success != secondSeason.Success)
                    comparison = firstSeason.Success ? 1 : -1;
                else
                    comparison = string.Compare(firstText, secondText, StringComparison.CurrentCultureIgnoreCase);
                if (comparison != 0)
                    return direction == ListSortDirection.Ascending ? comparison : -comparison;

                if (first.Tag is RegionMetadata { IsBaseGame: true } && second.Tag is RegionMetadata { IsBaseGame: true })
                {
                    var firstBaseOrder = BaseGameRegionOrder(Convert.ToString(first.Cells["RegionName"].Value, CultureInfo.CurrentCulture));
                    var secondBaseOrder = BaseGameRegionOrder(Convert.ToString(second.Cells["RegionName"].Value, CultureInfo.CurrentCulture));
                    if (firstBaseOrder != secondBaseOrder) return firstBaseOrder.CompareTo(secondBaseOrder);
                }

                return string.Compare(
                    Convert.ToString(first.Cells["RegionName"].Value, CultureInfo.CurrentCulture),
                    Convert.ToString(second.Cells["RegionName"].Value, CultureInfo.CurrentCulture),
                    StringComparison.CurrentCultureIgnoreCase);
            }
            else if (columnName == "RegionXP")
                comparison = Comparer<long>.Default.Compare(Convert.ToInt64(firstValue, CultureInfo.CurrentCulture), Convert.ToInt64(secondValue, CultureInfo.CurrentCulture));
            else if (columnName == "Include")
                comparison = Comparer<bool>.Default.Compare(Convert.ToBoolean(firstValue, CultureInfo.CurrentCulture), Convert.ToBoolean(secondValue, CultureInfo.CurrentCulture));
            else
                comparison = string.Compare(Convert.ToString(firstValue, CultureInfo.CurrentCulture), Convert.ToString(secondValue, CultureInfo.CurrentCulture), StringComparison.CurrentCultureIgnoreCase);

            return direction == ListSortDirection.Ascending ? comparison : -comparison;
        }

        private static int BaseGameRegionOrder(string? name)
        {
            if (string.Equals(name?.Trim(), "Michigan", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(name?.Trim(), "Alaska", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(name?.Trim(), "Taymyr", StringComparison.OrdinalIgnoreCase)) return 2;
            return int.MaxValue;
        }
    }

    private void UpdateRegionGridLayout()
    {
        // The visible columns use fill sizing to occupy the table width.
        if (_regionPanel is null) return;
        ApplySectionWidths();
        _regionPanel.PerformLayout();
        _regionSection?.PerformLayout();
        _controlsSection?.PerformLayout();
        _graphsSection?.PerformLayout();
        _leftColumn?.PerformLayout();
        ApplySectionHeights();
        UpdateEditorContentLayout();
    }

    private void SetAllRegionsIncluded(bool include)
    {
        _updatingRegionChecks = true;
        foreach (DataGridViewRow row in _regionGrid.Rows)
            row.Cells["Include"].Value = include;
        _updatingRegionChecks = false;
        UpdateSelectedRegionTotal();
        UpdateExperienceGraphLayout();
        _experienceGraph.Invalidate();
    }

    private void ResizeContentLayout()
    {
        if (_contentScrollHost is null || _contentLayout is null || _regionPanel is null) return;
        ApplySectionWidths();
        _regionPanel.PerformLayout();
        _regionSection?.PerformLayout();
        _controlsSection?.PerformLayout();
        _rightColumn?.PerformLayout();
        _graphsSection?.PerformLayout();
        _leftColumn?.PerformLayout();
        ApplySectionHeights();
    }

    private void ApplySectionWidths()
    {
        if (_regionPanel is null || _contentLayout is null) return;
        var leftWidth = LeftColumnWidth;
        var controlsWidth = leftWidth;
        var availableGraphSectionWidth = _contentScrollHost is null
            ? GraphsSectionWidth
            : _contentScrollHost.ClientSize.Width - _contentLayout.Padding.Horizontal - leftWidth - ColumnDividerWidth;
        var graphSectionWidth = Math.Max(GraphsSectionWidth, availableGraphSectionWidth);
        var regionPanelWidth = leftWidth - 24;
        var controlsPanelWidth = controlsWidth - 24;
        var rightInnerWidth = graphSectionWidth - 24;
        var rankTableWidth = Math.Max(170, rightInnerWidth * 0.216f);
        var graphWidth = Math.Max(180, rightInnerWidth - (int)rankTableWidth - 28);

        _regionPanel.Width = regionPanelWidth;
        if (_curveButtonsPanel is not null) _curveButtonsPanel.Width = controlsPanelWidth;
        if (_regionSection is not null) _regionSection.Width = leftWidth;
        if (_controlsSection is not null) _controlsSection.Width = controlsWidth;
        if (_leftColumn is not null)
        {
        _leftColumn.Width = leftWidth;
            _leftColumn.ColumnStyles[0].Width = leftWidth;
        }
        if (_rightColumn is not null)
        {
            _rightColumn.Width = rightInnerWidth;
            _rightColumn.ColumnStyles[0].Width = (int)rankTableWidth;
        }
        if (_rankPanel is not null) _rankPanel.Width = (int)rankTableWidth;
        _experienceGraph.Width = graphWidth;
        if (_graphsSection is not null) _graphsSection.Width = graphSectionWidth;

        _contentLayout.ColumnStyles[0].Width = leftWidth;
        _contentLayout.ColumnStyles[2].Width = graphSectionWidth;
        _contentLayout.Width = leftWidth + graphSectionWidth + _contentLayout.Padding.Horizontal + ColumnDividerWidth;
    }

    private void ApplySectionHeights()
    {
        if (_contentScrollHost is null || _contentLayout is null || _regionGrid is null) return;

        _contentLayout.PerformLayout();
        var fixedHeight = _contentLayout.Padding.Vertical;
        var availableContentWidth = Math.Max(1, _contentLayout.Width - _contentLayout.Padding.Horizontal);
        for (var row = 0; row < _contentLayout.RowCount; row++)
        {
            if (row == 3) continue;
            var control = _contentLayout.GetControlFromPosition(0, row);
            if (control is null) continue;
            var preferred = control.GetPreferredSize(new Size(availableContentWidth, 0));
            fixedHeight += preferred.Height + control.Margin.Vertical;
        }

        const int regionSectionHeight = RegionSectionHeight;
        const int controlsHeight = ControlsSectionHeight;
        const int graphsSectionHeight = GraphsSectionHeight;
        const int leftColumnHeight = GraphsSectionHeight;
        var contentHeight = fixedHeight + Math.Max(leftColumnHeight, graphsSectionHeight);
        _contentLayout.Height = contentHeight;
        _contentLayout.PerformLayout();

        _regionPanel!.Dock = DockStyle.Fill;
        _regionSection!.Height = regionSectionHeight;
        _controlsSection!.Height = controlsHeight;
        _leftColumn!.Height = leftColumnHeight;
        _leftColumn.RowStyles[0].SizeType = SizeType.Absolute;
        _leftColumn.RowStyles[0].Height = regionSectionHeight;
        _leftColumn.RowStyles[1].SizeType = SizeType.Absolute;
        _leftColumn.RowStyles[1].Height = controlsHeight;
        _graphsSection!.Height = graphsSectionHeight;
        _grid.Dock = DockStyle.Top;
        _rankPanel!.Dock = DockStyle.Fill;
        _rankPanel.RowStyles[0].SizeType = SizeType.Absolute;
        _rankPanel.RowStyles[0].Height = RankSummaryHeight;
        _rankPanel.RowStyles[1].SizeType = SizeType.AutoSize;
        _rightColumn!.Height = graphsSectionHeight - 44;
        _rightColumn.RowStyles[0].SizeType = SizeType.Percent;
        _rightColumn.RowStyles[0].Height = 100;

        _regionPanel.PerformLayout();
        _regionSection?.PerformLayout();
        _controlsSection?.PerformLayout();
        _leftColumn?.PerformLayout();
        _rightColumn?.PerformLayout();
        _graphsSection?.PerformLayout();
        _contentLayout.PerformLayout();
    }

    private void UpdateRankGridHeight()
    {
        _grid.Height = _grid.ColumnHeadersHeight + _grid.Rows.GetRowsHeight(DataGridViewElementStates.Visible) + 2;
        _grid.PerformLayout();
        _rankPanel?.PerformLayout();
    }

    private void UpdateEditorContentLayout()
    {
        if (_rankPanel is null) return;
        _rankPanel.PerformLayout();
        _progressionGraphPanel?.PerformLayout();
        _rightColumn?.PerformLayout();
        _graphsSection?.PerformLayout();
        UpdateExperienceGraphLayout();
        _contentLayout?.PerformLayout();
    }

    private void UpdateExperienceGraphLayout()
    {
        _experienceGraph.Invalidate();
    }

    private void DrawExperienceGraph(object? sender, PaintEventArgs e)
    {
        var originalSteps = TryReadGridColumnValues("OriginalXP", out var originalValues) ? originalValues : null;
        var modifiedSteps = TryReadGridColumnValues("UpdatedXP", out var modifiedValues) ? modifiedValues : null;
        DrawProgressionGraph(_experienceGraph, e.Graphics, originalSteps, modifiedSteps, _flipExperienceGraphAxes, _flipExperienceGraphYAxis);
    }

    private void DrawProgressionGraph(
        BufferedPanel graph,
        Graphics graphics,
        IReadOnlyList<int>? originalSteps,
        IReadOnlyList<int>? modifiedSteps,
        bool flipAxes,
        bool flipYAxis)
    {
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(AppColors.Get("Surface"));
        if ((originalSteps is null || originalSteps.Count == 0) && (modifiedSteps is null || modifiedSteps.Count == 0))
        {
            TextRenderer.DrawText(graphics, "Load rank XP settings to calculate region levels.", Font, graph.ClientRectangle, SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var regions = _regionGrid.Rows.Cast<DataGridViewRow>()
            .Where(row => row.Cells["Include"].Value is true)
            .Select(row => new RegionXpPoint(
                Convert.ToString(row.Cells["RegionName"].Value, CultureInfo.CurrentCulture) ?? "Region",
                long.TryParse(Convert.ToString(row.Cells["RegionXP"].Value, CultureInfo.CurrentCulture), NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) ? amount : 0))
            .ToList();
        if (regions.Count == 0)
        {
            TextRenderer.DrawText(graphics, "Select one or more regions to show level progression.", Font, graph.ClientRectangle, SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        const int maxLevel = 30;
        const int labelWidth = 190;
        const int topMargin = 42;
        const int bottomMargin = 36;
        const int rightMargin = 24;
        const int leftMargin = 48;
        var plotLeft = flipAxes ? leftMargin : labelWidth;
        var plotRight = Math.Max(plotLeft + 100, graph.ClientSize.Width - rightMargin);
        var plotTop = topMargin;
        var plotBottom = Math.Max(plotTop + 40, graph.ClientSize.Height - bottomMargin);
        var plotWidth = plotRight - plotLeft;
        var plotHeight = plotBottom - plotTop;
        var rowHeight = plotHeight / (float)regions.Count;
        using var axisPen = new Pen(AppColors.Get("GraphAxis"), 1);
        using var graphFont = new Font(Font.FontFamily, Math.Max(7f, Font.Size - 1f));

        for (var level = 1; level <= maxLevel; level++)
        {
            var coordinate = level - 1;
            var x = plotLeft + coordinate * plotWidth / (maxLevel - 1);
            var y = flipYAxis
                ? plotTop + coordinate * plotHeight / (maxLevel - 1f)
                : plotBottom - coordinate * plotHeight / (maxLevel - 1f);
            var major = level % 5 == 0;
            using var levelPen = new Pen(major ? AppColors.Get("GraphGridMajor") : AppColors.Get("GraphGridMinor"), major ? 1.5f : 1f);
            if (flipAxes)
            {
                graphics.DrawLine(levelPen, plotLeft, y, plotRight, y);
                if (major || level == 1)
                    TextRenderer.DrawText(graphics, level.ToString(CultureInfo.InvariantCulture), graphFont,
                        new Rectangle(4, (int)y - 8, leftMargin - 8, 16), SystemColors.ControlText,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            else
            {
                graphics.DrawLine(levelPen, x, plotTop, x, plotBottom);
                if (major || level == 1)
                    TextRenderer.DrawText(graphics, level.ToString(CultureInfo.InvariantCulture), graphFont,
                        new Rectangle(x - 16, 2, 32, topMargin - 4), SystemColors.ControlText,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
        graphics.DrawLine(axisPen, plotLeft, plotTop, plotLeft, plotBottom);
        graphics.DrawLine(axisPen, plotLeft, plotBottom, plotRight, plotBottom);

        if (!flipAxes)
        {
            for (var i = 0; i < regions.Count; i++)
            {
                var y = flipYAxis
                    ? plotBottom - rowHeight * (i + 0.5f)
                    : plotTop + rowHeight * (i + 0.5f);
                var labelBounds = new Rectangle(4, (int)(y - rowHeight / 2), labelWidth - 12, Math.Max(14, (int)rowHeight));
                TextRenderer.DrawText(graphics, regions[i].Name, graphFont, labelBounds, SystemColors.ControlText,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }

        var originalLevels = originalSteps is null ? null : GetRegionLevels(regions, originalSteps);
        var modifiedLevels = modifiedSteps is null ? null : GetRegionLevels(regions, modifiedSteps);
        DrawProgressionSeries(graphics, originalLevels, plotLeft, plotTop, plotWidth, plotHeight, rowHeight,
            regions.Count, flipAxes, flipYAxis, AppColors.Get("GraphOriginal"), markerIsSquare: false);
        DrawProgressionSeries(graphics, modifiedLevels, plotLeft, plotTop, plotWidth, plotHeight, rowHeight,
            regions.Count, flipAxes, flipYAxis, AppColors.Get("GraphModified"), markerIsSquare: true);
        DrawProgressionLegend(graphics, graphFont);

        if (flipAxes)
        {
            var labelStride = Math.Max(1, (int)Math.Ceiling(regions.Count / 8d));
            TextRenderer.DrawText(graphics, "Region order", graphFont,
                new Rectangle(plotLeft, graph.ClientSize.Height - 20, plotWidth, 16), SystemColors.ControlText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            for (var i = 0; i < regions.Count; i += labelStride)
            {
                var x = plotLeft + (i + 1f) * plotWidth / regions.Count;
                TextRenderer.DrawText(graphics, (i + 1).ToString(CultureInfo.InvariantCulture), graphFont,
                    new Rectangle((int)x - 18, plotBottom + 1, 36, 14), SystemColors.ControlText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }

    private static List<int> GetRegionLevels(IReadOnlyList<RegionXpPoint> regions, IReadOnlyList<int> rankSteps)
    {
        long cumulativeXp = 0;
        var levels = new List<int>(regions.Count);
        foreach (var region in regions)
        {
            cumulativeXp += region.Xp;
            levels.Add(LevelForExperience(cumulativeXp, rankSteps));
        }
        return levels;
    }

    private static void DrawProgressionSeries(
        Graphics graphics,
        IReadOnlyList<int>? levels,
        int plotLeft,
        int plotTop,
        int plotWidth,
        int plotHeight,
        float rowHeight,
        int regionCount,
        bool flipAxes,
        bool flipYAxis,
        Color color,
        bool markerIsSquare)
    {
        if (levels is null || levels.Count == 0) return;
        const int maxLevel = 30;
        var points = new List<PointF>(regionCount + 1)
        {
            flipAxes
                ? new PointF(plotLeft, flipYAxis ? plotTop : plotTop + plotHeight)
                : new PointF(plotLeft, flipYAxis ? plotTop + plotHeight : plotTop)
        };
        for (var i = 0; i < levels.Count; i++)
        {
            var offset = markerIsSquare ? 3f : -3f;
            var x = flipAxes
                ? plotLeft + (i + 1f) * plotWidth / regionCount + offset
                : plotLeft + (levels[i] - 1) * plotWidth / (maxLevel - 1f);
            var y = flipAxes
                ? (flipYAxis
                    ? plotTop + (levels[i] - 1) * plotHeight / (maxLevel - 1f)
                    : plotTop + plotHeight - (levels[i] - 1) * plotHeight / (maxLevel - 1f))
                : (flipYAxis
                    ? plotTop + plotHeight - rowHeight * (i + 0.5f) + offset
                    : plotTop + rowHeight * (i + 0.5f) + offset);
            points.Add(new PointF(x, y));
        }
        using var curvePen = new Pen(color, 2.5f);
        using var markerBrush = new SolidBrush(color);
        if (points.Count > 1) graphics.DrawCurve(curvePen, points.ToArray(), 0.2f);
        foreach (var point in points.Skip(1))
        {
            if (markerIsSquare)
                graphics.FillRectangle(markerBrush, point.X - 4f, point.Y - 4f, 8f, 8f);
            else
                graphics.FillEllipse(markerBrush, point.X - 4.5f, point.Y - 4.5f, 9f, 9f);
        }
    }

    private static void DrawProgressionLegend(Graphics graphics, Font font)
    {
        DrawLegendItem(graphics, font, 8, 8, "Original", AppColors.Get("GraphOriginal"), markerIsSquare: false);
        DrawLegendItem(graphics, font, 102, 8, "Modified", AppColors.Get("GraphModified"), markerIsSquare: true);
    }

    private static void DrawLegendItem(Graphics graphics, Font font, int x, int y, string label, Color color, bool markerIsSquare)
    {
        using var pen = new Pen(color, 2.5f);
        using var brush = new SolidBrush(color);
        graphics.DrawLine(pen, x, y + 7, x + 22, y + 7);
        if (markerIsSquare)
            graphics.FillRectangle(brush, x + 8, y + 3, 8, 8);
        else
            graphics.FillEllipse(brush, x + 8, y + 3, 9, 9);
        TextRenderer.DrawText(graphics, label, font, new Point(x + 28, y), SystemColors.ControlText);
    }

    private static int LevelForExperience(long totalXp, IReadOnlyList<int> rankSteps)
    {
        long threshold = 0;
        var level = 1;
        foreach (var stepXp in rankSteps)
        {
            threshold += stepXp;
            if (totalXp < threshold) break;
            level++;
        }
        return level;
    }

    private void SetEditorEnabled(bool enabled)
    {
        _grid.Enabled = enabled;
        _saveButton.Enabled = false;
        _revertUnstaged.Enabled = _revertStaged.Enabled = false;
        _reloadButton.Enabled = true;
    }

    private bool TryReadGridValues(out List<int> values) => TryReadGridColumnValues("UpdatedXP", out values);

    private bool TryReadGridColumnValues(string columnName, out List<int> values)
    {
        values = new List<int>(_grid.Rows.Count);
        foreach (DataGridViewRow row in RankRows)
        {
            if (!int.TryParse(Convert.ToString(row.Cells[columnName].Value, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
                return false;
            values.Add(value);
        }
        return true;
    }

    private void UpdateLinearProgression()
    {
        var stepCount = _grid.Rows.Count;
        long selectedXp = 0;
        foreach (DataGridViewRow row in _regionGrid.Rows)
        {
            if (row.Cells["Include"].Value is true &&
                long.TryParse(Convert.ToString(row.Cells["RegionXP"].Value, CultureInfo.CurrentCulture), NumberStyles.Number, CultureInfo.CurrentCulture, out var amount))
                selectedXp += amount;
        }

        var maxReductionSteps = (int)Math.Min(int.MaxValue, selectedXp / 5_000);
        _updatingXpSlider = true;
        _xpReductionSlider.Maximum = Math.Max(1, maxReductionSteps);
        _xpReductionSlider.TickFrequency = Math.Max(1, maxReductionSteps / 10);
        _xpReductionSlider.Value = Math.Min(_xpReductionSlider.Value, _xpReductionSlider.Maximum);
        _xpReductionSlider.Enabled = maxReductionSteps > 0;
        _updatingXpSlider = false;
        var reductionXp = (long)_xpReductionSlider.Value * 5_000L;
        var targetXp = Math.Max(0L, selectedXp - reductionXp);
        _xpBudgetLabel.Text = $"−{reductionXp:N0} → {targetXp:N0} XP";
        if (stepCount == 0)
        {
            UpdateEditorContentLayout();
            return;
        }

        var budgetHundreds = (long)Math.Round(targetXp / 100d, MidpointRounding.AwayFromZero);
        var magnitude = _curveMagnitudes.GetValueOrDefault(_selectedXpCurve, 1d);
        var weights = Enumerable.Range(0, stepCount)
            .Select(index => CurveWeight(_selectedXpCurve, (index + 1d) / stepCount, magnitude))
            .ToArray();
        var weightTotal = weights.Sum();
        var rawHundreds = weights.Select(weight => budgetHundreds * weight / weightTotal).ToArray();
        var allocatedHundreds = rawHundreds.Select(Math.Floor).Select(value => (long)value).ToArray();
        var remainingHundreds = budgetHundreds - allocatedHundreds.Sum();
        foreach (var index in Enumerable.Range(0, stepCount)
                     .OrderByDescending(index => rawHundreds[index] - allocatedHundreds[index])
                     .ThenBy(index => index)
                     .Take((int)remainingHundreds))
            allocatedHundreds[index]++;

        _isLoading = true;
        for (var i = 0; i < stepCount; i++)
            RankRows[i].Cells["UpdatedXP"].Value = allocatedHundreds[i] * 100L;
        _isLoading = false;
        _updatedXpTotal.Text = "Updated values are rounded to 100 XP.";
        UpdateEditorContentLayout();
        _experienceGraph.Invalidate();
        UpdateDirtyState();
    }

    private static double CurveWeight(string curveName, double progress, double magnitude) => curveName switch
    {
        "Exponential" => Math.Exp(magnitude * progress),
        _ => 1d
    };

    private static List<int> ParseIntegerArray(string text, Regex arrayPattern, string name)
    {
        var match = arrayPattern.Match(text);
        if (!match.Success) throw new InvalidDataException($"Could not find the {name} array.");
        var values = IntegerValue.Matches(match.Groups["body"].Value)
            .Select(value => int.Parse(value.Value, CultureInfo.InvariantCulture))
            .ToList();
        if (values.Count == 0) throw new InvalidDataException($"The {name} array is empty.");
        return values;
    }

    private static string ReplaceIntegerArray(string text, Regex arrayPattern, IReadOnlyList<int> values, string name)
    {
        var match = arrayPattern.Match(text);
        if (!match.Success) throw new InvalidDataException($"Could not find the {name} array.");
        var body = match.Groups["body"];
        var matches = IntegerValue.Matches(body.Value);
        if (matches.Count != values.Count) throw new InvalidDataException("The number of rank XP entries changed. Reload before saving.");

        var builder = new StringBuilder(body.Length + 16);
        var cursor = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            var valueMatch = matches[i];
            builder.Append(body.Value, cursor, valueMatch.Index - cursor);
            builder.Append(values[i].ToString(CultureInfo.InvariantCulture));
            cursor = valueMatch.Index + valueMatch.Length;
        }
        builder.Append(body.Value, cursor, body.Length - cursor);
        return text.Remove(body.Index, body.Length).Insert(body.Index, builder.ToString());
    }

    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }
}
