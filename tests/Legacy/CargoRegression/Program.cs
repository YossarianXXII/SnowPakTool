using System.Reflection;
using System.Text;
using SnowPakTool.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var workspace = Path.GetFullPath(args[0]);
        var catalog = CargoCatalogService.Load(workspace, WorkspaceDataCache.GetLocalizedNames(workspace));
        Check(catalog.Warnings.Count == 0, string.Join("\n", catalog.Warnings));
        Check(catalog.Entries.Select(entry => entry.Type).Distinct().Count() >= 77, "All base and DLC types");
        var slab = catalog.Entries.Single(entry => entry.Type == "CargoConcreteSlab");
        Check(slab.Name != slab.Type && !slab.Name.StartsWith("UI_"), "Localized cargo name");
        Check(slab.Slots == 2 && slab.MassKg == 6000 && slab.LoadValue == 1, "Slab join and nested body mass");
        Check(catalog.Entries.Single(entry => entry.Type == "CargoChiller").MassKg == 5000, "DLC nested body mass");
        Check(catalog.Entries.Single(entry => entry.Type == "CargoWater").MassKg is null, "Undefined mass stays unknown");
        Check(catalog.Entries.Count(entry => entry.Type == "CargoLogsMedium") > 1, "Carrier variants retained");
        Check(catalog.Entries.Any(entry => entry.Variant == "—"), "Types without addons retained");
        Check(slab.Properties.Any(property => property.Property.Contains("Body[1]") || property.Property.EndsWith("Body/@Mass")), "Source body attributes");

        var fixture = Path.Combine(AppContext.BaseDirectory, "fixture");
        Directory.CreateDirectory(Path.Combine(fixture, "original_files", "[media]", "classes", "cargo_types"));
        Directory.CreateDirectory(Path.Combine(fixture, "original_files", "[media]", "classes", "trucks", "cargo"));
        Directory.CreateDirectory(Path.Combine(fixture, "updated", "[media]", "classes", "trucks", "cargo"));
        File.WriteAllText(Path.Combine(fixture, "original_files", "[media]", "classes", "cargo_types", "CargoTest.xml"), "<CargoType><UiDesc UiName=\"TEST\" /></CargoType>");
        var relative = Path.Combine("[media]", "classes", "trucks", "cargo", "cargo_test.xml");
        File.WriteAllText(Path.Combine(fixture, "original_files", relative), "<TruckAddon><GameData><InstallSlot CargoType=\"CargoTest\" CargoLength=\"1\" /></GameData></TruckAddon>");
        File.WriteAllText(Path.Combine(fixture, "updated", relative), "<_templates/><TruckAddon><PhysicsModel><Body Mass=\"123\"/></PhysicsModel><GameData><InstallSlot CargoType=\"CargoTest\" CargoLength=\"3\" /></GameData></TruckAddon>");
        File.WriteAllText(Path.Combine(fixture, "original_files", "[media]", "classes", "cargo_types", "Broken.xml"), "<CargoType>");
        var overlay = CargoCatalogService.Load(Path.Combine(fixture, "updated"), new Dictionary<string, string>());
        Check(overlay.Entries.Count == 1 && overlay.Entries[0].Slots == 3 && overlay.Entries[0].MassKg == 123, "Sparse overlay wins and XML fragments read");
        Check(overlay.Warnings.Count == 1, "Malformed file reported without losing valid cargo");

        using var host = new Form { Size = new Size(1120, 800) };
        using var tab = new TrucksCargoTab(() => workspace) { Dock = DockStyle.Fill };
        host.Controls.Add(tab);
        host.CreateControl();
        tab.CreateControl();
        tab.EnsureLoaded();
        host.Controls.Remove(tab);
        tab.BindingContext = new BindingContext();
        tab.CreateControl();
        using var search = new CurrentTabSearch(() => tab);
        var grid = Field<DataGridView>(tab, "_cargo");
        Check(search.Search("CargoConcreteSlab") > 0 && grid.Rows.Count == catalog.Entries.Count, "Shared search highlights cargo without filtering the table");
        grid.CurrentCell = grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToString(row.Cells["Cargo type"].Value) == "CargoConcreteSlab").Cells[0];
        Check(Field<DataGridView>(tab, "_properties").Rows.Count > 0, "Selected cargo properties shown");
        Check(search.Search("no-such-cargo-123456") == 0 && grid.Rows.Count == catalog.Entries.Count, "Missing search does not hide cargo or change selection");
        search.Clear();
        Check(grid.Rows.Count == catalog.Entries.Count, "Clear search restores variants");
        grid.Sort(grid.Columns["Body mass sum (kg)"]!, System.ComponentModel.ListSortDirection.Descending);
        Check(grid.Rows[0].Cells["Body mass sum (kg)"].Value is decimal mass && mass >= 10000, "Numeric mass sorting");
        Console.WriteLine($"PASS: {catalog.Entries.Select(entry => entry.Type).Distinct().Count()} types, {catalog.Entries.Count} entries; data joins, overlays, parsing, filtering, details and numeric sorting.");
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
}

namespace SnowPakTool.Desktop
{
    // This harness exercises the real disk overlay without the unrelated global tab preload.
    internal static class WorkspaceDataCache
    {
        public static string ReadAllText(string path, Encoding? encoding = null) => File.ReadAllText(path, encoding ?? Encoding.UTF8);
        public static bool TryGetCurrentFiles(string workspace, string directory, string pattern, SearchOption option, out IReadOnlyList<string> files)
        { files = []; return false; }
        public static Task PreloadAsync(string workspace, bool forceReload = false) => Task.CompletedTask;
        public static IReadOnlyDictionary<string, string> GetLocalizedNames(string workspace) => LocalizedStrings.Load(WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[strings]", "strings_english.str")));
    }
}
