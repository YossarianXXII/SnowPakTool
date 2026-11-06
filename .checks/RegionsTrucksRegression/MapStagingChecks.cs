using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SnowPakTool.Desktop;

internal static class MapStagingChecks
{
    // Existing writer regressions explicitly stage before applying, matching the UI workflow.
    public static IReadOnlyList<SavedTruckReplacement> Apply(string workspace)
    {
        PendingTruckReplacements.Stage(workspace);
        return PendingTruckReplacements.ApplyAll(workspace);
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Call(object value, string name) => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(value, null);
    private static void Pump(Func<bool> finished)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!finished() && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
        if (!finished()) throw new TimeoutException("Map staging UI did not finish.");
    }
    private static void Wait(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    public static void Run()
    {
        Exception? failure = null;
        using var host = new Form { Opacity = 0, ShowInTaskbar = false };
        host.Shown += (_, _) =>
        {
            try { RunCore(); }
            catch (Exception ex) { failure = ex; }
            finally { host.Close(); }
        };
        Application.Run(host);
        if (failure is not null) throw new InvalidOperationException("Map staging regression failed.", failure);
    }
    private static void RunCore()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "map-staging-" + Guid.NewGuid().ToString("N"));
        var classes = Path.Combine(root, "original_files", "[media]", "classes");
        var maps = Path.Combine(root, "maps"); var strings = Path.Combine(root, "original_files", "[strings]");
        foreach (var path in new[] { Path.Combine(classes, "trucks"), Path.Combine(classes, "wheels"), maps, strings }) Directory.CreateDirectory(path);
        var names = new Dictionary<string, string> { ["ORIGINAL"] = "Original truck", ["SECOND"] = "Second truck", ["THIRD"] = "Third truck" };
        File.WriteAllText(Path.Combine(strings, "strings_english.str"), string.Join("\n", names.Select(pair => $"{pair.Key} \"{pair.Value}\"")));
        foreach (var (code, name) in new[] { ("test_truck", "ORIGINAL"), ("second_truck", "SECOND"), ("third_truck_long_name", "THIRD") })
            File.WriteAllText(Path.Combine(classes, "trucks", code + ".xml"), $"<Truck><TruckData TruckType=\"SCOUT\"><Wheels DefaultWheelType=\"test_wheels\" DefaultTire=\"highway_1\" DefaultRim=\"rim_1\"/></TruckData><GameData><UiDesc UiName=\"{name}\"/><CompatibleWheels Type=\"test_wheels\" Scale=\"0.8\"/></GameData></Truck>");
        File.WriteAllText(Path.Combine(classes, "wheels", "test_wheels.xml"), "<TruckWheels Radius=\"1\"><TruckRims><TruckRim Name=\"rim_1\"/></TruckRims><TruckTires><TruckTire Name=\"highway_1\"><WheelFriction _template=\"highway\"/></TruckTire></TruckTires></TruckWheels>");
        var data = (byte[])typeof(Program).GetMethod("BuildMap", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { new[] { "ONE", "TWO", "THREE" } })!;
        foreach (var map in new[] { "level_us_01_01", "level_us_02_01" })
            using (var zip = ZipFile.Open(Path.Combine(maps, map + ".pak"), ZipArchiveMode.Create))
            using (var output = zip.CreateEntry("[scene]/data_for_ssl.bin").Open()) output.Write(data);
        var inputs = Directory.GetFiles(maps).ToDictionary(path => path, File.ReadAllBytes);
        var availability = new RegionTruckAvailabilityStore(Path.Combine(root, "availability.json"));
        RegionTruckCatalog Load(bool preview = true) => RegionTruckCatalogService.Load(root, maps, names, new Dictionary<string, string>(), availability, preview);
        RegionTruckEntry Entry(string map, string id, bool preview = true) => Load(preview).Entries.Single(entry => entry.MapCode == map && entry.PlacementId == id);
        const string firstMap = "level_us_01_01", secondMap = "level_us_02_01";
        var second = new CustomTruck("second_truck", "Second truck", "SCOUT"); var third = new CustomTruck("third_truck_long_name", "Third truck", "SCOUT");
        var initial = Entry(firstMap, "ONE");
        PendingTruckReplacements.Queue(root, initial, second);
        Check(PendingTruckReplacements.HasUnstagedChanges(root) && PendingTruckReplacements.LoadStaged(root).Count == 0, "manual replacements begin as persisted drafts");
        Check(WorkspaceChangeAudit.MapFiles(root).All(row => row.State != WorkspaceChange.Pending) && PendingTruckReplacements.ApplyAll(root).Count == 0 && !TruckReplacementStore.SavedMaps(root).Any(), "Start ignores drafts and Apply never writes unstaged changes");
        Check(Entry(firstMap, "ONE").TruckCode == second.Code, "draft still appears in shared map preview");
        PendingTruckReplacements.Stage(root);
        Check(PendingTruckReplacements.LoadStaged(root).Count == 1 && !PendingTruckReplacements.HasUnstagedChanges(root)
            && WorkspaceChangeAudit.MapFiles(root).Count(row => row.State == WorkspaceChange.Pending) == 1, "explicit Stage persists and exposes the shared plan on Start");
        PendingTruckReplacements.Queue(root, Entry(firstMap, "ONE"), third);
        Check(PendingTruckReplacements.LoadStaged(root).Count == 0 && PendingTruckReplacements.HasUnstagedChanges(root), "editing a staged placement returns that placement to draft");
        PendingTruckReplacements.Stage(root);
        PendingTruckReplacements.Queue(root, Entry(firstMap, "TWO"), second);
        Check(PendingTruckReplacements.ApplyAll(root).Count == 1, "Apply writes staged placements while excluding a later draft in the same map");
        var raw = Load(false);
        Check(raw.Entries.Single(entry => entry.MapCode == firstMap && entry.PlacementId == "ONE").TruckCode == third.Code
            && raw.Entries.Single(entry => entry.MapCode == firstMap && entry.PlacementId == "TWO").TruckCode == "test_truck", "only staged truck is present in written scene data");
        var retained = PendingTruckReplacements.Load(root).Single();
        var rawTwo = raw.Entries.Single(entry => entry.MapCode == firstMap && entry.PlacementId == "TWO");
        Check(!retained.Staged && retained.Original.BinaryOffset == rawTwo.BinaryOffset && retained.Original.MapDataHash == rawTwo.MapDataHash
            && Entry(firstMap, "TWO").TruckCode == second.Code, "later draft survives changed scene offsets/hash and remains visible");
        Apply(root);
        Check(Entry(firstMap, "TWO", false).TruckCode == second.Code && PendingTruckReplacements.Load(root).Count == 0, "retained draft can be staged and applied later");
        var randomEntry = Entry(secondMap, "ONE"); var equipment = Load().EquipmentCatalog!;
        Check(equipment.TryRandomizationConfiguration(randomEntry, second.Code, 0, out var configured, out _), "randomized fixture has compatible equipment");
        PendingTruckReplacements.QueueRandomization(root, [(randomEntry, second, configured)], 123);
        Check(PendingTruckReplacements.Load(root).Single() is { Randomized: true, Staged: false } && PendingTruckReplacements.LoadStaged(root).Count == 0, "randomized replacements also begin as drafts");
        Apply(root);
        Check(PendingTruckReplacements.LoadAssignments(root).Count == 1, "staged randomization keeps the persistent assignment ledger");
        using var tab = new RegionsTrucksTab(() => root, () => "", availability) { BindingContext = new BindingContext(), Size = new Size(1150, 850) };
        Field<TextBox>(tab, "_mapFolder").Text = maps; tab.CreateControl(); Wait(tab.EnsureLoadedAsync());
        Field<HashSet<string>>(tab, "_selectedRegions").Clear(); Call(tab, "ApplyFilter");
        Check(Field<DataGridView>(tab, "_trucks").Rows.Count == 0 && Field<Button>(tab, "_resetAll").Enabled, "Reset all is available with no regions selected and no visible rows");
        Field<Button>(tab, "_resetAll").PerformClick(); Pump(() => !Field<bool>(tab, "_editingPlan"));
        Check(PendingTruckReplacements.Load(root).Count == 3 && PendingTruckReplacements.Load(root).All(change => change.ResetOriginal && !change.Staged)
            && PendingTruckReplacements.LoadAssignments(root).Count == 0, "Reset all creates restoration drafts across all maps and clears assignments");
        Check(Load().Entries.All(entry => !entry.TruckChanged) && Load(false).Entries.Count(entry => entry.TruckChanged) == 3, "Reset all previews originals without writing maps");
        Field<HashSet<string>>(tab, "_selectedRegions").UnionWith(new[] { "us_01", "us_02" }); Call(tab, "ApplyFilter");
        Check(Field<DataGridView>(tab, "_trucks").Rows.Cast<DataGridViewRow>().Count(row => Convert.ToString(row.Cells["Change state"].Value) == "Draft") == 3, "map table distinguishes draft restorations");
        var stageEvents = 0; tab.ChangesStaged += () => stageEvents++;
        using (var randomizer = new TruckRandomizationTab())
        {
            randomizer.Size = new Size(1150, 850); randomizer.CreateControl(); randomizer.PerformLayout();
            randomizer.ConfigureMapStaging(tab.StageChangesAsync, () => PendingTruckReplacements.HasUnstagedChanges(root));
            var button = Field<Button>(randomizer, "_stageChanges");
            Check(button.Text == "Stage changes" && button.Enabled, "randomization tab offers the shared Stage changes action");
            using (var bitmap = new Bitmap(randomizer.Width, randomizer.Height)) { randomizer.DrawToBitmap(bitmap, new Rectangle(Point.Empty, randomizer.Size)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "randomizer-staging-preview.png")); }
            button.PerformClick(); Pump(() => stageEvents == 1 && !Field<bool>(tab, "_editingPlan"));
            Check(PendingTruckReplacements.LoadStaged(root).Count == 3 && stageEvents == 1, "randomizer staging stages the Map trucks draft too");
        }
        PendingTruckReplacements.Queue(root, Entry(firstMap, "ONE"), second);
        Wait(tab.RefreshChangesAsync());
        Check(Field<Button>(tab, "_stageChanges").Enabled, "Map trucks Stage changes enables after another draft edit");
        Field<Button>(tab, "_stageChanges").PerformClick(); Pump(() => stageEvents == 2 && !Field<bool>(tab, "_editingPlan"));
        Check(PendingTruckReplacements.LoadStaged(root).Count == 3 && stageEvents == 2, "Map trucks staging updates the same persisted plan");
        Field<Button>(tab, "_resetAll").PerformClick(); Pump(() => !Field<bool>(tab, "_editingPlan"));
        Wait(tab.StageChangesAsync());
        Check(Field<DataGridView>(tab, "_trucks").Rows.Cast<DataGridViewRow>().Count(row => Convert.ToString(row.Cells["Change state"].Value) == "Staged") == 3,
            "map table distinguishes staged restorations; status=" + Field<Label>(tab, "_status").Text
            + "; rows=" + Field<DataGridView>(tab, "_trucks").Rows.Count + "; resetEnabled=" + Field<Button>(tab, "_resetAll").Enabled
            + "; editing=" + Field<bool>(tab, "_editingPlan") + "; parentEnabled=" + tab.Enabled
            + "; states=" + string.Join(",", Field<DataGridView>(tab, "_trucks").Rows.Cast<DataGridViewRow>().Select(row => Convert.ToString(row.Cells["Change state"].Value))));
        tab.PerformLayout(); using (var bitmap = new Bitmap(tab.Width, tab.Height)) { tab.DrawToBitmap(bitmap, new Rectangle(Point.Empty, tab.Size)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "map-staging-preview.png")); }
        PendingTruckReplacements.ApplyAll(root);
        Check(TruckReplacementStore.SavedMaps(root).All(map => RegionTruckCatalogService.ReadMapData(map.Source)!.SequenceEqual(data)), "Stage then Apply restores original model and exact equipment across every map");
        Check(inputs.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)), "source map archives remain byte-identical");
        foreach (var array in new[] { true, false })
        {
            var legacy = Path.Combine(root, "legacy-" + array); Directory.CreateDirectory(Path.Combine(legacy, "region-maps"));
            var item = JsonSerializer.SerializeToNode(new PendingTruckReplacement("legacy", initial, second, DateTimeOffset.UtcNow))!.AsObject(); item.Remove("Staged");
            var json = array ? new JsonArray(item).ToJsonString() : new JsonObject { ["Pending"] = new JsonArray(item), ["Assignments"] = new JsonArray() }.ToJsonString();
            File.WriteAllText(Path.Combine(legacy, "region-maps", "pending-replacements.json"), json);
            Check(PendingTruckReplacements.LoadStaged(legacy).Count == 1, "legacy " + (array ? "array" : "state") + " remains staged without data loss");
        }
        Console.WriteLine("PASS all explicit map staging, mixed-map draft rebasing, shared UI actions, reset-all, legacy migration and preservation checks.");
    }
}
