using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

static class Program
{
    static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    static Type T(string n) => App.GetType("SnowPakTool.Desktop." + n)!;
    static object F(object o, string n) => o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    static object P(object o, string n) => o.GetType().GetProperty(n)!.GetValue(o)!;
    static object? Call(object o, string n, params object?[] args)
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        return o.GetType().GetMethod(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(o, args);
    }
    static object? S(string t, string n, params object?[] args) => T(t).GetMethod(n, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args);
    static object[] Items(object o) => ((IEnumerable)o).Cast<object>().ToArray();
    static void Check(bool b, string m) { if (!b) throw new Exception(m); Console.WriteLine("PASS " + m); }
    static void Pump(Task t) { var until = DateTime.UtcNow.AddSeconds(45); while (!t.IsCompleted && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); } Check(t.IsCompleted, "load completes"); t.GetAwaiter().GetResult(); }
    static IEnumerable<Control> Controls(Control p) { foreach (Control c in p.Controls) { yield return c; foreach (var x in Controls(c)) yield return x; } }
    static void Button(Control tab, string text) { SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext()); Controls(tab).OfType<Button>().Single(b => b.Text == text).PerformClick(); Application.DoEvents(); }
    static DataGridViewRow Row(DataGridView grid, string id) => grid.Rows.Cast<DataGridViewRow>().Single(r => r.Cells["ID"].Value?.ToString() == id);
    [STAThread] static void Main(string[] args)
    {
        Control.CheckForIllegalCrossThreadCalls = true;
        if (args.Contains("--real-readonly"))
        {
            var workspace = @"S:\snowrunner_Shenanigans\AI\workspace";
            var names = S("WorkspaceDataCache", "GetLocalizedNames", workspace)!;
            var catalog = S("AddonCatalogService", "Load", workspace, names)!;
            var rows = (IDictionary)S("AddonExplorationStore", "ReadRows", workspace, catalog)!;
            var matches = rows.Values.Cast<object>().Where(r => (string)P(P(r, "Identity"), "Variant") == "engine_truck_hib_billert_m816_02" && (string)P(P(r, "Identity"), "Attribute") == "UnlockByExploration").ToArray();
            Check(matches.Length == 1 && (string)P(matches[0], "Original") == "false" && (string)P(matches[0], "Current") == "true", "reported HIB engine resolves to true (false) with exact variant identity");
            Console.WriteLine($"Read-only real catalog: {rows.Count} editable definitions."); return;
        }
        var root = Path.Combine(AppContext.BaseDirectory, "fixture-" + Guid.NewGuid().ToString("N"));
        var original = Path.Combine(root, "original_files"); var updated = Path.Combine(root, "updated");
        const string file = "[media]/classes/engines/test.xml";
        const string xml = "<_templates><Engine Name=\"a\"><GameData UnlockByExploration=\"true\"/></Engine></_templates>\r\n<EngineVariants>\r\n<Engine Name=\"a\"><GameData Price=\"100\" UnlockByRank=\"1\" UnlockByExploration=\"false\"/></Engine>\r\n<Engine Name=\"b\"><GameData Price=\"200\"/></Engine>\r\n</EngineVariants>";
        void Write(string tree, string path, string value) { var p = Path.Combine(tree, path); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, value); }
        Write(original, file, xml); Write(updated, file, xml.Replace("UnlockByExploration=\"false\"", "UnlockByExploration=\"true\"").Replace("Price=\"100\"", "Price=\"150\"").Replace("UnlockByRank=\"1\"", "UnlockByRank=\"7\"").Replace("Price=\"200\"", "Price=\"200\" UnlockByExploration=\"true\""));
        Write(original, "[media]/classes/engines/unchanged.xml", "<EngineVariants><Engine Name=\"unchanged\"><GameData Price=\"300\"/></Engine></EngineVariants>");
        const string addon = "[media]/classes/trucks/test_addon.xml";
        Write(original, addon, "<TruckAddon><GameData Category=\"frame_addons\" UnlockByExploration=\"false\"/></TruckAddon>");
        Write(updated, addon, File.ReadAllText(Path.Combine(original, addon)).Replace("false", "true"));
        Write(original, "[strings]/strings_english.str", "");
        var hashes = Directory.GetFiles(original, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => SHA256.HashData(File.ReadAllBytes(f)));
        using var host = new Form { Size = new Size(1400, 900) };
        using var tab = (Control)Activator.CreateInstance(T("AddonsTab"), new object[] { new Func<string>(() => root), false })!;
        tab.Dock = DockStyle.Fill; host.Controls.Add(tab); host.Show();
        Pump((Task)Call(tab, "EnsureLoadedAsync")!);
        var grid = (DataGridView)F(tab, "_addons"); var a = Row(grid, "a"); grid.CurrentCell = a.Cells["ID"];
        var changedOnly = (CheckBox)F(tab, "_changedOnly");
        changedOnly.Checked = true; Application.DoEvents();
        Check(grid.Rows.Count == 2, "Addons changed-only includes applied changes and hides unchanged definitions");
        var headers = S("TableHeaderBehavior", "Attach", grid, null, null, null)!;
        var idFilter = Call(headers, "AddFilter", "ID", null, Array.Empty<string>())!;
        Call(idFilter, "SelectValues", new object[] { new[] { "a" } }); Application.DoEvents();
        Check(grid.Rows.Count == 1, "changed-only combines with addon header filters");
        changedOnly.Checked = false; Application.DoEvents();
        Check(grid.Rows.Count == 1, "disabling changed-only preserves other addon filters");
        Call(idFilter, "Clear"); changedOnly.Checked = true;
        Button(tab, "Show external addons"); Check(grid.Rows.Count == 1 && changedOnly.Checked, "changed-only stays active across internal/external views");
        Button(tab, "Show internal addons"); Check(grid.Rows.Count == 2, "internal changed-only view is restored");
        changedOnly.Checked = false; Application.DoEvents();
        a = Row(grid, "a"); grid.CurrentCell = a.Cells["ID"];
        void Edit(string id, string column, string value)
        {
            grid.CurrentCell = Row(grid, id).Cells[column];
            Check(grid.BeginEdit(false) && grid.EditingControl is TextBox, column + " supports inline editing");
            ((TextBox)grid.EditingControl!).Text = value;
            Check(grid.EndEdit(), column + " accepts valid input"); grid.CurrentCell = Row(grid, id).Cells["ID"];
        }
        Check(a.Cells["Exploration unlock"].FormattedValue?.ToString() == "true (false)", "applied exploration flag compares against original");
        Check(a.Cells["Change state"].Value?.ToString() == "In files", "applied flag has an identifiable state");
        Check(a.Cells["Price"].FormattedValue?.ToString() == "150 (100)" && a.Cells["Unlock rank"].FormattedValue?.ToString() == "7 (1)", "price and unlock rank compare applied values with originals");
        Button(tab, "Reset selected to original");
        Check(Row(grid, "a").Cells["Change state"].Value?.ToString() == "Draft", "reset previews a specific variant restoration");
        Button(tab, "Stage changes");
        Check(Items(S("AddonExplorationStore", "Load", root)!).Length == 3 && Row(grid, "a").Cells["Change state"].Value?.ToString() == "Staged", "reset and stage cover price, rank and exploration for selected variant");
        Edit("a", "Exploration unlock", "true");
        using (var main = (Form)Activator.CreateInstance(T("MainForm"))!)
        {
            ((TextBox)F(main, "_workspacePath")).Text = root;
            Check(Items(Call(main, "PendingPakChanges", root)!).Length == 3, "Start discovers all staged addon properties without opening Addons");
            Call(main, "SavePendingPakChanges");
        }
        var text = File.ReadAllText(Path.Combine(updated, file));
        Check((string?)S("UpgradeChanges", "ReadExploration", text, "Engine", "a") == "false"
            && (string?)S("UpgradeChanges", "ReadExploration", text, "Engine", "b") == "true", "Apply uses staged restoration, excludes later preview and preserves other variant");
        Check(text.StartsWith(xml[..xml.IndexOf("<EngineVariants>")]), "templates and formatting are preserved");
        var properties = (Dictionary<string, string>)S("UpgradeChanges", "ReadAddonProperties", text, "Engine", "a")!;
        Check(properties["Price"] == "100" && properties["UnlockByRank"] == "1", "Apply restores original price and unlock rank alongside exploration");
        Pump((Task)Call(tab, "LoadAsync", true)!);
        Edit("a", "Price", "175"); Edit("a", "Unlock rank", "8");
        Check(Row(grid, "a").Cells["Price"].FormattedValue?.ToString() == "175 (100)", "inline numeric edits retain original-value comparison");
        grid.CurrentCell = Row(grid, "a").Cells["Unlock rank"]; grid.BeginEdit(false);
        ((TextBox)grid.EditingControl!).Text = "31";
        Button(tab, "Stage changes");
        Check(Items(S("AddonExplorationStore", "Load", root)!).Length == 0 && ((Label)F(tab, "_status")).Text.Contains("30"), "rank above 30 is rejected before staging: " + ((Label)F(tab, "_status")).Text); grid.CancelEdit(); grid.CurrentCell = Row(grid, "a").Cells["ID"];
        Button(tab, "Reset to original values"); Button(tab, "Stage changes");
        Check(Items(S("AddonExplorationStore", "Load", root)!).Length == 2, "reset all includes hidden external addon and absent original attribute");
        S("AddonExplorationStore", "Apply", root);
        text = File.ReadAllText(Path.Combine(updated, file));
        Check(S("UpgradeChanges", "ReadExploration", text, "Engine", "b") is null && text.Contains("Price=\"200\""), "reset removes introduced attribute and preserves unrelated fields");
        Check(File.ReadAllText(Path.Combine(updated, addon)).Contains("UnlockByExploration=\"false\""), "hidden external addon reset is applied");
        Pump((Task)Call(tab, "LoadAsync", true)!);
        a = Row(grid, "a"); grid.CurrentCell = a.Cells["Exploration unlock"];
        Edit("a", "Exploration unlock", "true"); Button(tab, "Stage changes");
        Write(updated, file, File.ReadAllText(Path.Combine(updated, file)).Replace("UnlockByExploration=\"false\"", ""));
        var failed = false;
        try { S("AddonExplorationStore", "Apply", root); } catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { failed = true; }
        Check(failed && Items(S("AddonExplorationStore", "Load", root)!).Length == 1, "conflicting exploration edit rejects Apply and retains snapshot");
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(S("AddonExplorationStore", "Load", root)))!;
        legacy[0]!.AsObject().Remove("Attribute");
        File.WriteAllText(Path.Combine(root, "upgrade-randomization", "addon-exploration.json"), legacy.ToJsonString());
        Check((string)P(Items(S("AddonExplorationStore", "Load", root)!).Single(), "Attribute") == "UnlockByExploration", "previous exploration-only staged snapshots remain compatible");
        Check(hashes.All(p => p.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(p.Key)))), "all original files remain byte identical");
        using var screenshot = new Bitmap(host.Width, host.Height); host.DrawToBitmap(screenshot, new Rectangle(Point.Empty, host.Size)); screenshot.Save(Path.Combine(AppContext.BaseDirectory, "addons-exploration.png"));
        Console.WriteLine("Fixture: " + root);
    }
}
