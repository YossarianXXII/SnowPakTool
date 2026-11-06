using System.Runtime.CompilerServices;

namespace SnowPakTool.Desktop;

/// <summary>Locks setup mutations without disabling navigation, selection or scrolling.</summary>
internal sealed class PresetBrowseMode : IDisposable
{
    private static readonly ConditionalWeakTable<Control, PresetBrowseMode> Scopes = new();
    private sealed class State(Control control)
    {
        public Control Control { get; } = control;
        public bool Enabled { get; set; } = control.Enabled;
        public bool AllowDrop { get; set; } = control.AllowDrop;
        public bool? TextReadOnly { get; set; } = (control as TextBoxBase)?.ReadOnly;
        public bool AddRows { get; set; } = (control as DataGridView)?.AllowUserToAddRows ?? false;
        public bool DeleteRows { get; set; } = (control as DataGridView)?.AllowUserToDeleteRows ?? false;
        public Dictionary<DataGridViewColumn, bool> Columns { get; } = [];
    }
    private readonly Control[] _roots;
    private readonly Dictionary<Control, State> _states = [];
    private bool _locked, _applying, _disposed;

    public PresetBrowseMode(params Control[] roots)
    {
        _roots = roots;
        foreach (var root in roots) { Scopes.Add(root, this); Watch(root); }
    }

    public static bool IsReadOnly(Control control)
    {
        for (Control? current = control; current is not null; current = current.Parent)
            if (Scopes.TryGetValue(current, out var scope) && scope._locked && !scope._disposed) return true;
        return false;
    }

    public void SetReadOnly(bool value)
    {
        if (_disposed) return;
        if (_locked == value) { Refresh(); return; }
        if (value)
            foreach (var state in _states.Values)
            {
                state.Enabled = state.Control.Enabled; state.AllowDrop = state.Control.AllowDrop;
                state.TextReadOnly = (state.Control as TextBoxBase)?.ReadOnly;
                if (state.Control is DataGridView grid)
                {
                    state.AddRows = grid.AllowUserToAddRows; state.DeleteRows = grid.AllowUserToDeleteRows;
                    foreach (DataGridViewColumn column in grid.Columns) state.Columns[column] = column.ReadOnly;
                }
            }
        _locked = value;
        // Restore the captured state once when unlocking. Ordinary unlocked
        // refreshes must leave editor-owned enabled/read-only settings alone.
        foreach (var state in _states.Values.ToArray()) Apply(state);
    }

    public void Refresh()
    {
        if (_disposed || _applying || !_locked) return;
        foreach (var state in _states.Values.ToArray()) Apply(state);
    }

    private static bool ViewColumn(DataGridViewColumn column) => column.Name == "SelectTruck";
    private static bool ViewAction(Control control)
    {
        var text = control.Text.Trim();
        if (control is RegionTileButton) return true; // Region selection filters the map view.
        if (control is CheckBox) return text.StartsWith("Show ", StringComparison.OrdinalIgnoreCase) || text == "Select all visible";
        if (control is not Button) return false;
        return new[] { "Reload", "Refresh", "Show ", "Hide ", "Order by ", "Columns", "Browse" }
            .Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || text is "Select all regions" or "Clear selection" || text.EndsWith('▾');
    }

    private static bool Disable(Control control) => control is ButtonBase ? !ViewAction(control)
        : control is ComboBox or NumericUpDown or TrackBar or CheckedListBox;

    private void Watch(Control control)
    {
        if (_disposed || _states.ContainsKey(control)) return;
        var state = new State(control); _states.Add(control, state);
        control.Disposed += (_, _) => _states.Remove(control);
        control.ControlAdded += (_, e) => { if (e.Control is not null) Watch(e.Control); };
        control.EnabledChanged += (_, _) =>
        {
            if (_disposed || _applying || !_locked || !Disable(control)) return;
            state.Enabled = control.Enabled; Apply(state);
        };
        control.TextChanged += (_, _) => { if (_locked && control is ButtonBase) Apply(state); };
        control.Layout += (_, _) => { if (_locked) Apply(state); };
        if (control is TextBoxBase text)
            text.ReadOnlyChanged += (_, _) =>
            {
                if (_disposed || _applying || !_locked) return;
                state.TextReadOnly = text.ReadOnly; Apply(state);
            };
        if (control is DataGridView grid)
        {
            foreach (DataGridViewColumn column in grid.Columns) state.Columns[column] = column.ReadOnly;
            grid.ColumnAdded += (_, e) => { state.Columns[e.Column] = e.Column.ReadOnly; if (_locked) Apply(state); };
            grid.ColumnRemoved += (_, e) => state.Columns.Remove(e.Column);
            grid.ColumnStateChanged += (_, e) =>
            {
                if (_disposed || _applying || e.StateChanged != DataGridViewElementStates.ReadOnly) return;
                // Editors establish per-column editability after binding. Remember those
                // changes while unlocked so a later Refresh does not restore stale defaults.
                state.Columns[e.Column] = e.Column.ReadOnly;
                if (_locked) Apply(state);
            };
            grid.CellBeginEdit += (_, e) => { if (_locked && !ViewColumn(grid.Columns[e.ColumnIndex])) e.Cancel = true; };
            grid.UserDeletingRow += (_, e) => { if (_locked) e.Cancel = true; };
            grid.AllowUserToAddRowsChanged += (_, _) =>
            { if (_locked && !_applying) { state.AddRows = grid.AllowUserToAddRows; Apply(state); } };
            grid.AllowUserToDeleteRowsChanged += (_, _) =>
            { if (_locked && !_applying) { state.DeleteRows = grid.AllowUserToDeleteRows; Apply(state); } };
        }
        foreach (Control child in control.Controls) Watch(child);
        if (_locked) Apply(state);
    }

    private void Apply(State state)
    {
        var control = state.Control;
        if (_disposed || _applying || control.IsDisposed) return;
        _applying = true;
        try
        {
            if (Disable(control)) control.Enabled = !_locked && state.Enabled;
            control.AllowDrop = !_locked && state.AllowDrop;
            if (control is TextBoxBase text && state.TextReadOnly is { } readOnly) text.ReadOnly = _locked || readOnly;
            if (control is DataGridView grid)
            {
                grid.AllowUserToAddRows = !_locked && state.AddRows;
                grid.AllowUserToDeleteRows = !_locked && state.DeleteRows;
                foreach (DataGridViewColumn column in grid.Columns)
                {
                    if (!state.Columns.TryGetValue(column, out var readOnlyColumn)) state.Columns[column] = readOnlyColumn = column.ReadOnly;
                    column.ReadOnly = _locked && !ViewColumn(column) || readOnlyColumn;
                }
            }
        }
        finally { _applying = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        SetReadOnly(false); _disposed = true;
        foreach (var root in _roots) Scopes.Remove(root);
        _states.Clear();
    }
}
