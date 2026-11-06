using System.Data;
using System.Reflection;
using SnowPakTool.Desktop;
internal static class PakSummaryChecks
{
 static T Field<T>(object o,string n)=>(T)o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(o)!;
 static void Check(bool b,string n){if(!b)throw new Exception(n);}
 static void Pump(Func<bool> ready){var until=DateTime.UtcNow.AddSeconds(12);while(!ready()&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(5);}Check(ready(),"UI operation finished");}
 public static void Run()
 {
  var root=Path.Combine(AppContext.BaseDirectory,"pak-summaries-"+Guid.NewGuid().ToString("N"));
  var originals=Path.Combine(root,"original_files");var updated=Path.Combine(root,"updated");
  var relA="[media]/classes/trucks/truck_a.xml";var relB="[media]/classes/trucks/truck_b.xml";var relEngine="[media]/classes/engines/shared_engine.xml";
  void Write(string tree,string relative,string text){var path=Path.Combine(tree,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text);}
  var source="<Truck><TruckData TruckType=\"SCOUT\" FuelCapacity=\"240\"><WinchUpgradeSocket Type=\"medium\"/></TruckData><GameData Price=\"10000\" UnlockByRank=\"10\"><UiDesc UiName=\"TRUCK_A\"/></GameData></Truck>";
  Write(originals,relA,source);Write(originals,relB,source.Replace("TRUCK_A","TRUCK_B"));
  Write(originals,relEngine,"<EngineVariants><Engine Name=\"shared\" Torque=\"100\"/></EngineVariants>");
  Write(originals,"[strings]/strings_english.str","TRUCK_A \"Truck A\"\nTRUCK_B \"Truck B\"\n");
  Write(updated,relA,source.Replace("240","360").Replace("10000","12000").Replace("10\"","8\"").Replace("Type=\"medium\"","Type=\"medium,scout\""));
  Write(updated,relEngine,"<EngineVariants><Engine Name=\"shared\" Torque=\"130\"/></EngineVariants>");
  var pending=new WorkspaceChange[]{new(WorkspaceChange.Pending,Path.Combine(updated,relA),"Truck A: Fuel capacity (L)","360","400"),new(WorkspaceChange.Pending,Path.Combine(updated,relA),"Truck A / Engine compatibility","default","advanced"),new(WorkspaceChange.Pending,Path.Combine(updated,relB),"Truck B: Unlock rank","10","2"),new(WorkspaceChange.Pending,"[ps]/rank_settings.sso","Rank XP","100, 200","150, 300")};
  var hasPending=true;var applied=0;var badge=-1;
  using var panel=new StartChangesPanel(()=>root,new Button(),_=>hasPending?pending:[],()=>{applied=pending.Length;hasPending=false;},()=>Task.CompletedTask,()=>Task.CompletedTask){BindingContext=new BindingContext(),Size=new Size(1120,850)};
  panel.PendingCountChanged+=n=>badge=n;panel.CreateControl();var pak=Field<DataGridView>(panel,"_changes");_=pak.Handle;
  var refresh=panel.RefreshAsync();Pump(()=>refresh.IsCompleted);refresh.GetAwaiter().GetResult();
  Check(pak.Rows.Count==5,"truck edits group by truck/state, shared engine and rank XP stay separate");
  var rows=pak.Rows.Cast<DataGridViewRow>().ToArray();
  var a=rows.Where(r=>r.Cells["File / target"].Value?.ToString()=="Truck A").ToArray();Check(a.Length==2,"localized truck name has separate applied and pending summaries");
  var saved=a.Single(r=>r.Cells["State"].Value?.ToString()==WorkspaceChange.InFiles);var staged=a.Single(r=>r.Cells["State"].Value?.ToString()==WorkspaceChange.Pending);
  Check(saved.Cells["Change"].Value!.ToString()!.Contains("Fuel capacity (L): 240 → 360")&&saved.Cells["Change"].Value!.ToString()!.Contains("+1 more"),"brief applied summary uses readable attributes and truncates by field count");
  Check(staged.Cells["Change"].Value!.ToString()!.Contains("Engine compatibility: default → advanced")&&staged.Cells["Change"].Value!.ToString()!.Contains("Fuel capacity (L): 360 → 400"),"pending property and compatibility edits combine under one truck");
  Check(rows.Any(r=>r.Cells["File / target"].Value!.ToString()!.Contains("shared_engine"))&&rows.Any(r=>r.Cells["Change"].Value!.ToString()!.Contains("Rank XP")),"shared component and general changes remain identifiable");
  Check(badge==4&&Field<Button>(panel,"_applyButton").Enabled,"pending count and Apply use individual edits, not summary count");
  Check(staged.DefaultCellStyle.BackColor==AppColors.Get("EngineMatch")&&saved.DefaultCellStyle.BackColor==AppColors.Get("DAF8E0"),"state colors preserved");
  var args=(DataGridViewCellToolTipTextNeededEventArgs)Activator.CreateInstance(typeof(DataGridViewCellToolTipTextNeededEventArgs),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new object[]{pak.Columns["Change"]!.Index,saved.Index,""},null)!;
  typeof(DataGridView).GetMethod("OnCellToolTipTextNeeded",BindingFlags.NonPublic|BindingFlags.Instance,null,new[]{typeof(DataGridViewCellToolTipTextNeededEventArgs)},null)!.Invoke(pak,new object[]{args});
  Check(args.ToolTipText.Contains("FuelCapacity")&&args.ToolTipText.Contains("UnlockByRank")&&args.ToolTipText.Contains("WinchUpgradeSocket")&&args.ToolTipText.Contains("truck_a.xml"),"hover preserves all raw fields, values and source paths");
  using(var image=new Bitmap(panel.Width,panel.Height)){panel.DrawToBitmap(image,new Rectangle(Point.Empty,panel.Size));image.Save(Path.Combine(AppContext.BaseDirectory,"pak-truck-summaries.png"));}
  TableHeaderBehavior.Attach(pak).AddFilter("File / target").SelectValues(["Truck A"]);TableHeaderBehavior.Attach(pak).AddFilter("State").SelectValues([WorkspaceChange.Pending]);Application.DoEvents();
  Check(pak.Rows.Count==1,"summary rows retain header filtering");
  Field<Button>(panel,"_applyButton").PerformClick();Pump(()=>Field<Label>(panel,"_status").Text.StartsWith("Pending changes applied"));
  Check(applied==4&&badge==0&&!Field<Button>(panel,"_applyButton").Enabled,"Apply includes hidden general and other-truck edits and refreshes pending state");
  Check(File.ReadAllText(Path.Combine(originals,relA))==source,"display grouping preserves original source files");
  Console.WriteLine("PASS PAK truck summaries: localized per-truck/state groups, concise before/after fields, full hover details, shared/rank separation, colors, filters, raw edit counts and Apply including hidden edits.");
 }
}
