using System.Reflection;
using System.Text.Json;
using System.IO.Compression;
using SnowPakTool.Desktop;
internal static class ObtainableRandomizerChecks
{
 sealed class Messages : IProgress<string>{public List<string> Lines {get;}=[];public void Report(string text)=>Lines.Add(text);}
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);}
 public static void Run()
 {
  var root=Path.Combine(AppContext.BaseDirectory,"randomizer-"+Guid.NewGuid().ToString("N"));
  var media=Path.Combine(root,"original_files","[media]","classes");var trucks=Path.Combine(media,"trucks");var addons=Path.Combine(trucks,"addons");var ps=Path.Combine(root,"original_files","[ps]");var maps=Path.Combine(root,"maps");var strings=Path.Combine(root,"original_files","[strings]");
  foreach(var dir in new[]{addons,ps,maps,strings,Path.Combine(media,"wheels")})Directory.CreateDirectory(dir);
  var names=new Dictionary<string,string>{{"SOURCE","Original vehicle"},{"GOOD","Secret choice one"},{"GOOD2","Secret choice two"},{"BAD","Secret incompatible"},{"SIDEBOARD","Sideboard Bed"}};
  File.WriteAllText(Path.Combine(strings,"strings_english.str"),string.Join("\n",names.Select(p=>p.Key+" \""+p.Value+"\"")));
  void Truck(string code,string key,bool bed,string extra="",string wheels="test_wheels")=>File.WriteAllText(Path.Combine(trucks,code+".xml"),
   $"<Truck><TruckData TruckType=\"HEAVY_DUTY\"><Wheels DefaultWheelType=\"{wheels}\" DefaultTire=\"highway_1\" DefaultRim=\"rim_1\"/></TruckData><GameData><UiDesc UiName=\"{key}\"/><CompatibleWheels Type=\"{wheels}\" Scale=\"0.8\"/><CompatibleWheels Type=\"{wheels}\" Scale=\"0.5\"/>"+
   (bed ? "<AddonSockets DefaultAddon=\"frame_addon_sideboard_2\"><Socket Names=\"Bed\" "+(code=="test_truck" ? "" : "NamesBlock=\"Other\"")+"/></AddonSockets>" : "")+
   "<AddonSockets DefaultAddon=\"visual_stock\"><Socket Names=\"Other\"/></AddonSockets>"+extra+"</GameData></Truck>");
  Truck("test_truck","SOURCE",true);Truck("good","GOOD",true);Truck("good2","GOOD2",true);Truck("bad","BAD",false);
  File.WriteAllText(Path.Combine(addons,"frame_addon_sideboard_2.xml"),"<TruckAddon><GameData Category=\"frame_addons\"><UiDesc UiName=\"SIDEBOARD\"/><InstallSocket Type=\"Bed\"/></GameData></TruckAddon>");
  File.WriteAllText(Path.Combine(addons,"frame_addon_kung.xml"),"<TruckAddon><GameData Category=\"frame_addons\"><InstallSocket Type=\"Kung\"/></GameData></TruckAddon>");
  foreach(var id in new[]{"visual_stock","test_upgrade"})File.WriteAllText(Path.Combine(addons,id+".xml"),"<TruckAddon><GameData><InstallSocket Type=\"Other\"/></GameData></TruckAddon>");
  File.WriteAllText(Path.Combine(media,"wheels","test_wheels.xml"),"<TruckWheels><TruckRims><TruckRim Name=\"rim_1\"/></TruckRims><TruckTires>"+string.Join("",new[]{"highway","allterrain","offroad","mudtires"}.Select(kind=>$"<TruckTire Name=\"{kind}_1\"><WheelFriction _template=\"{kind}\"/></TruckTire>"))+"</TruckTires></TruckWheels>");
  Truck("combo","GOOD",true,"<AddonSockets><Socket Names=\"Kung\"/></AddonSockets>");
  Truck("highway_only","GOOD",true,wheels:"highway_wheels");Truck("private_only","GOOD",true,wheels:"private_wheels");
  foreach(var (wheel,kind) in new[]{("highway_wheels","highway"),("private_wheels","offroad")})File.WriteAllText(Path.Combine(media,"wheels",wheel+".xml"),$"<TruckWheels Radius=\"1\"><TruckTires><TruckTire Name=\"{kind}_1\"><WheelFriction _template=\"{kind}\"/></TruckTire></TruckTires></TruckWheels>");
  var data=(byte[])typeof(Program).GetMethod("BuildMap",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,new object[]{new[]{"AWARD","REMOVE","UNKNOWN","",""}})!;
  foreach(var code in new[]{"us_01","us_02","ru_02"})
  {
   using(var zip=ZipFile.Open(Path.Combine(maps,"level_"+code+"_01.pak"),ZipArchiveMode.Create))using(var stream=zip.CreateEntry("[scene]/data_for_ssl.bin").Open())stream.Write(data);
   File.WriteAllText(Path.Combine(ps,code+"_01_tasks.sso"),"tasksSettings = { AWARD_TASK = { stages = [ { truckDelivery = [ { truckId = \"AWARD\" afterStageFinished = \"REWARD\" } ] } ] __type = \"TaskSettings\" } REMOVE_TASK = { stages = [ { truckDelivery = [ { truckId = \"REMOVE\" afterStageFinished = \"DESTROY\" } ] } ] __type = \"TaskSettings\" } }");
  }
  var inputBytes=Directory.EnumerateFiles(maps,"*.pak").ToDictionary(path=>path,File.ReadAllBytes);
  var availability=new RegionTruckAvailabilityStore(Path.Combine(root,"availability.json"));
  RegionTruckCatalog Load()=>RegionTruckCatalogService.Load(root,maps,names,new Dictionary<string,string>{{"us_01","First region"},{"us_02","Second region"},{"ru_02","Unsorted region"}},availability);
  var original=Load();Check(original.Entries.Count(e=>e.Availability.StartsWith("Obtainable"))==9,"obtainable fixture classifications");
  var progression=new RegionProgressionState{IsFinalized=true,HasRandomized=true,Order=["us_01","us_02","ru_02"]};
  var tiers=new List<GroupState>{new(){Name="FX",TruckCodes=["good","good2"]},new(){Name="SS",TruckCodes=["bad"]}};
  var difficulties=new List<GroupState>{new(){Name="D",TruckCodes=["us_01"]},new(){Name="S",TruckCodes=["us_02"]}};
  var owned=new List<CustomTruck>{new("good","Secret choice one","Heavy-duty"),new("good2","Secret choice two","Heavy-duty"),new("bad","Secret incompatible","Heavy-duty"),new("good","Wrong class decoy","Scout")};
  var regions=new HashSet<string>(new[]{"us_01","ru_02"},StringComparer.OrdinalIgnoreCase);var log=new Messages();
  void Randomize(RegionTruckCatalog catalog,int seed=42)=>ObtainableTruckRandomizer.Randomize(root,catalog,regions,owned,progression,tiers,difficulties,log,seed);
  progression.IsFinalized=false;bool refused=false;try{Randomize(original);}catch(InvalidOperationException){refused=true;}Check(refused && PendingTruckReplacements.Load(root).Count==0,"requires finalized progression");progression.IsFinalized=true;
  var source=original.Entries.First(e=>e.PlacementId=="AWARD");var eq=original.EquipmentCatalog!;
  Check(!eq.TryRandomizationConfiguration(source,"bad",3,out _,out _),"unsupported required addon rejected");
  Check(eq.TryRandomizationConfiguration(source,"good",3,out var exact,out _) && exact.Tire=="mudtires_1" && exact.WheelScale==0.5f,"smallest tire in requested category");
  Check(exact.Addons.SequenceEqual(new[]{"frame_addon_sideboard_2"}),"required addon installed, blocked optional default omitted");
  Check(eq.IsConfigurationSupported("good",exact),"configuration validation");
  var combo=source with{OriginalEquipment=new(true,["frame_addon_sideboard_2","frame_addon_kung"],"","","",WheelScale:0)};
  Check(!eq.TryRandomizationConfiguration(combo,"good",1,out _,out _),"unsupported multiple required addons rejected");
  Check(eq.TryRandomizationConfiguration(combo,"combo",1,out var combined,out _) && combined.Addons.Contains("frame_addon_kung") && combined.Addons.Contains("frame_addon_sideboard_2"),"rare multiple-addon combination installed together");
  Check(eq.TryRandomizationConfiguration(source,"highway_only",3,out var fallbackConfig,out var usedFallback) && usedFallback && fallbackConfig.Tire=="highway_1","nearest supported tire fallback");
  Check(eq.TryRandomizationConfiguration(source,"private_only",2,out var privateConfig,out _) && eq.ResolveConfigured("private_only",privateConfig).Tires=="Custom (Offroad)","custom tires use underlying category");
  Randomize(original);
  var pending=PendingTruckReplacements.Load(root);var assignments=PendingTruckReplacements.LoadAssignments(root);
  Check(pending.Count==3 && assignments.Count==3 && pending.All(p=>p.Original.RegionCode=="us_01"),"only owned ranked obtainable placements randomized");
  Check(pending.All(p=>p.Target.Code is "good" or "good2" && p.Configuration!.Addons.Contains("frame_addon_sideboard_2")),"incompatible truck never selected and equipment stored");
  Check(pending.Select(p=>p.Target.Code).Distinct().Count()==2,"diversity preference");
  Check(log.Lines.Any(l=>l.Contains("Unsorted region")&&l.Contains("unchanged")) && log.Lines.Any(l=>l.Contains("First region: complete")),"region completion and unsorted messages");
  Check(!log.Lines.Any(l=>l.Contains("Secret")||l.Contains("good")||l.Contains("bad")||l.Contains("test_truck")||l.Contains("mudtires_1")),"logs contain no replacement spoilers");
  void NoAuditSpoilers(string phase)
  {
   var rows=WorkspaceChangeAudit.MapFiles(root);
   Check(!rows.Any(row=>new[]{row.Property,row.Before,row.After}.Any(text=>text.Contains("Secret")||text.Contains("good")||text.Contains("mudtires_1")||text.Contains("frame_addon_sideboard_2"))),"Start stays spoiler free: "+phase);
  }
  NoAuditSpoilers("pending");
  var planPath=Path.Combine(root,"region-maps","pending-replacements.json");var savedJson=File.ReadAllText(planPath);Randomize(original,77);Check(File.ReadAllText(planPath)==savedJson,"rerun preserves pending assignments byte for byte");
  foreach(var pair in inputBytes)Check(File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value),"queue does not write input map");
  var goodXml=File.ReadAllText(Path.Combine(trucks,"good.xml"));var good2Xml=File.ReadAllText(Path.Combine(trucks,"good2.xml"));
  File.WriteAllText(Path.Combine(trucks,"good.xml"),goodXml.Replace("Names=\"Bed\"","Names=\"Unavailable\""));File.WriteAllText(Path.Combine(trucks,"good2.xml"),good2Xml.Replace("Names=\"Bed\"","Names=\"Unavailable\""));
  bool incompatible=false;try{MapStagingChecks.Apply(root);}catch(InvalidDataException){incompatible=true;}
  Check(incompatible && !TruckReplacementStore.SavedMaps(root).Any() && PendingTruckReplacements.Load(root).Count==3,"Apply revalidates changed definitions without partial map writes");
  File.WriteAllText(Path.Combine(trucks,"good.xml"),goodXml);File.WriteAllText(Path.Combine(trucks,"good2.xml"),good2Xml);
  var preview=Load();Check(preview.Entries.Where(e=>e.FileEntry is not null).All(e=>e.TireDetails.Contains("0.5")&&e.FrameAddons.Contains("Sideboard")),"Regions-Trucks preview shows exact equipment");
  var written=MapStagingChecks.Apply(root);Check(written.Count==1 && PendingTruckReplacements.Load(root).Count==0 && PendingTruckReplacements.LoadAssignments(root).Count==3,"Apply retains persistent assignment ledger");
  var applied=Load();NoAuditSpoilers("applied");
  foreach(var assignment in assignments)
  {var actual=applied.Entries.Single(e=>e.StablePlacementKey==assignment.PlacementKey);Check(actual.TruckCode==assignment.Target.Code && PendingTruckReplacements.EquipmentEqual(actual.RawEquipment,assignment.Configuration),"written model and all equipment fields match plan");}
  Randomize(applied,999);Check(PendingTruckReplacements.Load(root).Count==0 && PendingTruckReplacements.LoadAssignments(root).Count==3,"applied assignments not rerolled");
  regions.Add("us_02");Randomize(applied,123);
  Check(PendingTruckReplacements.Load(root).All(p=>p.Original.RegionCode=="us_02"),"new ownership only processes new placements");
  Check(assignments.All(a=>PendingTruckReplacements.LoadAssignments(root).Any(b=>a.PlacementKey==b.PlacementKey && a.Target==b.Target && a.Seed==b.Seed && a.CreatedAt==b.CreatedAt && PendingTruckReplacements.EquipmentEqual(a.Configuration,b.Configuration))),"previous assignments unchanged after new ownership");
  MapStagingChecks.Apply(root);applied=Load();PendingTruckReplacements.Reset(root,applied.Entries.Where(e=>e.RegionCode=="us_01").ToArray());
  Check(PendingTruckReplacements.Load(root).Count==3 && PendingTruckReplacements.Load(root).All(p=>p.ResetOriginal),"reset queues full restoration");NoAuditSpoilers("reset");
  Check(PendingTruckReplacements.LoadAssignments(root).All(a=>a.RegionCode=="us_02"),"reset clears only selected region assignments");
  Randomize(Load(),800);
  Check(PendingTruckReplacements.Load(root).Count==3 && PendingTruckReplacements.Load(root).All(item=>item.Randomized&&!item.ResetOriginal),"reset can be rerolled before an intermediate Apply");
  PendingTruckReplacements.Reset(root,Load().Entries.Where(e=>e.RegionCode=="us_01").ToArray());

  MapStagingChecks.Apply(root);
  var output=written.Single().OutputPath;Check(RegionTruckCatalogService.ReadMapData(output)!.SequenceEqual(data),"reset restores original scene bytes including engine/wheels/addons");
  Check(File.ReadAllBytes(Path.Combine(maps,"level_us_01_01.pak")).SequenceEqual(File.ReadAllBytes(written.Single().OriginalBackupPath)),"original archive preserved");
  Randomize(Load(),456);var afterReset=Load();PendingTruckReplacements.Reset(root,afterReset.Entries.Where(e=>e.RegionCode=="us_01").ToArray());
  Check(PendingTruckReplacements.Load(root).Count==0 && RegionTruckCatalogService.ReadMapData(output)!.SequenceEqual(data),"reset before Apply cancels plan without writing maps");
  var badOnly=new[]{new CustomTruck("bad","Secret incompatible","Heavy-duty")};var beforeFailure=File.ReadAllText(planPath);
  ObtainableTruckRandomizer.Randomize(root,Load(),new HashSet<string>{"us_01"},badOnly,progression,tiers,difficulties,log,1);
  Check(File.ReadAllText(planPath)==beforeFailure && log.Lines.Any(l=>l.Contains("no different owned")),"no valid candidate leaves state untouched and logs an error");
  var custom=Load() with{Entries=Load().Entries.Where(e=>e.RegionCode=="us_01").Select(e=>e with{Class="Farming Tractor",OriginalClass="Farming Tractor"}).ToArray()};
  ObtainableTruckRandomizer.Randomize(root,custom,new HashSet<string>{"us_01"},[new("good","Secret choice one","Farming Tractor"),new("good2","Secret choice two","Heavy-duty")],progression,tiers,difficulties,log,3);
  Check(PendingTruckReplacements.Load(root).All(p=>p.Target.Code=="good"),"custom Farming Tractor class respected");
  PendingTruckReplacements.Reset(root,Load().Entries.Where(e=>e.RegionCode=="us_01").ToArray());
  var legacy=Path.Combine(root,"legacy");Directory.CreateDirectory(Path.Combine(legacy,"region-maps"));var legacyPath=Path.Combine(legacy,"region-maps","pending-replacements.json");
  File.WriteAllText(legacyPath,JsonSerializer.Serialize(new[]{new PendingTruckReplacement("legacy",source,new("good","Secret choice one","Heavy-duty"),DateTimeOffset.UtcNow)}));
  Check(PendingTruckReplacements.Load(legacy).Count==1,"legacy pending array remains readable");PendingTruckReplacements.Discard(legacy,"legacy");Check(PendingTruckReplacements.Load(legacy).Count==0,"legacy plan migrated safely on write");
  var detailed=new List<RandomizationLogEntry>();
  ObtainableTruckRandomizer.Randomize(root,Load(),new HashSet<string>{"us_01"},[new("highway_only","Hidden choice","Heavy-duty")],new RegionProgressionState{IsFinalized=true,Order=["us_02","ru_02","us_01"]},
   [new(){Name="FX",TruckCodes=["highway_only"]}],[new(){Name="S",TruckCodes=["us_01"]}],new RandomizationLogProgress(detailed.Add),44);
  Check(detailed.Any(item=>item.IsDetail&&item.Message.Contains("→ Highway")&&item.Message.Contains("requested category is not supported")),"detailed tire fallback identifies requested and actual categories with reason");
  Check(detailed.FindIndex(item=>item.IsDetail&&item.Message.Contains("→ Highway"))<detailed.FindIndex(item=>!item.IsDetail&&item.Message.Contains("complete")),"tire detail emitted before region completion");
  Check(!detailed.Any(item=>item.Message.Contains("Hidden choice")||item.Message.Contains("Secret")),"detailed logs do not reveal replacement names");
  // Separate policy fixtures queue plans only; source map bytes are never written.
  foreach(var (code,bed) in new[]{("off_low",true),("off_high",true),("off_bad",false),("off_unranked",true)})
  {
   Truck(code,"GOOD",bed);
   var path=Path.Combine(trucks,code+".xml");File.WriteAllText(path,File.ReadAllText(path).Replace("HEAVY_DUTY","OFFROAD"));
  }
  var policyEquipment=Load().EquipmentCatalog!;
  var policyTiers=new List<GroupState>{new(){Name="FX",TruckCodes=["good","off_low","off_bad"]},new(){Name="SS",TruckCodes=["off_high"]}};
  var policyOwned=new List<CustomTruck>{new("good","Hidden HD","HEAVY_DUTY"),new("off_low","Hidden low","OFFROAD"),new("off_high","Hidden high","OFFROAD"),new("off_bad","Hidden incompatible","OFFROAD"),new("off_unranked","Hidden unranked","OFFROAD")};
  RegionTruckCatalog PolicyCatalog(int start,int count,string sourceClass="HEAVY_DUTY")=>original with{EquipmentCatalog=policyEquipment,Entries=Enumerable.Range(start,count).Select(i=>source with{RegionCode="us_01",Region="First region",PlacementIndex=100+i,PlacementId="POLICY"+i.ToString("D3"),X=i,Class=sourceClass,OriginalClass=sourceClass,FileEntry=null}).ToArray()};
  var policyRegions=new HashSet<string>{"us_01"};
  var totalOffroad=0;
  for(var seed=0;seed<40;seed++)
  {
   var policyRoot=Path.Combine(root,"policy",seed.ToString());
   var policyLogs=new List<RandomizationLogEntry>();var reporter=new RandomizationLogProgress(policyLogs.Add);
   ObtainableTruckRandomizer.Randomize(policyRoot,PolicyCatalog(0,10),policyRegions,policyOwned,progression,policyTiers,difficulties,reporter,seed);
   var first=PendingTruckReplacements.LoadAssignments(policyRoot).ToArray();
   Check(first.Length==10&&first.Count(a=>a.Target.Class=="OFFROAD")<=2,"first run one-in-five cap");
   Check(first.All(a=>a.OriginalClass=="HEAVY_DUTY"),"source class persisted for cap after source catalog is unavailable");
   // Only new placements are loaded on the next run; cap must still include the saved ledger.
   ObtainableTruckRandomizer.Randomize(policyRoot,PolicyCatalog(10,20),policyRegions,policyOwned,progression,policyTiers,difficulties,reporter,seed+100);
   var all=PendingTruckReplacements.LoadAssignments(policyRoot).ToArray();
   Check(all.Length==30&&first.All(a=>all.Any(b=>a.PlacementKey==b.PlacementKey&&a.Target==b.Target&&a.Seed==b.Seed&&a.CreatedAt==b.CreatedAt&&PendingTruckReplacements.EquipmentEqual(a.Configuration,b.Configuration))),"new placements preserve previous assignments");
   var substitutions=all.Count(a=>a.Target.Class=="OFFROAD");totalOffroad+=substitutions;
   Check(substitutions<=6&&all.All(a=>a.Target.Code is "good" or "off_low"),"strict cap and no high-tier, unranked or addon-incompatible offroad truck");
   Check(policyLogs.Count(l=>l.IsDetail&&l.Message.Contains("HEAVY_DUTY → OFFROAD"))==substitutions,"each exception gets a detailed log only");
   Check(!policyLogs.Any(l=>l.Message.Contains("Hidden")||l.Message.Contains("off_low")),"exception logs remain spoiler free");
   Check(all.Take(4).All(a=>a.Target.Class!="OFFROAD"),"no exception before five assignments");
  }
  Check(totalOffroad>60&&totalOffroad<240,"limited exception occurs across seeds and remains sparse");
  var otherRoot=Path.Combine(root,"policy","one-way");
  ObtainableTruckRandomizer.Randomize(otherRoot,PolicyCatalog(0,8,"OFFROAD"),policyRegions,[new("good","HD only","HEAVY_DUTY")],progression,policyTiers,difficulties,log,9);
  Check(PendingTruckReplacements.LoadAssignments(otherRoot).Count==0,"offroad placements never borrow heavy-duty trucks");
  var healthyRoot=Path.Combine(root,"policy","healthy");
  var healthyOwned=new[]{new CustomTruck("good","HD1","Heavy-duty"),new("good2","HD2","Heavy-duty"),new("combo","HD3","Heavy-duty"),new("off_low","Offroad","OFFROAD")};
  var healthyTiers=new[]{new GroupState{Name="FX",TruckCodes=["good","good2","combo","off_low"]}};
  ObtainableTruckRandomizer.Randomize(healthyRoot,PolicyCatalog(0,3),policyRegions,healthyOwned,progression,healthyTiers,difficulties,log,8);
  Check(PendingTruckReplacements.LoadAssignments(healthyRoot).Select(a=>a.Target.Code).Distinct().Count()==3,"healthy near-tier HD pool uses each model before repeating");
  var regionalOwned=new[]{new CustomTruck("good","Low HD","Heavy-duty"),new("good2","Next HD","Heavy-duty"),new("combo","Other HD","Heavy-duty"),new("off_high","Strong HD","Heavy-duty")};
  var regionalTiers=new[]{new GroupState{Name="D",TruckCodes=["good"]},new GroupState{Name="C",TruckCodes=["good2","combo"]},new GroupState{Name="SS",TruckCodes=["off_high"]}};
  for(var seed=0;seed<24;seed++)
  {
   var regionRoot=Path.Combine(root,"regional-variety",seed.ToString());var regionLog=new List<RandomizationLogEntry>();
   var reporter=new RandomizationLogProgress(regionLog.Add);
   // One D candidate, two C candidates: the narrow band must widen before repeating the D truck.
   ObtainableTruckRandomizer.Randomize(regionRoot,PolicyCatalog(0,3),policyRegions,regionalOwned,progression,regionalTiers,difficulties,reporter,seed);
   var firstThree=PendingTruckReplacements.LoadAssignments(regionRoot).ToArray();
   Check(firstThree.Length==3&&firstThree.Select(a=>a.Target.Code).Distinct().Count()==3,"first region avoids triplicate and uses widened-band alternatives");
   Check(firstThree.All(a=>a.Target.Code!="off_high"),"regional variety does not force an SS truck early");
   Check(regionLog.Any(l=>l.IsDetail&&l.Message.Contains("widened the HEAVY_DUTY")),"regional widening has a detail log");
   // Only new placements are loaded. Both the saved and pending copies of the first three must count once.
   ObtainableTruckRandomizer.Randomize(regionRoot,PolicyCatalog(3,3),policyRegions,regionalOwned,progression,regionalTiers,difficulties,reporter,seed+30);
   var six=PendingTruckReplacements.LoadAssignments(regionRoot);
   Check(six.Count==6&&six.GroupBy(a=>a.Target.Code).All(g=>g.Count()==2),"regional usage survives incremental runs and balances unavoidable repeats");
   Check(firstThree.All(a=>six.Any(b=>a.PlacementKey==b.PlacementKey&&a.Target==b.Target&&a.CreatedAt==b.CreatedAt)),"variety never rerolls saved placements");
   Check(regionLog.Any(l=>l.IsDetail&&l.Message.Contains("regional reuse is necessary")),"unavoidable regional reuse explained in detail logs");
  }
  Console.WriteLine("PASS regional variety: three distinct HD trucks before repeats, balanced reuse across incremental runs, saved/pending counts, one-tier widening and no forced early SS across 24 seeds.");
  Console.WriteLine($"PASS HEAVY_DUTY exception: {totalOffroad}/1200 OFFROAD substitutions across 40 seeds; persisted cap, incremental ownership, source-class metadata, compatible tier filtering, one-way restriction and spoiler-free detail logs.");
  Console.WriteLine("PASS randomizer: owned/class/tier filters, required addon rejection and retry, smallest configured tires, diversity, exact preview/Apply, stable assignments and new ownership, full equipment reset and cancellation, spoiler-free logs, custom classes, legacy plans and original preservation.");
 }
}
