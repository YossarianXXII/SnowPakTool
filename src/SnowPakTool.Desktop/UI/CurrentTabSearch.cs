using System.Collections;
using System.Data;
using System.Globalization;
using System.Reflection;

namespace SnowPakTool.Desktop;

internal interface ISearchHighlight
{
    bool SearchMatch { get; set; }
    bool SearchActive { get; set; }
}

internal interface ISearchTextContent
{
    IEnumerable<string?> GetSearchText();
}

internal interface ISearchGridViewport
{
    void RevealCell(int row, int column);
}

/// <summary>One text search for every page: selected sub-tabs, controls, tables and their attached data.</summary>
internal sealed class CurrentTabSearch : IDisposable, IMessageFilter
{
    internal const int MinimumLength = 3;
    private sealed record Hit(Action<bool> Highlight, Action Restore, Action<bool> Navigate);
    private readonly Func<Control?> _getScope;
    private readonly List<Hit> _hits = [];
    private readonly HashSet<Control> _watched = [];
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 100 };
    private QuickSearchOverlay? _popup;
    private string _query = "";
    private bool _disposed;
    private Control? _owner;
    private int _activeHit = -1;

    public CurrentTabSearch(Func<Control?> getScope)
    {
        _getScope = getScope;
        _refresh.Tick += (_, _) => { _refresh.Stop(); if (_query.Length >= MinimumLength) Search(_query); };
    }

    public void Attach(Control owner)
    {
        if (_popup is not null) throw new InvalidOperationException("Search is already attached.");
        _popup = new QuickSearchOverlay(owner, Find, Search);
        _owner = owner;
        Application.AddMessageFilter(this);
        Watch(owner);
    }

    public int Search(string query)
    {
        var previousHit = string.Equals(_query, query.Trim(), StringComparison.CurrentCultureIgnoreCase) ? _activeHit : -1;
        Clear();
        _query = query.Trim();
        if (_query.Length < MinimumLength || _getScope() is not { IsDisposed: false } scope) return 0;
        Visit(scope);
        _activeHit = _hits.Count == 0 ? -1 : Math.Clamp(previousHit, 0, _hits.Count - 1);
        HighlightActive();
        if (_activeHit >= 0) _hits[_activeHit].Navigate(false);
        return _hits.Count;
    }

    public bool Find(string query)
    {
        if (Search(query) == 0) return false;
        _activeHit = 0;
        HighlightActive();
        _hits[0].Navigate(true);
        return true;
    }

    public void Clear()
    {
        _refresh.Stop();
        foreach (var hit in _hits) { hit.Highlight(false); hit.Restore(); }
        _hits.Clear();
        _query = "";
        _activeHit = -1;
    }

    public bool MoveMatch(bool previous)
    {
        if (_hits.Count == 0 || _getScope() is not { IsDisposed: false, Visible: true }) return false;
        _activeHit = _activeHit < 0 ? (previous ? _hits.Count - 1 : 0)
            : (_activeHit + (previous ? -1 : 1) + _hits.Count) % _hits.Count;
        HighlightActive();
        _hits[_activeHit].Navigate(true);
        return true;
    }

    private void HighlightActive()
    {
        for (var index = 0; index < _hits.Count; index++) _hits[index].Highlight(index == _activeHit);
    }

    public bool PreFilterMessage(ref Message message)
    {
        if (_disposed || _owner?.ContainsFocus != true || message.Msg != 0x100 || (Keys)message.WParam.ToInt32() != Keys.Tab
            || (Control.ModifierKeys & (Keys.Control | Keys.Alt)) != Keys.None) return false;
        return MoveMatch((Control.ModifierKeys & Keys.Shift) != Keys.None);
    }

    private bool Matches(string? text) => text?.Contains(_query, StringComparison.CurrentCultureIgnoreCase) == true;

    private void Visit(Control control)
    {
        if (control.IsDisposed || !control.Visible) return;
        if (control is TabControl tabs)
        {
            if (tabs.SelectedTab is not null) Visit(tabs.SelectedTab);
            return; // Never index inactive tab pages or their values.
        }
        if (control is DataGridView grid) { VisitGrid(grid); return; }
        if (control is ISearchHighlight highlight)
        {
            if (ControlStrings(control, includeChildren: true).Any(Matches))
            {
                var previous = highlight.SearchMatch;
                var previousActive = highlight.SearchActive;
                _hits.Add(new(active => { if (!control.IsDisposed) { highlight.SearchMatch = true; highlight.SearchActive = active; } },
                    () => { if (!control.IsDisposed) { highlight.SearchMatch = previous; highlight.SearchActive = previousActive; } }, focus => Reveal(control, focus)));
            }
            return;
        }
        if (ControlStrings(control, includeChildren: false).Any(Matches)) AddControl(control);
        foreach (Control child in control.Controls) Visit(child);
    }

    private static IEnumerable<string?> ControlStrings(Control control, bool includeChildren)
    {
        yield return control.Text;
        yield return control.AccessibleName;
        yield return control.AccessibleDescription;
        foreach (var text in ValueStrings(control.Tag)) yield return text;
        if (control is ISearchTextContent custom)
            foreach (var text in custom.GetSearchText()) yield return text;
        if (control is ComboBox combo)
            foreach (var item in combo.Items) yield return combo.GetItemText(item);
        if (control is ListBox list)
            foreach (var item in list.Items) yield return list.GetItemText(item);
        if (includeChildren)
            foreach (Control child in control.Controls)
                if (child.Visible)
                    foreach (var text in ControlStrings(child, true)) yield return text;
    }

    private static IEnumerable<string?> ValueStrings(object? value, int depth = 0)
    {
        if (value is null or DBNull || depth > 6) yield break;
        if (value is string text) { yield return text; yield break; }
        if (value is IFormattable formatted) { yield return formatted.ToString(null, CultureInfo.CurrentCulture); yield break; }
        if (value is DataRowView row)
        {
            foreach (var item in row.Row.ItemArray)
                foreach (var part in ValueStrings(item, depth + 1)) yield return part;
            yield break;
        }
        if (value is IEnumerable items)
        {
            foreach (var item in items)
                foreach (var part in ValueStrings(item, depth + 1)) yield return part;
            yield break;
        }
        // Only inspect our data records, never arbitrary UI/native object graphs.
        if (value.GetType().Namespace != typeof(CurrentTabSearch).Namespace) yield break;
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            if (property.CanRead && property.GetIndexParameters().Length == 0)
                foreach (var part in ValueStrings(property.GetValue(value), depth + 1)) yield return part;
    }

    private void AddControl(Control control)
    {
        var previous = control.BackColor;
        var previousText = control.ForeColor;
        var themedButton = control is ButtonBase button && button.UseVisualStyleBackColor;
        var color = AppColors.Get("EngineMatch");
        _hits.Add(new(active => { if (!control.IsDisposed) { control.BackColor = active ? AppColors.Get("SearchActive") : color; control.ForeColor = SystemColors.ControlText; } },
            () =>
            {
                if (control.IsDisposed || control.BackColor != color) return;
                control.BackColor = previous;
                if (control.ForeColor == SystemColors.ControlText) control.ForeColor = previousText;
                if (control is ButtonBase restoreButton) restoreButton.UseVisualStyleBackColor = themedButton;
            },
            focus =>
            {
                Reveal(control, focus);
                if (focus && control is TextBoxBase text && text.Text.IndexOf(_query, StringComparison.CurrentCultureIgnoreCase) is var index && index >= 0)
                { text.Select(index, _query.Length); text.ScrollToCaret(); }
            }));
    }

    private void VisitGrid(DataGridView grid)
    {
        foreach (DataGridViewColumn column in grid.Columns)
            if (column.Visible && grid.ColumnHeadersVisible && (Matches(column.HeaderText) || Matches(column.HeaderCell.ToolTipText)))
                AddCell(grid, column.HeaderCell, null, column.Index);
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow || !row.Visible) continue;
            var foundCell = false;
            foreach (DataGridViewCell cell in row.Cells)
            {
                if (cell.OwningColumn?.Visible != true) continue;
                if (Matches(Convert.ToString(cell.FormattedValue, CultureInfo.CurrentCulture)) || Matches(cell.ToolTipText))
                { AddCell(grid, cell, row, cell.ColumnIndex); foundCell = true; }
            }
            if (foundCell) continue;
            var metadata = ValueStrings(row.DataBoundItem).Concat(ValueStrings(row.Tag))
                .Concat(row.Cells.Cast<DataGridViewCell>().Where(cell => cell.OwningColumn?.Visible == false).SelectMany(cell => ValueStrings(cell.Value)));
            if (metadata.Any(Matches) && row.Cells.Cast<DataGridViewCell>().FirstOrDefault(cell => cell.OwningColumn?.Visible == true) is { } first)
                AddCell(grid, first, row, first.ColumnIndex);
        }
    }

    private void AddCell(DataGridView grid, DataGridViewCell cell, DataGridViewRow? row, int column)
    {
        var color = AppColors.Get("EngineMatch");
        var previous = cell.Style.BackColor;
        var previousText = cell.Style.ForeColor;
        var previousSelection = cell.Style.SelectionBackColor;
        var previousSelectionText = cell.Style.SelectionForeColor;
        var themedHeaders = grid.EnableHeadersVisualStyles;
        _hits.Add(new(active =>
            {
                if (grid.IsDisposed) return;
                if (row is null) grid.EnableHeadersVisualStyles = false;
                cell.Style.BackColor = active ? AppColors.Get("SearchActive") : color;
                cell.Style.ForeColor = SystemColors.ControlText;
                cell.Style.SelectionBackColor = cell.Style.BackColor;
                cell.Style.SelectionForeColor = SystemColors.ControlText;
            },
            () =>
            {
                if (grid.IsDisposed) return;
                if (cell.Style.BackColor == color) { cell.Style.BackColor = previous; cell.Style.ForeColor = previousText; }
                cell.Style.SelectionBackColor = previousSelection;
                cell.Style.SelectionForeColor = previousSelectionText;
                if (row is null) grid.EnableHeadersVisualStyles = themedHeaders;
            }, focus =>
            {
                if (grid.IsDisposed || !grid.IsHandleCreated) return;
                Reveal(grid, false);
                if (row is { Index: >= 0, Visible: true })
                {
                    if (grid is ISearchGridViewport viewport) viewport.RevealCell(row.Index, column);
                    else if (!row.Frozen && grid.DisplayedRowCount(true) > 0) grid.FirstDisplayedScrollingRowIndex = row.Index;
                    if (focus) grid.CurrentCell = row.Cells[column];
                }
                if (column < grid.Columns.Count && grid.DisplayedColumnCount(true) > 0
                    && grid.Columns[column] is { Visible: true, Frozen: false })
                    grid.FirstDisplayedScrollingColumnIndex = column;
                if (focus) grid.Focus();
            }));
    }

    private static void Reveal(Control control, bool focus)
    {
        if (control.IsDisposed) return;
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
            if (parent is ScrollableControl { AutoScroll: true } scroll) scroll.ScrollControlIntoView(control);
        if (focus && control.CanFocus) control.Focus();
    }

    private void Watch(Control control)
    {
        if (!_watched.Add(control)) return;
        control.ControlAdded += ControlAdded;
        control.TextChanged += ContentChanged;
        control.Disposed += ControlDisposed;
        if (control is TabControl tabs) tabs.SelectedIndexChanged += TabChanged;
        if (control is DataGridView grid)
        {
            grid.DataBindingComplete += GridBound;
            grid.Sorted += ContentChanged;
            grid.CellValueChanged += GridCellChanged;
            grid.RowsAdded += GridRowsAdded;
            grid.RowsRemoved += GridRowsRemoved;
        }
        foreach (Control child in control.Controls) Watch(child);
    }

    private void ControlAdded(object? sender, ControlEventArgs e) { if (e.Control is not null) Watch(e.Control); ContentChanged(sender, e); }
    private void ControlDisposed(object? sender, EventArgs e) { if (sender is Control control) Unwatch(control); }
    private void TabChanged(object? sender, EventArgs e) { if (sender is Control { Visible: true }) { _popup?.Dismiss(); Clear(); } }
    private void GridBound(object? sender, DataGridViewBindingCompleteEventArgs e) => ContentChanged(sender, e);
    private void GridCellChanged(object? sender, DataGridViewCellEventArgs e) => ContentChanged(sender, e);
    private void GridRowsAdded(object? sender, DataGridViewRowsAddedEventArgs e) => ContentChanged(sender, e);
    private void GridRowsRemoved(object? sender, DataGridViewRowsRemovedEventArgs e) => ContentChanged(sender, e);
    private void ContentChanged(object? sender, EventArgs e)
    {
        if (!_disposed && _query.Length >= MinimumLength && sender is Control control && _getScope() is { } scope
            && (control == scope || scope.Contains(control))) { _refresh.Stop(); _refresh.Start(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Application.RemoveMessageFilter(this);
        _popup?.Dispose();
        Clear();
        _refresh.Dispose();
        foreach (var control in _watched.ToArray()) Unwatch(control);
        _watched.Clear();
    }

    private void Unwatch(Control control)
    {
        _watched.Remove(control);
        control.ControlAdded -= ControlAdded;
        control.TextChanged -= ContentChanged;
        control.Disposed -= ControlDisposed;
        if (control is TabControl tabs) tabs.SelectedIndexChanged -= TabChanged;
        if (control is not DataGridView grid) return;
        grid.DataBindingComplete -= GridBound;
        grid.Sorted -= ContentChanged;
        grid.CellValueChanged -= GridCellChanged;
        grid.RowsAdded -= GridRowsAdded;
        grid.RowsRemoved -= GridRowsRemoved;
    }
}
