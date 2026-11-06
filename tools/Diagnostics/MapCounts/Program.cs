using System.Collections;
using System.Reflection;
using System.Text.Json;
var app=Assembly.Load("SnowPakTool.Desktop");
Type T(string n)=>app.GetType("SnowPakTool.Desktop."+n)!;
object? Call(string type,string method,params object?[] args)=>T(type).GetMethod(method,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,args);
object Get(object o,string key)=>o.GetType().GetProperty(key)!.GetValue(o)!;
string S(object o,string key)=>(string)Get(o,key);
var workspace=@"S:\snowrunner_Shenanigans\AI\workspace";
var strings=Path.Combine(workspace,"initial.pak","original_files","[strings]","strings_english.str");
var names=(IReadOnlyDictionary<string,string>)Call("LocalizedStrings","Load",strings)!;
var maps=(string)Call("RegionTruckCatalogService","FindMapDirectory",@"S:\snowrunner_Shenanigans\AI\backup\initial.pak")!;
Console.WriteLine("Map directory: "+maps);
var store=Activator.CreateInstance(T("RegionTruckAvailabilityStore"),new object[]{Path.Combine(AppContext.BaseDirectory,"nonexistent-availability.json")});
var catalog=Call("RegionTruckCatalogService","Load",workspace,maps,names,new Dictionary<string,string>(),store)!;
var entries=((IEnumerable)Get(catalog,"Entries")).Cast<object>().ToArray();
var all=(IDictionary)Call("RegionTruckCatalogService","LoadTrucks",workspace,names,new List<string>())!;
var models=(Dictionary<string,(string,string)>)all;
var obtainable=entries.Where(e=>S(e,"Availability").StartsWith("Obtainable")).ToArray();
var mission=entries.Where(e=>S(e,"Availability")=="Mission/task only").ToArray();
var included=obtainable.Concat(mission).Select(e=>S(e,"BaselineTruckCode")).ToHashSet(StringComparer.OrdinalIgnoreCase);
var report=new {
MapsRead=Get(catalog,"MapsRead"),TotalPlacements=entries.Length,TotalTruckModels=models.Count,
Categories=entries.GroupBy(e=>S(e,"Availability")).Select(g=>new{Category=g.Key,Placements=g.Count(),UniqueModels=g.Select(e=>S(e,"BaselineTruckCode")).Distinct().Count()}),
CombinedUnique=included.Count,Missing=models.Where(p=>!included.Contains(p.Key)).Select(p=>new{Code=p.Key,Name=p.Value.Item1,Class=p.Value.Item2}),
ClassCounts=models.GroupBy(p=>p.Value.Item2).Select(g=>new{Class=g.Key,Models=g.Count(),Obtainable=obtainable.Count(e=>S(e,"OriginalClass")==g.Key),Missions=mission.Count(e=>S(e,"OriginalClass")==g.Key)}),
Warnings=Get(catalog,"Warnings")};
var json=JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"counts.json"),json);Console.WriteLine(json);
