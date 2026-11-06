using System.Collections;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace SnowPakTool.Desktop;

// Shared by sortable/filterable tables. Model order and row identities stay independent of display order.
internal sealed class TableHeaderBehavior
{
    private static readonly ConditionalWeakTable<DataGridView, TableHeaderBehavior> Instances = new();
    private readonly DataGridView _grid;
    private readonly Action? _restore;
    private readonly Action<DataGridViewColumn, ListSortDirection>? _sort;
    private readonly Func<DataGridViewColumn, bool>? _customHeader;
    private readonly Dictionary<string, HeaderValueFilter> _filters = new(StringComparer.Ordinal);
    private readonly Dictionary<DataGridViewRow, long> _defaultOrder = new();
    private readonly HashSet<string> _sortable = [];
    private long _nextOrder;
    private bool _busy;
    private bool _queued;
    private bool _hadActiveFilters;
    private string? _sorted;
    private SortOrder _direction;
    private Func<DataGridViewRow, bool>? _rowPredicate;
    private string? _additionalExpression;
    public event Action? Changed;

    public static TableHeaderBehavior Attach(DataGridView grid, Action? restore = null,
        Action<DataGridViewColumn, ListSortDirection>? sort = null, Func<DataGridViewColumn, bool>? customHeader = null)
        => Instances.GetValue(grid, _ => new(grid, restore, sort, customHeader));
    public static void Refresh(DataGridView grid) { if (Instances.TryGetValue(grid, out var behavior)) behavior.Refresh(); }

    public void SetAdditionalFilter(Func<DataGridViewRow, bool>? predicate = null, string? expression = null)
    {
        _rowPredicate = predicate; _additionalExpression = expression; ApplyFilters();
    }

    private TableHeaderBehavior(DataGridView grid, Action? restore, Action<DataGridViewColumn, ListSortDirection>? sort,
        Func<DataGridViewColumn, bool>? customHeader)
    {
        _grid = grid; _restore = restore; _sort = sort; _customHeader = customHeader;
        grid.ColumnHeaderMouseClick += HeaderClick;
        grid.ColumnAdded += (_, e) => Install(e.Column);
        grid.DataBindingComplete += (_, _) => Refresh();
        grid.RowsAdded += (_, _) => QueueRefresh();
        grid.CellValueChanged += (_, _) => QueueRefresh();
        grid.RowsRemoved += (_, _) =>
        {
            if (grid.Rows.Count == 0) { _defaultOrder.Clear(); _nextOrder = 0; }
            QueueRefresh();
        };
        grid.Disposed += (_, _) => { foreach (var filter in _filters.Values) filter.Dispose(); };
        foreach (DataGridViewColumn column in grid.Columns) Install(column);
        RememberOrder();
    }

    public HeaderValueFilter AddFilter(string columnName, Func<object?, string>? valueGroup = null, params string[] alwaysIncluded)
    {
        if (_filters.TryGetValue(columnName, out var existing))
        {
            if (valueGroup is null) return existing;
            existing.Dispose(); _filters.Remove(columnName);
        }
        var filter = new HeaderValueFilter(columnName, valueGroup, alwaysIncluded);
        _filters.Add(columnName, filter);
        filter.Changed += () => { ApplyFilters(); Changed?.Invoke(); };
        if (_grid.Columns.Contains(columnName)) Install(_grid.Columns[columnName]!);
        return filter;
    }

    private void Install(DataGridViewColumn column)
    {
        if (column.SortMode != DataGridViewColumnSortMode.NotSortable &&
            (column.ValueType is null || column.ValueType == typeof(string) || typeof(IComparable).IsAssignableFrom(column.ValueType)))
            _sortable.Add(column.Name);
        if (column.ValueType is not null && column.ValueType != typeof(string) && !typeof(IComparable).IsAssignableFrom(column.ValueType))
        { _sortable.Remove(column.Name); column.SortMode = DataGridViewColumnSortMode.NotSortable; }
        var sortable = _sortable.Contains(column.Name);
        if (sortable && !_filters.ContainsKey(column.Name) && _customHeader?.Invoke(column) != true) AddFilter(column.Name);
        var filterable = _filters.ContainsKey(column.Name);
        if (!sortable && !filterable) return;
        if (sortable) column.SortMode = DataGridViewColumnSortMode.Programmatic;
        if (_customHeader?.Invoke(column) != true && column.HeaderCell is not TableHeaderCell)
        {
            var old = column.HeaderCell;
            column.HeaderCell = new TableHeaderCell(sortable, filterable, () => _filters.TryGetValue(column.Name, out var filter) && filter.IsFiltered)
            { Value = old.Value ?? column.HeaderText, Style = old.Style };
            var menu = new ContextMenuStrip();
            if (sortable)
            {
                menu.Items.Add("Sort ascending", null, (_, _) => SetSort(column.Name, SortOrder.Ascending));
                menu.Items.Add("Sort descending", null, (_, _) => SetSort(column.Name, SortOrder.Descending));
                menu.Items.Add("Sort off (default order)", null, (_, _) => SetSort(null, SortOrder.None));
            }
            menu.Opening += (_, _) =>
            {
                // Filters may be registered after auto-generated columns are installed.
                foreach (ToolStripItem item in menu.Items.Cast<ToolStripItem>().Where(item => item.Tag as string == "filter").ToArray()) { menu.Items.Remove(item); item.Dispose(); }
                if (_filters.TryGetValue(column.Name, out var filter))
                {
                    menu.Items.Add(new ToolStripMenuItem("Filter " + column.HeaderText + "…", null, (_, _) => ShowFilter(column)) { Tag = "filter" });
                    menu.Items.Add(new ToolStripMenuItem("Clear filter", null, (_, _) => filter.Clear()) { Tag = "filter" });
                }
            };
            column.HeaderCell.ContextMenuStrip = menu;
            _grid.Disposed += (_, _) => menu.Dispose();
        }
        if (column.HeaderCell is TableHeaderCell header) header.HasFilter = filterable;
        column.HeaderCell.ToolTipText = column.HeaderText + "\nClick heading: ascending → descending → off."
            + (filterable ? " Click funnel to filter. " + _filters[column.Name].Description : "") + " Right-click for commands.";
    }

    private void HeaderClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.ColumnIndex < 0) return;
        var column = _grid.Columns[e.ColumnIndex];
        var bounds = _grid.GetCellDisplayRectangle(e.ColumnIndex, -1, true);
        if (_filters.ContainsKey(column.Name) && TableHeaderCell.FilterBounds(new(0, 0, bounds.Width, bounds.Height), _grid.DeviceDpi / 96f).Contains(e.X, e.Y))
        { ShowFilter(column); return; }
        if (!_sortable.Contains(column.Name)) return;
        SetSort(column.Name, _sorted != column.Name ? SortOrder.Ascending : _direction switch
        { SortOrder.Ascending => SortOrder.Descending, SortOrder.Descending => SortOrder.None, _ => SortOrder.Ascending });
    }

    private void ShowFilter(DataGridViewColumn column)
    {
        if (!_grid.EndEdit()) return;
        RefreshOptions();
        var bounds = _grid.GetCellDisplayRectangle(column.Index, -1, true);
        _filters[column.Name].Show(_grid, new Point(Math.Max(0, bounds.Right - 280), bounds.Bottom));
    }

    public void SetSort(string? columnName, SortOrder direction)
    {
        if (!_grid.EndEdit()) return;
        RememberOrder();
        var previous = (_sorted, _direction);
        _sorted = direction == SortOrder.None ? null : columnName;
        _direction = direction;
        try { ApplySort(); }
        catch { (_sorted, _direction) = previous; throw; }
        Changed?.Invoke();
    }

    private void ApplySort()
    {
        var table = (_grid.DataSource as DataTable) ?? (_grid.DataSource as DataView)?.Table;
        if (_sorted is not null && _grid.Columns.Contains(_sorted))
        {
            var column = _grid.Columns[_sorted]!;
            var direction = _direction == SortOrder.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            if (_sort is not null) _sort(column, direction);
            else if (table is not null) table.DefaultView.Sort = EscapeColumn(column.DataPropertyName.Length > 0 ? column.DataPropertyName : column.Name) + (direction == ListSortDirection.Ascending ? " ASC" : " DESC");
            else _grid.Sort(column, direction);
        }
        else
        {
            _sorted = null; _direction = SortOrder.None;
            if (_restore is not null) _restore();
            else if (table is not null) table.DefaultView.Sort = "";
            else if (_grid.Rows.Count > 0) _grid.Sort(new DefaultOrderComparer(_defaultOrder));
        }
        foreach (DataGridViewColumn column in _grid.Columns)
            column.HeaderCell.SortGlyphDirection = _customHeader?.Invoke(column) != true && column.Name == _sorted ? _direction : SortOrder.None;
        _grid.Invalidate();
    }

    private void RememberOrder()
    {
        if (_grid.DataSource is not null) return;
        foreach (DataGridViewRow row in _grid.Rows)
            if (!row.IsNewRow && !_defaultOrder.ContainsKey(row)) _defaultOrder.Add(row, _nextOrder++);
    }

    private void QueueRefresh()
    {
        if (_busy || _queued || !_grid.IsHandleCreated || _grid.IsDisposed) return;
        _queued = true;
        _grid.BeginInvoke((Action)(() => { _queued = false; if (!_grid.IsDisposed) Refresh(); }));
    }

    public void Refresh()
    {
        if (_busy || _grid.IsDisposed) return;
        _busy = true;
        try
        {
            RememberOrder();
            RefreshOptions();
            foreach (DataGridViewColumn column in _grid.Columns) Install(column);
            ApplyFilters();
            // Off leaves freshly populated tables in their model order, without an unnecessary sort.
            if (_sorted is not null) ApplySort();
        }
        finally { _busy = false; }
    }

    private void RefreshOptions()
    {
        var table = (_grid.DataSource as DataTable) ?? (_grid.DataSource as DataView)?.Table;
        foreach (var filter in _filters.Values)
        {
            if (!_grid.Columns.Contains(filter.ColumnName)) continue;
            var column = _grid.Columns[filter.ColumnName]!;
            filter.SetOptions(table is not null && table.Columns.Contains(column.DataPropertyName)
                ? table.Rows.Cast<DataRow>().Select(row => row[column.DataPropertyName])
                : _grid.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow).Select(row => row.Cells[column.Index].Value));
        }
    }

    public void ApplyFilters()
    {
        var table = (_grid.DataSource as DataTable) ?? (_grid.DataSource as DataView)?.Table;
        if (table is not null)
        {
            var clauses = _filters.Values.Where(filter => filter.IsFiltered && table.Columns.Contains(filter.ColumnName)).Select(filter =>
            {
                // Match grouped choices to actual raw values (e.g. Obtainable task/discovery variants).
                var values = table.Rows.Cast<DataRow>().Select(row => row[filter.ColumnName]).Distinct().Where(filter.Includes)
                    .Select(value => FilterTerm(filter.ColumnName, value)).ToArray();
                return values.Length == 0 ? "false" : "(" + string.Join(" OR ", values) + ")";
            });
            var expression = string.Join(" AND ", clauses.Concat(string.IsNullOrEmpty(_additionalExpression) ? [] : new[] { _additionalExpression }).Select(clause => "(" + clause + ")"));
            if (table.DefaultView.RowFilter != expression) table.DefaultView.RowFilter = expression;
        }
        else
        {
            var active = _rowPredicate is not null || _filters.Values.Any(filter => filter.IsFiltered);
            if (!active && !_hadActiveFilters) return;
            _hadActiveFilters = active;
            bool Includes(DataGridViewRow row) => (_rowPredicate?.Invoke(row) ?? true) && _filters.Values.All(filter => !_grid.Columns.Contains(filter.ColumnName) || filter.Includes(row.Cells[filter.ColumnName].Value));
            if (_grid.CurrentCell is { OwningRow: { } current } && !Includes(current)) _grid.CurrentCell = null;
            foreach (DataGridViewRow row in _grid.Rows)
                if (!row.IsNewRow) row.Visible = Includes(row);
        }
        foreach (DataGridViewColumn column in _grid.Columns) Install(column);
        _grid.Invalidate();
    }

    private static string EscapeColumn(string name) => "[" + name.Replace("\\", "\\\\").Replace("]", "\\]") + "]";
    private static string FilterTerm(string name, object value)
    {
        var column = EscapeColumn(name);
        if (value is null or DBNull) return column + " IS NULL";
        if (value is bool flag) return column + (flag ? " = true" : " = false");
        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            return column + " = " + Convert.ToString(value, CultureInfo.InvariantCulture);
        return "Convert(" + column + ", 'System.String') = '" + Convert.ToString(value, CultureInfo.InvariantCulture)!.Replace("'", "''") + "'";
    }
    private sealed class DefaultOrderComparer(Dictionary<DataGridViewRow, long> order) : IComparer
    {
        public int Compare(object? x, object? y) => order.GetValueOrDefault((DataGridViewRow)x!).CompareTo(order.GetValueOrDefault((DataGridViewRow)y!));
    }
}

internal sealed class HeaderValueFilter : IDisposable
{
    private readonly Func<object?, string> _group;
    private readonly HashSet<string> _always;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _values = [];
    private bool _all = true;
    private readonly ToolStripDropDown _popup = new() { AutoClose = true };
    public string ColumnName { get; }
    public event Action? Changed;
    public bool IsFiltered => !_all;
    public string Description => $"{_values.Count(value => _all || _selected.Contains(value) || _always.Contains(value))} of {_values.Count} selected.";
    public HeaderValueFilter(string columnName, Func<object?, string>? group, IEnumerable<string> alwaysIncluded)
    { ColumnName = columnName; _group = group ?? (value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); _always = new(alwaysIncluded, StringComparer.OrdinalIgnoreCase); }
    public void SetOptions(IEnumerable<object?> values) => _values = values.Select(_group).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase).ToList();
    public bool Includes(object? value) => _all || _selected.Contains(_group(value)) || _always.Contains(_group(value));
    public void Clear() { _all = true; _selected.Clear(); Changed?.Invoke(); }
    public void SelectValues(IEnumerable<string> values) { _all = false; _selected.Clear(); _selected.UnionWith(values); Changed?.Invoke(); }
    public void Show(Control owner, Point location)
    {
        foreach (ToolStripItem item in _popup.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        _popup.Items.Clear();
        var options = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true };
        foreach (var value in _values) options.Items.Add(value.Length == 0 ? "(blank)" : value, _all || _selected.Contains(value) || _always.Contains(value));
        var commands = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, WrapContents = false };
        var all = new Button { Text = "Select all", AutoSize = true }; var none = new Button { Text = "Select none", AutoSize = true };
        commands.Controls.Add(all); commands.Controls.Add(none);
        var content = new Panel { Size = new Size(280, 330), Padding = new Padding(4) }; content.Controls.Add(options); content.Controls.Add(commands);
        _popup.Items.Add(new ToolStripControlHost(content) { AutoSize = false, Size = content.Size, Margin = Padding.Empty, Padding = Padding.Empty });
        var updating = false;
        void SelectAll(bool selected)
        {
            updating = true; for (var index = 0; index < options.Items.Count; index++) options.SetItemChecked(index, selected || _always.Contains(_values[index])); updating = false;
            if (selected) Clear(); else SelectValues([]);
        }
        all.Click += (_, _) => SelectAll(true); none.Click += (_, _) => SelectAll(false);
        options.ItemCheck += (_, e) =>
        {
            if (updating) return;
            var value = _values[e.Index];
            if (_always.Contains(value)) { e.NewValue = CheckState.Checked; return; }
            if (_all) { _selected.UnionWith(_values); _all = false; }
            if (e.NewValue == CheckState.Checked) _selected.Add(value); else _selected.Remove(value);
            if (_values.All(item => _selected.Contains(item) || _always.Contains(item))) _all = true;
            Changed?.Invoke();
        };
        _popup.AccessibleName = ColumnName + " filter";
        _popup.Show(owner, location);
    }
    public void Dispose() => _popup.Dispose();
}
