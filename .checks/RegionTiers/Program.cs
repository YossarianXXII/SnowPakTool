using System.Reflection;
using SnowPakTool.Desktop;
internal static class Program
{
    static object Field(object o, string n) => o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    static void Assert(bool result, string text) { if (!result) throw new Exception(text); }
    static IEnumerable<Control> All(Control root) { foreach(Control child in root.Controls) { yield return child; foreach(var c in All(child)) yield return c; } }
    static TileFlowPanel Tier(CustomGroupWipTab board, int index) => All(((FlowLayoutPanel)Field(board,"_groupList")).Controls[index]).OfType<TileFlowPanel>().Single();
    static void Drop(CustomGroupWipTab board, TileFlowPanel target, Panel tile, int index)
    {
        target.SetDropPosition(index,tile,false);
        var data = CustomGroupWipTab.CreateTileDragData(tile);
        Assert(ReferenceEquals(data.GetData(typeof(Panel)),tile), "Production drag payload is not readable by drop targets");
        var args = new DragEventArgs(data,0,0,0,DragDropEffects.Move,DragDropEffects.Move);
        typeof(CustomGroupWipTab).GetMethod("CompleteTileDrop",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(board,[target,args]);
    }
    [STAThread]
    static void Main()
    {
        var truckTiers = TierListStore.Load(); truckTiers[0].TruckCodes = ["truck-sentinel"]; TierListStore.Save(truckTiers);
        var initial = RegionTierListStore.Load(); foreach(var tier in initial) tier.TruckCodes.Clear(); initial[0].TruckCodes = ["us_01"]; RegionTierListStore.Save(initial);
        var statePath = Path.Combine(AppContext.BaseDirectory,"test-state","SnowPakTool.Desktop","region-tiers.json");
        File.WriteAllText(statePath, "[{\"Name\":\"S\",\"RegionCodes\":[\"us_01\"]},{\"Name\":\"D\",\"RegionCodes\":[\"us_02\"]}]");
        var items = new List<CustomTruck>();
        using(var board = new CustomGroupWipTab(() => items, regionList:true) { Size = new Size(1200,700) })
        {
            Assert(((List<GroupState>)Field(board,"_states"))[0].TruckCodes.SequenceEqual(new[]{"us_01"}),"Empty startup lost rankings");
            items.AddRange([new("ru_03","Kola Peninsula","Season 1"),new("us_02","Alaska","Base game"),new("us_01","Michigan","Base game","Black River\nSmithville Dam\nIsland Lake\nDrummond Island")]);
            board.RefreshOwnedTrucks(); board.CreateControl(); board.PerformLayout();
            var states = (List<GroupState>)Field(board,"_states");
            Assert(states.Select(s=>s.Name).SequenceEqual(new[]{"S","A","B","C"}),"Wrong fixed tier set");
            Assert(!All(board).OfType<Button>().Any(b=>b.Visible && (b.Text=="Add group" || b.Text=="Remove")),"Editable region tiers");
            var unsorted = (TileFlowPanel)Field(board,"_unsortedTiles");
            Assert(!states.Any(s=>s.Name=="D"), "Removed D tier survived load");
            Assert(unsorted.Controls.Cast<Control>().Select(c=>(string)c.Tag!).SequenceEqual(new[]{"us_02","ru_03"}),"Old D tile not returned to Unsorted in release order");
            var michigan = (Panel)Tier(board,0).Controls[0];
            Assert(michigan.Controls.OfType<PictureBox>().Single().Image != null,"Embedded region thumbnail missing");
            michigan.CreateControl(); michigan.PerformLayout();
            var picture = michigan.Controls.OfType<PictureBox>().Single();
            Assert(michigan.Width==240, "Region tile not wider");
            Assert(Math.Abs(picture.Height - picture.Width * (double)picture.Image!.Height / picture.Image.Width) <= 1, "Image viewport aspect ratio differs from thumbnail");
            Assert(michigan.Controls.OfType<Label>().Any(l=>l.Text.Contains("Michigan") && l.Text.Contains("Base game")), "Header missing season");
            Assert(michigan.Controls.OfType<Label>().Any(l=>l.Text.Contains("Black River") && l.Text.Contains("Drummond Island")), "Footer missing map list");
            Assert(michigan.Controls.OfType<Label>().Any(l=>l.Text.Contains("Michigan")),"Missing region name");
            Drop(board,Tier(board,3),(Panel)unsorted.Controls[1],0);
            Assert(RegionTierListStore.Load()[3].TruckCodes.SequenceEqual(new[]{"ru_03"}),"Move did not autosave");
            Drop(board,Tier(board,3),(Panel)unsorted.Controls[0],0);
            Assert(RegionTierListStore.Load()[3].TruckCodes.SequenceEqual(new[]{"us_02","ru_03"}),"Order did not autosave");
            Drop(board,unsorted,(Panel)Tier(board,3).Controls[0],0);
            items.Clear(); board.RefreshOwnedTrucks();
            typeof(CustomGroupWipTab).GetMethod("SyncGroupAssignments",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(board,null);
            Assert(RegionTierListStore.Load()[3].TruckCodes.SequenceEqual(new[]{"ru_03"}),"Refresh cleared hidden region ranking");
        }
        items.AddRange([new("ru_03","Kola Peninsula","Season 1"),new("us_01","Michigan","Base game","Black River\nSmithville Dam\nIsland Lake\nDrummond Island")]);
        using(var restarted = new CustomGroupWipTab(()=>items,regionList:true))
        {
            Assert(Tier(restarted,3).Controls.Count==1 && (string)Tier(restarted,3).Controls[0].Tag! == "ru_03","Restart lost region ranking");
            Assert(All(restarted).OfType<ISearchHighlight>().Count()==2,"Region tiles not searchable/highlightable");
        }
        Assert(TierListStore.Load()[0].TruckCodes.SequenceEqual(new[]{"truck-sentinel"}),"Region rankings changed truck rankings");
        using(var truckTile = new SearchableTruckTile())
        {
            Assert(new DataObject(truckTile).GetData(typeof(Panel)) is null, "Original payload bug was not reproduced");
            Assert(ReferenceEquals(CustomGroupWipTab.CreateTileDragData(truckTile).GetData(typeof(Panel)),truckTile), "Payload fix failed");
        }
        Assert(!File.ReadAllText(statePath).Contains("\"D\""), "Removed D tier remained after save");
        var localized = LocalizedStrings.Load(Path.GetFullPath("../workspace/initial.pak/original_files/[strings]/strings_english.str"));
        var maps = RegionTileAssets.GetMapNames("us_01",localized);
        Assert(maps.Split(Environment.NewLine).SequenceEqual(new[]{"Black River","Smithville Dam","Island Lake","Drummond Island"}), "Michigan map name variants not resolved");
        Assert(RegionTileAssets.GetMapNames("ru_03",localized).Split(Environment.NewLine).SequenceEqual(new[]{"Lake Kovd","Imandra"}), "LEVEL_ prefix not resolved");
        Console.WriteLine("PASS: widened tiles, thumbnail aspect ratio, combined region/season header, map footer, real-workspace legacy and prefixed map names.");
        Console.WriteLine("PASS: fixed S-C tiers, D migration to Unsorted, production drag payload, embedded region images, release order, moves/reordering/Unsorted autosave, empty refresh and restart persistence, search highlights, separate truck storage.");
    }
}
namespace SnowPakTool.Desktop
{
    internal interface ISearchHighlight { bool SearchMatch { get; set; } bool SearchActive { get; set; } }
    internal static class TruckThumbnailProvider
    {
        public static Image PlaceholderImage { get; } = new Bitmap(1,1);
        public static Task<Image> LoadAsync(CustomTruck truck) => Task.FromResult(PlaceholderImage);
    }
}

