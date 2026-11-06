namespace SnowPakTool.Desktop;

internal sealed class UpgradeRandomizationTab : UserControl
{
    private readonly UpgradeRandomizationRuleStore _store;
    private readonly ToolTip _actionTips = new();
    private readonly Font _glyphFont = new("Segoe MDL2 Assets", 10f);
    private readonly UpgradeRandomizationRules _rules;
    private readonly Func<IProgress<RandomizationLogEntry>, Task>? _run;
    private readonly List<RandomizationLogEntry> _logEntries = [];
    private readonly CheckBox _showDetails = new() { Text = "Show detailed logs", AutoSize = true, Checked = false, Margin = new Padding(8, 6, 0, 0) };
    private readonly Button _randomize = new() { Text = "Randomize upgrades", AutoSize = true };
    private readonly Button _stageChanges = new() { Text = "Stage changes", AutoSize = true, Enabled = false };
    private Func<Task>? _stageMapChanges;
    private Func<bool>? _hasMapDrafts;
    private bool _running;
    private readonly TextBox _messages = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, MaxLength = int.MaxValue };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 8) };

    public UpgradeRandomizationTab() : this(null, new UpgradeRandomizationRuleStore()) { }
    public UpgradeRandomizationTab(Func<IProgress<RandomizationLogEntry>, Task> run) : this(run, new UpgradeRandomizationRuleStore()) { }

    internal UpgradeRandomizationTab(Func<IProgress<RandomizationLogEntry>, Task>? run,
        UpgradeRandomizationRuleStore store)
    {
        _store = store;
        _run = run;
        _randomize.Enabled = run is not null;
        _actionTips.SetToolTip(_randomize, "Generate and save an upgrade draft from the current obtainable truck assignments.");
        _actionTips.SetToolTip(_stageChanges, "Stage all upgrade drafts and exploration flags; apply from Start → Apply pending changes.");
        _randomize.Click += async (_, _) => await RandomizeAsync();
        _showDetails.CheckedChanged += (_, _) => RenderMessages();
        VisibleChanged += (_, _) => RefreshStagingState();
        _stageChanges.Click += async (_, _) => await StageMapChangesAsync();
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
            Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 1
        };
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panes.Controls.Add(CreatePane("Upgrades", _rules.Rules, Padding.Empty), 0, 0);
        if (_status.Text.Length == 0) _status.Text = "Rule text is editable notes. Stage upgrades and exploration locks, then apply from Start → Apply pending changes.";
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
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        actions.Controls.Add(_randomize);
        actions.Controls.Add(_stageChanges);
        actions.Controls.Add(_showDetails);
        actions.Controls.Add(new Label
        {
            Text = "Review/reset in Randomizer → Map upgrades. Stage changes, then Apply pending changes from Start.",
            AutoSize = true, Margin = new Padding(0, 7, 0, 6), MaximumSize = new Size(600, 0)
        });
        layout.Controls.Add(actions, 0, 2);
        layout.Controls.Add(_messages, 0, 3);
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
        _randomize.Enabled = !_running && _run is not null;
        var ready = false;
        try { ready = !_running && _stageMapChanges is not null && _hasMapDrafts?.Invoke() == true; }
        catch (Exception) { }
        _stageChanges.Enabled = ready;
    }

    private async Task StageMapChangesAsync()
    {
        if (_stageMapChanges is null || _running) return;
        _running = true; RefreshStagingState();
        void Message(string text) => AddMessage(new(text));
        try { await _stageMapChanges(); Message("Upgrades and exploration flags staged. Apply pending changes from Start."); }
        catch (Exception ex) { Message("Could not stage upgrades: " + ex.Message); }
        finally { _running = false; RefreshStagingState(); }
    }

    private async Task RandomizeAsync()
    {
        if (_run is null || !_randomize.Enabled || _running) return;
        _randomize.Enabled = false;
        _running = true;
        RefreshStagingState();
        _logEntries.Clear();
        _messages.Clear();
        AddMessage(new("Preparing owned regions and upgrades…"));
        try
        {
            await _run(new Progress<RandomizationLogEntry>(message =>
            {
                if (!_messages.IsDisposed) AddMessage(message);
            }));
            AddMessage(new("Finished. Review in Map upgrades, stage changes, then Apply pending changes from Start."));
        }
        catch (InvalidOperationException ex) { AddMessage(new("Error: " + ex.Message)); }
        catch (Exception) { AddMessage(new("Error: could not read or save the upgrade plan. Check the workspace and reload map data.")); }
        finally
        {
            _running = false;
            RefreshStagingState();
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
            MessageBox.Show(this, _status.Text, "Upgrades", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
