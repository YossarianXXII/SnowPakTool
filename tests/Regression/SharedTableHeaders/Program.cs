using System.Data;
using System.Globalization;
using System.Reflection;
using System.Linq.Expressions;

internal static class Program
{
    private static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    private static Type T(string name) => App.GetType("SnowPakTool.Desktop." + name)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, args);
    private static object? Static(string type, string name, params object?[] args) => T(type).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object Attach(DataGridView grid) => Static("TableHeaderBehavior", "Attach", grid, null, null, null)!;
    private static object Filter(object headers, string name) => Call(headers, "AddFilter", name, null, Array.Empty<string>())!;
    private static void Select(object filter, params string[] values) => Call(filter, "SelectValues", (object)values);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static void Click(DataGridView grid, string column)
    {
        var args = new DataGridViewCellMouseEventArgs(grid.Columns[column]!.Index, -1, 4, 10, new MouseEventArgs(MouseButtons.Left, 1, 4, 10, 0));
        typeof(DataGridView).GetMethod("OnColumnHeaderMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(grid, new object[] { args });
    }
    [STAThread] static void Main()
    {
        Application.EnableVisualStyles();
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sk-SK");
        using var grid = new DataGridView { AllowUserToAddRows = false, BindingContext = new BindingContext(), Size = new Size(900, 500) };
        grid.CreateControl();
        var headers = Attach(grid);
        var data = new DataTable(); data.Locale = CultureInfo.CurrentCulture;
        data.Columns.Add("Name"); data.Columns.Add("Class"); data.Columns.Add("Mass", typeof(decimal)); data.Columns.Add("odd]column");
        data.Rows.Add("Zulu", "Scout", 2.5m, "O'Brien"); data.Rows.Add("Alpha", "Offroad", 10m, "other"); data.Rows.Add("Beta", "Scout", DBNull.Value, "O'Brien");
        grid.DataSource = data; Call(headers, "Refresh");
        Click(grid, "Mass"); Check((string)data.DefaultView[0]["Name"] == "Beta" && (decimal)data.DefaultView[1]["Mass"] == 2.5m, "bound numeric sort uses actual numbers with blanks first");
        Click(grid, "Mass"); Check((decimal)data.DefaultView[0]["Mass"] == 10m, "second bound click sorts descending");
        Click(grid, "Mass"); Check(data.DefaultView.Sort == "" && (string)data.DefaultView[0]["Name"] == "Zulu", "third bound click restores original source order");
        var classFilter = Filter(headers, "Class"); Select(classFilter, "Scout");
        Check(data.DefaultView.Count == 2, "bound column filter narrows the view without removing source records");
        Select(Filter(headers, "Mass"), "2.5"); Check(data.DefaultView.Count == 1 && (string)data.DefaultView[0]["Name"] == "Zulu", "numeric filtering works with non-English decimal culture and combines with class");
        Select(Filter(headers, "odd]column"), "O'Brien"); Check(data.DefaultView.Count == 1, "filter expressions escape apostrophes and bracketed column names");
        Call(Filter(headers, "Mass"), "Clear"); Select(Filter(headers, "Mass"), ""); Check(data.DefaultView.Count == 1 && (string)data.DefaultView[0]["Name"] == "Beta", "blank numeric values can be selected");
        Call(Filter(headers, "Mass"), "Clear"); Click(grid, "Name"); Click(grid, "Name"); Click(grid, "Name");
        Check(data.DefaultView.Count == 2 && data.DefaultView.Sort == "", "sort off keeps active filters");
        foreach (var column in new[] { "Class", "odd]column" }) Call(Filter(headers, column), "Clear");
        Check(data.Rows.Count == 3 && data.DefaultView.Count == 3, "clearing filters restores every source row");
        var replacement = data.Clone(); replacement.Rows.Add("Gamma", "Scout", 7m, "O'Brien"); replacement.Rows.Add("Delta", "Offroad", 3m, "other");
        Select(classFilter, "Scout"); grid.DataSource = replacement; Call(headers, "Refresh");
        Check(replacement.DefaultView.Count == 1 && (string)replacement.DefaultView[0]["Name"] == "Gamma", "column filters survive table rebinding");
        using var unbound = new DataGridView { AllowUserToAddRows = false, Size = new Size(700, 400) };
        unbound.Columns.Add("Name", "Name"); unbound.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", ValueType = typeof(decimal) });
        var unboundHeaders = Attach(unbound); unbound.CreateControl();
        var first = unbound.Rows[unbound.Rows.Add("Zulu", 2m)]; first.Tag = "first record";
        var second = unbound.Rows[unbound.Rows.Add("Alpha", 10m)]; second.Tag = "second record";
        Call(unboundHeaders, "Refresh"); Click(unbound, "Value"); Click(unbound, "Value");
        Check(ReferenceEquals(unbound.Rows[0], second), "unbound numeric sorting moves the same row objects");
        first.Cells["Value"].Value = 30m;
        Click(unbound, "Value"); Check(ReferenceEquals(unbound.Rows[0], first) && (decimal)first.Cells["Value"].Value! == 30m && (string)first.Tag == "first record", "off restores initial row order while retaining edits and identity");
        unbound.CurrentCell = first.Cells["Value"]; first.Cells["Value"].Value = 31m; Application.DoEvents();
        Check(ReferenceEquals(unbound.CurrentCell, first.Cells["Value"]), "refreshing unrestricted filters preserves the active edit cell");
        RankAndConflictChecks();
        MapPopupAndEngineChecks();
        Application.DoEvents();
        using var bitmap = new Bitmap(grid.Width, grid.Height); grid.DrawToBitmap(bitmap, new Rectangle(Point.Empty, grid.Size)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "shared-headers.png"));
        Console.WriteLine("All shared header checks passed.");
    }

    private static void RankAndConflictChecks()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "fixture-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var original = Path.Combine(workspace, "initial.pak", "original_files", "[ps]", "rank_settings.sso");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!); File.WriteAllText(original, "rankExp = [700, 1000,]\nkeep = 1\n");
        var store = Activator.CreateInstance(T("RankXpStageStore"), Path.Combine(root, "preferences"))!;
        var progression = Activator.CreateInstance(T("RegionProgressionStore"), Path.Combine(root, "order.json"))!;
        using var editor = (Control)Activator.CreateInstance(T("ExperienceEditorTab"), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { new Func<string>(() => workspace), Activator.CreateInstance(T("OwnedRegionState"))!, store, progression }, null)!;
        editor.CreateControl(); Call(editor, "EnsureLoaded");
        var ranks = (DataGridView)Field(editor, "_grid");
        ranks.Rows[0].Cells["UpdatedXP"].Value = 300; ranks.Rows[1].Cells["UpdatedXP"].Value = 100;
        Call(Attach(ranks), "SetSort", "UpdatedXP", SortOrder.Ascending);
        Check(Convert.ToInt32(ranks.Rows[0].Cells["Step"].Value) == 3, "rank editor can sort the display by XP");
        Call(editor, "SaveSettings");
        var staged = Call(store, "Load", workspace)!;
        Check(((List<int>)staged.GetType().GetProperty("Values")!.GetValue(staged)!).SequenceEqual(new[] { 300, 100 }), "staging sorted XP rows retains their original rank numbers");
        Select(Filter(Attach(ranks), "Step"), "3"); Call(editor, "SaveSettings");
        staged = Call(store, "Load", workspace)!;
        Check(((List<int>)staged.GetType().GetProperty("Values")!.GetValue(staged)!).SequenceEqual(new[] { 300, 100 }) && File.ReadAllText(original).Contains("700, 1000"), "filtering ranks neither drops hidden ranks from staging nor writes game data");
        var itemType = T("FileConflictReviewDialog").GetNestedType("Item", BindingFlags.Public | BindingFlags.NonPublic)!;
        var items = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType))!;
        foreach (var name in new[] { "z.xml", "a.xml" })
        {
            var oldFile = Path.Combine(root, "old-" + name); var newFile = Path.Combine(root, "new-" + name);
            File.WriteAllText(oldFile, "old " + name); File.WriteAllText(newFile, "new " + name);
            items.Add(Activator.CreateInstance(itemType, name, oldFile, newFile)!);
        }
        var edits = Path.Combine(root, "edits"); Directory.CreateDirectory(edits);
        using var dialog = (Form)Activator.CreateInstance(T("FileConflictReviewDialog"), items, edits)!;
        var conflicts = (DataGridView)Field(dialog, "_grid"); dialog.CreateControl();
        Call(Attach(conflicts), "SetSort", "Path", SortOrder.Ascending);
        Call(dialog, "GridCellContentClick", conflicts, new DataGridViewCellEventArgs(conflicts.Columns["Pak"]!.Index, 0));
        Check(itemType.GetProperty("SelectedAction")!.GetValue(items[0]) is null && (string)itemType.GetProperty("SelectedAction")!.GetValue(items[1])! == "Pak", "sorted conflict actions target the displayed file, not its old row index");
    }

    private static object Record(Type type, Dictionary<string, object?> values)
    {
        var ctor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).OrderByDescending(ctor => ctor.GetParameters().Length).First();
        return ctor.Invoke(ctor.GetParameters().Select(parameter => values.TryGetValue(parameter.Name!, out var value) ? value
            : parameter.HasDefaultValue ? parameter.DefaultValue : parameter.ParameterType == typeof(string) ? "fixture"
            : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray());
    }

    private static void MapPopupAndEngineChecks()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "ui-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        object Entry(string code, string availability) => Record(T("RegionTruckEntry"), new(StringComparer.OrdinalIgnoreCase)
        { ["RegionCode"] = "us_01", ["MapCode"] = "level_us_01_01", ["TruckCode"] = code, ["Truck"] = code,
            ["Availability"] = availability, ["AvailabilityEditable"] = availability == "Unverified", ["MapDataHash"] = "" });
        var entries = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("RegionTruckEntry")))!;
        entries.Add(Entry("found", "Obtainable — discovery/start")); entries.Add(Entry("mission", "Mission/task only")); entries.Add(Entry("unknown", "Unverified"));
        var catalog = Activator.CreateInstance(T("RegionTruckCatalog"), entries, Array.Empty<string>(), 1, null)!;
        var availability = Activator.CreateInstance(T("RegionTruckAvailabilityStore"), Path.Combine(root, "availability.json"))!;
        using var maps = (Control)Activator.CreateInstance(T("RegionsTrucksTab"), new Func<string>(() => root), new Func<string>(() => ""), availability, null)!;
        maps.Size = new Size(1400, 900); maps.CreateControl();
        maps.GetType().GetField("_catalog", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(maps, catalog);
        ((HashSet<string>)Field(maps, "_selectedRegions")).Add("us_01");
        var grid = (DataGridView)Field(maps, "_trucks"); grid.BindingContext = new BindingContext(); Call(maps, "ApplyFilter");
        Check(grid.Rows.Count == 3, "Map trucks binds all selected-region placements");
        Select(Filter(Attach(grid), "Availability"), "Mission");
        Check(grid.Rows.Count == 2 && grid.Rows.Cast<DataGridViewRow>().Any(row => (string)row.Cells["Truck"].Value! == "unknown"), "Availability funnel groups mission/discovery labels and retains unverified placements");
        Select(Filter(Attach(grid), "Availability")); Check(grid.Rows.Count == 1 && grid.Rows[0].Cells["Availability"] is DataGridViewComboBoxCell, "unverified placements stay editable with all other availability groups hidden");
        Call(Attach(grid), "SetSort", "Truck", SortOrder.Descending); Call(Attach(grid), "SetSort", null, SortOrder.None);
        Check(grid.Rows.Count == 1, "Map trucks sorting off retains its availability filter");
        var availableType = typeof(IReadOnlyList<>).MakeGenericType(T("CustomTruck"));
        var available = Expression.Lambda(typeof(Func<>).MakeGenericType(availableType), Expression.Constant(Array.CreateInstance(T("CustomTruck"), 0), availableType)).Compile();
        using var popup = (Form)Activator.CreateInstance(T("TruckReplacementDialog"), root, entries[0], available, null)!;
        var replacements = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T("ReplacementTruck")))!;
        foreach (var (name, truckClass, tires) in new[] { ("scout_highway", "Scout", "Highway"), ("scout_mud", "Scout", "Mudtires"), ("offroad_mud", "Offroad", "Mudtires") })
        {
            var truck = Record(T("CustomTruck"), new(StringComparer.OrdinalIgnoreCase) { ["Code"] = name, ["Name"] = name, ["Class"] = truckClass });
            var equipment = Record(T("TruckEquipmentSummary"), new(StringComparer.OrdinalIgnoreCase) { ["Tires"] = tires });
            replacements.Add(Activator.CreateInstance(T("ReplacementTruck"), truck, equipment, new Dictionary<string, string>(), null)!);
        }
        popup.GetType().GetField("_trucks", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(popup, replacements);
        popup.CreateControl(); var candidates = (DataGridView)Field(popup, "_list"); candidates.BindingContext = new BindingContext(); Call(popup, "ApplyFilter");
        Select(Filter(Attach(candidates), "Class"), "Scout"); Check(candidates.Rows.Count == 2, "replacement class funnel filters candidates");
        Select(Filter(Attach(candidates), "Default tires"), "Mudtires");
        Check(candidates.Rows.Count == 1 && (string)candidates.Rows[0].Cells["Truck ID"].Value! == "scout_mud", "replacement tire funnel combines with class while retaining candidate identity");
        using var owned = (Control)Activator.CreateInstance(T("OwnedContentTab"), new Func<string>(() => root), Activator.CreateInstance(T("OwnedRegionState"))!)!;
        using var engine = (Control)Activator.CreateInstance(T("EngineEditTab"), new Func<string>(() => root), owned)!;
        engine.Size = new Size(1400, 900); engine.CreateControl();
        var matrixTypes = (System.Collections.IList)Field(engine, "_matrixEngineClasses");
        var engineClassType = T("EngineEditTab").GetNestedType("EngineClass", BindingFlags.NonPublic)!;
        matrixTypes.Add(Record(engineClassType, new(StringComparer.OrdinalIgnoreCase) { ["Code"] = "e_fixture", ["DisplayName"] = "Fixture engine" }));
        Call(engine, "ConfigureCompatibilityColumns");
        var matrix = (DataGridView)Field(engine, "_compatibilityGrid");
        var truckRowType = T("EngineEditTab").GetNestedType("TruckRow", BindingFlags.NonPublic)!;
        foreach (var (name, truckClass, hasEngine) in new[] { ("first", "Scout", false), ("second", "Offroad", true) })
        {
            var index = matrix.Rows.Add(true, name, truckClass, "Base game", hasEngine);
            var row = Activator.CreateInstance(truckRowType, "fixture.xml", name, true,
                new HashSet<string>(hasEngine ? new[] { "e_fixture" } : Array.Empty<string>()), new HashSet<string>(), "fixture")!;
            truckRowType.GetProperty("OriginalOrder")!.SetValue(row, index); matrix.Rows[index].Tag = row;
        }
        var matrixHeaders = Field(engine, "_compatibilityHeaders"); Call(matrixHeaders, "Refresh");
        Click(matrix, "e_fixture"); Check((string)matrix.Rows[0].Cells["Truck"].Value! == "first", "engine boolean columns start ascending");
        Click(matrix, "e_fixture"); Check((string)matrix.Rows[0].Cells["Truck"].Value! == "second", "engine boolean columns cycle descending");
        Click(matrix, "e_fixture"); Check((string)matrix.Rows[0].Cells["Truck"].Value! == "first", "engine sorting off restores original truck order");
        Select(Filter(matrixHeaders, "TruckClass"), "Scout"); Check(matrix.Rows.Cast<DataGridViewRow>().Count(row => row.Visible) == 1, "engine class filter uses the header and preserves compatibility records");
        using var snapshot = new Bitmap(engine.Width, engine.Height); engine.PerformLayout(); engine.DrawToBitmap(snapshot, new Rectangle(Point.Empty, engine.Size)); snapshot.Save(Path.Combine(AppContext.BaseDirectory, "engine-headers.png"));
    }
}
