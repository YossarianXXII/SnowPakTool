using System.Collections;
using System.Reflection;

internal static class Program
{
    private static object Field(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    private static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(obj, args);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    [STAThread] private static void Main()
    {
        Application.EnableVisualStyles();
        var app = Assembly.Load("SnowPakTool.Desktop");
        using var tab = (Control)Activator.CreateInstance(app.GetType("SnowPakTool.Desktop.OwnedContentTab")!, new Func<string>(() => ""), Activator.CreateInstance(app.GetType("SnowPakTool.Desktop.OwnedRegionState")!))!;
        tab.Size = new Size(1400, 900); tab.CreateControl(); tab.PerformLayout();
        var grid = (DataGridView)Field(tab, "_truckGrid");
        grid.Rows.Add("Zulu", "Season 2", "Scout"); grid.Rows.Add("Alpha", "Base game", "Offroad");
        grid.Rows.Add("Bravo", "Season 2", "Offroad"); grid.Rows.Add("Delta", "Base game", "Scout");
        var tagType = tab.GetType().GetNestedType("TruckRowTag", BindingFlags.NonPublic)!;
        foreach (DataGridViewRow row in grid.Rows)
        {
            var baseGame = (string)row.Cells["DlcName"].Value! == "Base game";
            row.Tag = Activator.CreateInstance(tagType, baseGame ? "base_game" : "season_2", row.Cells["DlcName"].Value,
                row.Cells["TruckName"].Value, baseGame ? null : new DateOnly(2020, 11, 16), baseGame);
        }
        Call(tab, "ClearTruckSort");
        Call(tab, "UpdateTruckFilterOptions");
        void ClickHeader(string column)
        {
            var e = new DataGridViewCellMouseEventArgs(grid.Columns[column]!.Index, -1, 8, 10, new MouseEventArgs(MouseButtons.Left, 1, 8, 10, 0));
            typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(grid, new object[] { e });
        }
        string[] Visible() => grid.Rows.Cast<DataGridViewRow>().Where(row => row.Visible).Select(row => (string)row.Cells["TruckName"].Value!).ToArray();
        void Capture(string name)
        {
            tab.PerformLayout(); using var bitmap = new Bitmap(tab.Width, tab.Height); tab.DrawToBitmap(bitmap, new Rectangle(Point.Empty, tab.Size));
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, name + ".png"));
        }
        ClickHeader("TruckName");
        Check(Visible().SequenceEqual(new[] { "Alpha", "Bravo", "Delta", "Zulu" }) && grid.Columns["TruckName"]!.HeaderCell.SortGlyphDirection == SortOrder.Ascending, "first header click sorts ascending with its glyph");
        Capture("ascending");
        ClickHeader("TruckName");
        Check(Visible().SequenceEqual(new[] { "Zulu", "Delta", "Bravo", "Alpha" }) && grid.Columns["TruckName"]!.HeaderCell.SortGlyphDirection == SortOrder.Descending, "second header click reverses sorting and glyph");
        Capture("descending");
        ClickHeader("TruckName");
        Check(Visible().SequenceEqual(new[] { "Alpha", "Delta", "Bravo", "Zulu" }) && grid.Columns.Cast<DataGridViewColumn>().All(column => column.HeaderCell.SortGlyphDirection == SortOrder.None), "third click turns sorting off and restores base-game/release default order without arrows");
        Capture("sort-off");
        ClickHeader("TruckName");
        Check(Visible().SequenceEqual(new[] { "Alpha", "Bravo", "Delta", "Zulu" }) && grid.Columns["TruckName"]!.HeaderCell.SortGlyphDirection == SortOrder.Ascending, "click after off starts a new ascending cycle");
        ClickHeader("TruckClass");
        Check((string)grid.Rows[0].Cells["TruckClass"].Value! == "Offroad" && grid.Columns["TruckName"]!.HeaderCell.SortGlyphDirection == SortOrder.None, "another heading starts ascending and clears the previous sort arrow");
        var filters = ((IEnumerable)Field(tab, "_truckFilters")).Cast<object>().ToArray();
        object Filter(string column) => filters.Single(filter => (string)filter.GetType().GetProperty("ColumnName")!.GetValue(filter)! == column);
        void Exclude(string column, string value)
        {
            var options = (CheckedListBox)Field(Filter(column), "_options"); options.CreateControl();
            options.SetItemChecked(options.Items.IndexOf(value), false); Application.DoEvents();
        }
        Exclude("DlcName", "Base game");
        Check(Visible().ToHashSet().SetEquals(new[] { "Zulu", "Bravo" }), "DLC filter hides only excluded DLC trucks");
        Exclude("TruckClass", "Scout");
        Check(Visible().SequenceEqual(new[] { "Bravo" }), "column filters combine using AND");
        Capture("filtered");
        ClickHeader("TruckName");
        Check(Visible().SequenceEqual(new[] { "Bravo" }), "sorting preserves filters");
        ClickHeader("TruckName"); ClickHeader("TruckName");
        Check(Visible().SequenceEqual(new[] { "Bravo" }) && grid.Columns.Cast<DataGridViewColumn>().All(column => column.HeaderCell.SortGlyphDirection == SortOrder.None), "sorting off preserves active filters");
        foreach (var filter in filters) Call(filter, "Clear");
        Check(Visible().Length == 4 && grid.Columns["DlcName"]!.HeaderCell.ToolTipText.Contains("2 of 2 selected"), "clear filter restores choices and refreshes header counts");
        var clone = grid.Columns[0].HeaderCell.Clone(); Check(clone.GetType() == grid.Columns[0].HeaderCell.GetType(), "custom headers support grid cloning");
        Capture("cleared");
        Console.WriteLine("Header filtering/sorting verified using display-only fixture rows; no workspace or ownership changes.");
    }
}
