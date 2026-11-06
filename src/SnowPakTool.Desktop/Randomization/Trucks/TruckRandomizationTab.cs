namespace SnowPakTool.Desktop;

internal sealed class TruckRandomizationTab : UserControl
{
    internal static readonly string[] DefaultObtainableRules =
    [
        "Randomize only owned regions and owned trucks. When another region or truck becomes owned, keep all previously randomized placements unchanged.",
        "Replace trucks with trucks from the same class, including the custom Farming tractor class. When HEAVY_DUTY variety is limited, allow a rare, similarly ranked OFFROAD replacement (18% chance when eligible, capped at one per five saved HEAVY_DUTY assignments).",
        "Prefer different replacement trucks, especially within each region. For HEAVY_DUTY, widen the target tier band by one tier when it offers less-used compatible trucks, then choose a least-used model before repeating others.",
        "Use the truck tier list and player-defined region progression: favor lower-rated trucks in earlier regions and top-rated trucks in later regions. Allow fuzzy variation: a moderate chance for an SS truck to appear one or two regions earlier, and a very low chance for it to appear many regions earlier.",
        "Roll tires separately for each truck using finalized region progression, region difficulty tier and Hard tire calculations. The first region requests 99% Highway / 1% All-terrain; better tires become more likely later, with 30% Mudtires in the final S-tier region. Use the nearest compatible category, smallest size and first variant.",
        "If the original truck has any of the following addons installed, the replacement must support and have those addons installed too: Sideboard Bed, Saddle High, Saddle Low, Avto-23 Heavy Crane, Van Body Addon, Log Carrier Front, Short logging frame, Seismic Vibrator Module, or Metal Detector Module Frame."
    ];

    internal static readonly string[] DefaultMissionRules =
    [
        "Randomize mission-only trucks in owned regions using enabled trucks. Keep saved assignments fixed when ownership or difficulty changes.",
        "Choose freely across truck classes and tiers. Ignore the original truck's addon requirements and use replacement default equipment. Preserve the mission's existing reward/removal behavior.",
        "Choose supported tires using the region difficulty tier, with variation toward neighboring categories. Use the nearest supported category, smallest size and first variant.",
        "Keep a broad mixture of light and heavy vehicles based on actual physics mass. Easy modestly favors lighter vehicles, Moderate is balanced, and Hard modestly favors heavier vehicles. The weight bias is limited to 3:1 so every mode retains substantial variety.",
        "Prefer different models across all regions. Use models not yet assigned anywhere before repeating, then choose the least-used models globally. Count saved assignments and drafts once per placement, and use regional variety as a tie-breaker."
    ];

    private readonly Func<MissionTruckDifficulty, IProgress<RandomizationLogEntry>, Task>? _runMissions;
    private readonly List<RandomizationLogEntry> _missionLogEntries = [];
    private readonly CheckBox _missionShowDetails = new() { Text = "Show detailed logs", AutoSize = true, Checked = false, Margin = new Padding(8, 6, 0, 0) };
    private readonly Button _missionRandomize = new() { Text = "Randomize mission trucks", AutoSize = true };
    private readonly Button _missionStageChanges = new() { Text = "Stage changes", AutoSize = true, Enabled = false };
    private readonly TextBox _missionMessages = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, MaxLength = int.MaxValue };

    private readonly TruckRandomizationRuleStore _store;
    private readonly ToolTip _actionTips = new();
    private readonly Font _glyphFont = new("Segoe MDL2 Assets", 10f);
    private readonly TruckRandomizationRules _rules;
    private readonly Func<IProgress<RandomizationLogEntry>, Task>? _runObtainable;
    private readonly List<RandomizationLogEntry> _logEntries = [];
    private readonly CheckBox _showDetails = new() { Text = "Show detailed logs", AutoSize = true, Checked = false, Margin = new Padding(8, 6, 0, 0) };
    private readonly Button _randomize = new() { Text = "Randomize obtainable trucks", AutoSize = true };
    private readonly Button _stageChanges = new() { Text = "Stage changes", AutoSize = true, Enabled = false };
    private Func<Task>? _stageMapChanges;
    private Func<bool>? _hasMapDrafts;
    private bool _running;
    private readonly TextBox _messages = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, MaxLength = int.MaxValue };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 8) };

    public TruckRandomizationTab() : this(null) { }

    public TruckRandomizationTab(Func<IProgress<RandomizationLogEntry>, Task>? runObtainable) : this(runObtainable, new TruckRandomizationRuleStore()) { }

    public TruckRandomizationTab(Func<IProgress<RandomizationLogEntry>, Task>? runObtainable,
        Func<MissionTruckDifficulty, IProgress<RandomizationLogEntry>, Task>? runMissions)
        : this(runObtainable, new TruckRandomizationRuleStore(), runMissions) { }

    internal TruckRandomizationTab(Func<IProgress<RandomizationLogEntry>, Task>? runObtainable, TruckRandomizationRuleStore store)
        : this(runObtainable, store, null) { }

    internal TruckRandomizationTab(Func<IProgress<RandomizationLogEntry>, Task>? runObtainable, TruckRandomizationRuleStore store,
        Func<MissionTruckDifficulty, IProgress<RandomizationLogEntry>, Task>? runMissions)
    {
        _store = store;
        _runObtainable = runObtainable;
        _runMissions = runMissions;
        _randomize.Enabled = runObtainable is not null;
        _missionRandomize.Enabled = runMissions is not null;
        _randomize.Click += async (_, _) => await RandomizeAsync();
        _missionRandomize.Click += async (_, _) => await RandomizeMissionsAsync();
        _showDetails.CheckedChanged += (_, _) => RenderMessages();
        _missionShowDetails.CheckedChanged += (_, _) => RenderMissionMessages();
        VisibleChanged += (_, _) => RefreshStagingState();
        _stageChanges.Click += async (_, _) => await StageMapChangesAsync(false);
        _missionStageChanges.Click += async (_, _) => await StageMapChangesAsync(true);
        Disposed += (_, _) => { _actionTips.Dispose(); _glyphFont.Dispose(); };
        try { _rules = _store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _rules = new();
            _status.Text = "Could not load saved rules: " + ex.Message;
        }
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var panes = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 1
        };
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panes.Controls.Add(CreatePane("Obtainable", _rules.Obtainable, new Padding(0, 0, 6, 0)), 0, 0);
        panes.Controls.Add(CreatePane("Missions", _rules.Missions, new Padding(6, 0, 0, 0)), 1, 0);
        if (_status.Text.Length == 0) _status.Text = "Rule text is editable notes; editing it does not change randomizer behavior.";
        root.Controls.Add(panes, 0, 0);
        root.Controls.Add(_status, 0, 1);
        Controls.Add(root);
    }

    private GroupBox CreatePane(string title, List<string> rules, Padding margin)
    {
        var pane = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(10), Margin = margin };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 10) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label
        {
            Text = "Rules", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            Anchor = AnchorStyles.Left, Margin = Padding.Empty
        }, 0, 0);
        var add = CreateGlyphAction("\uE710", $"Add {title} rule");
        header.Controls.Add(add, 1, 0);
        layout.Controls.Add(header, 0, 0);

        // Rules stay at the top of each pane and scroll independently on small windows.
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        TextAreaStyle.Mark(scroll);
        var list = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3, Margin = Padding.Empty
        };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        list.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        TextAreaStyle.Mark(list);
        void RefreshRules()
        {
            list.SuspendLayout();
            foreach (var child in list.Controls.Cast<Control>().ToArray()) child.Dispose();
            list.RowStyles.Clear();
            list.RowCount = Math.Max(1, rules.Count);
            if (rules.Count == 0)
            {
                var empty = new Label { Text = "No rules yet. Use + to add one.", AutoSize = true, Dock = DockStyle.Fill };
                list.Controls.Add(empty, 0, 0);
                list.SetColumnSpan(empty, 3);
            }
            for (var index = 0; index < rules.Count; index++)
            {
                var ruleIndex = index;
                list.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                list.Controls.Add(new Label { Text = $"{index + 1}.", AutoSize = true, Margin = new Padding(0, 5, 4, 8) }, 0, index);
                list.Controls.Add(new Label
                {
                    Text = rules[index], AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 5, 8, 8)
                }, 1, index);
                var actions = new FlowLayoutPanel
                {
                    AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                    Margin = new Padding(0, 0, 0, 8)
                };
                var edit = CreateGlyphAction("\uE70F", $"Edit {title} rule {index + 1}");
                var remove = CreateGlyphAction("\uE74D", $"Remove {title} rule {index + 1}");
                edit.Click += (_, _) =>
                {
                    var text = EditRule($"Edit {title} rule {ruleIndex + 1}", rules[ruleIndex]);
                    if (text is null) return;
                    ChangeRules(rules, values => values[ruleIndex] = text, RefreshRules);
                };
                remove.Click += (_, _) => ChangeRules(rules, values => values.RemoveAt(ruleIndex), RefreshRules);
                actions.Controls.Add(edit);
                actions.Controls.Add(remove);
                list.Controls.Add(actions, 2, index);
            }
            list.ResumeLayout(true);
        }
        add.Click += (_, _) =>
        {
            var text = EditRule($"Add {title} rule", "");
            if (text is not null) ChangeRules(rules, values => values.Add(text), RefreshRules);
        };
        RefreshRules();
        scroll.Controls.Add(list);
        layout.Controls.Add(scroll, 0, 1);
        if (title == "Obtainable")
        {
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            actions.Controls.Add(_randomize);
            actions.Controls.Add(_stageChanges);
            actions.Controls.Add(_showDetails);
            actions.Controls.Add(new Label
            {
                Text = "Each truck rolls tires using progression + region tier with Hard tire calculations. First region: 99% Highway, where compatible. Review/reset in Randomizer → Map trucks; stage here or there, then apply from Start.", AutoSize = true,
                Margin = new Padding(0, 7, 0, 6), MaximumSize = new Size(400, 0)
            });
            layout.Controls.Add(actions, 0, 2);
            layout.Controls.Add(_messages, 0, 3);
        }
        else
        {
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            var difficulty = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            difficulty.Controls.Add(new Label { Text = "Vehicle weight:", AutoSize = true, Margin = new Padding(0, 5, 6, 0) });
            foreach (var mode in Enum.GetValues<MissionTruckDifficulty>())
            {
                var radio = new RadioButton { Text = mode.ToString(), AutoSize = true, Checked = _rules.MissionDifficulty == mode,
                    AccessibleName = "Mission weight difficulty " + mode };
                radio.CheckedChanged += (_, _) =>
                {
                    if (!radio.Checked) return;
                    _rules.MissionDifficulty = mode;
                    try { _store.Save(_rules); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = "Could not save mission difficulty: " + ex.Message; }
                };
                difficulty.Controls.Add(radio);
            }
            actions.Controls.Add(difficulty);
            actions.Controls.Add(_missionRandomize);
            actions.Controls.Add(_missionStageChanges);
            actions.Controls.Add(_missionShowDetails);
            actions.Controls.Add(new Label
            {
                Text = "Broad weight mix: Easy leans light, Hard leans heavy. Prefer different models across all regions. Tires use region difficulty with variance. Review/reset in Randomizer → Map trucks; stage here or there, then apply from Start.",
                AutoSize = true, Margin = new Padding(0, 7, 0, 6), MaximumSize = new Size(400, 0)
            });
            layout.Controls.Add(actions, 0, 2);
            layout.Controls.Add(_missionMessages, 0, 3);
        }
        var fitting = false;
        void FitRules()
        {
            if (fitting || layout.ClientSize.Height <= 0 || scroll.ClientSize.Width <= 0) return;
            fitting = true;
            try
            {
                var controlsHeight = layout.GetControlFromPosition(0, 2)?.GetPreferredSize(new Size(layout.ClientSize.Width, 0)).Height ?? 0;
                var limit = Math.Max(48, layout.ClientSize.Height - header.PreferredSize.Height - controlsHeight - 130);
                var desired = list.Height;
                var height = Math.Clamp(desired, 24, limit);
                if (Math.Abs(layout.RowStyles[1].Height - height) > 1) layout.RowStyles[1].Height = height;
            }
            finally { fitting = false; }
        }
        var fitQueued = false;
        void QueueFit()
        {
            if (fitQueued || !layout.IsHandleCreated || layout.IsDisposed) return;
            fitQueued = true;
            layout.BeginInvoke((Action)(() =>
            {
                fitQueued = false;
                if (!layout.IsDisposed) FitRules();
            }));
        }
        layout.Layout += (_, _) => QueueFit();
        layout.HandleCreated += (_, _) => QueueFit();
        list.SizeChanged += (_, _) => QueueFit();
        pane.Controls.Add(layout);
        return pane;
    }

    public void ConfigureMapStaging(Func<Task> stage, Func<bool> hasDrafts)
    {
        _stageMapChanges = stage; _hasMapDrafts = hasDrafts; RefreshStagingState();
    }

    public void RefreshStagingState()
    {
        if (IsDisposed) return;
        _randomize.Enabled = !_running && _runObtainable is not null;
        _missionRandomize.Enabled = !_running && _runMissions is not null;
        var ready = false;
        try { ready = !_running && _stageMapChanges is not null && _hasMapDrafts?.Invoke() == true; }
        catch (Exception) { }
        _stageChanges.Enabled = _missionStageChanges.Enabled = ready;
    }

    private async Task StageMapChangesAsync(bool missions)
    {
        if (_stageMapChanges is null || _running) return;
        _running = true; RefreshStagingState();
        void Message(string text) { if (missions) AddMissionMessage(new(text)); else AddMessage(new(text)); }
        try { await _stageMapChanges(); Message("Map changes staged. Apply them from Start → Apply pending changes."); }
        catch (Exception ex) { Message("Could not stage map changes: " + ex.Message); }
        finally { _running = false; RefreshStagingState(); }
    }

    private async Task RandomizeMissionsAsync()
    {
        if (_runMissions is null || !_missionRandomize.Enabled || _running) return;
        _running = true; RefreshStagingState();
        _missionLogEntries.Clear(); _missionMessages.Clear();
        AddMissionMessage(new("Preparing owned regions and enabled trucks…"));
        try
        {
            await _runMissions(_rules.MissionDifficulty, new Progress<RandomizationLogEntry>(entry =>
            {
                if (!_missionMessages.IsDisposed) AddMissionMessage(entry);
            }));
            AddMissionMessage(new("Finished. Review the draft, stage changes here or in Map trucks, then apply from Start."));
        }
        catch (InvalidOperationException ex) { AddMissionMessage(new("Error: " + ex.Message)); }
        catch (Exception) { AddMissionMessage(new("Error: could not read or save the mission plan. Check the workspace and reload map data.")); }
        finally { _running = false; RefreshStagingState(); }
    }

    private void AddMissionMessage(RandomizationLogEntry entry)
    {
        if (_missionMessages.IsDisposed) return;
        _missionLogEntries.Add(entry);
        if (!entry.IsDetail || _missionShowDetails.Checked) _missionMessages.AppendText(entry.Message + Environment.NewLine);
    }

    private void RenderMissionMessages()
    {
        _missionMessages.Text = string.Concat(_missionLogEntries.Where(entry => !entry.IsDetail || _missionShowDetails.Checked)
            .Select(entry => entry.Message + Environment.NewLine));
        _missionMessages.SelectionStart = _missionMessages.TextLength;
        _missionMessages.ScrollToCaret();
    }

    private async Task RandomizeAsync()
    {
        if (_runObtainable is null || !_randomize.Enabled) return;
        _randomize.Enabled = false;
        _running = true;
        RefreshStagingState();
        _logEntries.Clear();
        _messages.Clear();
        AddMessage(new("Preparing owned regions and trucks…"));
        try
        {
            await _runObtainable(new Progress<RandomizationLogEntry>(message =>
            {
                if (!_messages.IsDisposed) AddMessage(message);
            }));
            AddMessage(new("Finished. Review the draft, stage changes here or in Map trucks, then apply from Start."));
        }
        catch (InvalidOperationException ex) { AddMessage(new("Error: " + ex.Message)); }
        catch (Exception) { AddMessage(new("Error: could not read or save the randomization plan. Check the workspace and reload map data.")); }
        finally
        {
            _running = false;
            RefreshStagingState();
            if (!_randomize.IsDisposed) _randomize.Enabled = true;
        }
    }

    private void AddMessage(RandomizationLogEntry entry)
    {
        _logEntries.Add(entry);
        if (!entry.IsDetail || _showDetails.Checked) _messages.AppendText(entry.Message + Environment.NewLine);
    }

    private void RenderMessages()
    {
        _messages.Text = string.Concat(_logEntries.Where(entry => !entry.IsDetail || _showDetails.Checked)
            .Select(entry => entry.Message + Environment.NewLine));
        _messages.SelectionStart = _messages.TextLength;
        _messages.ScrollToCaret();
    }

    private Button CreateGlyphAction(string glyph, string description)
    {
        var action = new Button
        {
            Text = glyph, Font = _glyphFont, Size = new Size(26, 26), Margin = Padding.Empty,
            Padding = Padding.Empty, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            AccessibleName = description, AccessibleDescription = description,
            UseVisualStyleBackColor = true
        };
        action.FlatAppearance.BorderSize = 0;
        _actionTips.SetToolTip(action, description);
        return action;
    }

    private void ChangeRules(List<string> rules, Action<List<string>> change, Action refresh)
    {
        var previous = rules.ToArray();
        change(rules);
        try { _store.Save(_rules); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            rules.Clear();
            rules.AddRange(previous);
            _status.Text = "Could not save rules: " + ex.Message;
            MessageBox.Show(this, _status.Text, "Truck randomization", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        refresh();
        _status.Text = "Rule notes saved.";
    }

    private string? EditRule(string title, string value)
    {
        using var dialog = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
            MinimizeBox = false, MaximizeBox = false, Size = new Size(580, 340), MinimumSize = new Size(380, 240)
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var text = new TextBox { Text = value, Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, AccessibleName = "Rule text" };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK, Enabled = !string.IsNullOrWhiteSpace(value) };
        text.TextChanged += (_, _) => save.Enabled = !string.IsNullOrWhiteSpace(text.Text);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        layout.Controls.Add(text, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        dialog.Controls.Add(layout);
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => { text.Focus(); text.SelectAll(); };
        return dialog.ShowDialog(this) == DialogResult.OK ? text.Text.Trim() : null;
    }
}
