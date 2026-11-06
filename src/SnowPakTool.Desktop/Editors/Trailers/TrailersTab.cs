namespace SnowPakTool.Desktop;

internal sealed class TrailersTab : UserControl
{
    private readonly Func<string> _workspace;
    private readonly DataGridView _files = Grid(), _properties = Grid();
    private readonly Label _status = new() { AutoSize = true };
    private readonly Dictionary<(string File, string Path), TrailerEdit> _draft = [];
    private string? _loaded;
    private bool _binding;
    internal event Action? StagingChanged;
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowTemplate = { Height = 26 } };
    public TrailersTab(Func<string> workspace)
    {
        _workspace = workspace;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 4, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.AutoSize)); layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 35)); layout.RowStyles.Add(new(SizeType.Percent, 65));
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        void Button(string title, Action action) { var b = new Button { Text = title, AutoSize = true }; tools.Controls.Add(b); b.Click += (_, _) => Run(action); }
        Button("Reload trailers", Reload);
        Button("Stage changes", () => { _properties.EndEdit(); TrailerStore.Save(_workspace(), _draft.Values.ToList()); StagingChanged?.Invoke(); _status.Text = $"Staged {_draft.Count} properties. Apply pending changes from Start."; });
        Button("Revert unstaged edits", Reload);
        Button("Revert staged changes", () => { TrailerStore.Save(_workspace(), []); Reload(); StagingChanged?.Invoke(); });
        layout.Controls.Add(tools, 0, 0); layout.Controls.Add(_status, 0, 1); layout.Controls.Add(_files, 0, 2); layout.Controls.Add(_properties, 0, 3); Controls.Add(layout);
        _files.Columns.Add("Trailer", "Trailer ID"); _files.Columns.Add("Source", "Source file"); _files.ReadOnly = true;
        foreach (var name in new[] { "Property", "Current", "Original", "Preview" }) _properties.Columns.Add(name, name);
        foreach (DataGridViewColumn c in _properties.Columns) c.ReadOnly = c.Name != "Preview";
        TableHeaderBehavior.Attach(_files); TableHeaderBehavior.Attach(_properties);
        _files.CurrentCellChanged += (_, _) => Run(ShowProperties);
        _properties.CellEndEdit += (_, e) => Run(() =>
        {
            if (_binding || _files.CurrentRow is null) return;
            var file = (string)_files.CurrentRow.Cells["Source"].Value!; var row = _properties.Rows[e.RowIndex];
            var path = (string)row.Cells["Property"].Value!; var before = (string)row.Cells["Current"].Value!;
            var after = Convert.ToString(row.Cells["Preview"].Value) ?? "";
            if (before == after) _draft.Remove((file, path)); else _draft[(file, path)] = new(file, path, before, after);
            _status.Text = $"{_draft.Count} preview changes. Edit explicit XML values; templates and inherited values are not expanded. Stage, then apply from Start.";
        });
    }
    internal void WorkspaceUpdated() => _loaded = null;
    internal void EnsureLoaded() { if (_loaded != _workspace()) Run(Reload); }
    private void Run(Action action) { try { action(); } catch (Exception ex) { _status.Text = ex.Message; } }
    private void Reload()
    {
        _binding = true;
        try
        {
            _files.Rows.Clear(); _properties.Rows.Clear(); _draft.Clear();
            if (!Directory.Exists(_workspace())) { _status.Text = "Choose an extracted workspace on Start."; return; }
            foreach (var e in TrailerStore.Load(_workspace())) _draft[(e.File, e.Path)] = e;
            foreach (var file in TrailerStore.Files(_workspace()).Order()) _files.Rows.Add(System.IO.Path.GetFileNameWithoutExtension(file), WorkspaceOriginals.GetRelativePath(_workspace(), file));
            _loaded = _workspace(); _status.Text = $"{_files.Rows.Count} trailers. Edit Preview values, stage, then apply from Start. Structural/template references are read-only.";
        }
        finally { _binding = false; }
        ShowProperties();
    }
    private void ShowProperties()
    {
        if (_binding) return;
        _properties.EndEdit(); _properties.Rows.Clear();
        if (_files.CurrentRow?.Cells["Source"].Value is not string file) return;
        var current = TrailerStore.Attributes(TrailerStore.Parse(File.ReadAllText(WorkspaceOriginals.GetCurrentPath(_workspace(), file))));
        var original = TrailerStore.Attributes(TrailerStore.Parse(File.ReadAllText(WorkspaceOriginals.GetOriginalReadPath(_workspace(), file))));
        foreach (var (path, a) in current)
            _properties.Rows.Add(path, a.Value, original.TryGetValue(path, out var o) ? o.Value : "Not present", _draft.TryGetValue((file, path), out var edit) ? edit.After : a.Value);
    }
    internal void ExportPreview(string destination) { if (_loaded == _workspace()) TrailerStore.Save(destination, _draft.Values.ToList()); }
    internal IEnumerable<WorkspaceChange> PresetPreviewChanges() => _loaded == _workspace() ? TrailerStore.Changes(_workspace(), _draft.Values) : [];
}
