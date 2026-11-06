using System.Collections;
using System.Reflection;
using System.Text.Json;

internal static class Program
{
    static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    static Type T(string name) => App.GetType("SnowPakTool.Desktop." + name)!;
    static object P(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
    static object F(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    static object? Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(value, args);
    static object[] Items(object value) => ((IEnumerable)value).Cast<object>().ToArray();
    static void Check(bool value, string label) { if (!value) throw new Exception(label); }

    [STAThread] static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        var workspace = @"S:\snowrunner_Shenanigans\AI\workspace";
        using var canvas = new Panel { Size = new Size(1100, 850) };
        using var tab = (Control)Activator.CreateInstance(T("AddonsTab"), new Func<string>(() => workspace))!;
        tab.Dock = DockStyle.Fill;
        canvas.Controls.Add(tab); canvas.CreateControl(); tab.CreateControl();
        var start = DateTime.UtcNow;
        var load = (Task)Call(tab, "EnsureLoadedAsync")!;
        while (!load.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
        load.GetAwaiter().GetResult();
        var catalog = F(tab, "_catalog");
        Check(catalog is not null, ((Label)F(tab, "_status")).Text);
        var entries = Items(P(catalog!, "Entries"));
        var references = Items(P(catalog!, "MapReferences"));
        var warnings = Items(P(catalog!, "Warnings"));
        Console.WriteLine($"Loaded {entries.Length} definitions, {references.Length} refs, {warnings.Length} warnings.");
        foreach (var warning in warnings.Take(8)) Console.WriteLine(warning);
        var engine = entries.Single(e => (string)P(e, "Id") == "us_scout_old_engine_ck1500");
        Console.WriteLine($"CK engine type {P(engine, "Type")}, refs {Items(P(engine, "MapReferences")).Length}");
        Check((string)P(engine, "Type") == "Engine" && Items(P(engine, "MapReferences")).Any(r => (string)P(r, "Zone") == "US_01_01_UPGRADE_SCOUT_OLD_ENGINE"), "variant ID joins exact map zone");
        var awd = entries.Single(e => (string)P(e, "Id") == "international_fleetstar_f2070a_transferbox_allwheels");
        Check((string)P(awd, "Family") == "FleetstarF2070aTransferBox", "AWD inherits install socket from file parent");
        Check(Items(P(awd, "Properties")).Any(p => ((string)P(p, "Source")).Contains("transferbox_default")), "inherited property provenance retained");
        Check(references.Count(r => (string)P(r, "Map") == "level_us_01_01") == 7, "Black River contains seven upgrade rewards");
        // Change only an upgrade token in an isolated copy, then read it through
        // the production reader. The real workspace and installed archives stay untouched.
        var proof = Path.Combine(AppContext.BaseDirectory, "replacement-proof");
        Directory.CreateDirectory(Path.Combine(proof, "[ps]"));
        var source = Path.Combine(workspace, "initial.pak", "original_files", "[ps]", "level_us_01_01.sso");
        var original = File.ReadAllText(source);
        var replacement = original.Replace("\"chevrolet_ck1500_suspension_high\"", "\"us_scout_old_engine_2\"", StringComparison.Ordinal);
        Check(replacement != original && original.Split("\"chevrolet_ck1500_suspension_high\"").Length == 2, "exactly one source reward token");
        File.WriteAllText(Path.Combine(proof, "[ps]", "level_us_01_01.sso"), replacement);
        var proofWarnings = new List<string>();
        var proofReferences = Items(T("AddonCatalogService").GetMethod("ReadMapReferences", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { proof, proofWarnings })!);
        Check(proofWarnings.Count == 0 && proofReferences.Length == 7, "isolated changed level remains readable with all zones");
        Check(proofReferences.Single(r => (string)P(r, "Zone") == "US_01_01_UPGRADE_CK_SUSPENSION") is { } changed && (string)P(changed, "UpgradeId") == "us_scout_old_engine_2", "cross-category replacement resolves through the same existing zone");
        Check(File.ReadAllText(source) == original, "workspace input unchanged");
        var grid = (DataGridView)F(tab, "_addons");
        Check(grid.ReadOnly && grid.Rows.Count == entries.Length, "read-only catalog is fully bound");
        var index = grid.Rows.Cast<DataGridViewRow>().Single(r => (string)r.Cells["ID"].Value == "international_fleetstar_f2070a_transferbox_allwheels").Index;
        grid.CurrentCell = grid.Rows[index].Cells[0];
        canvas.PerformLayout(); Application.DoEvents();
        Check(((DataGridView)F(tab, "_properties")).Rows.Cast<DataGridViewRow>().Any(r => Convert.ToString(r.Cells[0].Value)!.StartsWith("Map reward:")), "selected details show map source and zone");
        using (var bitmap = new Bitmap(canvas.Width, canvas.Height)) { canvas.DrawToBitmap(bitmap, new Rectangle(Point.Empty, canvas.Size)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "addons.png")); }
        var summary = new
        {
            Definitions = entries.Length,
            Types = entries.GroupBy(e => (string)P(e, "Type")).ToDictionary(g => g.Key, g => g.Count()),
            RewardEntries = references.Length,
            Zones = references.Select(r => P(r, "Map") + "|" + P(r, "Zone")).Distinct().Count(),
            Maps = references.Select(r => P(r, "Map")).Distinct().Count(),
            UniqueRewardIds = references.Select(r => P(r, "UpgradeId")).Distinct().Count(),
            Warnings = warnings,
            References = references,
            Entries = entries.Select(e => new { Id = P(e,"Id"), Type = P(e,"Type"), Name = P(e,"Name"), Source = P(e,"Source"), Family = P(e,"Family"), Exploration = P(e,"Exploration") })
        };
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "inventory.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS real catalog: {entries.Length} definitions, {references.Length} rewards, {summary.Zones} zones, {summary.Maps} maps, {summary.UniqueRewardIds} distinct reward IDs, {warnings.Length} warnings. Loaded and rendered in {(DateTime.UtcNow-start).TotalSeconds:F1}s.");
        foreach (var warning in warnings.Take(15)) Console.WriteLine(warning);
    }
}
