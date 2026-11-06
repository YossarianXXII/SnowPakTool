using SnowPakTool.Desktop;
using System.Reflection;
using System.IO.Compression;
internal static class MissionRandomizerChecks
{
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 public static void Run()
 {
  var root=Path.Combine(AppContext.BaseDirectory,"missions-"+Guid.NewGuid().ToString("N"));
  var trucks=Path.Combine(root,"original_files","[media]","classes","trucks");var wheels=Path.Combine(root,"original_files","[media]","classes","wheels");
  var ps=Path.Combine(root,"original_files","[ps]");var maps=Path.Combine(root,"maps");var templates=Path.Combine(root,"original_files","[media]","_templates");
  foreach(var p in new[]{trucks,wheels,ps,maps,templates,Path.Combine(trucks,"addons")})Directory.CreateDirectory(p);
  File.WriteAllText(Path.Combine(templates,"trucks.xml"),"<_templates><Body><Mirror Mass=\"5\"/></Body></_templates>");
  string Xml(string mass,string cls,string extra="",string wheel="test_wheels")=>$"<_templates Include=\"trucks\"/><Truck><TruckData TruckType=\"{cls}\"><Wheels DefaultWheelType=\"{wheel}\" DefaultTire=\"highway_1\" DefaultRim=\"rim_1\"/></TruckData><PhysicsModel><Body Mass=\"{mass}\" ModelFrame=\"Chassis\">{extra}</Body></PhysicsModel><GameData><CompatibleWheels Type=\"{wheel}\" Scale=\"0.8\"/><CompatibleWheels Type=\"{wheel}\" Scale=\"0.5\"/></GameData></Truck>";
  File.WriteAllText(Path.Combine(trucks,"test_truck.xml"),Xml("5000","SCOUT").Replace("</GameData>","<AddonSockets DefaultAddon=\"frame_addon_sideboard_2\"><Socket Names=\"Bed\"/></AddonSockets></GameData>"));
  File.WriteAllText(Path.Combine(trucks,"addons","frame_addon_sideboard_2.xml"),"<TruckAddon><GameData Category=\"frame_addons\"><UiDesc UiName=\"Sideboard Bed\"/><InstallSocket Type=\"Bed\"/></GameData></TruckAddon>");
  File.WriteAllText(Path.Combine(trucks,"mission_light.xml"),Xml("2000","SCOUT","<Body _template=\"Mirror\" ModelFrame=\"Mirror\"/>"));
  File.WriteAllText(Path.Combine(trucks,"mission_medium.xml"),Xml("8000","HIGHWAY"));
  File.WriteAllText(Path.Combine(trucks,"mission_heavy.xml"),Xml("20000","HEAVY"));
  File.WriteAllText(Path.Combine(trucks,"mission_unknown.xml"),Xml("invalid","SCOUT"));
  File.WriteAllText(Path.Combine(trucks,"mission_parent.xml"),Xml("11000","OFFROAD"));
  File.WriteAllText(Path.Combine(trucks,"mission_child.xml"),"<_parent File=\"mission_parent\"/><Truck><TruckData TruckType=\"OFFROAD\"/></Truck>");
  File.WriteAllText(Path.Combine(trucks,"mission_highway.xml"),Xml("9000","OFFROAD",wheel:"highway_wheels"));
  File.WriteAllText(Path.Combine(wheels,"test_wheels.xml"),"<TruckWheels><TruckRims><TruckRim Name=\"rim_1\"/></TruckRims><TruckTires>"+string.Join("",new[]{"highway","allterrain","offroad","mudtires"}.Select(kind=>$"<TruckTire Name=\"{kind}_1\"><WheelFriction _template=\"{kind}\"/></TruckTire>"))+"</TruckTires></TruckWheels>");
  File.WriteAllText(Path.Combine(wheels,"highway_wheels.xml"),"<TruckWheels><TruckRims><TruckRim Name=\"rim_1\"/></TruckRims><TruckTires><TruckTire Name=\"highway_1\"><WheelFriction _template=\"highway\"/></TruckTire></TruckTires></TruckWheels>");
  Directory.CreateDirectory(Path.Combine(root,"original_files","[strings]"));File.WriteAllText(Path.Combine(root,"original_files","[strings]","strings_english.str"),"");
  var ids=Enumerable.Range(0,18).Select(i=>"MISSION"+i.ToString("D2")).Concat(new[]{"AWARD","UNKNOWN"}).ToArray();
  var bytes=(byte[])typeof(Program).GetMethod("BuildMap",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,new object[]{ids})!;
  var taskText=string.Join(" ",ids.Where(id=>id.StartsWith("MISSION")).Select(id=>$"{id} = {{ __type = \"TaskSettings\" stages = [ {{ truckDelivery = [ {{ truckId = \"{id}\" afterStageFinished = \"DESTROY\" }} ] }} ] }}"))+" AWARD_TASK = { __type = \"TaskSettings\" stages = [ { truckDelivery = [ { truckId = \"AWARD\" afterStageFinished = \"REWARD\" } ] } ] }";
  foreach(var region in new[]{"us_01","us_02","ru_02"})
  {
   using(var zip=ZipFile.Open(Path.Combine(maps,"level_"+region+"_01.pak"),ZipArchiveMode.Create))using(var stream=zip.CreateEntry("[scene]/data_for_ssl.bin").Open())stream.Write(bytes);
   File.WriteAllText(Path.Combine(ps,region+"_01_tasks.sso"),taskText);
  }
  var names=new Dictionary<string,string>();var availability=new RegionTruckAvailabilityStore(Path.Combine(root,"availability.json"));
  RegionTruckCatalog Load()=>RegionTruckCatalogService.Load(root,maps,names,new Dictionary<string,string>{{"us_01","First region"},{"us_02","Second region"},{"ru_02","Unsorted region"}},availability);
  var catalog=Load();var eq=catalog.EquipmentCatalog!;
  Check(eq.VehicleMassKg("mission_light")==2005&&eq.VehicleMassKg("mission_child")==11000&&eq.VehicleMassKg("mission_unknown") is null,"actual physics mass, shared body templates, parent inheritance and unknown mass");
  var original=catalog.Entries.First(e=>e.Availability=="Mission/task only");
  var missionValid=eq.TryMissionConfiguration("mission_heavy",0,out var free,out _);
  Check(!eq.TryRandomizationConfiguration(original,"mission_heavy",0,out _,out _)&&missionValid,"mission replacement ignores original required sideboard addon");
  Check(!free.Addons.Contains("frame_addon_sideboard_2")&&eq.IsConfigurationSupported("mission_heavy",free),"replacement default equipment valid without original addon");
  var fleet=new CustomTruck[]{new("mission_light","Hidden light","Scout"),new("mission_medium","Hidden medium","Highway"),new("mission_heavy","Hidden heavy","Heavy"),new("mission_unknown","Hidden unknown","Scout")};
  var tiers=new GroupState[]{new(){Name="D",TruckCodes=["us_01"]},new(){Name="S",TruckCodes=["us_02"]}};
  var owned=new HashSet<string>{"us_01","ru_02"};var logs=new List<RandomizationLogEntry>();var reporter=new RandomizationLogProgress(logs.Add);
  void Run(RegionTruckCatalog c,int seed=10)=>MissionTruckRandomizer.Randomize(root,c,owned,fleet,tiers,MissionTruckDifficulty.Hard,reporter,seed);
  Run(catalog);var assignments=PendingTruckReplacements.LoadAssignments(root).ToArray();var pending=PendingTruckReplacements.Load(root);
  Check(assignments.Length==18&&assignments.All(a=>a.MissionOnly&&a.RegionCode=="us_01"),"only owned, ranked-region mission placements are queued; unranked truck models remain eligible");
  Check(pending.All(p=>!p.Staged)&&PendingTruckReplacements.ApplyAll(root).Count==0,"mission rolls are drafts and Apply does not write unstaged plans");
  Check(assignments.GroupBy(a=>a.Target.Code).Count()==3&&assignments.GroupBy(a=>a.Target.Code).All(g=>g.Count()==6),"least-used regional selection balances repeats");
  Check(logs.Any(l=>l.Message.Contains("Unsorted region")&&l.Message.Contains("unchanged"))&&!logs.Any(l=>l.Message.Contains("Hidden")||l.Message.Contains("mission_light")),"unranked region logged and replacement identities hidden");
  var saved=File.ReadAllText(Path.Combine(root,"region-maps","pending-replacements.json"));Run(catalog,999);Check(File.ReadAllText(Path.Combine(root,"region-maps","pending-replacements.json"))==saved,"existing mission drafts and settings preserved byte for byte");
  Check(!TruckReplacementStore.SavedMaps(root).Any(),"randomization does not write map files");
  PendingTruckReplacements.Stage(root);var written=PendingTruckReplacements.ApplyAll(root);Check(written.Count==1&&PendingTruckReplacements.Load(root).Count==0,"staged mission changes apply");
  var applied=Load();
  Check(applied.Entries.Where(e=>e.RegionCode=="us_01"&&e.Availability=="Mission/task only").All(e=>assignments.Any(a=>a.PlacementKey==e.StablePlacementKey&&a.Target.Code==e.TruckCode&&PendingTruckReplacements.EquipmentEqual(a.Configuration,e.RawEquipment))),"Map trucks and Apply have exact chosen model, tires and defaults");
  Check(applied.Entries.Where(e=>e.RegionCode=="us_01"&&e.PlacementId is "AWARD" or "UNKNOWN").All(e=>e.TruckCode=="test_truck"),"obtainable and unverified placements remain untouched");
  Run(applied);Check(PendingTruckReplacements.Load(root).Count==0,"applied mission assignments do not reroll");
  owned.Add("us_02");Run(applied,700);Check(PendingTruckReplacements.Load(root).Count==18&&PendingTruckReplacements.Load(root).All(p=>p.Original.RegionCode=="us_02"),"new ownership only processes newly eligible mission placements");
  PendingTruckReplacements.Reset(root,applied.Entries.Where(e=>e.RegionCode=="us_01").ToArray());PendingTruckReplacements.Stage(root);PendingTruckReplacements.ApplyAll(root);
  Check(RegionTruckCatalogService.ReadMapData(written.Single().OutputPath)!.SequenceEqual(bytes),"reset restores exact original mission scene");
  Check(Directory.EnumerateFiles(ps).All(p=>File.ReadAllText(p)==taskText),"mission reward/removal scripts unchanged");
  foreach(var map in Directory.EnumerateFiles(maps,"*.pak"))Check(RegionTruckCatalogService.ReadMapData(map)!.SequenceEqual(bytes),"source archives unchanged");
  var codes=new[]{"mission_light","mission_medium","mission_heavy"};var means=new List<double>();
  foreach(var mode in Enum.GetValues<MissionTruckDifficulty>())
  {
   var total=0d;var seen=new HashSet<string>();
   for(var seed=0;seed<120;seed++)
   {
    var sampleRoot=Path.Combine(root,"bias",mode.ToString(),seed.ToString());
    MissionTruckRandomizer.Randomize(sampleRoot,catalog with{Entries=[original]},new HashSet<string>{original.RegionCode},fleet,
     [new(){Name="D",TruckCodes=[original.RegionCode]}],mode,new RandomizationLogProgress(_=>{}),seed);
    var choice=PendingTruckReplacements.LoadAssignments(sampleRoot).Single().Target.Code;seen.Add(choice);total+=Array.IndexOf(codes,choice)/2d;
   }
   means.Add(total/120);Check(seen.Count==3,"every weight remains possible on "+mode);
  }
  Check(means[0]>0.2&&means[0]<0.45&&means[1]>0.35&&means[1]<0.65&&means[2]>0.55&&means[2]<0.8,"broader weight mix retains ordered Easy/Moderate/Hard biases without dominating extremes");
  Check(MissionTruckRandomizer.Weight(1,MissionTruckDifficulty.Hard)/MissionTruckRandomizer.Weight(0,MissionTruckDifficulty.Hard)==3
   &&MissionTruckRandomizer.Weight(0,MissionTruckDifficulty.Easy)/MissionTruckRandomizer.Weight(1,MissionTruckDifficulty.Easy)==3,"weight bias capped at 3:1");
  var allOwned=new HashSet<string>{"us_01","us_02","ru_02"};var allTiers=new[]{new GroupState{Name="D",TruckCodes=["us_01","us_02","ru_02"]}};
  var firstByRegion=new[]{"us_01","us_02","ru_02"}.Select(code=>catalog.Entries.First(e=>e.RegionCode==code&&e.Availability=="Mission/task only")).ToArray();
  for(var seed=0;seed<24;seed++)
  {
   var across=Path.Combine(root,"global-variety",seed.ToString());var globalLog=new List<RandomizationLogEntry>();
   // Restart the function with only the next region loaded: usage must come from saved state across runs.
   foreach(var placement in firstByRegion)
    MissionTruckRandomizer.Randomize(across,catalog with{Entries=[placement]},allOwned,fleet,allTiers,MissionTruckDifficulty.Hard,new RandomizationLogProgress(globalLog.Add),seed);
   var firstThree=PendingTruckReplacements.LoadAssignments(across);
   Check(firstThree.Count==3&&firstThree.Select(a=>a.Target.Code).Distinct().Count()==3,"three regions consume three different models before global reuse");
   Check(globalLog.Count(l=>l.IsDetail&&l.Message.Contains("not yet used in saved assignments or drafts across all regions"))==3,"global novelty details reflect saved usage");
   // Model a committed first assignment: its ledger remains, while only the other assignments have drafts.
   var first=firstThree.Single(a=>a.RegionCode=="us_01");var path=Path.Combine(across,"region-maps","pending-replacements.json");
   var state=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
   var pendingNodes=state["Pending"]!.AsArray();
   for(var i=pendingNodes.Count-1;i>=0;i--)if(pendingNodes[i]!["Original"]!["RegionCode"]!.GetValue<string>()=="us_01")pendingNodes.RemoveAt(i);
   File.WriteAllText(path,state.ToJsonString());
   var next=catalog.Entries.First(e=>e.RegionCode=="us_01"&&e.Availability=="Mission/task only"&&e.StablePlacementKey!=firstByRegion[0].StablePlacementKey);
   MissionTruckRandomizer.Randomize(across,catalog with{Entries=[next]},allOwned,fleet,allTiers,MissionTruckDifficulty.Hard,new RandomizationLogProgress(globalLog.Add),seed+100);
   var fourth=PendingTruckReplacements.LoadAssignments(across).Single(a=>a.PlacementKey==next.StablePlacementKey);
   Check(fourth.Target.Code!=first.Target.Code,"assignment/draft deduplication leaves regional variety as tie-breaker");
   Check(globalLog.Any(l=>l.IsDetail&&l.Message.Contains("Global reuse was necessary")),"globally balanced reuse explained in detailed logs");
   var manual=Path.Combine(root,"manual-global",seed.ToString());
   PendingTruckReplacements.Queue(manual,firstByRegion[1],fleet.Single(t=>t.Code=="mission_heavy"));
   MissionTruckRandomizer.Randomize(manual,catalog with{Entries=[firstByRegion[0]]},allOwned,fleet,allTiers,MissionTruckDifficulty.Hard,new RandomizationLogProgress(_=>{}),seed);
   Check(PendingTruckReplacements.LoadAssignments(manual).Single().Target.Code!="mission_heavy","manual draft in another region also prevents avoidable global reuse");
  }
  Console.WriteLine("PASS global mission variety: 24 seeds, three different models across three incremental regions, least-used global balance, regional tie-breaks, applied/draft deduplication and manual-draft usage.");
  var fallbackRoot=Path.Combine(root,"fallback");var fallbackLog=new List<RandomizationLogEntry>();
  MissionTruckRandomizer.Randomize(fallbackRoot,catalog with{Entries=[original]},new HashSet<string>{original.RegionCode},[new("mission_highway","Hidden tires","Offroad")],
   [new(){Name="S",TruckCodes=[original.RegionCode]}],MissionTruckDifficulty.Moderate,new RandomizationLogProgress(fallbackLog.Add),10);
  Check(fallbackLog.Any(l=>l.IsDetail&&l.Message.Contains("→ Highway"))&&fallbackLog.FindIndex(l=>l.IsDetail&&l.Message.Contains("→ Highway"))<fallbackLog.FindIndex(l=>!l.IsDetail&&l.Message.Contains("complete")),"tire fallback is detailed and precedes completion");
  var tireRandom=new Random(200);Check(Enumerable.Range(0,1000).Select(_=>MissionTruckRandomizer.TireCategory(0,tireRandom)).All(c=>c is 0 or 1),"easy region tires vary only into neighbor category");
  Check(Enumerable.Range(0,1000).Select(_=>MissionTruckRandomizer.TireCategory(4,tireRandom)).All(c=>c is 2 or 3),"hard region tires vary only into neighbor category");
  Console.WriteLine($"PASS missions: mass/templates/inheritance, owned regions, free classes/addons, variety, draft/Stage/Apply/reset, stable/new ownership, task/source preservation, spoiler-free ordered tire logs. Mean weight percentiles Easy/Moderate/Hard: {string.Join(" / ",means.Select(m=>m.ToString("0.00")))}.");
 }
}
