namespace SnowPakTool.Desktop;

internal sealed class VariousEditorTab : UserControl
{
    private readonly Func<string> _workspace;
    private readonly CheckBox _ignoreIce = new() { Text = "Ignore ice on all tires", Appearance = Appearance.Button, AutoSize = true };
    private readonly Button _stage = new() { Text = "Stage changes", AutoSize = true };
    private readonly Button _reload = new() { Text = "Reload", AutoSize = true };
    private readonly Button _revert = new() { Text = "Revert unstaged edits", AutoSize = true };
    private readonly Button _clear = new() { Text = "Revert staged changes", AutoSize = true };
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
    private List<TireIceEdit> _preview = [];
    private string? _loadedWorkspace;
    private bool _loading;
    internal event Action? StagingChanged;

    public VariousEditorTab(Func<string> workspace)
    {
        _workspace = workspace;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, Padding = new Padding(12) };
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        actions.Controls.AddRange([_reload, _stage, _revert, _clear]);
        layout.Controls.Add(actions);
        layout.Controls.Add(_ignoreIce);
        layout.Controls.Add(new Label { AutoSize = true, Text = "On: all tires ignore ice, including inherited tires and friction templates.\nOff: restore original ice settings, keeping original chained-tire behavior.\nStage changes, then use Start → Apply pending changes." });
        layout.Controls.Add(_status);
        Controls.Add(layout);
        _ignoreIce.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            Run(() => { _preview = TireIceStore.Preview(LoadedWorkspace(), _ignoreIce.Checked); UpdateStatus(); }, reloadOnError: true);
        };
        _stage.Click += (_, _) => Run(() => { TireIceStore.Save(LoadedWorkspace(), _preview); StagingChanged?.Invoke(); UpdateStatus(); });
        _reload.Click += (_, _) => Run(Reload);
        _revert.Click += (_, _) => Run(Reload);
        _clear.Click += (_, _) => Run(() => { TireIceStore.Save(LoadedWorkspace(), []); Reload(); StagingChanged?.Invoke(); });
        _ignoreIce.Enabled = _stage.Enabled = _revert.Enabled = _clear.Enabled = false;
        _status.Text = "Choose a workspace and unpack a PAK to load tire settings.";
    }

    internal void EnsureLoaded()
    {
        if (_loadedWorkspace != _workspace()) Run(Reload);
    }
    internal void WorkspaceUpdated() { _loadedWorkspace = null; }
    private string LoadedWorkspace()
    {
        var workspace = _workspace();
        if (_loadedWorkspace != workspace || !Directory.Exists(workspace))
            throw new InvalidOperationException("Reload tire settings for the selected workspace before editing.");
        return workspace;
    }
    private void Reload()
    {
        var workspace = _workspace();
        var targets = TireIceStore.Files(workspace).SelectMany(file => TireIceStore.Targets(File.ReadAllText(file))
            .Select(t => (File: WorkspaceOriginals.GetRelativePath(workspace, file), Target: t))).ToArray();
        _preview = TireIceStore.Load(workspace);
        var staged = _preview.ToDictionary(e => e.RelativeFile.Replace('\\', '/') + "|" + e.Path, StringComparer.OrdinalIgnoreCase);
        _loading = true;
        try
        {
            _ignoreIce.Checked = targets.Length > 0 && targets.All(t => staged.TryGetValue(t.File.Replace('\\', '/') + "|" + t.Target.Path, out var e)
                ? e.AfterExists && e.After == "true" : t.Target.Exists && t.Target.Value == "true");
        }
        finally { _loading = false; }
        _loadedWorkspace = workspace;
        _ignoreIce.Enabled = targets.Length > 0;
        UpdateStatus();
        if (targets.Length == 0) _status.Text = "No tire friction definitions found. Unpack an initial PAK first.";
    }
    private void UpdateStatus()
    {
        var staged = TireIceStore.Load(_workspace());
        _stage.Enabled = _ignoreIce.Enabled;
        _revert.Enabled = !_preview.SequenceEqual(staged);
        _clear.Enabled = staged.Count > 0;
        _status.Text = $"Preview: {_preview.Count:N0} attribute changes in {_preview.Select(e => e.RelativeFile).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} files. Staged: {staged.Count:N0}.";
    }
    private void Run(Action action, bool reloadOnError = false)
    {
        try { UseWaitCursor = true; action(); }
        catch (Exception ex)
        {
            if (reloadOnError) { try { Reload(); } catch { _ignoreIce.Enabled = false; } }
            _status.Text = ex.Message;
        }
        finally { UseWaitCursor = false; }
    }
    internal IEnumerable<WorkspaceChange> PresetPreviewChanges() => _loadedWorkspace == _workspace()
        ? TireIceStore.Changes(_workspace(), _preview) : [];
    internal void ExportPreview(string temporary)
    {
        if (_loadedWorkspace == _workspace()) TireIceStore.Save(temporary, _preview);
    }
}
