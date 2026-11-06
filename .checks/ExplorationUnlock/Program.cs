using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

internal static class Program
{
    private static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    private static Type T(string type) => App.GetType("SnowPakTool.Desktop." + type)!;
    private static object? Static(string type, string name, params object?[] args) => T(type).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, args);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(target, args);
    private static object P(object obj, string name) => obj.GetType().GetProperty(name)!.GetValue(obj)!;
    private static object F(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    private static object Field(object target, string name, object value) { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value); return target; }
    private static IEnumerable<object> Items(object items) => ((IEnumerable)items).Cast<object>();
    private static Dictionary<string, decimal> Draft(object row) => (Dictionary<string, decimal>)P(row, "Draft");
    private static string Code(object row) => (string)P(P(row, "Truck"), "Code");
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static void Wait(Task task) { while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(2); } task.GetAwaiter().GetResult(); }
    private static object Entry(string code, string availability)
    {
        var ctor = T("RegionTruckEntry").GetConstructors().Single();
        var args = ctor.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : p.ParameterType == typeof(string) ? "fixture" : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        foreach (var p in ctor.GetParameters())
        {
            if (p.Name == "TruckCode") args[p.Position] = code;
            if (p.Name == "Availability") args[p.Position] = availability;
            if (p.Name == "OriginalTruckCode") args[p.Position] = "removed";
        }
        return ctor.Invoke(args);
    }
    private static object Catalog(string[] warnings, int maps, params object[] entries)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("RegionTruckEntry")))!;
        foreach (var entry in entries) list.Add(entry);
        return Activator.CreateInstance(T("RegionTruckCatalog"), list, warnings, maps, null)!;
    }
    [STAThread] private static void Main()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixture-" + Guid.NewGuid().ToString("N"));
        var files = Path.Combine(root, "initial.pak", "original_files", "[media]", "classes", "trucks"); Directory.CreateDirectory(files);
        foreach (var (code, flag) in new[] { ("onmap", " UnlockByExploration=\"false\""), ("removed", " UnlockByExploration=\"true\""), ("other", " UnlockByExploration=\"true\""), ("missing", ""), ("mission", " UnlockByExploration=\"false\"") })
            File.WriteAllText(Path.Combine(files, code + ".xml"), $"<_templates><GameData UnlockByExploration=\"false\"/></_templates><Truck><TruckData TruckType=\"SCOUT\" FuelCapacity=\"100\"/><GameData Country=\"US\" Price=\"1000\" UnlockByRank=\"20\"{flag}><UiDesc UiName=\"{code}\"/></GameData><Keep Value=\"untouched\"/></Truck>");
        var rows = Static("TruckPropertiesStore", "LoadRows", root)!;
        var byCode = Items(rows).ToDictionary(Code);
        var catalog = Catalog([], 1, Entry("onmap", "Obtainable — discovery/start"), Entry("missing", "Obtainable — task reward"), Entry("mission", "Mission/task only"));
        var plan = Static("TruckExplorationUnlockGenerator", "Generate", rows, catalog)!;
        var values = (Dictionary<string, decimal>)P(plan, "Values");
        decimal Value(string code) => values[(string)P(byCode[code], "RelativeFile")];
        Check(Value("onmap") == 1 && Value("missing") == 1 && Value("removed") == 0 && Value("other") == 0 && Value("mission") == 0, "current replacements and rewards require exploration; removed/store-only/mission trucks do not");
        Check(Items(P(plan, "Log")).Any(line => line.ToString()!.Contains("UNCHANGED")), "unchanged exploration results are explained");
        var empty = Static("TruckExplorationUnlockGenerator", "Generate", rows, Catalog([], 0))!;
        Check(((IDictionary)P(empty, "Values")).Count == 0 && Items(P(empty, "Log")).Any(line => line.ToString()!.Contains("no maps were read")), "missing map data preserves every flag and explains why");
        var partial = Static("TruckExplorationUnlockGenerator", "Generate", rows, Catalog(["map unreadable"], 1, Entry("onmap", "Obtainable — discovery/start")))!;
        Check(((IDictionary)P(partial, "Values")).Count == 1, "partial reads only update confirmed obtainable trucks");
        var uncertain = Static("TruckExplorationUnlockGenerator", "Generate", rows, Catalog([], 1, Entry("other", "Unverified")))!;
        Check(!((IDictionary)P(uncertain, "Values")).Contains(P(byCode["other"], "RelativeFile")), "unverified placements preserve exploration flags");
        var getter = Expression.Lambda(typeof(Func<>).MakeGenericType(typeof(Task<>).MakeGenericType(T("RegionTruckCatalog"))),
            Expression.Call(typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(T("RegionTruckCatalog")), Expression.Constant(catalog))).Compile();
        var context = Activator.CreateInstance(T("UnlockStarterContext"), "first", "First", "US", "Test saved order.")!;
        var starter = Expression.Lambda(typeof(Func<>).MakeGenericType(T("UnlockStarterContext")), Expression.Constant(context)).Compile();
        using var tab = (Control)Activator.CreateInstance(T("TruckPropertiesTab"), new Func<string>(() => root), getter, null, starter)!;
        tab.Size = new Size(1450, 850); tab.CreateControl(); Wait((Task)Call(tab, "EnsureLoadedAsync", false)!);
        Field(tab, "ConfirmExplorationUpdate", new Func<DialogResult>(() => DialogResult.Cancel));
        Wait((Task)Call(tab, "GenerateUnlockRanks")!);
        Check(Items(F(tab, "_rows")).All(row => Draft(row)["UnlockByRank"] == 20) && ((TextBox)F(tab, "_unlockLog")).Text.Contains("Cancelled"), "Cancel leaves ranks and flags untouched");
        Field(tab, "ConfirmExplorationUpdate", new Func<DialogResult>(() => DialogResult.No));
        Wait((Task)Call(tab, "GenerateUnlockRanks")!);
        Check(Items(F(tab, "_rows")).All(row => Draft(row)["UnlockByExploration"] == Draft(byCode[Code(row)])["UnlockByExploration"]), "No generates ranks without changing exploration flags");
        var ranksBeforeYes = Items(F(tab, "_rows")).ToDictionary(Code, row => Draft(row)["UnlockByRank"]);
        Field(tab, "ConfirmExplorationUpdate", new Func<DialogResult>(() => DialogResult.Yes));
        Wait((Task)Call(tab, "GenerateUnlockRanks")!);
        var uiRows = Items(F(tab, "_rows")).ToDictionary(Code);
        Check(uiRows.All(pair => Draft(pair.Value)["UnlockByExploration"] == Value(pair.Key)), "Yes previews exploration flags alongside ranks");
        Check(Draft(uiRows["onmap"])["UnlockByRank"] == ranksBeforeYes["onmap"] && Draft(uiRows["missing"])["UnlockByRank"] == ranksBeforeYes["missing"] && uiRows.Where(pair => pair.Key is "removed" or "other" or "mission").Any(pair => Draft(pair.Value)["UnlockByRank"] == 1), "rank-1 store starter respects the updated discovery requirements");
        var grid = (DataGridView)F(tab, "_grid");
        Check(grid.Columns["UnlockByExploration"]!.Visible && grid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells["UnlockByExploration"].FormattedValue?.ToString() == "true (false)"), "table shows true/false flags, highlights and original values");
        Check(!Directory.Exists(Path.Combine(root, "initial.pak", "updated")), "generation never writes game files");
        using (var bitmap = new Bitmap(tab.Width, tab.Height)) { tab.PerformLayout(); tab.DrawToBitmap(bitmap, new Rectangle(Point.Empty, tab.Size)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "exploration-preview.png")); }
        Call(tab, "Stage");
        var edits = Static("TruckPropertiesStore", "LoadPending", root)!;
        Check(Items(edits).Count(edit => (string)P(edit, "Attribute") == "UnlockByExploration") == 4, "staging includes exploration changes");
        var json = (string)Static("TruckPropertiesStore", "SerializeSnapshot", edits)!;
        var reloaded = Static("TruckPropertiesStore", "ReadPreset", json)!;
        Check(Items(reloaded).Count() == Items(edits).Count(), "presets round-trip exploration and numeric changes");
        Draft(uiRows["onmap"])["UnlockByExploration"] = 0;
        Call(tab, "RestoreDraft");
        Check(Draft(uiRows["onmap"])["UnlockByExploration"] == 1, "revert unstaged restores staged exploration flag");
        Static("TruckPropertiesStore", "Apply", root);
        var applied = Items(Static("TruckPropertiesStore", "LoadRows", root)!).ToDictionary(Code);
        Check(applied.All(pair => Draft(pair.Value)["UnlockByExploration"] == Value(pair.Key)), "Apply persists every boolean as true/false, including missing attributes");
        var current = (string)Static("WorkspaceOriginals", "GetCurrentPath", root, P(applied["missing"], "RelativeFile"))!;
        var text = File.ReadAllText(current);
        Check(text.Contains("UnlockByExploration=\"true\"") && text.Contains("<_templates><GameData UnlockByExploration=\"false\"/></_templates>") && text.Contains("<Keep Value=\"untouched\"/>"), "patch inserts the missing flag into the truck only and preserves other XML");
        Check(File.ReadAllText(Path.Combine(files, "onmap.xml")).Contains("UnlockByExploration=\"false\""), "original truck files remain unchanged");
        Wait((Task)Call(tab, "EnsureLoadedAsync", true)!);
        var reset = tab.Controls.Cast<Control>().SelectMany(Flatten).OfType<Button>().Single(button => button.Text == "Reset to original values"); reset.PerformClick();
        Check(Items(F(tab, "_rows")).All(row => Draft(row)["UnlockByExploration"] == Draft(byCode[Code(row)])["UnlockByExploration"]), "reset restores original exploration flags in preview");
        Call(tab, "Stage"); Static("TruckPropertiesStore", "Apply", root);
        Check(Items(Static("TruckPropertiesStore", "LoadRows", root)!).All(row => Draft(row)["UnlockByExploration"] == Draft(byCode[Code(row)])["UnlockByExploration"]), "reset, staging and Apply restore exploration booleans in files");
        Console.WriteLine("All exploration unlock checks passed; isolated fixtures only.");
    }
    private static IEnumerable<Control> Flatten(Control root) => new[] { root }.Concat(root.Controls.Cast<Control>().SelectMany(Flatten));
}
