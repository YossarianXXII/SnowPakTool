using System.Reflection;
using System.Diagnostics;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Control.CheckForIllegalCrossThreadCalls = true;
        var workspace = args[0];
        var path = Path.Combine(workspace, "original_files", "[ps]", "rank_settings.sso");
        File.WriteAllText(path, "rankExp = [ 100 ]");
        var assembly = Assembly.Load("SnowPakTool.Desktop");
        using var form = (Form)Activator.CreateInstance(assembly.GetType("SnowPakTool.Desktop.MainForm")!)!;
        form.Size = new Size(1200, 1000);
        Field<TextBox>(form, "_workspacePath").Text = workspace;
        form.Show();
        var start = Field<Control>(form, "_startChanges");
        var cache = assembly.GetType("SnowPakTool.Desktop.WorkspaceDataCache")!;
        Pump((Task)cache.GetMethod("PreloadAsync")!.Invoke(null, [workspace, false])!);
        var experience = Field<Control>(form, "_experienceTab");
        Set(experience, "_isLoading", true);
        Set(experience, "_loadedWorkspace", workspace); Set(experience, "_rankSettingsPath", path);
        Set(experience, "_loadedFileText", "rankExp = [ 100 ]"); Set(experience, "_loadedValues", new List<int> { 100 });
        Field<DataGridView>(experience, "_grid").Rows.Add(2, 100, 150); Set(experience, "_isLoading", false);
        Pump((Task)start.GetType().GetMethod("RefreshAsync")!.Invoke(start, null)!);
        var tabs = form.Controls.OfType<TabControl>().Single();
        if (tabs.TabPages[0].Text != "Start") throw new Exception("Start tab was not renamed.");
        var pak = Field<DataGridView>(start, "_changes");
        if (!pak.Rows.Cast<DataGridViewRow>().Any(row => row.Cells["State"].Value?.ToString() == "Pending" && row.Cells["Change"].Value?.ToString()?.Contains("Rank 2 XP") == true))
            throw new Exception("Actual rank editor changes were not listed on Start.");
        using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new Rectangle(0, 0, form.Width, form.Height));
        image.Save(Path.Combine(AppContext.BaseDirectory, "start-main-preview.png"));
        Field<Button>(start, "_applyButton").PerformClick(); Application.DoEvents();
        Pump((Task)start.GetType().GetMethod("RefreshAsync")!.Invoke(start, null)!);
        var updated = Path.Combine(workspace, "[ps]", "rank_settings.sso");
        if (!File.Exists(updated) || !File.ReadAllText(updated).Contains("150")) throw new Exception("Start did not apply the actual XP edit.");
        if (File.ReadAllText(path) != "rankExp = [ 100 ]") throw new Exception("Start changed the preserved default baseline.");
        if (pak.Rows.Cast<DataGridViewRow>().Any(row => row.Cells["State"].Value?.ToString() == "Pending")) throw new Exception("Applied XP still marked pending.");
        Console.WriteLine("PASS: actual MainForm Start layout, real XP pending detection, Start save and transition to file change.");
        // Dispose rather than Close to avoid writing the user's window/last-workspace preferences.
    }
    private static T Field<T>(object item, string name) => (T)item.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(item)!;
    private static void Set(object item, string name, object value) => item.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(item, value);
    private static void Pump(Task task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted && timer.ElapsedMilliseconds < 15000) { Application.DoEvents(); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); Application.DoEvents();
    }
}
