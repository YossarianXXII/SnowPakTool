using System.ComponentModel;
using System.Globalization;

namespace SnowPakTool.Desktop;

internal sealed class TruckPropertiesTab : UserControl
{
    private readonly Func<string> _getWorkspace;
    private readonly Func<Task<RegionTruckCatalog>> _getRegions;
    private readonly Func<IReadOnlyList<CustomTruck>>? _getEnabledTrucks;
    private readonly Func<UnlockStarterContext>? _getStarterContext;
    private readonly TableHeaderBehavior _tableHeaders = null!;
    private readonly TextBox _unlockLog = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, MaxLength = int.MaxValue, AccessibleName = "Unlock rank generation report" };
    private readonly CheckBox _showUnlockLog = new() { Text = "Show unlock log", AutoSize = true, Margin = new Padding(8, 7, 4, 0) };
    private readonly SplitContainer _unlockLogSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1000, 500), SplitterDistance = 320, Panel1MinSize = 60, Panel2MinSize = 60, SplitterWidth = 6, Panel2Collapsed = true };
    private readonly List<RadioButton> _unlockDifficultyOptions = [];
    private ProgressionDifficulty _unlockDifficulty = ProgressionDifficulty.Normal;
    private bool _loadingUnlockDifficulty;
    internal Func<DialogResult> ConfirmExplorationUpdate;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false, EditMode = DataGridViewEditMode.EditOnEnter,
        RowTemplate = { Height = 26 }
    };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(4, 6, 4, 4) };
    private readonly ToolTip _tips = new();
    private readonly CheckBox _changedOnly = new() { Text = "Show changed only", Appearance = Appearance.Button, AutoSize = true, Margin = new Padding(4) };
    private bool _changingChangedFilter;
    private readonly TrackBar _fuelMultiplier = new() { Minimum = 1, Maximum = 6, Value = 2, TickFrequency = 1, SmallChange = 1, LargeChange = 1, Width = 150, Height = 38, AutoSize = false, AccessibleName = "Fuel capacity multiplier" };
    private readonly Label _fuelMultiplierValue = new() { Text = "×1.0", AutoSize = true, Margin = new Padding(4, 8, 8, 0) };
    private readonly ContextMenuStrip _columnsMenu = new();
    private List<TruckPropertyRow> _rows = [];
    private string? _loadedWorkspace;
    private bool _updating;
    private bool _loading;
    private Form? _hover;
    private string _regionStatus = "";
    private readonly HashSet<string> _removeExploration = new(StringComparer.OrdinalIgnoreCase);

    public TruckPropertiesTab(Func<string> getWorkspace, Func<Task<RegionTruckCatalog>> getRegions) : this(getWorkspace, getRegions, null) { }

    public TruckPropertiesTab(Func<string> getWorkspace, Func<Task<RegionTruckCatalog>> getRegions, Func<IReadOnlyList<CustomTruck>>? getEnabledTrucks)
        : this(getWorkspace, getRegions, getEnabledTrucks, null) { }

    public TruckPropertiesTab(Func<string> getWorkspace, Func<Task<RegionTruckCatalog>> getRegions, Func<IReadOnlyList<CustomTruck>>? getEnabledTrucks, Func<UnlockStarterContext>? getStarterContext)
    {
        _getWorkspace = getWorkspace;
        _getRegions = getRegions;
        _getEnabledTrucks = getEnabledTrucks;
        _getStarterContext = getStarterContext;
        ConfirmExplorationUpdate = () => MessageBox.Show(this,
            "Update UnlockByExploration as well?\n\nYes is strongly recommended: trucks obtainable on current maps will require discovery before purchase; other trucks will not. This follows randomized/pending map-truck replacements.\n\nChanges are previewed only. Stage the truck properties and any map changes, then Apply from Start.\n\nYes = update exploration flags and ranks. No = ranks only. Cancel = change nothing.",
            "Update exploration unlocks? — Yes recommended", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { AutoSize = true, Padding = new Padding(0, 0, 0, 8),
            Text = "Edit individual values or multiply all fuel capacities, then stage and apply from Start. Changed values show the original in parentheses. Amber = edited/staged; green = applied. Blank values stay unchanged." }, 0, 0);
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        AddButton(toolbar, "Reload properties", async () => await EnsureLoadedAsync(true));
        AddButton(toolbar, "Order by tier", () => _tableHeaders.SetSort("Tier", SortOrder.Ascending));
        toolbar.Controls.Add(_changedOnly);
        AddButton(toolbar, "Revert unstaged edits", () => { RestoreDraft(); ShowRows(); });
        AddButton(toolbar, "Reset to original values", () =>
        {
            _grid.CancelEdit();
            _removeExploration.Clear();
            foreach (var row in _rows)
            {
                foreach (var spec in TruckPropertiesStore.Specs)
                    if (row.Draft.ContainsKey(spec.Attribute) && row.Original.TryGetValue(spec.Attribute, out var value)) row.Draft[spec.Attribute] = value;
                if (!row.OriginalExplorationExplicit && row.CurrentExplorationExplicit) _removeExploration.Add(row.RelativeFile);
            }
            ShowRows();
            var changes = DraftValues(true).Count;
            _status.Text = changes == 0
                ? "Truck properties already match the original values in the files. Nothing to stage."
                : $"Original values restored in preview: {changes} property changes. Stage changes, then Apply pending changes from Start.";
        });
        AddButton(toolbar, "Save preset…", SavePreset);
        AddButton(toolbar, "Load preset…", LoadPreset);
        AddButton(toolbar, "Stage changes", Stage);
        AddButton(toolbar, "Revert staged changes", () =>
        {
            if (!Ready()) return;
            TruckPropertiesStore.SavePending(_loadedWorkspace!, []);
            RestoreDraft(); ShowRows();
        });
        layout.Controls.Add(toolbar, 0, 1);
        var bulk = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        bulk.Controls.Add(new Label { Text = "Fuel multiplier", AutoSize = true, Margin = new Padding(4, 8, 0, 4) });
        bulk.Controls.Add(_fuelMultiplier);
        bulk.Controls.Add(_fuelMultiplierValue);
        _fuelMultiplier.ValueChanged += (_, _) => _fuelMultiplierValue.Text = "×" + (_fuelMultiplier.Value / 2m).ToString("0.0", CultureInfo.CurrentCulture);
        _tips.SetToolTip(_fuelMultiplier, "0.5× to 3× in 0.5 steps. Moving the slider only selects the factor.");
        var multiply = AddButton(bulk, "Multiply all fuel", MultiplyFuel);
        _tips.SetToolTip(multiply, "Multiply current fuel capacities and round to the nearest 5 litres (minimum 5 L). Repeated clicks multiply again. Individual fuel edits also round to 5 L. Stage changes to keep the result.");
        var columns = AddButton(bulk, "Columns ▾", () => { });
        columns.Click += (_, _) => _columnsMenu.Show(columns, new Point(0, columns.Height));
        var unlockOptions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(12, 0, 4, 0) };
        unlockOptions.Controls.Add(new Label { Text = "Unlock rank difficulty", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
        foreach (var difficulty in new[] { ProgressionDifficulty.Easy, ProgressionDifficulty.Normal, ProgressionDifficulty.Hard })
        {
            var radio = new RadioButton { Text = difficulty.ToString(), AutoSize = true, Checked = difficulty == _unlockDifficulty, Margin = new Padding(0, 7, 8, 0) };
            _tips.SetToolTip(radio, $"Soft cap about rank {TruckUnlockRankGenerator.SoftCap(difficulty)}. All modes have broad per-truck variation; Easy is widest and early-biased, Hard unlocks later. Gaps and shared ranks remain possible. Maximum rank 30.");
            radio.CheckedChanged += (_, _) =>
            {
                if (_loadingUnlockDifficulty || !radio.Checked) return;
                var previous = _unlockDifficulty;
                try
                {
                    if (Ready()) TruckUnlockRankSettings.Save(_loadedWorkspace!, difficulty);
                    _unlockDifficulty = difficulty;
                }
                catch (Exception ex)
                {
                    SetUnlockDifficulty(previous);
                    _status.Text = "Could not save unlock-rank difficulty: " + ex.Message;
                }
            };
            _unlockDifficultyOptions.Add(radio); unlockOptions.Controls.Add(radio);
        }
        bulk.Controls.Add(unlockOptions);
        var unlock = AddButton(bulk, "Generate unlock ranks", async () => await GenerateUnlockRanks());
        _tips.SetToolTip(unlock, "Preview tier-based ranks with variation. First saved region only: SCOUT/OFFROAD starters at rank 1, other game classes at ranks 1–3. The log explains changed, unchanged and skipped trucks. Stage to keep the ranks.");
        bulk.Controls.Add(_showUnlockLog);
        _showUnlockLog.CheckedChanged += (_, _) => _unlockLogSplit.Panel2Collapsed = !_showUnlockLog.Checked;
        layout.SizeChanged += (_, _) => _status.MaximumSize = new Size(Math.Max(200, layout.ClientSize.Width - layout.Padding.Horizontal), 0);
        layout.Controls.Add(bulk, 0, 2);
        _unlockLogSplit.Panel1.Controls.Add(_grid);
        _unlockLogSplit.Panel2.Controls.Add(_unlockLog);
        layout.Controls.Add(_unlockLogSplit, 0, 3);
        layout.Controls.Add(_status, 0, 4);
        Controls.Add(layout);
        foreach (var (name, title, width) in new[] { ("Truck", "Truck", 220), ("Class", "Class", 130), ("Tier", "Tier", 65),
                     ("FuelCapacity", "Fuel capacity (L)", 135), ("UnlockByRank", "Unlock rank", 100), ("Price", "Price", 105),
                     ("Regions", "Obtainable in region / map", 360), ("Country", "Store countries", 120), ("DiffLock", "Differential lock", 130),
                     ("UnlockByExploration", "Unlock by exploration", 160) })
        {
            var index = _grid.Columns.Add(name, title);
            var column = _grid.Columns[index]; column.Width = width;
            column.ReadOnly = name == "UnlockByExploration" || !TruckPropertiesStore.Specs.Any(spec => spec.Attribute == name);
            if (name == "UnlockByExploration") column.ValueType = typeof(decimal);
            column.SortMode = DataGridViewColumnSortMode.Automatic;
            if (!column.ReadOnly) { column.ValueType = typeof(decimal); column.DefaultCellStyle.Format = "N0"; }
            column.Visible = name != "DiffLock";
            var item = new ToolStripMenuItem(title) { CheckOnClick = true, Checked = column.Visible, Name = name };
            item.CheckedChanged += (_, _) => { HideHover(); column.Visible = item.Checked; };
            _columnsMenu.Items.Add(item);
        }
        _tableHeaders = TableHeaderBehavior.Attach(_grid);
        _tableHeaders.AddFilter("UnlockByExploration", value => Convert.ToString(value, CultureInfo.InvariantCulture) == "1" ? "true" : "false");
        _tips.SetToolTip(_changedOnly, "Show trucks with preview/staged changes or values differing from the originals. Combines with header filters; staging and reset still cover all trucks.");
        _changedOnly.CheckedChanged += (_, _) =>
        {
            if (_changingChangedFilter) return;
            if (!_grid.EndEdit())
            { _changingChangedFilter = true; _changedOnly.Checked = !_changedOnly.Checked; _changingChangedFilter = false; return; }
            _tableHeaders.SetAdditionalFilter(_changedOnly.Checked ? row => row.Tag is TruckPropertyRow data && HasChangedValues(data) : null);
        };
        _grid.CellFormatting += FormatValue;
        _grid.EditingControlShowing += (_, e) =>
        {
            if (_grid.CurrentCell is { Value: decimal value } && e.Control is TextBox editor)
            {
                editor.Text = value.ToString(CultureInfo.CurrentCulture);
                editor.SelectAll();
            }
        };
        _grid.Columns["Regions"]!.DefaultCellStyle.NullValue = "Unknown — load maps in Map trucks";
        _grid.CellValidating += ValidateCell;
        _grid.CellValueChanged += ValueChanged;
        _grid.DataError += (_, e) => { e.ThrowException = false; _status.Text = "Enter a whole number within the property's allowed range."; };
        _grid.SortCompare += (_, e) =>
        {
            if (e.Column.Name != "Tier") return;
            e.SortResult = TierOrder(Convert.ToString(e.CellValue1)).CompareTo(TierOrder(Convert.ToString(e.CellValue2)));
            if (e.SortResult == 0) e.SortResult = string.Compare(Convert.ToString(_grid.Rows[e.RowIndex1].Cells["Truck"].Value),
                Convert.ToString(_grid.Rows[e.RowIndex2].Cells["Truck"].Value), StringComparison.CurrentCultureIgnoreCase);
            e.Handled = true;
        };
        _grid.CellMouseEnter += (_, e) =>
        {
            HideHover();
            if (e.RowIndex < 0 || e.ColumnIndex != _grid.Columns["Truck"]!.Index || _grid.Rows[e.RowIndex].Tag is not TruckPropertyRow row) return;
            var preview = new TilePreview(row.Truck);
            _hover = preview;
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            preview.Location = new Point(Math.Clamp(Cursor.Position.X + 18, area.Left, Math.Max(area.Left, area.Right - preview.Width)),
                Math.Clamp(Cursor.Position.Y + 20, area.Top, Math.Max(area.Top, area.Bottom - preview.Height)));
            preview.Show(this);
        };
        _grid.CellMouseLeave += (_, _) => HideHover();
        _grid.Scroll += (_, _) => HideHover();
        VisibleChanged += (_, _) => { if (!Visible) HideHover(); };
        AppColors.Changed += RefreshColors;
        Disposed += (_, _) => { HideHover(); _tips.Dispose(); _columnsMenu.Dispose(); AppColors.Changed -= RefreshColors; };
        RefreshColors(this, EventArgs.Empty);
    }

    private Button AddButton(FlowLayoutPanel panel, string text, Action action)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(4) };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { _status.Text = ex.Message; }
        };
        panel.Controls.Add(button); return button;
    }

    public async Task EnsureLoadedAsync(bool reload = false)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var workspace = _getWorkspace().Trim();
            if (workspace.Length == 0 || !Directory.Exists(workspace))
            {
                _loadedWorkspace = null; _rows.Clear(); _grid.Rows.Clear();
                _status.Text = "Choose an extracted workspace on Start."; return;
            }
            if (reload || !string.Equals(workspace, _loadedWorkspace, StringComparison.OrdinalIgnoreCase))
            {
                _loadedWorkspace = null; _rows.Clear(); _grid.Rows.Clear();
                await WorkspaceDataCache.PreloadOwnedAsync(workspace);
                var rows = await Task.Run(() => TruckPropertiesStore.LoadRows(workspace));
                if (IsDisposed || workspace != _getWorkspace().Trim()) return;
                _rows = rows; _loadedWorkspace = workspace; _unlockLog.Clear();
                SetUnlockDifficulty(TruckUnlockRankSettings.Load(workspace));
                RestoreDraft();
            }
            ShowRows();
            _regionStatus = "Reading obtainable map placements…"; UpdateStatus();
            try
            {
                var catalog = await _getRegions();
                if (IsDisposed || workspace != _getWorkspace().Trim()) return;
                var regions = catalog.Entries.Where(entry => entry.Availability.StartsWith("Obtainable", StringComparison.Ordinal))
                    .GroupBy(entry => entry.TruckCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => string.Join("; ", group.Select(entry => $"{entry.Region} / {entry.Map}"
                        + (entry.TruckChanged ? " (replacement)" : "") + (entry.FileEntry is not null ? " [pending]" : "")).Distinct()), StringComparer.OrdinalIgnoreCase);
                foreach (DataGridViewRow row in _grid.Rows)
                    if (row.Tag is TruckPropertyRow data)
                    {
                        row.Cells["Regions"].Value = regions.TryGetValue(data.Truck.Code, out var locations) ? locations
                            : catalog.MapsRead > 0 ? "─────" : "Unknown — load maps in Map trucks";
                        row.Cells["Regions"].ToolTipText = regions.ContainsKey(data.Truck.Code) || catalog.MapsRead == 0
                            ? Convert.ToString(row.Cells["Regions"].Value) : "No verified obtainable placement in scanned maps";
                    }
                _regionStatus = $"Regions use current placements, including randomized/pending replacements ({catalog.MapsRead} maps; {catalog.Warnings.Count} warnings).";
            }
            catch (Exception ex)
            {
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    row.Cells["Regions"].Value = "Unknown — load maps in Map trucks";
                    row.Cells["Regions"].ToolTipText = ex.Message;
                }
                _regionStatus = "Obtainable regions unavailable: " + ex.Message;
            }
            UpdateStatus();
        }
        catch (Exception ex) { _loadedWorkspace = null; _status.Text = "Could not load truck properties: " + ex.Message; }
        finally { _loading = false; }
    }

    private bool Ready() => _loadedWorkspace is not null && _loadedWorkspace.Equals(_getWorkspace().Trim(), StringComparison.OrdinalIgnoreCase);
    private void RestoreDraft()
    {
        if (!Ready()) return;
        _grid.CancelEdit();
        var pending = TruckPropertiesStore.LoadSnapshot(_loadedWorkspace!);
        _removeExploration.Clear();
        foreach (var row in _rows)
        {
            row.Draft.Clear(); foreach (var pair in row.Current) row.Draft.Add(pair.Key, pair.Value);
            foreach (var edit in pending.Values.Where(edit => edit.RelativeFile.Equals(row.RelativeFile, StringComparison.OrdinalIgnoreCase)))
                if (row.Draft.ContainsKey(edit.Attribute))
                {
                    row.Draft[edit.Attribute] = edit.After;
                    if (edit.RemoveAttribute) _removeExploration.Add(row.RelativeFile);
                }
        }
    }

    private static int TierOrder(string? tier) => tier switch { "SS" => 0, "S" => 1, "A" => 2, "B" => 3, "C" => 4, "D" => 5, "FX" => 6, _ => 7 };
    private void ShowRows()
    {
        HideHover();
        var sorted = _grid.SortedColumn; var direction = _grid.SortOrder == SortOrder.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        var regions = _grid.Rows.Cast<DataGridViewRow>().Where(row => row.Tag is TruckPropertyRow)
            .ToDictionary(row => ((TruckPropertyRow)row.Tag!).RelativeFile, row => row.Cells["Regions"].Value, StringComparer.OrdinalIgnoreCase);
        _updating = true;
        try
        {
            _grid.Rows.Clear();
            var tiers = TierListStore.Load();
            foreach (var data in _rows.OrderBy(row => row.Truck.Name))
            {
                var tier = tiers.FirstOrDefault(tier => tier.TruckCodes.Contains(data.Truck.Code, StringComparer.OrdinalIgnoreCase))?.Name ?? "Unsorted";
                var index = _grid.Rows.Add(data.Truck.Name, data.Truck.Class, tier,
                    data.Draft.GetValueOrDefault("FuelCapacity"), data.Draft.GetValueOrDefault("UnlockByRank"), data.Draft.GetValueOrDefault("Price"),
                    regions.GetValueOrDefault(data.RelativeFile) ?? "Unknown — load maps in Map trucks", data.Country, data.DiffLock,
                    data.Draft.GetValueOrDefault("UnlockByExploration"));
                var row = _grid.Rows[index]; row.Tag = data;
                row.Cells["Truck"].ToolTipText = data.Truck.Code + "\n" + data.RelativeFile;
                if (tier != "Unsorted") row.Cells["Tier"].Style.BackColor = TierListStore.BackgroundFor(tier);
                foreach (var spec in TruckPropertiesStore.Specs)
                {
                    var cell = row.Cells[spec.Attribute];
                    if (!data.Draft.ContainsKey(spec.Attribute)) { cell.Value = null; cell.ReadOnly = true; }
                    PaintValue(row, spec);
                }
            }
            if (sorted is not null) _grid.Sort(sorted, direction);
        }
        finally { _updating = false; }
        _tableHeaders.Refresh();
        UpdateStatus();
    }

    private void PaintValue(DataGridViewRow row, TruckPropertySpec spec)
    {
        if (row.Tag is not TruckPropertyRow data || !data.Draft.TryGetValue(spec.Attribute, out var draft)) return;
        var cell = row.Cells[spec.Attribute];
        cell.Style.BackColor = draft != data.Current[spec.Attribute] || spec.IsBoolean && _removeExploration.Contains(data.RelativeFile) ? Color.LightGoldenrodYellow
            : data.Original.TryGetValue(spec.Attribute, out var original) && (draft != original || spec.IsBoolean && data.OriginalExplorationExplicit != data.CurrentExplorationExplicit) ? Color.Honeydew : AppColors.Get("Surface");
        cell.ToolTipText = $"Original: {(data.Original.TryGetValue(spec.Attribute, out var baseline) ? baseline.ToString("N0") : "undefined")}\nIn files: {data.Current[spec.Attribute]:N0}\nRange: {spec.Minimum:N0}–{spec.Maximum:N0}";
        if (spec.IsBoolean) cell.ToolTipText = $"Original: {Flag(data.Original.GetValueOrDefault(spec.Attribute))}\nIn files: {Flag(data.Current[spec.Attribute])}\nPreview: {Flag(draft)}\nUpdated optionally when generating ranks. True requires discovery before purchase.";
    }

    private void FormatValue(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Rows[e.RowIndex].Tag is not TruckPropertyRow data) return;
        var attribute = _grid.Columns[e.ColumnIndex].Name;
        if (e.Value is not decimal value) return;
        if (attribute == "UnlockByExploration")
        {
            if (_removeExploration.Contains(data.RelativeFile)) { e.Value = "Not present"; e.FormattingApplied = true; return; }
            var flag = value == 1 ? "true" : "false";
            if (!data.OriginalExplorationExplicit && data.CurrentExplorationExplicit)
            { e.Value = flag + " (Not present)"; e.FormattingApplied = true; return; }
            e.Value = data.Original.TryGetValue(attribute, out var baseline) && value != baseline
                ? $"{flag} ({(baseline == 1 ? "true" : "false")})" : flag;
            e.FormattingApplied = true;
            return;
        }
        if (data.Original.TryGetValue(attribute, out var original) && value != original && !_grid[e.ColumnIndex, e.RowIndex].IsInEditMode)
        {
            e.Value = $"{value:N0} ({original:N0})";
            e.FormattingApplied = true;
        }
    }

    private void SetUnlockDifficulty(ProgressionDifficulty difficulty)
    {
        _loadingUnlockDifficulty = true;
        try
        {
            _unlockDifficulty = difficulty;
            foreach (var radio in _unlockDifficultyOptions) radio.Checked = radio.Text == difficulty.ToString();
        }
        finally { _loadingUnlockDifficulty = false; }
    }

    private async Task GenerateUnlockRanks()
    {
        if (!_grid.EndEdit()) { ShowUnlockReport("Not generated: correct the active numeric cell first. No ranks were changed."); return; }
        if (!Ready()) { ShowUnlockReport("Not generated: load an extracted workspace first. No ranks were changed."); return; }
        var answer = ConfirmExplorationUpdate();
        if (answer == DialogResult.Cancel) { ShowUnlockReport("Cancelled. No ranks or exploration flags were changed."); return; }
        var workspace = _loadedWorkspace!;
        var sourceRows = _rows;
        var explorationLog = "EXPLORATION UNLOCK UPDATE — declined; existing preview flags were preserved.";
        TruckUnlockPlan plan;
        TruckExplorationUnlockPlan? exploration = null;
        Enabled = false;
        try
        {
            if (answer == DialogResult.Yes)
            {
                var catalog = await _getRegions();
                if (IsDisposed || !Ready() || workspace != _loadedWorkspace || !ReferenceEquals(sourceRows, _rows)) return;
                exploration = TruckExplorationUnlockGenerator.Generate(_rows, catalog);
                explorationLog = string.Join(Environment.NewLine, exploration.Log);
            }
            var enabled = (_getEnabledTrucks?.Invoke() ?? _rows.Select(row => row.Truck).ToArray()).Select(truck => truck.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var projected = _rows.Select(row => exploration is not null && exploration.Values.TryGetValue(row.RelativeFile, out var flag)
                ? row with { Draft = new(row.Draft) { ["UnlockByExploration"] = flag } } : row).ToArray();
            var context = _getStarterContext?.Invoke() ?? TruckUnlockRankGenerator.ResolveFirstRegion(_loadedWorkspace!, new RegionProgressionStore().Load());
            plan = TruckUnlockRankGenerator.Generate(projected, TierListStore.Load(), enabled, _unlockDifficulty, Random.Shared.Next(), context);
        }
        catch (Exception ex) { ShowUnlockReport("Not generated: " + ex.Message + Environment.NewLine + "No ranks or exploration flags were changed."); _status.Text = "Generation failed; see unlock log."; return; }
        finally { if (!IsDisposed) Enabled = true; }
        var byFile = plan.Choices.ToDictionary(choice => choice.RelativeFile, StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            if (byFile.TryGetValue(row.RelativeFile, out var choice)) row.Draft["UnlockByRank"] = choice.Rank;
            if (exploration is not null && exploration.Values.TryGetValue(row.RelativeFile, out var flag))
            { row.Draft["UnlockByExploration"] = flag; _removeExploration.Remove(row.RelativeFile); }
        }
        ShowRows();
        var changed = plan.Report.Count(row => row.State == "Changed");
        var unchanged = plan.Report.Count(row => row.State == "Unchanged");
        var skipped = plan.Report.Count(row => row.State == "Skipped");
        _status.Text = $"Unlock ranks: {changed} changed, {unchanged} unchanged, {skipped} skipped. First region: {plan.StarterContext.RegionName} ({plan.StarterContext.Country}). "
            + (plan.MissingCoverage.Count > 0 ? $"{plan.MissingCoverage.Count} starter coverage issue(s); see unlock log. " : "Starter coverage met: Scout/Offroad rank 1, other classes by rank 3. ")
            + "Preview only; stage changes to keep the ranks.";
        _tips.SetToolTip(_status, _status.Text);
        var report = new System.Text.StringBuilder();
        report.AppendLine($"UNLOCK RANK GENERATION — {_unlockDifficulty}; soft cap ≈{plan.SoftCap}; maximum rank 30; seed {plan.Seed}");
        report.AppendLine($"First region: {plan.StarterContext.RegionName} [{plan.StarterContext.RegionCode}]; store country: {(plan.StarterContext.Country.Length > 0 ? plan.StarterContext.Country : "unresolved")}. {plan.StarterContext.Note}");
        report.AppendLine("Starter rule applies only to that region: SCOUT/OFFROAD at rank 1; HIGHWAY/HEAVY_DUTY/HEAVY at a randomly selected rank 1–3. Countries and task locks are preserved; starter eligibility uses the resulting exploration flags.");
        report.AppendLine($"RESULT: {changed} changed; {unchanged} unchanged; {skipped} skipped. Before/after values refer to the preview. Stage then Apply to write changes.");
        foreach (var issue in plan.MissingCoverage) report.AppendLine("STARTER WARNING: " + issue);
        report.AppendLine(explorationLog);
        report.AppendLine();
        foreach (var row in plan.Report)
            report.AppendLine($"{row.State.ToUpperInvariant()} | {row.Name} [{row.Code}] | game class {row.GameClass}, tier {row.Tier} | preview {row.Before?.ToString(CultureInfo.InvariantCulture) ?? "undefined"} → {row.After?.ToString(CultureInfo.InvariantCulture) ?? "not edited"} | in files {row.InFiles?.ToString(CultureInfo.InvariantCulture) ?? "undefined"}; original {row.Original?.ToString(CultureInfo.InvariantCulture) ?? "undefined"} | {row.Reason}");
        ShowUnlockReport(report.ToString());
    }

    private void ShowUnlockReport(string text)
    {
        _unlockLog.Text = text;
        _showUnlockLog.Checked = true;
        var available = _unlockLogSplit.ClientSize.Height - _unlockLogSplit.SplitterWidth;
        if (available >= _unlockLogSplit.Panel1MinSize + _unlockLogSplit.Panel2MinSize)
            _unlockLogSplit.SplitterDistance = Math.Clamp(available - 190, _unlockLogSplit.Panel1MinSize, available - _unlockLogSplit.Panel2MinSize);
        _unlockLog.SelectionStart = 0; _unlockLog.SelectionLength = 0; _unlockLog.ScrollToCaret();
    }

    private void ValidateCell(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (_updating || e.RowIndex < 0 || _grid[e.ColumnIndex, e.RowIndex].ReadOnly) return;
        var spec = TruckPropertiesStore.Specs.FirstOrDefault(spec => spec.Attribute == _grid.Columns[e.ColumnIndex].Name);
        if (spec is null) return;
        if (!decimal.TryParse(Convert.ToString(e.FormattedValue), NumberStyles.Number, CultureInfo.CurrentCulture, out var value)
            || decimal.Truncate(value) != value || value < spec.Minimum || value > spec.Maximum)
        {
            e.Cancel = true;
            _status.Text = $"{spec.Label}: enter a whole number from {spec.Minimum:N0} to {spec.Maximum:N0}.";
        }
    }

    private void ValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_updating || e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Rows[e.RowIndex].Tag is not TruckPropertyRow data) return;
        var spec = TruckPropertiesStore.Specs.FirstOrDefault(spec => spec.Attribute == _grid.Columns[e.ColumnIndex].Name);
        if (spec is null || _grid[e.ColumnIndex, e.RowIndex].Value is not decimal value) return;
        if (spec.Attribute == "FuelCapacity")
        {
            var rounded = RoundFuel(value);
            if (value != rounded)
            {
                _updating = true;
                try { _grid[e.ColumnIndex, e.RowIndex].Value = rounded; }
                finally { _updating = false; }
                value = rounded;
            }
        }
        data.Draft[spec.Attribute] = value; PaintValue(_grid.Rows[e.RowIndex], spec); UpdateStatus();
        if (_changedOnly.Checked && _grid.IsHandleCreated) _grid.BeginInvoke((Action)(() => { if (!IsDisposed) _tableHeaders.ApplyFilters(); }));
    }

    private static decimal RoundFuel(decimal value) => TruckPropertiesStore.RoundFuel(value);
    private bool HasChangedValues(TruckPropertyRow row) => _removeExploration.Contains(row.RelativeFile)
        || row.OriginalExplorationExplicit != row.CurrentExplorationExplicit || row.Draft.Any(pair =>
        row.Current.TryGetValue(pair.Key, out var current) && pair.Value != current
        || row.Original.TryGetValue(pair.Key, out var original) && pair.Value != original);

    private void MultiplyFuel()
    {
        if (!_grid.EndEdit() || !Ready()) return;
        var spec = TruckPropertiesStore.Specs.Single(spec => spec.Attribute == "FuelCapacity");
        var factor = _fuelMultiplier.Value / 2m;
        foreach (var row in _rows)
            if (row.Draft.TryGetValue(spec.Attribute, out var value))
                row.Draft[spec.Attribute] = RoundFuel(value * factor);
        ShowRows();
    }

    private List<TruckPropertyEdit> DraftValues(bool changedOnly) => _rows.SelectMany(row => row.Draft
        .Where(pair => !changedOnly || pair.Value != row.Current[pair.Key] || pair.Key == "UnlockByExploration" && _removeExploration.Contains(row.RelativeFile))
        .Select(pair => new TruckPropertyEdit(row.RelativeFile, row.Truck.Name, pair.Key, row.Current[pair.Key], pair.Value,
            pair.Key == "UnlockByExploration" && _removeExploration.Contains(row.RelativeFile)))).ToList();

    private void Stage()
    {
        if (!_grid.EndEdit()) return;
        if (!Ready()) { _status.Text = "Reload truck properties before staging changes."; return; }
        var edits = DraftValues(true);
        TruckPropertiesStore.SavePending(_loadedWorkspace!, edits);
        _status.Text = edits.Count == 0
            ? "No truck property changes to stage: the preview already matches the files. The staged truck-property list is empty."
            : $"Staged {edits.Count} truck property changes. Apply pending changes from Start.";
    }

    internal bool HasPresetPreview => _loadedWorkspace == _getWorkspace().Trim() && DraftValues(true).Count > 0;

    internal IEnumerable<WorkspaceChange> PresetPreviewChanges() => !HasPresetPreview ? [] : DraftValues(true).Select(e =>
        new WorkspaceChange("Preview", WorkspaceOriginals.GetCurrentPath(_loadedWorkspace!, e.RelativeFile),
            e.TruckName + " / " + e.Attribute, e.Before.ToString(CultureInfo.InvariantCulture),
            e.RemoveAttribute ? "Remove explicit value (inherit)" : e.After.ToString(CultureInfo.InvariantCulture)));

    internal void ExportPreview(string destination)
    {
        if (_loadedWorkspace != _getWorkspace().Trim()) return;
        if (!_grid.EndEdit()) throw new InvalidDataException("Finish editing truck properties before sharing.");
        TruckPropertiesStore.SavePending(destination, DraftValues(true));
    }

    private void SavePreset()
    {
        if (!_grid.EndEdit() || !Ready()) return;
        using var dialog = new SaveFileDialog { Title = "Save truck properties preset", Filter = "Truck properties preset (*.json)|*.json", FileName = "truck-properties.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        TruckPropertiesStore.WriteAtomic(dialog.FileName, TruckPropertiesStore.SerializeSnapshot(DraftValues(false)));
        _status.Text = "Truck properties preset saved.";
    }

    private void LoadPreset()
    {
        if (!_grid.EndEdit() || !Ready()) return;
        using var dialog = new OpenFileDialog { Title = "Load truck properties preset", Filter = "Truck properties preset (*.json)|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var preset = TruckPropertiesStore.ReadPresetSnapshot(File.ReadAllText(dialog.FileName));
        var values = preset.Values;
        var count = 0;
        foreach (var row in _rows)
            foreach (var edit in values.Where(edit => edit.RelativeFile.Equals(row.RelativeFile, StringComparison.OrdinalIgnoreCase)))
                if (row.Draft.ContainsKey(edit.Attribute))
                {
                    row.Draft[edit.Attribute] = edit.After; count++;
                    if (edit.Attribute == "UnlockByExploration")
                    { if (edit.RemoveAttribute) _removeExploration.Add(row.RelativeFile); else _removeExploration.Remove(row.RelativeFile); }
                }
        ShowRows(); _status.Text = $"Loaded {count} preset values; {values.Count - count} unavailable values skipped. Stage changes to keep them.";
    }

    public IEnumerable<WorkspaceChange> PendingChanges(string workspace)
    {
        var pending = TruckPropertiesStore.LoadSnapshot(workspace);
        return pending.Values.Select(edit => new WorkspaceChange(WorkspaceChange.Pending, WorkspaceOriginals.GetCurrentPath(workspace, edit.RelativeFile),
            edit.TruckName + ": " + TruckPropertiesStore.Specs.Single(spec => spec.Attribute == edit.Attribute).Label,
            edit.Attribute == "UnlockByExploration" ? Flag(edit.Before) : edit.Before.ToString(CultureInfo.InvariantCulture),
            edit.RemoveAttribute ? "Not present" : edit.Attribute == "UnlockByExploration" ? Flag(edit.After) : edit.After.ToString(CultureInfo.InvariantCulture), edit.RelativeFile));
    }

    private static string Flag(decimal value) => value == 1 ? "true" : "false";

    public void SavePendingChanges()
    {
        TruckPropertiesStore.Apply(_getWorkspace().Trim());
        _loadedWorkspace = null; // Re-read applied values on the next visit.
    }

    private void UpdateStatus() => _status.Text = $"{_rows.Count} trucks · {DraftValues(true).Count} changed values. {_regionStatus}";
    private void HideHover() { _hover?.Dispose(); _hover = null; }
    private void RefreshColors(object? sender, EventArgs e)
    {
        _grid.BackgroundColor = AppColors.Get("Surface");
        _grid.DefaultCellStyle.BackColor = AppColors.Get("Surface");
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Cells["Tier"].Value is string tier && tier != "Unsorted") row.Cells["Tier"].Style.BackColor = TierListStore.BackgroundFor(tier);
            foreach (var spec in TruckPropertiesStore.Specs) PaintValue(row, spec);
        }
        _grid.Invalidate();
    }

    private sealed class TilePreview : Form
    {
        public TilePreview(CustomTruck truck)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(166, 226);
            var tile = CustomGroupWipTab.CreateTile(truck); tile.Location = new Point(8, 8); Controls.Add(tile);
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x00000020 | 0x00000080 | 0x08000000; return value; }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084) { message.Result = (IntPtr)(-1); return; }
            base.WndProc(ref message);
        }
    }
}
