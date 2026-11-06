using System.Reflection;
using System.Collections;
using System.Data;
static class Program
{
 static readonly Assembly App=Assembly.Load("SnowPakTool.Desktop");
 static Type T(string n)=>App.GetType("SnowPakTool.Desktop."+n)!;
 static object F(object o,string n)=>o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(o)!;
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
 [STAThread]static void Main()
 {
  var change=T("WorkspaceChange");var list=new List<object>();
  void Add(string state,string target,string property,string before="1",string after="2",string group="",string name="")=>list.Add(Activator.CreateInstance(change,[state,target,property,before,after,"",group,name])!);
  var file="[media]/classes/winches/winches_heavy_trucks.xml";
  foreach(var field in new[]{"Length","StrengthMultiplier","IgnitionRequired","UnlockByRank"})Add("Pending",file,"Heavy winch / "+field,group:"winch:heavy/default",name:"Heavy winch");
  Add("Pending","[strings]/strings_english.str","UI_HEAVY / Winch display name","Heavy winch","Rescue", "winch:heavy/default","Heavy winch");
  Add("Pending",file,"Heavy winch / Length",group:"winch:heavy/extended",name:"Heavy winch");
  Add("In files",file,"/WinchVariants[1]/Winch[w_heavy_trucks_default]/@Length");
  Add("In files",file,"/WinchVariants[1]/Winch[w_heavy_trucks_default]/@StrengthMult");
  Add("Pending","[media]/classes/trucks/test.xml","Test truck / Winch compatibility");
  Add("Pending","[media]/classes/trucks/test.xml","Test truck / FuelCapacity");
  Add("Pending",file,"Added file");Add("Pending","rank_settings.sso","XP");
  var array=Array.CreateInstance(change,list.Count);for(int i=0;i<list.Count;i++)array.SetValue(list[i],i);
  using var main=(Form)Activator.CreateInstance(T("MainForm"))!;
  var panel=F(main,"_startChanges");
  var groups=panel.GetType().GetMethod("PakGroups",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[array,new Dictionary<string,(string Name,string Class)>{{"test",("Test truck","Heavy")}}]);
  panel.GetType().GetMethod("BindChanges",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(panel,[groups]);
  var grid=(DataGridView)F(panel,"_changes");var table=(DataTable)grid.DataSource!;
  Check(table.Rows.Count==6,"separate winches, states, truck and unrelated entries");
  var pending=table.Rows.Cast<DataRow>().Single(r=>(string)r["File / target"]=="Heavy winch" && ((string)r["Change"]).Contains("+2 more"));
  Check(((string)pending["Change"]).StartsWith("Length (m):"),"brief fields and overflow count");
  var entry=pending["Entry"];var changes=(IEnumerable)entry.GetType().GetProperty("Changes")!.GetValue(entry)!;
  Check(changes.Cast<object>().Count()==5,"all pending fields and localization retained for tooltip");
  Check(table.Rows.Cast<DataRow>().Any(r=>(string)r["File / target"]=="w_heavy_trucks_default" && ((string)r["Change"]).Contains("Strength multiplier")),"applied fields group by named XML variant");
  Check(table.Rows.Cast<DataRow>().Any(r=>(string)r["File / target"]=="Test truck" && ((string)r["Change"]).Contains("Winch compatibility")),"truck compatibility remains in truck summary");
  Check(array.Length==12,"underlying change count retained");
 }
}
