using System.Reflection;

internal static class Preview
{
    [STAThread] static void Main()
    {
        var app = Assembly.Load("SnowPakTool.Desktop");
        var state = Activator.CreateInstance(app.GetType("SnowPakTool.Desktop.OwnedRegionState")!);
        using var tab = (Control)Activator.CreateInstance(app.GetType("SnowPakTool.Desktop.OwnedContentTab")!, new Func<string>(() => ""), state)!;
        tab.Size = new Size(1400, 900); tab.CreateControl();
        IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(control => new[] { control }.Concat(All(control)));
        void Capture(string name)
        {
            tab.PerformLayout();
            using var bitmap = new Bitmap(tab.Width, tab.Height); tab.DrawToBitmap(bitmap, new Rectangle(Point.Empty, tab.Size));
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, name + ".png"));
        }
        var toggles = All(tab).OfType<LinkLabel>().ToArray();
        void Toggle(LinkLabel glyph) => typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(glyph, new object[] { new LinkLabelLinkClickedEventArgs(glyph.Links[0]) });
        Capture("expanded");
        Toggle(toggles.Single(glyph => glyph.AccessibleName == "Collapse Truck DLCs")); Capture("truck-collapsed");
        foreach (var glyph in toggles.Where(glyph => glyph.AccessibleName!.StartsWith("Collapse "))) Toggle(glyph);
        Capture("all-collapsed");
        foreach (var glyph in toggles) Toggle(glyph);
        Capture("reopened");
        Console.WriteLine("Captured expanded, single collapsed, all collapsed, and reopened DLC panes using an empty workspace.");
    }
}
