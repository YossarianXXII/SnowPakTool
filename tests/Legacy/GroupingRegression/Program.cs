using System.Reflection;
using SnowPakTool.Desktop;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        foreach (var tierMode in new[] { false, true }) CheckPersistence(tierMode);
        CheckColors();
        CheckLayoutAndSearch();
        Console.WriteLine("PASS: both tabs preserve startup/filter assignments, auto-save moves and ordering, restore after restart, and apply all five class colors live.");
    }
    static object Field(object o, string name) => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    static void Invoke(object o, string name) => o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o, null);
    static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    static List<GroupState> Load(bool tiers) => tiers ? TierListStore.Load() : CustomGroupStore.Load();
    static void Save(bool tiers, List<GroupState> states) { if(tiers) TierListStore.Save(states); else CustomGroupStore.Save(states); }
    static TileFlowPanel FirstTiles(CustomGroupWipTab tab)
    {
        var list = (FlowLayoutPanel)Field(tab, "_groupList");
        return Descendants(list.Controls[0]).OfType<TileFlowPanel>().Single();
    }
    static IEnumerable<Control> Descendants(Control c)
    {
        foreach(Control child in c.Controls) { yield return child; foreach(var d in Descendants(child)) yield return d; }
    }
    static void CheckPersistence(bool tiers)
    {
        var seed = tiers ? TierListStore.Load() : new List<GroupState> { new() { Name = "Regression", Description = "Saved description" } };
        seed[0].TruckCodes = ["alpha", "beta", "gamma"];
        Save(tiers, seed);
        var trucks = new List<CustomTruck>();
        using(var tab = new CustomGroupWipTab(() => trucks, tiers))
        {
            Assert(((List<GroupState>)Field(tab, "_states"))[0].TruckCodes.Count == 3, "Empty startup erased assignments");
            trucks.AddRange([new("alpha", "Alpha", "Heavy"), new("beta", "Beta", "Scout"), new("gamma", "Gamma", "Offroad")]);
            tab.RefreshOwnedTrucks();
            Assert(FirstTiles(tab).Controls.Count == 3, "Saved trucks did not reappear");
            trucks.RemoveAt(2);
            tab.RefreshOwnedTrucks();
            var panel = FirstTiles(tab);
            panel.Controls.SetChildIndex(panel.Controls[1], 0);
            Invoke(tab, "SyncGroupAssignments");
            Assert(Load(tiers)[0].TruckCodes.SequenceEqual(new[] { "beta", "alpha", "gamma" }), "Reorder/filter auto-save failed");
            var unsorted = (TileFlowPanel)Field(tab, "_unsortedTiles");
            unsorted.Controls.Add(panel.Controls[1]);
            Invoke(tab, "SyncGroupAssignments");
            Assert(Load(tiers)[0].TruckCodes.SequenceEqual(new[] { "beta", "gamma" }), "Move back to Unsorted not saved");
        }
        trucks.Clear();
        using(var restarted = new CustomGroupWipTab(() => trucks, tiers))
        {
            trucks.AddRange([new("alpha", "Alpha", "Heavy"), new("beta", "Beta", "Scout"), new("gamma", "Gamma", "Offroad")]);
            restarted.RefreshOwnedTrucks();
            Assert(FirstTiles(restarted).Controls.Cast<Control>().Select(c => (string)c.Tag!).SequenceEqual(new[] { "beta", "gamma" }), "Restart lost assignments/order");
            Assert(((TileFlowPanel)Field(restarted, "_unsortedTiles")).Controls.Count == 1, "Unsorted restore failed");
        }
    }
    static void CheckLayoutAndSearch()
    {
        var trucks = Enumerable.Range(0, 14).Select(i => new CustomTruck("code" + i, "Truck " + i, "Heavy")).ToList();
        var states = TierListStore.Load();
        foreach(var state in states) state.TruckCodes.Clear();
        states[0].TruckCodes = ["code0", "code1"];
        states[^1].TruckCodes = trucks.Skip(2).Select(t => t.Code).ToList();
        TierListStore.Save(states);
        using var tab = new CustomGroupWipTab(() => trucks, true) { Size = new Size(1000, 600) };
        tab.CreateControl(); tab.PerformLayout();
        var list = (FlowLayoutPanel)Field(tab, "_groupList");
        list.CreateControl(); list.PerformLayout();
        foreach(Control pane in list.Controls) { pane.CreateControl(); pane.PerformLayout(); }
        var first = FirstTiles(tab);
        first.CreateControl(); first.PerformLayout();
        Assert(first.Controls[1].Left - first.Controls[0].Right == 1, "Tile gap is not one pixel");
        var header = Descendants(list.Controls[0]).OfType<Label>().Single(label => label.Text == "SS");
        Assert(header.PointToScreen(Point.Empty).Y == first.PointToScreen(first.Controls[0].Location).Y, "Header/tile tops differ");
        var find = typeof(CustomGroupWipTab).GetMethod("FindTruck", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert((bool)find.Invoke(tab, ["TRUCK 13"])!, "Case-insensitive name search failed");
        Assert(list.VerticalScroll.Value > 0, "Search did not scroll to bottom tier");
        var last = Descendants(list.Controls[list.Controls.Count - 1]).OfType<TileFlowPanel>().Single();
        Assert(last.HorizontalScroll.Value > 0, "Search did not scroll within tier");
        Assert((bool)find.Invoke(tab, ["code0"])!, "Code search failed");
        Assert(!(bool)find.Invoke(tab, ["no matching truck"])!, "Missing search unexpectedly matched");
        var search = typeof(CustomGroupWipTab).GetMethod("SearchTrucks", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tiles = Descendants(tab).OfType<SearchableTruckTile>().ToArray();
        int Search(string query) => (int)search.Invoke(tab, [query])!;
        Assert(Search("Tr") == 0 && tiles.All(t => !t.SearchMatch), "Search started before three characters");
        Assert(Search("tRu") == 14 && tiles.All(t => t.SearchMatch), "Not all mixed-case matches highlighted");
        Assert(tiles.All(t => t.Controls.OfType<Label>().All(l => l.BackColor == AppColors.Get("EngineMatch"))), "Highlight color missing");
        Assert(Search("tRuCk 1") == 5 && tiles.Count(t => t.SearchMatch) == 5, "Refinement did not update all highlights");
        Assert(Search("truck 13") == 1 && list.VerticalScroll.Value > 0 && last.HorizontalScroll.Value > 0, "Live search failed to scroll to first refined result");
        Assert(Search("t") == 0 && tiles.All(t => !t.SearchMatch), "Deleting below threshold did not clear highlights");
        Assert(tiles.All(t => t.Controls.OfType<Label>().All(l => l.BackColor == AppColors.Get("TruckClassHeavy"))), "Class colors were not restored");
        Console.WriteLine("PASS: three-character threshold, mixed-case matching, all highlights, refinement, live scrolling and class color restoration.");
        Console.WriteLine("PASS: 1px tile gap, aligned header tops, name/code search, outer and inner scrolling, missing result.");
    }

    static void CheckColors()
    {
        var method = typeof(CustomGroupWipTab).GetMethod("CreateTile", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach(var item in new[] { ("Heavy", "Heavy"), ("Heavy-duty", "HeavyDuty"), ("Offroad", "Offroad"), ("Scout", "Scout"), ("Highway", "Highway") })
        {
            using var tile = (Panel)method.Invoke(null, [new CustomTruck("test", "Test", item.Item1)])!;
            var labels = tile.Controls.OfType<Label>().ToArray();
            Assert(labels.Length == 2 && labels.All(l => l.BackColor == AppColors.Get("TruckClass" + item.Item2)), "Class color missing: " + item.Item1);
            AppColors.Set("TruckClass" + item.Item2, Color.FromArgb(220, 220, 220));
            Assert(labels.All(l => l.BackColor == Color.FromArgb(220, 220, 220) && l.ForeColor == Color.Black), "Live update/readable text failed");
        }
    }
}
namespace SnowPakTool.Desktop
{
    internal static class TruckThumbnailProvider
    {
        public static Image PlaceholderImage { get; } = new Bitmap(1, 1);
        public static Task<Image> LoadAsync(CustomTruck truck) => Task.FromResult(PlaceholderImage);
    }
}

