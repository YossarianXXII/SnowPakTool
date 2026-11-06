using System.Collections;
using System.Reflection;
using System.Text.Json;

internal static class Program
{
    private static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    private static Type T(string name) => App.GetType("SnowPakTool.Desktop." + name)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, args);
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Static(string type, string name, params object?[] args) => T(type).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    [STAThread] private static void Main()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixture-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var originals = Path.Combine(workspace, "initial.pak", "original_files", "[ps]"); Directory.CreateDirectory(originals);
        const string rankText = "rankExp = [100, 200, 300,]\nkeep = 1\n";
        File.WriteAllText(Path.Combine(originals, "rank_settings.sso"), rankText);
        foreach (var code in new[] { "us_01", "us_02", "ru_02", "us_03" }) File.WriteAllText(Path.Combine(originals, code + "_01_tasks.sso"), "ObjectiveRewardExperience = { amount = 10000 }");
        var progressionPath = Path.Combine(root, "region-progression.json");
        var progressionStore = Activator.CreateInstance(T("RegionProgressionStore"), progressionPath)!;
        void SaveOrder(params string[] order) => File.WriteAllText(progressionPath, JsonSerializer.Serialize(new { Order = order, Difficulty = 1, IsFinalized = false }));
        SaveOrder("ru_02", "missing_region", "US_02", "us_01");
        var rankStore = Activator.CreateInstance(T("RankXpStageStore"), Path.Combine(root, "preferences"))!;
        var preferences = (string)Static("WorkspaceOriginals", "GetMetadataPath", workspace, new[] { "progression-settings.json" })!;
        Directory.CreateDirectory(Path.GetDirectoryName(preferences)!);
        File.WriteAllText(preferences, "{\"RegionOrder\":[\"us_01\",\"us_02\",\"us_03\",\"ru_02\"],\"IncludedRegions\":[\"ru_02\",\"us_01\"],\"XpReductionSteps\":1}");
        using var editor = (Control)Activator.CreateInstance(T("ExperienceEditorTab"), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { new Func<string>(() => workspace), Activator.CreateInstance(T("OwnedRegionState"))!, rankStore, progressionStore }, null)!;
        editor.Size = new Size(1600, 1000); editor.CreateControl(); Call(editor, "EnsureLoaded");
        var regions = (DataGridView)Field(editor, "_regionGrid");
        string[] Order() => regions.Rows.Cast<DataGridViewRow>().Select(row => (string)row.Cells["RegionCode"].Value!).ToArray();
        Check(Order().SequenceEqual(new[] { "ru_02", "us_02", "us_01", "us_03" }), "saved progression overrides legacy XP order, ignores absent regions and appends unlisted regions");
        Check(regions.Rows.Cast<DataGridViewRow>().Where(row => (bool)row.Cells["Include"].Value).Select(row => (string)row.Cells["RegionCode"].Value).ToHashSet().SetEquals(new[] { "ru_02", "us_01" }), "restored region selections survive shared ordering");
        Check(!regions.AllowDrop && regions.Columns.Cast<DataGridViewColumn>().All(column => column.SortMode == DataGridViewColumnSortMode.NotSortable), "local sorting and dragging cannot diverge from Region progression");
        var ranks = (DataGridView)Field(editor, "_grid");
        ranks.Rows[0].Cells["UpdatedXP"].Value = 777;
        Call(rankStore, "Stage", workspace, rankText, new List<int> { 400, 500, 600 });
        var stagedBefore = JsonSerializer.Serialize(Call(rankStore, "Load", workspace));
        var settingsBefore = File.ReadAllText(preferences);
        SaveOrder("us_01", "ru_02");
        Call(editor, "EnsureLoaded");
        Check(Order().SequenceEqual(new[] { "us_01", "ru_02", "us_02", "us_03" }), "returning to the editor refreshes the saved progression order");
        Check(Convert.ToInt32(ranks.Rows[0].Cells["UpdatedXP"].Value) == 777 && JsonSerializer.Serialize(Call(rankStore, "Load", workspace)) == stagedBefore, "order refresh preserves manual XP preview and the staged snapshot");
        Check(File.ReadAllText(preferences) == settingsBefore, "following shared order does not rewrite XP controls or selections");
        Check(File.ReadAllText(Path.Combine(originals, "rank_settings.sso")) == rankText && !File.Exists(Path.Combine(workspace, "initial.pak", "updated", "[ps]", "rank_settings.sso")), "opening and refreshing order never write rank game data");
        SaveOrder(); Call(editor, "EnsureLoaded");
        var fallback = Order(); Call(editor, "EnsureLoaded");
        Check(fallback.Length == 4 && Order().SequenceEqual(fallback), "no saved progression keeps all regions in deterministic fallback order");
        Call(editor, "SaveProgressionSettings");
        Check(!File.ReadAllText(preferences).Contains("RegionOrder"), "XP settings no longer save a competing region order");
        using var bitmap = new Bitmap(editor.Width, editor.Height); editor.PerformLayout(); editor.DrawToBitmap(bitmap, new Rectangle(Point.Empty, editor.Size)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "experience-order-preview.png"));
        Console.WriteLine("All shared region-order checks passed using isolated workspace/preferences.");
    }
}
