using System.Collections;
using System.Data;
using System.Reflection;
using System.Text.Json;

static class Program
{
    static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    static Type T(string name) => App.GetType("SnowPakTool.Desktop." + name)!;
    static object F(object o, string name) => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    static object? Call(object o, string name, params object?[] args)
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        return o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(o, args);
    }
    static object? Static(string type, string name, params object?[] args) => T(type).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args);
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static void Pump(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); }
        Check(task.IsCompleted, "async operation completes"); task.GetAwaiter().GetResult();
    }
    static bool HasPak = true;
    static readonly List<object> Rows = [];
    public static IReadOnlyList<TChange> Pending<TChange>(string workspace) => HasPak ? Rows.Cast<TChange>().ToArray() : [];
    static object[] Items(object data) => ((IEnumerable)data).Cast<object>().ToArray();

    [STAThread] static void Main()
    {
        Control.CheckForIllegalCrossThreadCalls = true;
        var root = Path.Combine(AppContext.BaseDirectory, "fixture-" + Guid.NewGuid().ToString("N"));
        var trucks = Path.Combine(root, "original_files", "[media]", "classes", "trucks");
        Directory.CreateDirectory(trucks); Directory.CreateDirectory(Path.Combine(root, "original_files", "[strings]"));
        var source = Path.Combine(trucks, "test.xml");
        const string original = "<Truck><TruckData FuelCapacity=\"100\"/><GameData Price=\"100\"/></Truck>";
        File.WriteAllText(source, original); File.WriteAllText(Path.Combine(root, "original_files", "[strings]", "strings_english.str"), "");
        var planPath = Path.Combine(root, "region-maps", "pending-replacements.json"); Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
        object Entry(string key, string map, bool staged) => new { Key = key, Original = new { TruckCode = "test", Truck = "Test truck", MapCode = map,
            RegionCode = map.Contains("us_01") ? "us_01" : "us_02", PlacementId = key, Class = "SCOUT" }, Target = new { Code = "test", Name = "Secret replacement", Class = "SCOUT" }, QueuedAt = DateTimeOffset.UtcNow, Staged = staged };
        void Seed() => File.WriteAllText(planPath, JsonSerializer.Serialize(new { Pending = new[] { Entry("one", "level_us_01_01", true), Entry("two", "level_us_01_02", true), Entry("draft", "level_us_02_01", false) }, Assignments = Array.Empty<object>() }));
        Seed();
        foreach (var property in new[] { "FuelCapacity", "Price" }) Rows.Add(Activator.CreateInstance(T("WorkspaceChange"), "Pending", "[media]/classes/trucks/test.xml", "Test truck / " + property, "100", "150", "", "", "")!);
        var changeType = T("WorkspaceChange");
        var pending = typeof(Program).GetMethod(nameof(Pending))!.MakeGenericMethod(changeType).CreateDelegate(typeof(Func<,>).MakeGenericType(typeof(string), typeof(IReadOnlyList<>).MakeGenericType(changeType)));
        var saved = 0; var mapCalls = 0; var failPak = false; var failMaps = false;
        Action save = () => { if (failPak) throw new InvalidDataException("PAK conflict"); saved++; HasPak = false;
            var updated = Path.Combine(root, "updated", "[media]", "classes", "trucks"); Directory.CreateDirectory(updated); File.WriteAllText(Path.Combine(updated, "test.xml"), original.Replace("100", "150")); };
        Func<Task> maps = () => { mapCalls++; if (failMaps) throw new InvalidDataException("Map conflict");
            Static("PendingTruckReplacements", "DiscardMany", root, new[] { "one", "two" }, true); return Task.CompletedTask; };
        using var host = new Form { Size = new Size(1100, 850) };
        using var panel = (Control)Activator.CreateInstance(T("StartChangesPanel"), new object[] { new Func<string>(() => root), new Button { Text = "Pack to new PAK…" }, pending, save, maps, new Func<Task>(() => Task.CompletedTask) })!;
        panel.Dock = DockStyle.Fill; host.Controls.Add(panel); host.Show();
        var count = 0;
        T("StartChangesPanel").GetEvent("PendingCountChanged")!.AddEventHandler(panel, new Action<int>(value => count = value));
        Pump((Task)Call(panel, "RefreshAsync")!);
        var grid = (DataGridView)F(panel, "_changes"); var apply = (Button)F(panel, "_applyButton");
        var data = (DataTable)grid.DataSource!;
        Check(data.Rows.Count == 2 && data.Rows.Cast<DataRow>().Select(r => (string)r["Area"]).Distinct().Count() == 2, "one table groups both initial PAK and multi-map region changes");
        Check(count == 4, "pending badge counts underlying edits rather than summary rows");
        Check(data.Rows.Cast<DataRow>().Any(r => ((string)r["Change"]).Contains("2 change(s) across 2 map(s)")), "regional summary retains map counts");
        Check(!data.Rows.Cast<DataRow>().Any(r => ((string)r["Change"]).Contains("Secret")), "map summaries do not reveal replacement identities");
        grid.CurrentCell = grid.Rows[0].Cells["File / target"];
        Check(!((Button)F(panel, "_discardMap")).Enabled, "PAK row cannot trigger map discard");
        var headers = Static("TableHeaderBehavior", "Attach", grid, null, null, null)!;
        var filter = Call(headers, "AddFilter", "Area", null, Array.Empty<string>())!;
        Call(filter, "SelectValues", new object[] { new[] { "Initial PAK" } }); Application.DoEvents();
        Check(grid.Rows.Count == 1, "shared table can filter to one file area");
        Check(grid.Rows.Cast<DataGridViewRow>().Where(r => (string)r.Cells["State"].Value == "Pending")
            .All(r => r.DefaultCellStyle.BackColor == (Color)Static("AppColors", "Get", "EngineMatch")!), "pending colors survive shared header filtering");
        Pump((Task)Call(panel, "ApplyPendingAsync")!);
        Check(saved == 1 && mapCalls == 1, "one Apply handles PAK and maps even when map rows are filtered out");
        Check(Items(Static("PendingTruckReplacements", "Load", root)!).Length == 1 && Items(Static("PendingTruckReplacements", "LoadStaged", root)!).Length == 0, "unstaged map draft remains untouched");
        Check(File.ReadAllText(source) == original, "preserved source file is unchanged");
        Check(!apply.Enabled && ((Label)F(panel, "_status")).Text.Contains("manually"), "Apply disables after completion and explains manual installation");

        Seed(); HasPak = true; failMaps = true;
        Pump((Task)Call(panel, "ApplyPendingAsync")!);
        Check(!HasPak && saved == 2 && Items(Static("PendingTruckReplacements", "LoadStaged", root)!).Length == 2, "map failure preserves completed PAK outputs and staged maps for retry");
        Check(apply.Enabled && ((Label)F(panel, "_status")).Text.Contains("Map conflict"), "failure is visible and retry remains available");
        failMaps = false; Pump((Task)Call(panel, "ApplyPendingAsync")!);
        Check(saved == 2 && Items(Static("PendingTruckReplacements", "LoadStaged", root)!).Length == 0, "retry applies remaining maps without repeating PAK saves");
        Seed(); HasPak = true; failPak = true; var previousMapCalls = mapCalls;
        Pump((Task)Call(panel, "ApplyPendingAsync")!);
        Check(mapCalls == previousMapCalls && HasPak, "PAK failure stops the map operation and retains staged work");
        failPak = false; Pump((Task)Call(panel, "RefreshAsync")!);
        data = (DataTable)grid.DataSource!; Call(filter, "SelectValues", new object[] { new[] { "Initial PAK", "Maps" } }); Application.DoEvents();
        using var bitmap = new Bitmap(host.Width, host.Height); host.DrawToBitmap(bitmap, new Rectangle(Point.Empty, host.Size)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "unified-changes.png"));
        Console.WriteLine("Fixture: " + root);
    }
}
