using SnowPakTool.Desktop;
internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var root = new UserControl { Size = new Size(500, 400) };
        var outer = new Panel { Dock = DockStyle.Fill, AutoScroll = true, AutoScrollMinSize = new Size(1400, 1400) };
        var inner = new Panel { Location = new Point(10, 10), Size = new Size(300, 180), AutoScroll = true, AutoScrollMinSize = new Size(1200, 0) };
        var label = new Label { Location = new Point(5, 5), Size = new Size(100, 60), Text = "Tile title" };
        root.Controls.Add(outer); outer.Controls.Add(inner); inner.Controls.Add(label);
        root.CreateControl(); outer.CreateControl(); inner.CreateControl(); label.CreateControl();
        root.PerformLayout(); outer.PerformLayout(); inner.PerformLayout();
        var filter = new DirectionalWheelFilter(root);
        void Assert(bool result, string text) { if(!result) throw new Exception(text); }
        bool Wheel(int msg, int delta, Point point)
        {
            var screen = root.PointToScreen(point);
            var message = Message.Create(label.Handle, msg, new IntPtr((long)(ushort)(short)delta << 16), new IntPtr((screen.X & 0xffff) | ((long)(screen.Y & 0xffff) << 16)));
            return filter.PreFilterMessage(ref message);
        }
        var hit = new Point(30, 30);
        Assert(Wheel(0x20e, 120, hit), "Horizontal input not consumed");
        Assert(inner.HorizontalScroll.Value > 0 && outer.VerticalScroll.Value == 0, "Horizontal input changed wrong axis");
        inner.AutoScrollPosition = Point.Empty;
        Wheel(0x20a, -120, hit);
        Assert(outer.VerticalScroll.Value > 0 && inner.HorizontalScroll.Value == 0 && outer.HorizontalScroll.Value == 0, "Vertical input did not bubble vertically");
        outer.AutoScrollPosition = Point.Empty;
        Wheel(0x20a, 120, hit);
        Assert(inner.HorizontalScroll.Value == 0 && outer.HorizontalScroll.Value == 0, "Vertical edge scrolled horizontally");
        Wheel(0x20e, 120, hit); Wheel(0x20e, -120, hit);
        Assert(inner.HorizontalScroll.Value == 0, "Horizontal reverse did not scroll left");
        Wheel(0x20e, 30, hit); Wheel(0x20e, 30, hit); Wheel(0x20e, 30, hit); Wheel(0x20e, 30, hit);
        Assert(inner.HorizontalScroll.Value > 0, "High-resolution input lost");
        Assert(!Wheel(0x20e,120,new Point(700,700)), "Outside input consumed");
        root.Visible = false;
        Assert(!Wheel(0x20a,-120,hit), "Inactive tab consumed wheel");
        Console.WriteLine("PASS: horizontal left/right, vertical parent scrolling, edge axis isolation, label hover, fractional wheel input, outside and inactive tabs.");
    }
}
