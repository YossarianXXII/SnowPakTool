using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class Program
{
    private static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    private static Type T(string name) => App.GetType("SnowPakTool.Desktop." + name)!;
    private static object? Static(string type, string name, params object?[] args) => T(type).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
    private static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(obj, args);
    private static object Property(object obj, string name) => obj.GetType().GetProperty(name)!.GetValue(obj)!;
    private static object Field(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    private static object ListOf(string type) => Activator.CreateInstance(typeof(List<>).MakeGenericType(T(type)))!;
    private static IEnumerable<object> Items(object list) => ((IEnumerable)list).Cast<object>();
    private static string Code(object row) => (string)Property(Property(row, "Truck"), "Code");
    private static Dictionary<string, decimal> Draft(object row) => (Dictionary<string, decimal>)Property(row, "Draft");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static void Wait(Task task)
    {
        var end = DateTime.UtcNow.AddSeconds(45);
        while (!task.IsCompleted && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult();
    }
    private static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
    private static object Mode(string mode) => Enum.Parse(T("ProgressionDifficulty"), mode);
    private static object Context(string country) => Activator.CreateInstance(T("UnlockStarterContext"), "first_" + country.ToLowerInvariant(), "First " + country, country, "Test saved progression.")!;
    private static object Plan(object rows, object tiers, HashSet<string> owned, string mode, int seed, string? country = "US") => Static("TruckUnlockRankGenerator", "Generate", rows, tiers, owned, Mode(mode), seed, country is null ? null : Context(country))!;
    private static double Variance(IEnumerable<int> source) { var values = source.Select(value => (double)value).ToArray(); var mean = values.Average(); return values.Average(value => (value - mean) * (value - mean)); }
    private static void Coverage(object rows, object plan, HashSet<string> owned)
    {
        var byFile = Items(rows).ToDictionary(row => (string)Property(row, "RelativeFile"));
        var choices = Items(Property(plan, "Choices")).ToArray();
        var country = (string)Property(Property(plan, "StarterContext"), "Country");
        var missing = Items(Property(plan, "MissingCoverage")).Cast<string>().ToHashSet();
        if (country.Length == 0) { if (missing.Count == 0) throw new Exception("Unresolved first region not reported"); return; }
            foreach (var gameClass in new[] { "HIGHWAY", "HEAVY_DUTY", "OFFROAD", "HEAVY", "SCOUT" })
            {
                var limit = gameClass is "SCOUT" or "OFFROAD" ? 1 : 3;
                bool Eligible(object row) => (string)Property(row, "GameClass") == gameClass && owned.Contains(Code(row))
                    && !(bool)Property(row, "RequiresExploration") && (string)Property(row, "UnlockObjective") == "" && !(bool)Property(row, "Locked")
                    && ((string)Property(row, "Country")).Split(',').Contains(country);
                var covered = choices.Any(choice =>
                {
                    var row = byFile[(string)Property(choice, "RelativeFile")];
                    return (int)Property(choice, "Rank") >= 1 && (int)Property(choice, "Rank") <= limit && Eligible(row);
                });
                var gap = missing.Any(item => item.StartsWith(country + " / " + gameClass + " by rank "));
                if (byFile.Values.Any(Eligible) ? !covered || gap : !gap)
                    throw new Exception($"Incorrect coverage or gap report for {gameClass} in {country}");
            }
    }
    [STAThread] private static void Main(string[] args)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixture-" + Guid.NewGuid().ToString("N"));
        var truckRoot = Path.Combine(root, "initial.pak", "original_files", "[media]", "classes", "trucks"); Directory.CreateDirectory(truckRoot);
        var tiers = (IList)ListOf("GroupState");
        var tierByCode = new Dictionary<string, string>();
        foreach (var tier in new[] { "FX", "D", "C", "B", "A", "S", "SS" })
        {
            var codes = new List<string>();
            foreach (var gameClass in new[] { "HIGHWAY", "HEAVY_DUTY", "OFFROAD", "HEAVY", "SCOUT" })
                for (var index = 0; index < 8; index++)
                {
                    var code = gameClass.ToLowerInvariant() + "_" + tier.ToLowerInvariant() + "_" + index;
                    if (gameClass == "HEAVY" && tier == "D" && index == 7) code = "aramatsu_forester";
                    if (gameClass == "HEAVY" && tier == "C" && index == 7) code = "kirovets_k700";
                    codes.Add(code); tierByCode[code] = tier;
                    var countries = gameClass is "HIGHWAY" or "HEAVY_DUTY" ? "US,NE,CE" : "US,RU,CAS,WA,NE,CE,CA";
                    File.WriteAllText(Path.Combine(truckRoot, code + ".xml"), $"<_templates><TruckData FuelCapacity=\"999\"/></_templates>\n<Truck><TruckData TruckType=\"{gameClass}\" FuelCapacity=\"100\"/><GameData Country=\"{countries}\" UnlockByRank=\"20\" Price=\"1000\" UnlockByExploration=\"{(index == 1 ? "true" : "false")}\" {(index == 2 ? "UnlockByObjective=\"TASK_LOCK\"" : "")}><UiDesc UiName=\"{code}\"/></GameData><Unrelated Keep=\"true\"/></Truck>");
                }
            var state = Activator.CreateInstance(T("GroupState"))!;
            state.GetType().GetProperty("Name")!.SetValue(state, tier); state.GetType().GetProperty("TruckCodes")!.SetValue(state, codes); tiers.Add(state);
        }
        File.WriteAllText(Path.Combine(truckRoot, "unranked.xml"), "<Truck><TruckData TruckType=\"SCOUT\" FuelCapacity=\"80\"/><GameData Country=\"US\" UnlockByRank=\"17\" Price=\"3000\" UnlockByExploration=\"false\"><UiDesc UiName=\"UNRANKED\"/></GameData></Truck>");
        var rows = Static("TruckPropertiesStore", "LoadRows", root)!;
        var owned = Items(rows).Select(Code).Where(code => !code.EndsWith("_0")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byFile = Items(rows).ToDictionary(row => (string)Property(row, "RelativeFile"));
        Check(Items(rows).Single(row => Code(row) == "aramatsu_forester") is var logging && (string)Property(logging, "GameClass") == "HEAVY" && (string)Property(Property(logging, "Truck"), "Class") == "Logging", "generator keeps original game class independently of custom app class");
        var samples = new Dictionary<string, List<int>>();
        var hardScoutRanks = new HashSet<int>();
        var otherStarterRanks = new HashSet<int>();
        foreach (var mode in new[] { "Easy", "Normal", "Hard" })
        {
            samples[mode] = [];
            var means = new Dictionary<string, List<int>>(); foreach (var tier in tierByCode.Values.Distinct()) means[tier] = [];
            var exceeds = false;
            for (var seed = 0; seed < 80; seed++)
            {
                var plan = Plan(rows, tiers, owned, mode, seed); Coverage(rows, plan, owned);
                var cap = (int)Property(plan, "SoftCap");
                foreach (var choice in Items(Property(plan, "Choices")))
                {
                    var rank = (int)Property(choice, "Rank"); var row = byFile[(string)Property(choice, "RelativeFile")];
                    if (rank is < 1 or > 30) throw new Exception("Rank range");
                    if ((bool)Property(choice, "Starter") && (string)Property(row, "GameClass") is "HIGHWAY" or "HEAVY_DUTY" or "HEAVY") otherStarterRanks.Add(rank);
                    if (Code(row) == "unranked") { if (rank != 17) throw new Exception("Unranked changed"); continue; }
                    var tier = tierByCode[Code(row)]; means[tier].Add(rank);
                    if (tier == "SS") { samples[mode].Add(rank); if (rank > cap) exceeds = true; }
                    if (mode == "Hard" && tier == "SS" && (string)Property(row, "GameClass") == "SCOUT") hardScoutRanks.Add(rank);
                    if (choice.GetType().GetProperty("Countries") is not null) throw new Exception("Rank plan exposes country edits");
                }
            }
            var averages = new[] { "FX", "D", "C", "B", "A", "S", "SS" }.Select(tier => means[tier].Average()).ToArray();
            Check(averages.Zip(averages.Skip(1)).All(pair => pair.First < pair.Second), mode + " tier means rise from FX to SS");
            Check(exceeds, mode + " soft cap can be exceeded without going beyond rank 30");
        }
        Check(Variance(samples["Easy"]) > Variance(samples["Normal"]) && Variance(samples["Normal"]) > Variance(samples["Hard"])
            && samples["Easy"].Any(rank => rank <= 11), "Easy has the widest variance and meaningful early SS unlocks; Hard is tightest");
        Check(hardScoutRanks.Count >= 8 && hardScoutRanks.Min() <= 21 && hardScoutRanks.Max() >= 28,
            "Hard now spreads same-tier scouts across a substantially wider range of ranks");
        Check(otherStarterRanks.SetEquals(new[] { 1, 2, 3 }), "other original game classes can receive starter ranks throughout 1–3");
        var sparse = (IList)ListOf("TruckPropertyRow");
        foreach (var row in Items(rows).Where(row => Code(row).EndsWith("_fx_3") || Code(row) == "scout_ss_3")) sparse.Add(row);
        var sparsePlan = Plan(sparse, tiers, owned, "Hard", 42);
        Check(Enumerable.Range(4, 3).All(rank => !Items(Property(sparsePlan, "Choices")).Any(choice => (int)Property(choice, "Rank") == rank)),
            "sparse catalogues retain empty-rank gaps rather than actively filling or spacing them");
        Check(Items(rows).All(row => Draft(row)["UnlockByRank"] == (Code(row) == "unranked" ? 17 : 20)), "generation plans never mutate preview/game data before accepted by the UI");
        Check(true, "first-region Scout/Offroad rank-1 and other-class rank-1–3 coverage hold across all seeds");
        var noOwnedPlan = Plan(rows, tiers, new HashSet<string>(), "Easy", 1);
        Check(Items(Property(noOwnedPlan, "MissingCoverage")).Count() == 5, "only first-region class gaps are reported when no trucks are owned");
        var ruPlan = Plan(rows, tiers, owned, "Hard", 42, "RU"); Coverage(rows, ruPlan, owned);
        Check(Items(Property(ruPlan, "MissingCoverage")).Count() == 2
            && Items(Property(ruPlan, "Choices")).Where(choice => (bool)Property(choice, "Starter")).All(choice =>
                ((string)Property(byFile[(string)Property(choice, "RelativeFile")], "Country")).Split(',').Contains("RU")),
            "first-region RU does not force US-only classes or report other countries");
        Check(Items(Property(Plan(rows, tiers, owned, "Hard", 42, null), "MissingCoverage")).Any(item => item.ToString()!.Contains("skipped")), "missing first region explicitly reports skipped coverage");
        var samplePlan = Plan(rows, tiers, owned, "Hard", 42);
        Check(Items(Property(samplePlan, "Report")).Count() == Items(rows).Count()
            && Items(Property(samplePlan, "Report")).Any(item => (string)Property(item, "Code") == "unranked" && (string)Property(item, "State") == "Unchanged" && ((string)Property(item, "Reason")).Contains("No assigned tier"))
            && Items(Property(samplePlan, "Report")).Any(item => ((string)Property(item, "Reason")).Contains("UnlockByExploration=true"))
            && Items(Property(samplePlan, "Report")).Any(item => ((string)Property(item, "Reason")).Contains("not owned/enabled")),
            "report covers every truck and explains unchanged tiers, exploration locks and ownership exclusions");
        var sameRow = Items(rows).Single(row => Code(row) == "scout_a_3");
        var sameChoice = Items(Property(samplePlan, "Choices")).Single(choice => (string)Property(choice, "RelativeFile") == (string)Property(sameRow, "RelativeFile"));
        Draft(sameRow)["UnlockByRank"] = (int)Property(sameChoice, "Rank");
        Check(Items(Property(Plan(rows, tiers, owned, "Hard", 42), "Report")).Any(item => (string)Property(item, "Code") == "scout_a_3" && (string)Property(item, "State") == "Unchanged" && ((string)Property(item, "Reason")).Contains("returned the same rank")), "equal random draws are reported as unchanged with a reason");
        Draft(sameRow)["UnlockByRank"] = 20;
        var missingRows = (IList)ListOf("TruckPropertyRow"); foreach (var row in Items(rows)) missingRows.Add(row);
        var missingTruck = Activator.CreateInstance(T("CustomTruck"), "missing_rank", "Missing rank", "SCOUT", "")!;
        missingRows.Add(Activator.CreateInstance(T("TruckPropertyRow"), "[media]/classes/trucks/missing_rank.xml", missingTruck, "US", "", new Dictionary<string, decimal>(), new Dictionary<string, decimal>(), new Dictionary<string, decimal>(), "SCOUT", false, "", false)!);
        Check(Items(Property(Plan(missingRows, tiers, owned, "Hard", 42), "Report")).Any(item => (string)Property(item, "Code") == "missing_rank" && (string)Property(item, "State") == "Skipped" && ((string)Property(item, "Reason")).Contains("missing/inherited")), "undefined rank attributes are explicitly reported as skipped");
        var ps = Path.Combine(root, "initial.pak", "original_files", "[ps]"); Directory.CreateDirectory(ps);
        File.WriteAllText(Path.Combine(ps, "zone_desc_storage.sso"), "regions = [ { name = \"first_ne\" country = \"NE\" } { name = \"ru_02\" } ]");
        var progression = Activator.CreateInstance(T("RegionProgressionState"))!;
        progression.GetType().GetProperty("Order")!.SetValue(progression, new List<string> { "first_ne", "ru_02" });
        Check((string)Property(Static("TruckUnlockRankGenerator", "ResolveFirstRegion", root, progression)!, "Country") == "NE", "first store country comes from the saved first region's definition");
        progression.GetType().GetProperty("Order")!.SetValue(progression, new List<string> { "ru_02" });
        Check((string)Property(Static("TruckUnlockRankGenerator", "ResolveFirstRegion", root, progression)!, "Country") == "RU", "built-in Russian regions resolve their omitted RU default");
        Static("TruckUnlockRankSettings", "Save", root, Mode("Normal"));
        var emptyEntries = Array.CreateInstance(T("RegionTruckEntry"), 0);
        var catalog = Activator.CreateInstance(T("RegionTruckCatalog"), emptyEntries, Array.Empty<string>(), 0, null)!;
        var getter = Expression.Lambda(typeof(Func<>).MakeGenericType(typeof(Task<>).MakeGenericType(T("RegionTruckCatalog"))),
            Expression.Call(typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(T("RegionTruckCatalog")), Expression.Constant(catalog))).Compile();
        var trucks = (IList)ListOf("CustomTruck"); foreach (var row in Items(rows).Where(row => owned.Contains(Code(row)))) trucks.Add(Property(row, "Truck"));
        var ownedGetter = Expression.Lambda(typeof(Func<>).MakeGenericType(typeof(IReadOnlyList<>).MakeGenericType(T("CustomTruck"))), Expression.Constant(trucks, typeof(IReadOnlyList<>).MakeGenericType(T("CustomTruck")))).Compile();
        var starterGetter = Expression.Lambda(typeof(Func<>).MakeGenericType(T("UnlockStarterContext")), Expression.Constant(Context("US"))).Compile();
        using var tab = (Control)Activator.CreateInstance(T("TruckPropertiesTab"), new Func<string>(() => root), getter, ownedGetter, starterGetter)!;
        tab.GetType().GetField("ConfirmExplorationUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tab, new Func<DialogResult>(() => DialogResult.No));
        tab.Size = new Size(1400, 850); tab.CreateControl(); Wait((Task)Call(tab, "EnsureLoadedAsync", false)!);
        var radios = All(tab).OfType<RadioButton>().ToArray();
        Check(radios.Length == 3 && radios.Single(radio => radio.Checked).Text == "Normal", "unlock difficulty radios load saved setting");
        radios.Single(radio => radio.Text == "Easy").Checked = true;
        Check(Static("TruckUnlockRankSettings", "Load", root)!.ToString() == "Easy", "difficulty persists independently of staged edits");
        All(tab).OfType<Button>().Single(button => button.Text == "Generate unlock ranks").PerformClick();
        var uiRows = Field(tab, "_rows");
        Check(Items(uiRows).Any(row => Draft(row)["UnlockByRank"] == 1)
            && Items(uiRows).All(row => (string)Property(row, "Country") == (string)Property(byFile[(string)Property(row, "RelativeFile")], "Country")), "button previews starter ranks and preserves every country assignment");
        Check(((TextBox)Field(tab, "_unlockLog")).Text.Contains("UNLOCK RANK GENERATION") && ((TextBox)Field(tab, "_unlockLog")).Text.Contains("UNCHANGED")
            && ((TextBox)Field(tab, "_unlockLog")).Text.Contains("No assigned tier") && ((CheckBox)Field(tab, "_showUnlockLog")).Checked
            && !((SplitContainer)Field(tab, "_unlockLogSplit")).Panel2Collapsed, "UI opens a detailed copyable changed/unchanged/skipped report");
        using (var bitmap = new Bitmap(tab.Width, tab.Height)) { tab.PerformLayout(); tab.DrawToBitmap(bitmap, new Rectangle(Point.Empty, tab.Size)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "unlock-ranks-preview.png")); }
        Check(Items(Property(Static("TruckPropertiesStore", "LoadSnapshot", root)!, "Values")).Count() == 0 && !Directory.Exists(Path.Combine(root, "initial.pak", "updated")), "generating/changing difficulty does not stage or write truck files");
        Call(tab, "Stage"); var snapshot = Static("TruckPropertiesStore", "LoadSnapshot", root)!;
        Check(Items(Property(snapshot, "Values")).Any() && snapshot.GetType().GetProperty("Countries") is null, "staged snapshot contains numeric edits only");
        var presetJson = (string)Static("TruckPropertiesStore", "SerializeSnapshot", Property(snapshot, "Values"))!;
        Check(!presetJson.Contains("Countries"), "exported presets contain no country edits");
        var pendingPath = Path.Combine(root, "truck-properties", "pending-changes.json");
        var oldPlan = JsonNode.Parse(presetJson)!;
        var firstEdit = Items(Property(snapshot, "Values")).First();
        var relative = (string)Property(firstEdit, "RelativeFile");
        oldPlan["Countries"] = new JsonArray(new JsonObject { ["RelativeFile"] = relative, ["TruckName"] = "Old starter", ["Before"] = "US", ["After"] = "US,RU,CAS,WA" });
        File.WriteAllText(pendingPath, oldPlan.ToJsonString());
        Check(Items(Property(Static("TruckPropertiesStore", "ReadPresetSnapshot", oldPlan.ToJsonString())!, "Values")).Count() == Items(Property(snapshot, "Values")).Count(), "old version-2 presets retain ranks while ignoring country payloads");
        var legacy = Path.Combine(root, "legacy"); Directory.CreateDirectory(Path.Combine(legacy, "truck-properties"));
        File.WriteAllText(Path.Combine(legacy, "truck-properties", "pending-changes.json"), JsonSerializer.Serialize(Property(snapshot, "Values")));
        var upgraded = Static("TruckPropertiesStore", "LoadSnapshot", legacy)!;
        Check(Items(Property(upgraded, "Values")).Any(), "legacy numeric-only pending arrays remain readable");
        using (var fresh = (Control)Activator.CreateInstance(T("TruckPropertiesTab"), new Func<string>(() => root), getter, ownedGetter)!)
        {
            Check(Items(Call(fresh, "PendingChanges", root)!).Count() == Items(Property(snapshot, "Values")).Count(), "Start restores only numeric changes from older staged snapshots");
            Wait((Task)Call(fresh, "EnsureLoadedAsync", false)!);
            Check(Items(Field(fresh, "_rows")).All(row => (string)Property(row, "Country") == (string)Property(byFile[(string)Property(row, "RelativeFile")], "Country")), "tab restart ignores unwanted country payloads");
        }
        var currentPath = (string)Static("WorkspaceOriginals", "GetCurrentPath", root, relative)!;
        var originalText = File.ReadAllText(currentPath);
        var updatePath = (string)Static("WorkspaceOriginals", "PrepareForEdit", root, currentPath)!;
        var externalText = originalText.Replace("Country=\"" + (string)Property(byFile[relative], "Country") + "\"", "Country=\"EXTERNAL\"");
        File.WriteAllText(updatePath, externalText);
        Call(tab, "SavePendingChanges");
        var applied = Static("TruckPropertiesStore", "LoadRows", root)!;
        bool CountryPreserved(object row) => (string)Property(row, "Country") == ((string)Property(row, "RelativeFile") == relative ? "EXTERNAL" : (string)Property(byFile[(string)Property(row, "RelativeFile")], "Country"));
        Check(Items(applied).All(CountryPreserved) && Items(applied).All(row => Draft(row)["FuelCapacity"] == (Code(row) == "unranked" ? 80 : 100)), "Apply writes ranks only and preserves original/external country assignments and fuel");
        Check(Directory.GetFiles(truckRoot).All(file => File.ReadAllText(file).Contains("FuelCapacity=\"100\"") || Path.GetFileName(file) == "unranked.xml"), "Apply preserves original truck files");
        Wait((Task)Call(tab, "EnsureLoadedAsync", false)!);
        oldPlan["Values"] = new JsonArray(); File.WriteAllText(pendingPath, oldPlan.ToJsonString());
        Check(!Items(Call(tab, "PendingChanges", root)!).Any(), "old country-only plans cannot appear as pending writes on Start");
        Static("TruckPropertiesStore", "Apply", root);
        Wait((Task)Call(tab, "EnsureLoadedAsync", true)!);
        All(tab).OfType<Button>().Single(button => button.Text == "Reset to original values").PerformClick(); Call(tab, "Stage"); Call(tab, "SavePendingChanges");
        var restored = Static("TruckPropertiesStore", "LoadRows", root)!;
        Check(Items(restored).All(row => CountryPreserved(row) && Draft(row)["UnlockByRank"] == (Code(row) == "unranked" ? 17 : 20)), "reset/stage/apply restores ranks while keeping existing country assignments");
        if (args.Length > 0)
        {
            var real = Static("TruckPropertiesStore", "LoadRows", args[0])!;
            var realOwned = Items(real).Where(row => ((string)Property(row, "RelativeFile")).StartsWith("[media]/classes/trucks/", StringComparison.OrdinalIgnoreCase) || Code(row) == "ank_mk38_ht").Select(Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var realPlan = Plan(real, tiers, realOwned, "Hard", 42, "RU"); Coverage(real, realPlan, realOwned);
            Check(Items(Property(realPlan, "MissingCoverage")).Any(), "real workspace reports unavailable pairs without extending countries");
        }
        Console.WriteLine("All unlock progression, coverage, UI, staging and preset checks passed.");
    }
}
