namespace SnowPakTool.Desktop;

/// <summary>Routes wheel input to the hovered pane without changing its axis.</summary>
internal sealed class DirectionalWheelFilter(Control root) : IMessageFilter
{
    private const int MouseWheel = 0x020A;
    private const int MouseHorizontalWheel = 0x020E;
    private ScrollableControl? _lastVerticalTarget;
    private ScrollableControl? _lastHorizontalTarget;
    private double _verticalRemainder;
    private double _horizontalRemainder;

    public bool PreFilterMessage(ref Message message)
    {
        if (message.Msg is not (MouseWheel or MouseHorizontalWheel) || root.IsDisposed || !root.Visible || !root.IsHandleCreated)
            return false;

        var coordinates = message.LParam.ToInt64();
        var screenPoint = new Point((short)(coordinates & 0xffff), (short)((coordinates >> 16) & 0xffff));
        if (!root.RectangleToScreen(root.ClientRectangle).Contains(screenPoint))
            return false;

        Control hovered = root;
        while (hovered.GetChildAtPoint(hovered.PointToClient(screenPoint), GetChildAtPointSkip.Invisible | GetChildAtPointSkip.Disabled) is { } child)
            hovered = child;

        var horizontal = message.Msg == MouseHorizontalWheel;
        var delta = (short)((message.WParam.ToInt64() >> 16) & 0xffff);
        for (Control? current = hovered; current is not null; current = current.Parent)
        {
            if (current is ScrollableControl { AutoScroll: true } pane)
            {
                ScrollProperties scroll = horizontal ? pane.HorizontalScroll : pane.VerticalScroll;
                var maximum = Math.Max(scroll.Minimum, scroll.Maximum - scroll.LargeChange + 1);
                if (scroll.Visible && maximum > scroll.Minimum)
                {
                    // A vertical wheel's positive delta means up; horizontal means right.
                    var direction = horizontal ? delta : -delta;
                    if (direction < 0 && scroll.Value > scroll.Minimum || direction > 0 && scroll.Value < maximum)
                    {
                        Move(pane, horizontal, direction, maximum);
                        break;
                    }
                }
            }
            if (ReferenceEquals(current, root)) break;
        }

        // Consume even at the edge: WinForms otherwise converts vertical wheel input
        // into horizontal scrolling when only a horizontal scrollbar is present.
        message.Result = IntPtr.Zero;
        return true;
    }

    private void Move(ScrollableControl pane, bool horizontal, int delta, int maximum)
    {
        ref var lastTarget = ref (horizontal ? ref _lastHorizontalTarget : ref _lastVerticalTarget);
        ref var remainder = ref (horizontal ? ref _horizontalRemainder : ref _verticalRemainder);
        if (!ReferenceEquals(lastTarget, pane)) remainder = 0;
        lastTarget = pane;
        ScrollProperties scroll = horizontal ? pane.HorizontalScroll : pane.VerticalScroll;
        var lines = SystemInformation.MouseWheelScrollLines;
        var distance = lines < 0 ? scroll.LargeChange : Math.Max(40, scroll.SmallChange) * lines;
        remainder += delta / 120.0 * distance;
        var pixels = (int)remainder;
        remainder -= pixels;
        var position = Math.Clamp(scroll.Value + pixels, scroll.Minimum, maximum);
        var offset = pane.AutoScrollPosition;
        pane.AutoScrollPosition = horizontal
            ? new Point(position, -offset.Y)
            : new Point(-offset.X, position);
    }
}

