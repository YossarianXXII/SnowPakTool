namespace SnowPakTool.Desktop;

/// <summary>Keep the full table inside a pixel-scrolling viewport instead of snapping to variable-height rows.</summary>
internal sealed class SmoothComparisonPanel : Panel
{
    private readonly DataGridView _grid;
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 15 };
    private double _target;
    private bool _moving;
    private bool _layoutPending;

    public SmoothComparisonPanel(DataGridView grid)
    {
        _grid = grid;
        AutoScroll = true; DoubleBuffered = true;
        grid.Dock = DockStyle.Top; grid.ScrollBars = ScrollBars.None;
        Controls.Add(grid);
        grid.DataBindingComplete += (_, _) => { StopAnimation(); AutoScrollPosition = Point.Empty; RequestExtent(); };
        grid.RowHeightChanged += (_, _) => RequestExtent();
        grid.ColumnWidthChanged += (_, _) => RequestExtent();
        grid.CurrentCellChanged += (_, _) => EnsureCurrentCellVisible();
        SizeChanged += (_, _) => RequestExtent();
        HandleCreated += (_, _) => RequestExtent();
        _animation.Tick += (_, _) =>
        {
            var current = -AutoScrollPosition.Y;
            _target = Math.Clamp(_target, 0, MaximumOffset);
            var remaining = _target - current;
            var next = Math.Abs(remaining) < 2 ? (int)Math.Round(_target) : current + (int)(Math.Sign(remaining) * Math.Ceiling(Math.Abs(remaining) * .28));
            _moving = true; AutoScrollPosition = new Point(0, next); _moving = false;
            if (Math.Abs(_target + AutoScrollPosition.Y) < 1) _animation.Stop();
        };
    }

    internal int MaximumOffset => Math.Max(0, _grid.Height - ClientSize.Height);

    internal void ScrollWheel(int delta)
    {
        if (!_animation.Enabled) _target = -AutoScrollPosition.Y;
        var distance = SystemInformation.MouseWheelScrollLines < 0 ? ClientSize.Height * .85 : Math.Max(1, SystemInformation.MouseWheelScrollLines) * 18;
        _target = Math.Clamp(_target - delta / 120d * distance, 0, MaximumOffset);
        _animation.Start();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        ScrollWheel(e.Delta);
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        if (!_moving) StopAnimation();
        base.OnScroll(se);
    }

    // The grid is larger than the viewport; focusing it must not jump to its bottom edge.
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

    private void RequestExtent()
    {
        if (_layoutPending || !IsHandleCreated || IsDisposed) return;
        _layoutPending = true;
        BeginInvoke((Action)(() =>
        {
            _layoutPending = false;
            if (IsDisposed) return;
            _grid.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCells);
            _grid.Height = Math.Max(40, _grid.ColumnHeadersHeight + _grid.Rows.Cast<DataGridViewRow>().Sum(row => row.Height) + 2);
            VerticalScroll.SmallChange = 18;
            VerticalScroll.LargeChange = Math.Max(18, ClientSize.Height - 36);
        }));
    }

    private void EnsureCurrentCellVisible()
    {
        if (_grid.CurrentCell is not { RowIndex: >= 0 } cell || !IsHandleCreated) return;
        RevealCell(cell.RowIndex, cell.ColumnIndex);
    }

    internal void RevealCell(int row, int column)
    {
        var bounds = _grid.GetCellDisplayRectangle(column, row, false);
        var offset = -AutoScrollPosition.Y;
        if (bounds.Top < offset) offset = bounds.Top;
        else if (bounds.Bottom > offset + ClientSize.Height) offset = Math.Min(bounds.Top, bounds.Bottom - ClientSize.Height);
        StopAnimation(); AutoScrollPosition = new Point(0, Math.Clamp(offset, 0, MaximumOffset));
    }

    private void StopAnimation() { _animation.Stop(); _target = -AutoScrollPosition.Y; }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _animation.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class SmoothComparisonGrid : DataGridView, ISearchGridViewport
{
    public SmoothComparisonGrid() { DoubleBuffered = true; }

    public void RevealCell(int row, int column)
    {
        if (Parent is SmoothComparisonPanel viewport) viewport.RevealCell(row, column);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (Parent is SmoothComparisonPanel viewport)
        {
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            viewport.ScrollWheel(e.Delta);
        }
        else base.OnMouseWheel(e);
    }
}
