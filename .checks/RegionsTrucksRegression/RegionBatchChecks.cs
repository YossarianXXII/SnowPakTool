using System.Data;
using System.Reflection;
using System.IO.Compression;
using SnowPakTool.Desktop;
internal static class RegionBatchChecks
{
 static void Check(bool ok,string text){if(!ok)throw new Exception(text);}
 static T Field<T>(object o,string n)=>(T)o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(o)!;
 static void Set(object o,string n,object? value)=>o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(o,value);
 static object? Call(object o,string n,params object?[] args)=>o.GetType().GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(o,args);
 static void Pump(Func<bool> ready){var until=DateTime.UtcNow.AddSeconds(15);while(!ready()&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(5);}Check(ready(),"operation finished");}
 public static void Run()
 {
  var root=Path.Combine(AppContext.BaseDirectory,"region-batches-"+Guid.NewGuid().ToString("N"));var trucks=Path.Combine(root,"original_files","[media]","classes","trucks");var wheels=Path.Combine(root,"original_files","[media]","classes","wheels");var strings=Path.Combine(root,"original_files","[strings]");var maps=Path.Combine(root,"maps");
  foreach(var dir in new[]{trucks,wheels,strings,maps})Directory.CreateDirectory(dir);
  File.WriteAllText(Path.Combine(strings,"strings_english.str"),"");
  foreach(var code in new[]{"test_truck","second_truck","third_truck"})File.WriteAllText(Path.Combine(trucks,code+".xml"),$"<Truck><TruckData TruckType=\"SCOUT\"><Wheels DefaultWheelType=\"test_wheels\" DefaultTire=\"highway_1\" DefaultRim=\"rim_1\"/></TruckData><GameData><CompatibleWheels Type=\"test_wheels\" Scale=\"0.8\"/></GameData></Truck>");
  File.WriteAllText(Path.Combine(wheels,"test_wheels.xml"),"<TruckWheels><TruckRims><TruckRim Name=\"rim_1\"/></TruckRims><TruckTires><TruckTire Name=\"highway_1\"><WheelFriction _template=\"highway\"/></TruckTire></TruckTires></TruckWheels>");
  var bytes=(byte[])typeof(Program).GetMethod("BuildMap",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{new[]{"ONE","TWO","THREE"}})!;
  foreach(var map in new[]{"level_us_01_01","level_us_01_02","level_us_02_01"})using(var zip=ZipFile.Open(Path.Combine(maps,map+".pak"),ZipArchiveMode.Create))using(var s=zip.CreateEntry("[scene]/data_for_ssl.bin").Open())s.Write(bytes);
  var names=new Dictionary<string,string>();var regions=new Dictionary<string,string>{{"us_01","Michigan"},{"us_02","Alaska"}};var availability=new RegionTruckAvailabilityStore(Path.Combine(root,"availability.json"));
  RegionTruckCatalog Load()=>RegionTruckCatalogService.Load(root,maps,names,regions,availability);
  var original=Load();RegionTruckEntry E(string map,string id)=>original.Entries.Single(e=>e.MapCode==map&&e.PlacementId==id);
  const string first="level_us_01_01",second="level_us_01_02",other="level_us_02_01";
  var replacement=new CustomTruck("second_truck","Secret second","Scout");var third=new CustomTruck("third_truck","Secret third","Scout");
  Check(original.EquipmentCatalog!.TryMissionConfiguration(replacement.Code,0,out var equipment,out _),"fixture tires resolved");
  PendingTruckReplacements.QueueRandomization(root,[(E(first,"ONE"),replacement,equipment)],5);
  PendingTruckReplacements.QueueRandomization(root,[(E(first,"TWO"),replacement,equipment),(E(second,"ONE"),replacement,equipment)],5,missionOnly:true);
  PendingTruckReplacements.QueueRandomization(root,[(E(other,"ONE"),replacement,equipment)],5);
  PendingTruckReplacements.Stage(root);PendingTruckReplacements.Queue(root,E(other,"TWO"),replacement);
  using(var panel=new StartChangesPanel(()=>root,new Button(),_=>[],()=>{},()=>Task.CompletedTask,()=>Task.CompletedTask))
  {
   panel.BindingContext=new BindingContext();panel.Size=new Size(1100,850);panel.CreateControl();
   var actual=WorkspaceChangeAudit.MapFiles(root);
   var rows=actual.Concat(new[]{new WorkspaceChange(WorkspaceChange.InFiles,second,"Truck randomization","Original placement","Saved — details in Map trucks"),new WorkspaceChange(WorkspaceChange.InFiles,first,TruckReplacementStore.RepairRequired,"Original scene","Use Repair saved maps")}).ToArray();
   var groups=typeof(StartChangesPanel).GetMethod("MapGroups",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{rows,regions});
   Call(panel,"BindChanges",groups);var grid=Field<DataGridView>(panel,"_changes");_=grid.Handle;
   Check(grid.Rows.Count==4,"two pending regions, separate applied region, and standalone repair warning");
   var michigan=grid.Rows.Cast<DataGridViewRow>().Single(r=>r.Cells["File / target"].Value?.ToString()=="Michigan"&&r.Cells["State"].Value?.ToString()==WorkspaceChange.Pending);
   Check(michigan.Cells["Change"].Value!.ToString()!.Contains("3 change(s) across 2 map(s)"),"obtainable and mission changes grouped across maps by region");
   Check(!grid.Rows.Cast<DataGridViewRow>().Any(r=>r.Cells.Cast<DataGridViewCell>().Any(c=>c.OwningColumn.Name!="Entry"&&(c.Value?.ToString()?.Contains("Secret")==true))),"group summaries do not expose replacement identities");
   Check(grid.Rows.Cast<DataGridViewRow>().Where(r=>r.Cells["State"].Value?.ToString()==WorkspaceChange.Pending).All(r=>r.DefaultCellStyle.BackColor==AppColors.Get("EngineMatch")),"pending group colors retained");
   grid.Sort(grid.Columns["File / target"]!,System.ComponentModel.ListSortDirection.Descending);grid.CurrentCell=grid.Rows.Cast<DataGridViewRow>().Single(r=>r.Cells["File / target"].Value?.ToString()=="Michigan"&&r.Cells["State"].Value?.ToString()==WorkspaceChange.Pending).Cells["File / target"];
   using(var image=new Bitmap(panel.Width,panel.Height)){panel.DrawToBitmap(image,new Rectangle(Point.Empty,panel.Size));image.Save(Path.Combine(AppContext.BaseDirectory,"region-change-groups.png"));}
   var discard=Field<Button>(panel,"_discardMap");Check(discard.Enabled,"pending group can be discarded");discard.PerformClick();
   Pump(()=>Field<Label>(panel,"_status").Text.StartsWith("Discarded 3"));
   Check(PendingTruckReplacements.Load(root).Count==2&&PendingTruckReplacements.Load(root).All(c=>c.Original.RegionCode=="us_02"),"discard sorted region group preserves other region and its draft");
   Check(PendingTruckReplacements.LoadAssignments(root).Count==1,"discard grouped randomizations clears exactly their assignments");
  }
  // Build a multi-map filtered selection, with an unmatched model and another region left outside it.
  PendingTruckReplacements.Queue(root,E(first,"TWO"),replacement);PendingTruckReplacements.Queue(root,E(second,"ONE"),replacement);PendingTruckReplacements.Queue(root,E(first,"THREE"),third);
  using(var tab=new RegionsTrucksTab(()=>root,()=>"",availability))
  {
   tab.BindingContext=new BindingContext();tab.Size=new Size(1100,850);tab.CreateControl();
   Set(tab,"_catalog",Load());Set(tab,"_loadedWorkspace",root);Field<TextBox>(tab,"_mapFolder").Text=maps;Field<HashSet<string>>(tab,"_selectedRegions").Add("us_01");Call(tab,"ApplyFilter");
   var grid=Field<DataGridView>(tab,"_trucks");_=grid.Handle;
   var filter=TableHeaderBehavior.Attach(grid).AddFilter("Truck ID");filter.SelectValues([replacement.Code]);Application.DoEvents();
   Check(grid.Rows.Count==2&&((RegionTruckEntry[])Call(tab,"CurrentFilteredEntries")!).Length==2,"filtered reset includes both matching rows across selected-region maps");
   grid.Sort(grid.Columns["Placement ID"]!,System.ComponentModel.ListSortDirection.Descending);grid.CurrentCell=grid.Rows[0].Cells["Truck"];
   var reset=Field<Button>(tab,"_resetTruck");Check(reset.Text=="Reset selected to original"&&reset.Enabled,"renamed filtered reset enabled");reset.PerformClick();
   Pump(()=>!Field<bool>(tab,"_editingPlan")&&!Field<bool>(tab,"_loading"));
   var remaining=PendingTruckReplacements.Load(root);
   Check(remaining.Count==3&&remaining.Any(c=>c.Original.MapCode==first&&c.Original.PlacementId=="THREE"&&c.Target.Code==third.Code)&&remaining.Count(c=>c.Original.RegionCode=="us_02")==2,"filtered reset removes both matching drafts while preserving hidden models and other regions");
   Check(((RegionTruckEntry[])Call(tab,"CurrentFilteredEntries")!).Length==0&&!reset.Enabled,"empty filter result disables reset");
  }
  foreach(var file in Directory.GetFiles(maps,"*.pak"))Check(RegionTruckCatalogService.ReadMapData(file)!.SequenceEqual(bytes),"UI actions preserve source maps");
  Check(!TruckReplacementStore.SavedMaps(root).Any(),"group discard and filtered reset do not apply map files");
  Console.WriteLine("PASS region grouping and filtered reset: mixed randomization types, multi-map summaries, state colors, repair warnings, sorted bulk discard, assignment cleanup, draft preservation, column/region filters and source-map preservation.");
 }
}
