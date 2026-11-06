using System.Data;
using System.ComponentModel;
using System.Reflection;
using System.IO.Compression;
using SnowPakTool.Desktop;
internal static class ChangedTruckChecks
{
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);}
 static void Set(object value,string name,object field)=>value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(value,field);
 static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(value)!;
 static object? Call(object value,string name,params object?[] args)=>value.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(value,args);
 public static void Run()
 {
  var root=Path.Combine(AppContext.BaseDirectory,"changed-trucks-"+Guid.NewGuid().ToString("N"));
  var media=Path.Combine(root,"original_files","[media]","classes","trucks");var maps=Path.Combine(root,"maps");Directory.CreateDirectory(media);Directory.CreateDirectory(maps);
  var names=new Dictionary<string,string>{{"ORIGINAL","Original truck"},{"SECOND","Second truck"},{"THIRD","Third truck"}};
  var strings=Path.Combine(root,"original_files","[strings]");Directory.CreateDirectory(strings);File.WriteAllText(Path.Combine(strings,"strings_english.str"),"ORIGINAL \"Original truck\"\nSECOND \"Second truck\"\nTHIRD \"Third truck\"\n");
  foreach(var (code,name) in new[]{("test_truck","ORIGINAL"),("test_truck_second","SECOND"),("test_truck_third_long_name","THIRD")})
   File.WriteAllText(Path.Combine(media,code+".xml"),$"<Truck><TruckData TruckType=\"SCOUT\"/><GameData><UiDesc UiName=\"{name}\"/></GameData></Truck>");
  var data=(byte[])typeof(Program).GetMethod("BuildMap",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{new[]{"AWARD","SAME","",""}})!;
  var source=Path.Combine(maps,"level_us_01_01.pak");
  using(var zip=ZipFile.Open(source,ZipArchiveMode.Create))using(var stream=zip.CreateEntry("[scene]/data_for_ssl.bin").Open())stream.Write(data);
  var availability=new RegionTruckAvailabilityStore(Path.Combine(root,"availability.json"));
  RegionTruckCatalog Load(string folder)=>RegionTruckCatalogService.Load(root,folder,names,new Dictionary<string,string>(),availability);
  var original=Load(maps);Check(original.Entries.Count==4 && original.Entries.All(e=>!e.TruckChanged),"original rows unchanged");
  UI(original,root);
  var named=original.Entries.Single(e=>e.PlacementId=="AWARD");
  PendingTruckReplacements.Queue(root,named,new("test_truck_second","Second truck","Scout"));
  var pending=Load(maps);var pendingNamed=pending.Entries.Single(e=>e.PlacementId=="AWARD");
  Check(pendingNamed.TruckChanged && pendingNamed.BaselineTruck=="Original truck" && pendingNamed.FileEntry is not null,"pending baseline");UI(pending,root);
  var saved=MapStagingChecks.Apply(root).Single();var reloaded=Load(maps);
  var changed=reloaded.Entries.Single(e=>e.PlacementId=="AWARD");
  Check(changed.TruckChanged && changed.FileEntry is null && changed.BaselineTruckCode=="test_truck" && changed.BaselineTruck=="Original truck","saved baseline survives apply");UI(reloaded,root);
  Check(reloaded.Entries.Where(e=>e.PlacementId!="AWARD").All(e=>!e.TruckChanged),"shifted offsets do not highlight unrelated rows");
  PendingTruckReplacements.Queue(root,changed,new("test_truck_third_long_name","Third truck","Scout"));
  var secondPending=Load(maps);Check(secondPending.Entries.Single(e=>e.PlacementId=="AWARD").BaselineTruck=="Original truck","second replacement retains true original");UI(secondPending,root);
  MapStagingChecks.Apply(root);var twice=Load(maps);changed=twice.Entries.Single(e=>e.PlacementId=="AWARD");
  Check(changed.TruckChanged && changed.BaselineTruck=="Original truck" && changed.TruckCode=="test_truck_third_long_name","repeated saved replacement baseline");
  var revert=PendingTruckReplacements.Queue(root,changed,new("test_truck","Original truck","Scout"));
  var revertedPreview=Load(maps);Check(!revertedPreview.Entries.Single(e=>e.PlacementId=="AWARD").TruckChanged,"pending return to original clears highlight");UI(revertedPreview,root);PendingTruckReplacements.Discard(root,revert.Key);
  var anonymous=twice.Entries.Where(e=>e.PlacementId.Length==0).OrderBy(e=>e.BinaryOffset).Last();
  PendingTruckReplacements.Queue(root,anonymous,new("test_truck_second","Second truck","Scout"));MapStagingChecks.Apply(root);
  var unnamed=Load(maps);Check(unnamed.Entries.Where(e=>e.PlacementId.Length==0).Count(e=>e.TruckChanged)==1,"anonymous identical-position entries matched by scene order");UI(unnamed,root);
  File.Delete(saved.OutputPath);var restored=Load(maps);Check(restored.Entries.All(e=>!e.TruckChanged),"deleting output clears replacement highlights");UI(restored,root);
  var binFolder=Path.Combine(root,"bin-maps","level_us_01_02","[scene]");Directory.CreateDirectory(binFolder);File.WriteAllBytes(Path.Combine(binFolder,"data_for_ssl.bin"),data);
  var binCatalog=Load(Path.Combine(root,"bin-maps"));var binOriginal=binCatalog.Entries.First(e=>e.MapCode=="level_us_01_02"&&e.PlacementId=="SAME");
  PendingTruckReplacements.Queue(root,binOriginal,new("test_truck_second","Second truck","Scout"));MapStagingChecks.Apply(root);
  var binChanged=Load(Path.Combine(root,"bin-maps")).Entries.Single(e=>e.MapCode=="level_us_01_02"&&e.PlacementId=="SAME");
  Check(binChanged.TruckChanged && binChanged.BaselineTruck=="Original truck","original.bin comparison");
  Check(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(saved.OriginalBackupPath)),"input and original preserved");
  Console.WriteLine("PASS changed trucks: original, pending, applied and repeated replacements, return to original, shifted offsets, duplicate unnamed placements, original.bin, output deletion, selected-cell colors, sorting and original-name tooltips.");
 }
 static DataGridViewCellToolTipTextNeededEventArgs Tip(int column,int row) => (DataGridViewCellToolTipTextNeededEventArgs)Activator.CreateInstance(typeof(DataGridViewCellToolTipTextNeededEventArgs),BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,new object[]{column,row,""},null)!;
 static void UI(RegionTruckCatalog catalog,string root)
 {
  using var tab=new RegionsTrucksTab(()=>root,()=>"",new RegionTruckAvailabilityStore(Path.Combine(root,"ui-availability.json"))){BindingContext=new BindingContext(),Size=new Size(1100,800)};
  Set(tab,"_catalog",catalog);Field<HashSet<string>>(tab,"_selectedRegions").Add("us_01");tab.CreateControl();Call(tab,"ApplyFilter");
  var grid=Field<DataGridView>(tab,"_trucks");Check(grid.Rows.Count==catalog.Entries.Count,"all rows bound");
  grid.Sort(grid.Columns["Truck"]!,ListSortDirection.Descending);
  foreach(DataGridViewRow row in grid.Rows)
  {
   var entry=(RegionTruckEntry)((DataRowView)row.DataBoundItem!)["Entry"];
   var cell=row.Cells["Truck"];var expected=AppColors.Get(entry.TruckChanged ? entry.FileEntry is null ? "DAF8E0" : "EngineMatch" : "Surface");
   Check(cell.Style.BackColor==expected && cell.Style.SelectionBackColor==expected,"truck color including selected state");
   var tip=Tip(cell.ColumnIndex,row.Index);Call(tab,"EquipmentToolTipNeeded",null,tip);
   Check(tip.ToolTipText.Contains("Original truck: Original truck (test_truck)"),"original tooltip follows sorted bound row");
   if(entry.FileEntry is not null)Check(tip.ToolTipText.Contains("Preview:"),"pending tooltip retained");
   var tires=Tip(grid.Columns["Tires"]!.Index,row.Index);Call(tab,"EquipmentToolTipNeeded",null,tires);Check(tires.ToolTipText==entry.TireDetails,"equipment tooltip retained");
  }
 }
}
