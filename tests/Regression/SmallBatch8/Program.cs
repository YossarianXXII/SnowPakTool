using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

internal static class Program
{
    private static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    private static Type T(string type) => App.GetType("SnowPakTool.Desktop." + type)!;
    private static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(obj, args);
    private static object? Static(string type, string name, params object?[] args) => T(type).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
    private static object Field(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(control => new[] { control }.Concat(All(control)));
    private static void Wait(Task task) { while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(2); } task.GetAwaiter().GetResult(); }
    private static void Capture(Control control, string name)
    {
        Application.DoEvents(); control.PerformLayout(); Application.DoEvents(); using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, name + ".png"));
    }
    private static object Record(Type type, Dictionary<string, object?> values)
    {
        var ctor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderByDescending(ctor => ctor.GetParameters().Length).First();
        return ctor.Invoke(ctor.GetParameters().Select(parameter => values.TryGetValue(parameter.Name!, out var value) ? value : parameter.HasDefaultValue ? parameter.DefaultValue
            : parameter.ParameterType == typeof(string) ? "fixture" : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray());
    }
    [STAThread] static void Main()
    {
        Application.EnableVisualStyles();
        var root = Path.Combine(AppContext.BaseDirectory, "fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var rulesStore = Activator.CreateInstance(T("TruckRandomizationRuleStore"), Path.Combine(root, "rules.json"))!;
        using var randomizer = (Control)Activator.CreateInstance(T("TruckRandomizationTab"), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object?[] { null, rulesStore }, null)!;
        randomizer.Size = new Size(1400, 900); randomizer.CreateControl(); Static("TextAreaStyle", "Watch", randomizer); randomizer.PerformLayout();
        Capture(randomizer, "randomization-rules");
        foreach (var panel in All(randomizer).OfType<Panel>().Where(panel => panel.AutoScroll))
            Console.WriteLine($"Rule layout: body={panel.Height}, list={panel.Controls[0].Height}, preferred={panel.Controls[0].PreferredSize.Height}, style={((TableLayoutPanel)panel.Parent!).RowStyles[1].SizeType}/{((TableLayoutPanel)panel.Parent!).RowStyles[1].Height}");
        var log = (TextBox)Field(randomizer, "_messages");
        Check(log.BackColor == Color.FromArgb(248, 248, 248) && All(randomizer).OfType<Panel>().Any(panel => panel.AutoScroll && panel.BackColor == Color.FromArgb(248, 248, 248)), "logs and randomization rules use very light gray text surfaces");
        var fuelRoot = Path.Combine(root, "workspace", "initial.pak", "original_files", "[media]", "classes", "trucks"); Directory.CreateDirectory(fuelRoot);
        File.WriteAllText(Path.Combine(fuelRoot, "fixture.xml"), "<Truck><TruckData TruckType=\"SCOUT\" FuelCapacity=\"90\"/><GameData Country=\"US\" UnlockByRank=\"1\" Price=\"1000\"><UiDesc UiName=\"fixture\"/></GameData></Truck>");
        var workspace = Path.Combine(root, "workspace");
        var entries = Array.CreateInstance(T("RegionTruckEntry"), 0);
        var catalog = Activator.CreateInstance(T("RegionTruckCatalog"), entries, Array.Empty<string>(), 1, null)!;
        var getter = Expression.Lambda(typeof(Func<>).MakeGenericType(typeof(Task<>).MakeGenericType(T("RegionTruckCatalog"))), Expression.Call(typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(T("RegionTruckCatalog")), Expression.Constant(catalog))).Compile();
        using var properties = (Control)Activator.CreateInstance(T("TruckPropertiesTab"), new Func<string>(() => workspace), getter)!;
        properties.Size = new Size(1500, 750); properties.CreateControl(); Static("TextAreaStyle", "Watch", properties);
        Wait((Task)Call(properties, "EnsureLoadedAsync", false)!);
        var grid = (DataGridView)Field(properties, "_grid"); var row = grid.Rows[0];
        Check((string)row.Cells["Regions"].Value! == "─────" && row.Cells["Regions"].ToolTipText.Contains("No verified"), "no verified placement uses a five-character line with an explanatory tooltip");
        row.Cells["FuelCapacity"].Value = 99m;
        Check((decimal)row.Cells["FuelCapacity"].Value! == 100m, "individual fuel changes round to 5 litres");
        ((TrackBar)Field(properties, "_fuelMultiplier")).Value = 3; Call(properties, "MultiplyFuel"); row = grid.Rows[0];
        Check((decimal)row.Cells["FuelCapacity"].Value! == 150m, "fuel multiplication rounds midpoint values to the next 5 litres");
        row.Cells["Tier"].Value = "SS"; Call(properties, "RefreshColors", null, EventArgs.Empty);
        Check(row.Cells["Tier"].Style.BackColor == (Color)Static("TierListStore", "BackgroundFor", "SS")!, "tier cells reuse the exact tier pane tint");
        Capture(properties, "truck-properties");
        Check(File.ReadAllText(Path.Combine(fuelRoot, "fixture.xml")).Contains("FuelCapacity=\"90\""), "preview rounding leaves truck files unchanged");
        using var owned = (Control)Activator.CreateInstance(T("OwnedContentTab"), new Func<string>(() => workspace), Activator.CreateInstance(T("OwnedRegionState"))!)!;
        using var winches = (Control)Activator.CreateInstance(T("TruckEditTab"), new Func<string>(() => workspace), owned)!;
        winches.Size = new Size(1500, 900); winches.CreateControl();
        var types = (IList)Field(winches, "_matrixWinchClasses");
        types.Add(Record(T("TruckEditTab").GetNestedType("WinchClass", BindingFlags.NonPublic)!, new(StringComparer.OrdinalIgnoreCase) { ["Code"] = "w_fixture", ["DisplayName"] = "Fixture winch" }));
        var matrix = (DataGridView)Field(winches, "_winchGrid"); Call(winches, "ConfigureWinchColumns", matrix);
        var truckType = T("TruckEditTab").GetNestedType("TruckRow", BindingFlags.NonPublic)!;
        foreach (var (name, code, truckClass, compatible) in new[] { ("First truck", "first_id", "Scout", true), ("Second truck", "second_id", "Offroad", false) })
        {
            var index = matrix.Rows.Add(name, truckClass, "Open", compatible);
            var set = new HashSet<string>(compatible ? new[] { "w_fixture" } : Array.Empty<string>());
            matrix.Rows[index].Tag = Activator.CreateInstance(truckType, Path.Combine(root, code + ".xml"), name, true, set, new HashSet<string>(set), "fixture")!;
        }
        var headers = Field(winches, "_winchHeaders"); Call(headers, "Refresh");
        Check(!All(winches).OfType<TextBox>().Any(box => box.AccessibleName == "Search truck winch compatibility"), "winches uses whole-tab search without a separate search box");
        var filter = Call(headers, "AddFilter", "w_fixture", null, Array.Empty<string>())!;
        Call(filter, "SelectValues", (object)new[] { "Compatible" });
        Check(matrix.Rows.Cast<DataGridViewRow>().Count(row => row.Visible) == 1 && matrix.Rows[0].Visible, "winch header filters can select compatible trucks");

        Call(filter, "Clear");
        Check(matrix.Rows.Cast<DataGridViewRow>().All(row => row.Visible) && matrix.Rows[0].Cells[3].Value is true && matrix.Rows[1].Cells[3].Value is false, "clearing winch filters restores rows and preserves compatibility values");
        Capture(winches, "winch-search");
        var bounds = new Rectangle(0, 0, 100, 24);
        var off = (Rectangle)Static("TableHeaderCell", "TextBounds", bounds, 1f, true, SortOrder.None)!;
        var on = (Rectangle)Static("TableHeaderCell", "TextBounds", bounds, 1f, true, SortOrder.Ascending)!;
        Check(off.Width == 74 && on.Width == 64, "headers reserve only the visible icons, reclaiming text width when sorting is off");
        Console.WriteLine("Small-batch checks passed using isolated fixtures; screenshots captured.");
    }
}
