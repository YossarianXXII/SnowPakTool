namespace SnowPakTool.Desktop;

/// <summary>Native keyboard/tab behavior with a clearly highlighted active destination.</summary>
internal sealed class NavigationTabControl : TabControl
{
    private Font? _activeFont;

    public NavigationTabControl()
    {
        Dock = DockStyle.Fill;
        DrawMode = TabDrawMode.OwnerDrawFixed;
        Padding = new Point(18, 5);
        ItemSize = new Size(0, 32);
        Multiline = false;
        HotTrack = false;
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        _activeFont?.Dispose();
        _activeFont = null;
        base.OnFontChanged(e);
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= TabPages.Count) return;
        var selected = e.Index == SelectedIndex;
        var bounds = GetTabRect(e.Index);
        var background = selected ? SystemColors.Highlight : SystemColors.Control;
        var foreground = selected ? SystemColors.HighlightText : SystemColors.ControlText;
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, bounds);
        using var border = new Pen(selected ? SystemColors.Highlight : SystemColors.ControlDark);
        e.Graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        if (selected)
        {
            var stripe = new Rectangle(bounds.X + 1, bounds.Bottom - Math.Max(3, DeviceDpi / 32), bounds.Width - 2, Math.Max(3, DeviceDpi / 32));
            using var stripeBrush = new SolidBrush(SystemColors.HighlightText);
            e.Graphics.FillRectangle(stripeBrush, stripe);
        }
        var textBounds = Rectangle.Inflate(bounds, -8, -3);
        TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text,
            selected ? _activeFont ??= new Font(Font, FontStyle.Bold) : Font,
            textBounds, foreground, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (selected && Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -4, -5), foreground, background);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _activeFont?.Dispose();
        base.Dispose(disposing);
    }
}
