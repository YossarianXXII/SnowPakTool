using System.Reflection;
using System.Text.Json;
using SnowPakTool.Desktop;
internal static class Program
{
 static void Check(bool ok,string name) { if(!ok) throw new Exception(name); }
 static T Field<T>(object value,string name) => (T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
 static void Call(object value,string name,params object[] args) => value.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(value,args);
 static IEnumerable<Control> All(Control root) { foreach(Control c in root.Controls) { yield return c; foreach(var child in All(c)) yield return child; } }
 static string[] Codes(TileFlowPanel panel) => panel.Controls.Cast<Control>().Select(c=>(string)c.Tag!).ToArray();
 [STAThread] static void Main(string[] args)
 {
  Application.SetHighDpiMode(HighDpiMode.SystemAware);
  if (!args.Contains("--ui-only"))
  {
  var sample=Enumerable.Range(0,5).SelectMany(d=>Enumerable.Range(0,4).Select(i=>new ProgressionRegion($"r{d}_{i}",d))).ToList();
  sample.Add(new("new1",null));sample.Add(new("new2",null));
  var lookup=sample.ToDictionary(r=>r.Code);
  var averages=new List<double>();
  foreach(var mode in new[]{ProgressionDifficulty.Easy,ProgressionDifficulty.Normal,ProgressionDifficulty.Hard})
  {
   double sum=0;bool variance=false;
   for(int seed=0;seed<4000;seed++)
   {
    var order=RegionProgressionService.Generate(sample,mode,seed);
    Check(order.Count==sample.Count && order.Distinct().Count()==sample.Count,"permutation");
    Check(lookup[order[0]].Difficulty==0,"easiest start");
    Check(order.TakeLast(2).SequenceEqual(new[]{"new1","new2"}),"unsorted tail");
    sum+=order.Skip(1).Take(5).Average(code=>lookup[code].Difficulty!.Value);
    variance|=order.Take(5).Any(code=>lookup[code].Difficulty>=(mode==ProgressionDifficulty.Easy ? 2 : 3));
   }
   Check(variance,"harder region occasionally early");averages.Add(sum/4000);
  }
  Check(averages[0]<averages[1] && averages[1]<averages[2],"difficulty effect");
  var once=RegionProgressionService.Generate(sample,ProgressionDifficulty.Normal,42);
  Check(once.SequenceEqual(RegionProgressionService.Generate(sample.Where(r=>r.Difficulty.HasValue).Reverse().Concat(sample.Where(r=>r.Difficulty is null)).ToList(),ProgressionDifficulty.Normal,42)),"within-tier ordering irrelevant");
  var expanded=sample.Append(new ProgressionRegion("dlc",null)).ToList();
  Check(RegionProgressionService.Generate(expanded,ProgressionDifficulty.Normal,42).Take(sample.Count).SequenceEqual(once),"new unsorted has no effect on draw");
  Check(RegionProgressionService.Generate([],ProgressionDifficulty.Normal,1).Count==0,"empty input");
  Check(RegionProgressionService.Generate([new("one",4)],ProgressionDifficulty.Hard,1).Single()=="one","single ranked");
  Check(RegionProgressionService.Generate([new("one",null),new("two",null)],ProgressionDifficulty.Hard,1).SequenceEqual(new[]{"one","two"}),"all unsorted");
  Check(RegionProgressionService.Generate([new("hard",4),new("easy",2)],ProgressionDifficulty.Hard,1)[0]=="easy","easiest available tier");
  Check(RegionProgressionService.Move(["hidden","a","b","c"],["a","b","c"],"a",2).SequenceEqual(new[]{"hidden","b","c","a"}),"hidden positions preserved");
  Console.WriteLine($"PASS algorithm: 12,000 seeds, easiest start, Unsorted last, uniqueness, variance, deterministic tiers, new DLC stability. Early difficulty Easy/Normal/Hard: {string.Join(" / ",averages.Select(v=>v.ToString("F2")))}");
  }
  var path=Path.Combine(AppContext.BaseDirectory,"test-state","progression.json");
  Directory.CreateDirectory(Path.GetDirectoryName(path)!); if(File.Exists(path)) File.Delete(path);
  var store=new RegionProgressionStore(path);
  var regions=new List<CustomTruck>{new("us_01","Michigan","Base game","Black River"),new("us_02","Alaska","Base game"),new("ru_02","Taymyr","Base game"),new("ru_03","Kola","Season 1"),new("us_04","Yukon","Season 2"),new("us_03","Wisconsin","Season 3")};
  var tiers=new List<GroupState>{new(){Name="D",TruckCodes=["us_01","us_02"]},new(){Name="A",TruckCodes=["ru_02"]},new(){Name="S",TruckCodes=["ru_03"]}};
  using(var tab=new RegionProgressionTab(()=>regions,store,()=>tiers){Size=new Size(1080,740)})
  {
   tab.RefreshRegions();tab.CreateControl();tab.PerformLayout();
   var tiles=Field<TileFlowPanel>(tab,"_tiles"); tiles.CreateControl(); tiles.PerformLayout();
   Check(tiles.WrapContents && tiles.FlowDirection==FlowDirection.LeftToRight,"wrapping tile pane");
   Check(tiles.Controls[1].Left>tiles.Controls[0].Left && tiles.Controls[4].Top>tiles.Controls[0].Top,"left to right multirow layout");
   Check(tiles.Controls[0].Controls.OfType<PictureBox>().Single().Image is not null,"real region thumbnail");
   Check(tiles.Controls[0] is ISearchHighlight,"searchable region tile");
   Call(tab,"Randomize");var first=store.Load().Order.ToArray();
   Check(first.TakeLast(2).SequenceEqual(new[]{"us_04","us_03"}),"UI unsorted tail");
   Check(!Field<Button>(tab,"_restore").Enabled,"no previous before second roll");
   Call(tab,"Randomize");Check(Field<Button>(tab,"_restore").Enabled,"one previous available");
   var lockedOrder=store.Load().Order.ToArray();
   Call(tab,"ToggleFinalized");
   Check(store.Load().IsFinalized && Field<Button>(tab,"_finalize").Text=="Unlock","finalize persisted");
   Check(!Field<Button>(tab,"_randomize").Enabled && !Field<Button>(tab,"_restore").Enabled && !tiles.AllowDrop,"finalized actions disabled");
   Check(All(tab).OfType<RadioButton>().All(r=>!r.Enabled),"finalized settings disabled");
   Call(tab,"Randomize");Call(tab,"RestorePrevious");
   var lockedTile=(Panel)tiles.Controls[0]; tiles.SetDropPosition(tiles.Controls.Count-1,lockedTile,false);
   Call(tab,"CompleteDrop",new DragEventArgs(CustomGroupWipTab.CreateTileDragData(lockedTile),0,0,0,DragDropEffects.Move,DragDropEffects.Move));
   Check(store.Load().Order.SequenceEqual(lockedOrder),"finalized order guarded against mutation");
   tab.RefreshRegions(); Check(!Field<Button>(tab,"_randomize").Enabled,"refresh preserves lock");
   using(var lockedRestart=new RegionProgressionTab(()=>regions,store,()=>tiers))
   {lockedRestart.RefreshRegions();Check(Field<Button>(lockedRestart,"_finalize").Text=="Unlock" && !Field<Button>(lockedRestart,"_randomize").Enabled,"restart preserves lock");}
   Call(tab,"ToggleFinalized");
   Check(!store.Load().IsFinalized && Field<Button>(tab,"_finalize").Text=="Finalize" && Field<Button>(tab,"_restore").Enabled && tiles.AllowDrop,"unlock restores editing");
   var sortedTile=(SearchableTruckTile)tiles.Controls.Cast<Control>().Single(c=>(string)c.Tag! =="us_01");
   var sortedHeader=sortedTile.Controls.OfType<Label>().Single(l=>l.Dock==DockStyle.Top);
   Check(sortedHeader.BackColor==RegionTierListStore.BackgroundFor("D") && sortedTile.BackColor==sortedHeader.BackColor,"difficulty color used");
   Check(sortedHeader.ForeColor==Color.FromArgb(20,20,20),"dark text on soft blue tier tint");
   sortedTile.SearchMatch=true;Check(sortedHeader.BackColor==AppColors.Get("EngineMatch"),"search match preserved");
   sortedTile.SearchActive=true;Check(sortedHeader.BackColor==AppColors.Get("SearchActive"),"active search preserved");
   sortedTile.SearchActive=false;sortedTile.SearchMatch=false;Check(sortedHeader.BackColor==RegionTierListStore.BackgroundFor("D"),"search cleanup restores tier");
   var before=store.Load().Order.ToArray();
   var radios=All(tab).OfType<RadioButton>().ToArray();radios.Single(r=>r.Text=="Hard").Checked=true;
   Check(store.Load().Difficulty==ProgressionDifficulty.Hard && store.Load().Order.SequenceEqual(before),"setting persists without reshuffle");
   var tile=(Panel)tiles.Controls[0];var expected=Codes(tiles).Skip(1).Append((string)tile.Tag!).ToArray();
   tiles.SetDropPosition(tiles.Controls.Count-1,tile,false);
   Call(tab,"CompleteDrop",new DragEventArgs(CustomGroupWipTab.CreateTileDragData(tile),0,0,0,DragDropEffects.Move,DragDropEffects.Move));
   Check(Codes(tiles).SequenceEqual(expected) && store.Load().Order.SequenceEqual(expected),"drop reorders and saves across rows");
   Check(tiles.Controls[0].Controls.OfType<Label>().Single(l=>l.Dock==DockStyle.Top).Controls.OfType<RegionOrderBadge>().Single().Text=="1","drop renumbers badge");
   foreach(Control card in tiles.Controls) { var header=card.Controls.OfType<Label>().Single(l=>l.Dock==DockStyle.Top); var badge=header.Controls.OfType<RegionOrderBadge>().Single(); Check(badge.Width==header.Height && badge.Height==header.Height,"badge matches header height"); }
   regions.Add(new("us_19","New DLC","Season 19"));tab.RefreshRegions();
   Check(store.Load().Order.Take(expected.Length).SequenceEqual(expected) && store.Load().Order.Last()=="us_19","append without cascade");
   tiers[0].TruckCodes.Remove("us_01");tiers[2].TruckCodes.Add("us_01");tab.RefreshRegions();
   Check(store.Load().Order.Take(expected.Length).SequenceEqual(expected),"tier change no cascade");
   Call(tab,"RestorePrevious");
   Check(store.Load().Order.Take(first.Length).SequenceEqual(first) && store.Load().Order.Last()=="us_19","previous restores and retains new DLC");
   Check(!Field<Button>(tab,"_restore").Enabled,"previous consumed");
   using(var preview=new Bitmap(tab.Width,tab.Height)) { tab.DrawToBitmap(preview,new Rectangle(Point.Empty,tab.Size)); preview.Save(Path.Combine(AppContext.BaseDirectory,"region-progression-preview.png")); }
   var saved=File.ReadAllText(path);Directory.CreateDirectory(path+".tmp");Call(tab,"Randomize");
   Check(File.ReadAllText(path)==saved && Field<RegionProgressionState>(tab,"_state").Order.SequenceEqual(store.Load().Order),"failed save leaves state unchanged");
   Call(tab,"ToggleFinalized");Check(!store.Load().IsFinalized && Field<Button>(tab,"_finalize").Text=="Finalize","failed finalize leaves state unlocked");
   Directory.Delete(path+".tmp");
  }
  using(var restarted=new RegionProgressionTab(()=>regions,store,()=>tiers))
  {
   restarted.RefreshRegions();Check(Codes(Field<TileFlowPanel>(restarted,"_tiles")).SequenceEqual(store.Load().Order),"restart saved order");
   Check(!Field<Button>(restarted,"_restore").Enabled,"previous memory only");
  }
  File.WriteAllText(path,"{broken");
  using(var broken=new RegionProgressionTab(()=>regions,store,()=>tiers))
  {broken.RefreshRegions();Check(!Field<Button>(broken,"_randomize").Enabled && File.ReadAllText(path)=="{broken","bad state not overwritten");}
  File.Delete(path);
  Console.WriteLine("PASS UI/store: finalize/unlock persistence and mutation guards, difficulty colors and contrast, search cleanup, badges, wrapping, drag reordering, append-only DLC, previous order, restart and failed writes.");
 }
}
namespace SnowPakTool.Desktop
{
 internal static class TruckThumbnailProvider
 {
  public static Image PlaceholderImage {get;}=new Bitmap(1,1);
  public static Task<Image> LoadAsync(CustomTruck truck)=>Task.FromResult(PlaceholderImage);
 }
}
