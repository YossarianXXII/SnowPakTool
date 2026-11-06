using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

internal static class Program
{
    private static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    private static Type T(string name) => App.GetType("SnowPakTool.Desktop." + name)!;
    private static object? Call(object obj, string method, params object[] args) => obj.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(obj, args);
    private static object Field(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static object Prepare(string workspace) => T("OwnedContentTab").GetMethod("PrepareWorkspaceData", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { workspace, new Dictionary<string, string>() })!;
    private static string[] BaseFiles(string workspace) { var prepared = Prepare(workspace); return (string[])prepared.GetType().GetProperty("BaseTruckFiles")!.GetValue(prepared)!; }
    private static void Put(string workspace, string relative, string text)
    {
        var path = Path.Combine(workspace, "initial.pak", "original_files", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text);
    }
    [STAThread] private static void Main(string[] args)
    {
        var workspace = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        const string civilian = "[media]/_dlc/dlc_6/classes/trucks/ank_mk38_ht.xml";
        const string xml = "<Truck><TruckData TruckType=\"OFFROAD\"/><GameData><UiDesc UiName=\"TEST_ANK_CIVILIAN\"/></GameData></Truck>";
        Put(workspace, "[media]/classes/trucks/ank_mk38.xml", xml);
        Put(workspace, "[media]/_dlc/dlc_6/classes/trucks/unlisted_paid.xml", xml);
        Check(BaseFiles(workspace).Select(Path.GetFileNameWithoutExtension).SequenceEqual(new[] { "ank_mk38" }), "missing Civilian does not create a phantom tile or unlock unlisted DLC trucks");
        Put(workspace, civilian, xml);
        var files = BaseFiles(workspace).Select(Path.GetFileNameWithoutExtension).ToArray();
        Check(files.Contains("ank_mk38") && files.Contains("ank_mk38_ht") && files.Length == 2, "both ANKs are included independently; unrelated DLC remains excluded");
        Put(workspace, "[ps]/dlc_library.sso", "");
        Put(workspace, "[ps]/zone_desc_storage.sso", "");
        Put(workspace, "[strings]/strings_english.str", "TEST_ANK_CIVILIAN \"ANK MK38 Civilian\"\n");
        using var owned = (Control)Activator.CreateInstance(T("OwnedContentTab"), new Func<string>(() => workspace), Activator.CreateInstance(T("OwnedRegionState"))!)!;
        Call(owned, "EnsureLoaded");
        var trucks = Call(owned, "GetAvailableCustomGroupingTrucks")!;
        var codes = ((IEnumerable)trucks).Cast<object>().Select(truck => (string)truck.GetType().GetProperty("Code")!.GetValue(truck)!).ToArray();
        Check(codes.Contains("ank_mk38_ht") && !codes.Contains("unlisted_paid"), "shared grouping catalog exposes Civilian without owning a DLC");
        Check(((IEnumerable)Call(owned, "GetOwnedTrucks")!).Cast<object>().Any(truck => (string)truck.GetType().GetProperty("Code")!.GetValue(truck)! == "ank_mk38_ht"), "Civilian is also available to consumers of the owned-truck catalog");
        var resultType = typeof(IReadOnlyList<>).MakeGenericType(T("CustomTruck"));
        var getter = Expression.Lambda(typeof(Func<>).MakeGenericType(resultType), Expression.Constant(trucks, resultType)).Compile();
        using var tiers = (Control)Activator.CreateInstance(T("CustomGroupWipTab"), getter, true, false)!;
        var unsorted = (Control)Field(tiers, "_unsortedTiles");
        Check(unsorted.Controls.Cast<Control>().Any(tile => Convert.ToString(tile.Tag) == "ank_mk38_ht"), "ANK Civilian appears in the actual tier-list Unsorted pane");
        var duplicateWorkspace = workspace + "-duplicate";
        Put(duplicateWorkspace, "[media]/classes/trucks/ank_mk38_ht.xml", xml);
        Put(duplicateWorkspace, civilian, xml);
        Check(BaseFiles(duplicateWorkspace).Count(path => Path.GetFileNameWithoutExtension(path) == "ank_mk38_ht") == 1, "base-folder Civilian is not duplicated");
        if (args.Length > 0)
        {
            Check(BaseFiles(args[0]).Count(path => Path.GetFileNameWithoutExtension(path) == "ank_mk38_ht") == 1, "real workspace includes exactly one ANK Civilian in its base catalog");
        }
        Console.WriteLine("All ANK Civilian checks passed; no user rankings or game data were modified.");
    }
}
