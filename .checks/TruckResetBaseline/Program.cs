using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

internal static class Program
{
    private static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    private static Type T(string name) => App.GetType("SnowPakTool.Desktop." + name)!;
    private static object? Call(object obj, string name, params object?[] args) => obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(obj, args);
    private static object? Store(string name, params object?[] args) => T("TruckPropertiesStore").GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
    private static object Field(object obj, string name) => obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static object Make(string name, Dictionary<string, object?> values)
    {
        var ctor = T(name).GetConstructors().OrderByDescending(ctor => ctor.GetParameters().Length).First();
        return ctor.Invoke(ctor.GetParameters().Select(parameter => values.GetValueOrDefault(parameter.Name!, parameter.HasDefaultValue ? parameter.DefaultValue : parameter.ParameterType == typeof(string) ? "" : Activator.CreateInstance(parameter.ParameterType))).ToArray());
    }
    private static object Edits(string file, int before, int after, string attribute = "FuelCapacity") => Store("ReadEdits", JsonSerializer.Serialize(new[] { new { RelativeFile = file, TruckName = "Test truck", Attribute = attribute, Before = before, After = after } }))!;
    private static void Wait(Task task)
    {
        var end = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(10); }
        if (!task.IsCompleted) throw new TimeoutException();
        task.GetAwaiter().GetResult();
    }
    private static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(All(child)));
    [STAThread] private static void Main(string[] args)
    {
        var workspace = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        const string relative = "[media]/classes/trucks/chevrolet_ck1500.xml";
        var original = Path.Combine(workspace, "initial.pak", "original_files", relative);
        var updated = Path.Combine(workspace, "initial.pak", "updated", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(original)!);
        const string source = "<_templates><TruckData FuelCapacity=\"999\" /></_templates>\r\n<Truck>\r\n<TruckData FuelCapacity=\"80\" TruckType=\"SCOUT\" DiffLockType=\"Always\" />\r\n<GameData UnlockByRank=\"1\" Price=\"3000\" Country=\"US\"><UiDesc UiName=\"TEST_NAME\" /></GameData>\r\n<Unrelated Keep=\"true\" />\r\n</Truck>";
        File.WriteAllText(original, source);
        var edits = Edits(relative, 80, 90);
        Store("SavePending", workspace, edits);
        Check(File.ReadAllText(original) == source && !File.Exists(updated), "staging never writes game data");
        var patched = (string)Store("Patch", source, edits)!;
        Check(patched == source.Replace("FuelCapacity=\"80\"", "FuelCapacity=\"90\""), "patch changes only the real truck attribute, preserving templates and XML formatting");
        Directory.CreateDirectory(Path.GetDirectoryName(updated)!);
        File.WriteAllText(updated, source.Replace("Keep=\"true\"", "Keep=\"external-edit\""));
        Store("Apply", workspace);
        Check(File.ReadAllText(original) == source && File.ReadAllText(updated) == patched.Replace("Keep=\"true\"", "Keep=\"external-edit\""), "Apply preserves originals and unrelated editor changes");
        Check(((IList)Store("LoadPending", workspace)!).Count == 0, "successful Apply clears staged snapshot");
        Store("SavePending", workspace, Edits(relative, 90, 100));
        File.WriteAllText(updated, patched.Replace("FuelCapacity=\"90\"", "FuelCapacity=\"95\""));
        var refused = false;
        try { Store("Apply", workspace); } catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { refused = true; }
        Check(refused && ((IList)Store("LoadPending", workspace)!).Count == 1 && File.ReadAllText(updated).Contains("FuelCapacity=\"95\""), "conflicting staged values rejected and retained");
        var preset = (string)Store("SerializePreset", edits)!;
        Check(((IList)Store("ReadPreset", preset)!).Count == 1, "versioned preset round trip");
        foreach (var invalid in new[] { "../escape.xml", "[media]/classes/trucks/../../other.xml" })
        {
            refused = false; try { Edits(invalid, 80, 90); } catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { refused = true; }
            Check(refused, "unsafe preset path rejected: " + invalid);
        }
        refused = false; try { Edits(relative, 1, 31, "UnlockByRank"); } catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { refused = true; }
        Check(refused, "out of range preset rank rejected");
        File.WriteAllText(updated, source);
        Store("SavePending", workspace, Edits(relative, 80, 90));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(original)!, "unknown.xml"), "<Truck><TruckData TruckType=\"SCOUT\"/><GameData><UiDesc UiName=\"Unknown\"/></GameData></Truck>");
        var entries = Array.CreateInstance(T("RegionTruckEntry"), 1);
        var originalEntry = Make("RegionTruckEntry", new() { ["TruckCode"] = "gmc_9500", ["Truck"] = "GMC", ["RegionCode"] = "us_01", ["Region"] = "Michigan", ["Map"] = "Black River", ["Availability"] = "Obtainable — discovery/start" });
        entries.SetValue(Make("RegionTruckEntry", new() { ["TruckCode"] = "chevrolet_ck1500", ["Truck"] = "Chevrolet", ["RegionCode"] = "us_01", ["Region"] = "Michigan", ["Map"] = "Black River", ["Availability"] = "Obtainable — discovery/start", ["OriginalTruckCode"] = "gmc_9500", ["FileEntry"] = originalEntry }), 0);
        var catalog = Activator.CreateInstance(T("RegionTruckCatalog"), entries, Array.Empty<string>(), 1, null)!;
        var taskType = typeof(Task<>).MakeGenericType(T("RegionTruckCatalog"));
        var getterType = typeof(Func<>).MakeGenericType(taskType);
        var getter = Expression.Lambda(getterType, Expression.Call(typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(T("RegionTruckCatalog")), Expression.Constant(catalog, T("RegionTruckCatalog")))).Compile();
        using var tab = (Control)Activator.CreateInstance(T("TruckPropertiesTab"), new Func<string>(() => workspace), getter)!;
        Check(((IEnumerable)Call(tab, "PendingChanges", workspace)!).Cast<object>().Count() == 1, "Start lists persistent changes before tab is opened");
        var pendingRow = ((IEnumerable)Call(tab, "PendingChanges", workspace)!).Cast<object>().Single();
        var target = (string)pendingRow.GetType().GetProperty("Target")!.GetValue(pendingRow)!;
        var displayedPath = (string)T("WorkspaceOriginals").GetMethod("GetRelativePath")!.Invoke(null, new object[] { workspace, target })!;
        Check(displayedPath.Replace('\\', '/') == relative, "pending change targets satisfy Start dashboard path conversion");
        tab.Size = new Size(1350, 650); tab.CreateControl();
        Wait((Task)Call(tab, "EnsureLoadedAsync", false)!);
        var grid = (DataGridView)Field(tab, "_grid");
        Check(grid.Rows.Count == 2, "truck catalog loads explicit truck definitions");
        var row = grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToString(row.Cells["Truck"].Value) == "chevrolet_ck1500");
        Check(Convert.ToDecimal(row.Cells["FuelCapacity"].Value) == 90, "restart restores staged preview");
        Check(Convert.ToString(row.Cells["Regions"].Value) == "Michigan / Black River (replacement) [pending]", "obtainable region follows current randomized replacement");
        var unknown = grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToString(row.Cells["Truck"].Value) == "unknown");
        Check(unknown.Cells["FuelCapacity"].ReadOnly && unknown.Cells["FuelCapacity"].Value is null, "undefined numeric values remain blank and uneditable");
        Check(!All(tab).OfType<Button>().Any(button => button.Text is "+" or "−"), "old bulk adjustment buttons removed");
        var slider = (TrackBar)Field(tab, "_fuelMultiplier");
        Check(slider.Minimum == 1 && slider.Maximum == 6 && slider.Value == 2 && slider.SmallChange == 1, "fuel multiplier selects 0.5–3 in 0.5 steps, default 1");
        slider.Value = 3;
        Check(Convert.ToDecimal(row.Cells["FuelCapacity"].Value) == 90, "moving the slider does not edit or stage fuel");
        grid.CurrentCell = row.Cells["FuelCapacity"];
        Check(grid.BeginEdit(false) && grid.EditingControl is TextBox, "individual fuel editing remains available");
        var editor = (TextBox)grid.EditingControl!;
        Check(editor.Text == "90", "editing shows the raw number without original-value annotation");
        editor.Text = "99";
        Check(grid.EndEdit() && Convert.ToDecimal(row.Cells["FuelCapacity"].Value) == 100, "individual fuel edit rounds to the nearest 5 litres");
        grid.CurrentCell = row.Cells["Truck"];
        Check(Convert.ToString(row.Cells["FuelCapacity"].FormattedValue) == "100 (80)" && row.Cells["FuelCapacity"].Style.BackColor == Color.LightGoldenrodYellow,
            "changed fuel is highlighted and displays preserved original in parentheses");
        Call(tab, "MultiplyFuel");
        row = grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToString(row.Cells["Truck"].Value) == "chevrolet_ck1500");
        Check(Convert.ToDecimal(row.Cells["FuelCapacity"].Value) == 150, "1.5 multiplier applies to the current individual value and rounds to 5 litres");
        Check(Convert.ToDecimal(row.Cells["UnlockByRank"].Value) == 1 && Convert.ToDecimal(row.Cells["Price"].Value) == 3000
            && grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToString(row.Cells["Truck"].Value) == "unknown").Cells["FuelCapacity"].Value is null,
            "fuel multiplier preserves other properties and undefined fuel");
        row.Cells["FuelCapacity"].Value = 100000m; slider.Value = 6; Call(tab, "MultiplyFuel");
        row = grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToString(row.Cells["Truck"].Value) == "chevrolet_ck1500");
        Check(Convert.ToDecimal(row.Cells["FuelCapacity"].Value) == 100000, "multiplier respects fuel capacity upper limit");
        Call(tab, "RestoreDraft"); Call(tab, "ShowRows");
        row = grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToString(row.Cells["Truck"].Value) == "chevrolet_ck1500");
        Check(Convert.ToDecimal(row.Cells["FuelCapacity"].Value) == 90 && File.ReadAllText(updated) == source, "revert unstaged edits restores staged snapshot without writing files");
        slider.Value = 1; Call(tab, "MultiplyFuel");
        row = grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToString(row.Cells["Truck"].Value) == "chevrolet_ck1500");
        Check(Convert.ToDecimal(row.Cells["FuelCapacity"].Value) == 45, "0.5 multiplier rounds the halved capacity to 5 litres");
        var menu = (ContextMenuStrip)Field(tab, "_columnsMenu");
        var differential = menu.Items.Cast<ToolStripMenuItem>().Single(item => item.Name == "DiffLock");
        Check(!grid.Columns["DiffLock"]!.Visible && !differential.Checked && menu.Items.Cast<ToolStripMenuItem>().Where(item => item != differential).All(item => item.Checked),
            "Differential Lock alone starts hidden with an unchecked column checkbox");
        differential.PerformClick();
        Check(grid.Columns["DiffLock"]!.Visible && differential.Checked, "column dropdown reveals Differential Lock");
        differential.PerformClick();
        Check(!grid.Columns["DiffLock"]!.Visible, "column dropdown hides Differential Lock again");
        row.Cells["FuelCapacity"].Value = 100m;
        Call(tab, "Stage"); row.Cells["FuelCapacity"].Value = 110m;
        Call(tab, "SavePendingChanges");
        Check(File.ReadAllText(updated).Contains("FuelCapacity=\"100\""), "Apply uses explicit staged snapshot and excludes later preview");
        Wait((Task)Call(tab, "EnsureLoadedAsync", false)!);
        row = grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToString(row.Cells["Truck"].Value) == "chevrolet_ck1500");
        Check(Convert.ToString(row.Cells["FuelCapacity"].FormattedValue) == "100 (80)" && row.Cells["FuelCapacity"].Style.BackColor == Color.Honeydew,
            "applied fuel retains original-value annotation and applied highlight");
        All(tab).OfType<Button>().Single(button => button.Text == "Order by tier").PerformClick();
        Check(grid.SortedColumn?.Name == "Tier", "tier sort wired to table");
        grid.Rows[0].Cells["Tier"].Value = "S"; grid.Rows[1].Cells["Tier"].Value = "SS";
        grid.Sort(grid.Columns["Tier"]!, System.ComponentModel.ListSortDirection.Ascending);
        Check(Convert.ToString(grid.Rows[0].Cells["Tier"].Value) == "SS", "tier ordering uses assigned tier strength rather than alphabetic order");
        All(tab).OfType<Button>().Single(button => button.Text == "Reset to original values").PerformClick();
        row = grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToString(row.Cells["Truck"].Value) == "chevrolet_ck1500");
        Check(Convert.ToDecimal(row.Cells["FuelCapacity"].Value) == 80 && File.ReadAllText(updated).Contains("FuelCapacity=\"100\""), "reset to preserved baseline is preview only");
        Call(tab, "Stage");
        All(tab).OfType<Button>().Single(button => button.Text == "Revert staged changes").PerformClick();
        Check(((IList)Store("LoadPending", workspace)!).Count == 0, "revert staged changes clears persistent intent");
        row = grid.Rows.Cast<DataGridViewRow>().Single(row => Convert.ToString(row.Cells["Truck"].Value) == "chevrolet_ck1500");
        row.Cells["FuelCapacity"].Value = 160m;
        if (!args.Contains("--no-visual")) {
        tab.PerformLayout(); using var bitmap = new Bitmap(tab.Width, tab.Height); tab.DrawToBitmap(bitmap, new Rectangle(Point.Empty, tab.Size));
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, "truck-properties-preview.png"));
        }
        if (args.Length > 0 && args[0] != "--no-visual")
        {
            Wait((Task)T("WorkspaceDataCache").GetMethod("PreloadOwnedAsync")!.Invoke(null, new object[] { args[0] })!);
            var real = (IList)Store("LoadRows", args[0])!;
            Check(real.Count > 100, "real workspace catalog loads more than 100 truck definitions: " + real.Count);
        }
        Console.WriteLine("PASS all Truck properties regression checks; fixtures isolated from user workspace.");
    }
}
