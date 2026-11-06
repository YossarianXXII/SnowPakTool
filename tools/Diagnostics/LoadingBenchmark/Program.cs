using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Linq.Expressions;

var workspace = Path.GetFullPath(args[0]);
var maps = Path.GetFullPath(args[1]);
var assembly = Assembly.Load("SnowPakTool.Desktop");
Type Type(string name) => assembly.GetType("SnowPakTool.Desktop." + name)!;
object? Call(string type, string method, params object?[] values) => Type(type).GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, values);
var timings = new Dictionary<string, double>();
var clock = Stopwatch.StartNew();
await (Task)Call("WorkspaceDataCache", "PreloadAsync", workspace, false)!;
timings["Workspace preload"] = clock.Elapsed.TotalMilliseconds;
var names = (IReadOnlyDictionary<string, string>)Call("WorkspaceDataCache", "GetLocalizedNames", workspace)!;
clock.Restart();
var trucks = (Dictionary<string, (string Name, string Class)>)Call("RegionTruckCatalogService", "LoadTrucks", workspace, names, new List<string>())!;
timings["Truck metadata"] = clock.Elapsed.TotalMilliseconds;
clock.Restart();
Call("RegionTruckCatalogService", "LoadTasks", workspace, names, new List<string>());
timings["Task links"] = clock.Elapsed.TotalMilliseconds;
clock.Restart();
var equipment = Activator.CreateInstance(Type("TruckEquipmentCatalog"), workspace, names, new List<string>())!;
timings["Equipment catalog"] = clock.Elapsed.TotalMilliseconds;
var defaultEquipment = Activator.CreateInstance(Type("MapTruckEquipment"), true, Array.Empty<string>(), "", "", "", "", "", "", "", null)!;
var resolve = Type("TruckEquipmentCatalog").GetMethod("Resolve")!;
var properties = Type("TruckEquipmentCatalog").GetMethod("ComparisonProperties")!;
clock.Restart();
foreach (var truck in trucks.Keys) resolve.Invoke(equipment, [truck, defaultEquipment]);
timings[$"Default equipment ({trucks.Count} trucks)"] = clock.Elapsed.TotalMilliseconds;
clock.Restart();
foreach (var truck in trucks.Keys) properties.Invoke(equipment, [truck]);
timings[$"Comparison options ({trucks.Count} trucks)"] = clock.Elapsed.TotalMilliseconds;
var readMap = Type("RegionTruckCatalogService").GetMethod("ReadMapData", BindingFlags.NonPublic | BindingFlags.Static)!;
var readPlacements = Type("RegionTruckCatalogService").GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
    .Single(method => method.Name == "ReadPlacements" && method.GetParameters()[1].ParameterType.Name.StartsWith("IReadOnlySet"));
var codes = trucks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
double archiveMs = 0, placementMs = 0;
foreach (var path in Directory.EnumerateFiles(maps, "level_*.pak"))
{
    clock.Restart(); var data = (byte[]?)readMap.Invoke(null, [path]); archiveMs += clock.Elapsed.TotalMilliseconds;
    if (data is null) continue;
    clock.Restart(); readPlacements.Invoke(null, [data, codes]); placementMs += clock.Elapsed.TotalMilliseconds;
}
timings["Map archive reads"] = archiveMs; timings["Placement scans"] = placementMs;
clock.Restart();
var catalog = Call("RegionTruckCatalogService", "Load", workspace, maps, names, new Dictionary<string, string>(), null)!;
timings["Regions-Trucks total"] = clock.Elapsed.TotalMilliseconds;
var entries = ((IEnumerable)catalog.GetType().GetProperty("Entries")!.GetValue(catalog)!).Cast<object>().ToArray();
var enabled = Array.CreateInstance(Type("CustomTruck"), trucks.Count);
var index = 0;
foreach (var (code, metadata) in trucks) enabled.SetValue(Activator.CreateInstance(Type("CustomTruck"), code, metadata.Name, metadata.Class), index++);
var listType = typeof(IReadOnlyList<>).MakeGenericType(Type("CustomTruck"));
var provider = Expression.Lambda(typeof(Func<>).MakeGenericType(listType), Expression.Convert(Expression.Constant(enabled), listType)).Compile();
var popupTimings = new TaskCompletionSource<double[]>();
var uiThread = new Thread(() =>
{
    try
    {
        Control.CheckForIllegalCrossThreadCalls = true;
        var times = new List<double>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var popup = (Form)Activator.CreateInstance(Type("TruckReplacementDialog"), workspace, entries[0], provider,
                catalog.GetType().GetProperty("EquipmentCatalog")!.GetValue(catalog))!;
            popup.StartPosition = FormStartPosition.Manual; popup.Location = new Point(-10000, -10000);
            var elapsed = Stopwatch.StartNew(); popup.Show();
            var grid = (DataGridView)popup.GetType().GetField("_comparison", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(popup)!;
            while (grid.Rows.Count == 0 && elapsed.Elapsed.TotalSeconds < 30) { Application.DoEvents(); Thread.Sleep(1); }
            if (grid.Rows.Count == 0) throw new Exception("Popup did not finish loading.");
            times.Add(elapsed.Elapsed.TotalMilliseconds); popup.Close(); Application.DoEvents();
        }
        popupTimings.SetResult(times.ToArray());
    }
    catch (Exception ex) { popupTimings.SetException(ex); }
});
uiThread.SetApartmentState(ApartmentState.STA); uiThread.Start();
var popupTimes = await popupTimings.Task;
timings[$"Popup first open ({trucks.Count} enabled trucks)"] = popupTimes[0];
timings[$"Popup reopened ({trucks.Count} enabled trucks)"] = popupTimes[1];
var result = new { TimingsMs = timings, Maps = catalog.GetType().GetProperty("MapsRead")!.GetValue(catalog), Placements = entries.Length,
    Warnings = ((IEnumerable)catalog.GetType().GetProperty("Warnings")!.GetValue(catalog)!).Cast<object>().ToArray(),
    Entries = entries.Select(entry => JsonSerializer.SerializeToElement(entry, entry.GetType())).ToArray() };
File.WriteAllText(args[2], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
foreach (var timing in timings) Console.WriteLine($"{timing.Key}: {timing.Value:N1} ms");
Console.WriteLine($"Maps: {result.Maps}; placements: {entries.Length}; warnings: {result.Warnings.Length}");
