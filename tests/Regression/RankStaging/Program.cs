using System.Collections;
using System.Reflection;
internal static class Program
{
 static readonly Assembly App=Assembly.Load("SnowPakTool.Desktop");
 static Type T(string n)=>App.GetType("SnowPakTool.Desktop."+n)!;
 static object? Invoke(object value,string name,params object?[] args)=>value.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(value,args);
 static object? Static(string type,string name,params object?[] args)=>T(type).GetMethod(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(null,args);
 static object Field(object obj,string name)=>obj.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(obj)!;
 static void Set(object obj,string name,object value)=>obj.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(obj,value);
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
 static List<object> Rows(object? value)=>((IEnumerable)value!).Cast<object>().ToList();
 static List<int> Values(object staged)=>(List<int>)staged.GetType().GetProperty("Values")!.GetValue(staged)!;
 static string Text(object row,string name)=>(string)row.GetType().GetProperty(name)!.GetValue(row)!;
 static Control Editor(string workspace,object store)=> (Control)Activator.CreateInstance(T("ExperienceEditorTab"),BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,new object[]{new Func<string>(()=>workspace),Activator.CreateInstance(T("OwnedRegionState"))!,store},null)!;
 [STAThread]static void Main()
 {
  var root=Path.Combine(AppContext.BaseDirectory,"fixtures",Guid.NewGuid().ToString("N"));
  var workspace=Path.Combine(root,"workspace");var original=Path.Combine(workspace,"initial.pak","original_files","[ps]","rank_settings.sso");var updated=Path.Combine(workspace,"initial.pak","updated","[ps]","rank_settings.sso");
  Directory.CreateDirectory(Path.GetDirectoryName(original)!);const string source="rankExp = [700, 1000,]\nother = 1\n";File.WriteAllText(original,source);
  var stateDir=Path.Combine(root,"preferences");var store=Activator.CreateInstance(T("RankXpStageStore"),new object[]{stateDir})!;
  using(var editor=Editor(workspace,store))
  {
   var grid=(DataGridView)Field(editor,"_grid");Set(editor,"_isLoading",true);grid.Rows.Clear();grid.Rows.Add(2,700,1000);grid.Rows.Add(3,1000,2000);
   Set(editor,"_loadedWorkspace",workspace);Set(editor,"_rankSettingsPath",original);Set(editor,"_loadedFileText",source);Set(editor,"_loadedValues",new List<int>{700,1000});Set(editor,"_isLoading",false);
   List<object> Pending()=>Rows(Invoke(editor,"PendingChanges",workspace));
   Check(Pending().Count==0,"preview edits not pending");
   Invoke(editor,"EnsureLoaded");Check(Pending().Count==0,"navigation not pending");
   Invoke(editor,"SaveSettings");
   Check(File.ReadAllText(original)==source && !File.Exists(updated),"Save rank XP leaves game files untouched");
   var staged=Invoke(store,"Load",workspace)!;Check(Values(staged).SequenceEqual(new[]{1000,2000}),"explicit staged snapshot");
   Check(Pending().Count==1 && Text(Pending()[0],"After")=="2 steps; 3,000 XP total","staged pending summary");
   Check(Rows(Invoke(editor,"PendingChanges",Path.Combine(root,"other"))).Count==0,"workspace isolation");
   using(var restarted=Editor(workspace,store))
   {
    Check(Rows(Invoke(restarted,"PendingChanges",workspace)).Count==1,"staged settings available before rank tab loaded");
    Invoke(restarted,"EnsureLoaded");var restored=(DataGridView)Field(restarted,"_grid");
    Check(restored.Rows.Count==2 && Convert.ToInt32(restored.Rows[0].Cells["UpdatedXP"].Value)==1000 && Convert.ToInt32(restored.Rows[1].Cells["UpdatedXP"].Value)==2000,"staged grid survives restart/reload");
   }
   grid.Rows[0].Cells["UpdatedXP"].Value=9999;grid.Rows[1].Cells["UpdatedXP"].Value="invalid";
   Check(Text(Pending().Single(),"After")=="2 steps; 3,000 XP total","later invalid preview cannot replace saved snapshot");
   Invoke(editor,"SavePendingChanges");
   Check(File.ReadAllText(original)==source,"Apply preserves original");
   var result=File.ReadAllText(updated);Check(result=="rankExp = [1000, 2000,]\nother = 1\n","Apply writes only saved values with formatting preserved");
   Check(Pending().Count==0 && Invoke(store,"Load",workspace) is null,"Apply clears pending stage");
   Check((string)grid.Rows[1].Cells["UpdatedXP"].Value=="invalid","Apply does not consume unstaged preview edits");
   Invoke(store,"Stage",workspace,result,new List<int>{1500,2500});
   File.WriteAllText(updated,result+"external = 2\n");
   bool refused=false;try{Invoke(editor,"SavePendingChanges");}catch(TargetInvocationException e)when(e.InnerException is InvalidOperationException){refused=true;}
   Check(refused && File.ReadAllText(updated)==result+"external = 2\n" && Invoke(store,"Load",workspace) is not null,"external edits protected and staged values retained");
   Invoke(store,"Stage",workspace,result,new List<int>{1000,2000});Check(Invoke(store,"Load",workspace) is null,"saving current values removes staged change");
   var malformed=false;try{Invoke(store,"Stage",workspace,result,new List<int>{-1,5});}catch(TargetInvocationException e)when(e.InnerException is System.Text.Json.JsonException){malformed=true;}
   Check(malformed && Invoke(store,"Load",workspace) is null,"invalid values rejected without staging");
   File.WriteAllText(updated,result);
   using(var fresh=Editor(workspace,store))
   {Invoke(fresh,"EnsureLoaded");Check((string)Field(fresh,"_loadedFileText")==result,"reload sees applied file rather than stale cache");Check(Rows(Invoke(fresh,"PendingChanges",workspace)).Count==0,"default preview after Apply does not re-stage");}
  }
  using(var editor=Editor(workspace,store))
  using(var form=new Form {Size=new Size(1120,920),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new Point(-20000,-20000)})
  {
   editor.Dock=DockStyle.Fill;form.Controls.Add(editor);Invoke(editor,"EnsureLoaded");form.Show();Application.DoEvents();
   var grid=(DataGridView)Field(editor,"_grid");var stage=(Button)Field(editor,"_saveButton");var revertPreview=(Button)Field(editor,"_revertUnstaged");var revertStage=(Button)Field(editor,"_revertStaged");
   Check(stage.Text=="Stage changes"&&stage.Visible,"explicit Stage changes action is visible on opening the editor");
   var scroll=(ScrollableControl)Field(editor,"_contentScrollHost");var toolbarY=stage.PointToScreen(Point.Empty).Y;scroll.AutoScrollPosition=new Point(200,500);Application.DoEvents();
   Check(stage.Visible&&stage.PointToScreen(Point.Empty).Y==toolbarY,"staging toolbar stays visible while graphs and tables scroll");
   var fileBefore=File.ReadAllText(updated);var valuesBefore=(List<int>)Field(editor,"_loadedValues");
   grid.Rows[0].Cells["UpdatedXP"].Value=valuesBefore[0]+100;Invoke(editor,"UpdateDirtyState");
   Check(stage.Enabled&&revertPreview.Enabled&&!revertStage.Enabled&&Rows(Invoke(editor,"PendingChanges",workspace)).Count==0,"new XP preview enables staging without creating pending changes");
   var notifications=0;editor.GetType().GetEvent("StagingChanged",BindingFlags.Instance|BindingFlags.NonPublic)!.GetAddMethod(true)!.Invoke(editor,new object[]{(Action)(()=>notifications++)});
   stage.PerformClick();var staged=Invoke(store,"Load",workspace)!;
   Check(Values(staged)[0]==valuesBefore[0]+100&&notifications==1&&!stage.Enabled&&revertStage.Enabled&&File.ReadAllText(updated)==fileBefore,"Stage button persists snapshot and reports it without writing workspace files");
   Check(grid.Rows[0].Cells["UpdatedXP"].ToolTipText.StartsWith("Staged"),"rank cell tooltip identifies the staged snapshot");
   grid.Rows[0].Cells["UpdatedXP"].Value=valuesBefore[0]+900;
   Check(Values(Invoke(store,"Load",workspace)!)[0]==valuesBefore[0]+100&&grid.Rows[0].Cells["UpdatedXP"].ToolTipText.StartsWith("Unstaged"),"later previews stay distinct from the saved stage");
   revertPreview.PerformClick();Check(Convert.ToInt32(grid.Rows[0].Cells["UpdatedXP"].Value)==valuesBefore[0]+100&&!stage.Enabled,"revert unstaged restores the saved snapshot");
   revertStage.PerformClick();Check(Invoke(store,"Load",workspace) is null&&Convert.ToInt32(grid.Rows[0].Cells["UpdatedXP"].Value)==valuesBefore[0]&&notifications==2&&File.ReadAllText(updated)==fileBefore,"revert staged clears pending intent and restores file values without file edits");
   grid.Rows[0].Cells["UpdatedXP"].Value=valuesBefore[0]+200;stage.PerformClick();
   var browse=(IDisposable)Activator.CreateInstance(T("PresetBrowseMode"),new object[]{new Control[]{editor}})!;
   try{Invoke(browse,"SetReadOnly",true);Check(!stage.Enabled&&!revertPreview.Enabled&&!revertStage.Enabled,"shared preset browsing disables XP stage and revert actions");Invoke(editor,"RevertStagedRankChanges");Check(Invoke(store,"Load",workspace) is not null,"read-only mode also guards direct staged-revert entry point");}finally{browse.Dispose();}
   using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(AppContext.BaseDirectory,"experience-staging-toolbar.png"));}
  }
  Console.WriteLine("PASS rank XP: preview/navigation never stage, explicit Save persists only preferences, restart before tab load, exact snapshot Apply, original/unrelated-data preservation, invalid later previews, workspace isolation, external-edit conflicts, no-op and invalid staging, applied-file cache refresh.");
 }
}
