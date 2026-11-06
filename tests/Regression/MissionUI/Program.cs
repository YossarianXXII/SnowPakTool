using System.Reflection;
using System.Collections;
using System.Text.Json;
internal static class Program
{
 static readonly Assembly App=Assembly.Load("SnowPakTool.Desktop");
 static readonly TaskCompletionSource Finished=new();
 static string Selected="";
 static Type T(string n)=>App.GetType("SnowPakTool.Desktop."+n)!;
 static object? Call(object o,string n,params object?[] args)=>o.GetType().GetMethod(n,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(o,args);
 static object Field(object o,string n)=>o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(o)!;
 static IEnumerable<Control> All(Control c){yield return c;foreach(Control child in c.Controls)foreach(var item in All(child))yield return item;}
 static void Check(bool b,string n){if(!b)throw new Exception(n);}
 static void Pump(Func<bool> complete){var until=DateTime.UtcNow.AddSeconds(8);while(!complete()&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(5);}Check(complete(),"UI continuation completed");}
 public static Task RunMission<TDifficulty,TEntry>(TDifficulty difficulty,IProgress<TEntry> progress)
 {
  Selected=difficulty!.ToString()!;
  foreach(var (text,detail) in new[]{("First region started",false),("Mudtires → Highway: fallback",true),("First region complete",false)})
   progress.Report((TEntry)Activator.CreateInstance(typeof(TEntry),new object[]{text,detail})!);
  return Finished.Task;
 }
 public static Task RunObtainable<TEntry>(IProgress<TEntry> progress)=>Task.CompletedTask;
 [STAThread]static void Main()
 {
  Application.SetHighDpiMode(HighDpiMode.SystemAware);
  var root=Path.Combine(AppContext.BaseDirectory,"fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);var path=Path.Combine(root,"rules.json");
  object Store()=>Activator.CreateInstance(T("TruckRandomizationRuleStore"),new object[]{path})!;
  object Load()=>Call(Store(),"Load")!;
  int MissionCount(object rules)=>((ICollection)rules.GetType().GetProperty("Missions")!.GetValue(rules)!).Count;
  File.WriteAllText(path,"{\"Obtainable\":[\"Custom obtainable\"],\"Missions\":[]}");
  var migrated=Load();Check(MissionCount(migrated)==5,"legacy empty mission list receives agreed rules");
  Check(((IList)migrated.GetType().GetProperty("Obtainable")!.GetValue(migrated)!)[0]!.ToString()=="Custom obtainable","migration preserves obtainable notes");
  Check(File.ReadAllText(path).Contains("MissionRulesInitialized"),"migration saved once to preferences");
  ((IList)migrated.GetType().GetProperty("Missions")!.GetValue(migrated)!).Clear();Call(Store(),"Save",migrated);Check(MissionCount(Load())==0,"intentional empty list remains empty");
  File.WriteAllText(path,"{\"Obtainable\":[],\"Missions\":[\"Custom mission\"]}");Check(MissionCount(Load())==1,"custom mission notes preserved");
  File.WriteAllText(path,JsonSerializer.Serialize(new{Obtainable=new[]{"Custom obtainable"},Missions=new[]{"Custom mission", "Keep a mixture of light and heavy vehicles based on actual vehicle physics mass: Easy favors lighter vehicles, Moderate is balanced, and Hard favors heavier vehicles. All weights remain possible in every mode.", "Prefer different models within each region. Use models not yet represented before repeating, then choose among the least-used models while retaining the selected weight bias."},MissionRulesInitialized=true}));
  var revised=Load();var revisedMissions=(IList)revised.GetType().GetProperty("Missions")!.GetValue(revised)!;
  Check(revisedMissions.Count==3&&revisedMissions[0]!.ToString()=="Custom mission"&&revisedMissions[1]!.ToString()!.Contains("3:1")&&revisedMissions[2]!.ToString()!.Contains("across all regions"),"former default mission rules migrate while custom notes stay intact");
  var revisedSaved=File.ReadAllText(path);Load();Check(File.ReadAllText(path)==revisedSaved,"revised rules persist without repeated migration writes");
  File.Delete(path);
  var callbackType=typeof(Func<,,>).MakeGenericType(T("MissionTruckDifficulty"),typeof(IProgress<>).MakeGenericType(T("RandomizationLogEntry")),typeof(Task));
  var callback=typeof(Program).GetMethod(nameof(RunMission))!.MakeGenericMethod(T("MissionTruckDifficulty"),T("RandomizationLogEntry")).CreateDelegate(callbackType);
  var obtainableType=typeof(Func<,>).MakeGenericType(typeof(IProgress<>).MakeGenericType(T("RandomizationLogEntry")),typeof(Task));
  var obtainable=typeof(Program).GetMethod(nameof(RunObtainable))!.MakeGenericMethod(T("RandomizationLogEntry")).CreateDelegate(obtainableType);
  using var panel=(Control)Activator.CreateInstance(T("TruckRandomizationTab"),BindingFlags.Instance|BindingFlags.NonPublic,null,new object?[]{obtainable,Store(),callback},null)!;
  panel.Size=new Size(1120,900);panel.CreateControl();panel.PerformLayout();
  Check(All(panel).OfType<GroupBox>().Select(g=>g.Text).SequenceEqual(new[]{"Obtainable","Missions"}),"matching side-by-side panes");
  var hard=All(panel).OfType<RadioButton>().Single(r=>r.Text=="Hard");hard.Checked=true;
  Check(Load().GetType().GetProperty("MissionDifficulty")!.GetValue(Load())!.ToString()=="Hard","mission difficulty saved independently");
  var details=(CheckBox)Field(panel,"_missionShowDetails");var text=(TextBox)Field(panel,"_missionMessages");
  Check(!details.Checked,"mission details hidden by default");
  var task=(Task)Call(panel,"RandomizeMissionsAsync")!;
  Check(!((Button)Field(panel,"_randomize")).Enabled&&!((Button)Field(panel,"_missionRandomize")).Enabled,"both run buttons disabled during shared map operation");
  Pump(()=>text.Text.Contains("First region complete"));Check(!text.Text.Contains("fallback"),"hidden details buffered");
  details.Checked=true;Check(text.Text.IndexOf("started")<text.Text.IndexOf("fallback")&&text.Text.IndexOf("fallback")<text.Text.IndexOf("complete"),"details revealed in original order");
  Finished.SetResult();Pump(()=>task.IsCompleted);task.GetAwaiter().GetResult();Check(Selected=="Hard"&&((Button)Field(panel,"_randomize")).Enabled,"selected weight mode passed and buttons restored");
  var full=text.Text;details.Checked=false;details.Checked=true;Check(text.Text==full,"mission details toggle without reordering or duplication");
  var drafts=true;var staged=0;Call(panel,"ConfigureMapStaging",new Func<Task>(()=>{staged++;drafts=false;return Task.CompletedTask;}),new Func<bool>(()=>drafts));
  var missionStage=(Button)Field(panel,"_missionStageChanges");Check(missionStage.Enabled,"mission stage button uses draft state");missionStage.PerformClick();
  Check(staged==1&&!missionStage.Enabled&&!((Button)Field(panel,"_stageChanges")).Enabled,"shared stage state refreshed in both panes");
  using(var bitmap=new Bitmap(panel.Width,panel.Height)){panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,panel.Size));bitmap.Save(Path.Combine(AppContext.BaseDirectory,"mission-pane.png"));}
  var workspace=@"S:\snowrunner_Shenanigans\AI\workspace";
  var types=T("RegionTruckCatalogService").GetMethod("LoadTrucks",BindingFlags.Static|BindingFlags.NonPublic)!;
  var models=(Dictionary<string,(string,string)>)types.Invoke(null,new object[]{workspace,new Dictionary<string,string>(),new List<string>()})!;
  var equipment=Activator.CreateInstance(T("TruckEquipmentCatalog"),new object[]{workspace,new Dictionary<string,string>(),new List<string>()})!;
  var masses=models.Keys.Select(code=>(Code:code,Mass:(double?)Call(equipment,"VehicleMassKg",code))).ToArray();
  Console.WriteLine($"Real definitions (read-only): {masses.Count(m=>m.Mass.HasValue)}/{masses.Length} vehicle masses resolved. Missing: {string.Join(", ",masses.Where(m=>!m.Mass.HasValue).Select(m=>m.Code))}");
  Check(masses.Count(m=>m.Mass.HasValue)>100,"real fleet mass coverage");
  Console.WriteLine("PASS mission UI: agreed rule migration/custom/empty-list persistence, saved difficulty, independent ordered hidden logs, run exclusion, shared staging state and matching pane layout.");
 }
}
