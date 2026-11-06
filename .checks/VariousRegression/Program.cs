using SnowPakTool.Desktop;
using System.Security.Cryptography;
using System.Reflection;

internal static class Program
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    [STAThread]
    private static void Main(string[] args)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "initial.pak", "original_files"));
        const string relative = "[media]/classes/wheels/test.xml";
        const string sample = "<!-- keep -->\r\n<_templates><WheelFriction><Default BodyFriction='2'/><Chains IsIgnoreIce='true'/></WheelFriction><TruckTire><Mud><WheelFriction _template='Default'/></Mud></TruckTire></_templates>\r\n<TruckWheels><TruckTires><TruckTire Name='one'><WheelFriction BodyFriction='3' IsIgnoreIce='false'/><GameData Price='10'/></TruckTire><TruckTire Name='inherited' _template='Mud'/></TruckTires><TruckRims><TruckRim Name='untouched'/></TruckRims></TruckWheels>";
        var original = WorkspaceOriginals.GetOriginalPath(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(original)!); File.WriteAllText(original, sample);
        var edits = TireIceStore.Preview(root, true);
        Check(edits.Count == 4, "Includes global/local templates, explicit and inherited tires; chains unchanged");
        TireIceStore.Save(root, edits);
        Check(TireIceStore.Load(root).SequenceEqual(edits), "Staging persists");
        Check(File.ReadAllText(original) == sample && !File.Exists(WorkspaceOriginals.GetUpdatedPath(root, relative)), "Preview/stage preserves files");
        var changed = TireIceStore.Patch(sample, edits);
        Check(TireIceStore.Targets(changed).All(t => t.Exists && t.Value == "true"), "All targets enabled");
        Check(changed.Contains("BodyFriction='3'") && changed.Contains("<GameData Price='10'/>") && changed.StartsWith("<!-- keep -->\r\n"), "Unrelated text preserved");
        Check(TireIceStore.Patch(changed, edits) == changed, "Idempotent apply");
        try { TireIceStore.Patch(sample.Replace("Name='one'", "Name='different'"), edits); throw new Exception("Missed identity conflict"); }
        catch (InvalidDataException) { }
        try { TireIceStore.Patch(sample.Replace("IsIgnoreIce='false'", "IsIgnoreIce='changed'"), edits); throw new Exception("Missed value conflict"); }
        catch (InvalidDataException) { }
        TireIceStore.Apply(root);
        Check(TireIceStore.Load(root).Count == 0, "Applied snapshot cleared");
        Check(File.ReadAllText(original) == sample, "Original remains immutable");
        var reset = TireIceStore.Preview(root, false);
        var restored = TireIceStore.Patch(changed, reset);
        Check(TireIceStore.Targets(restored).Select(t => (t.Path,t.Exists,t.Value)).SequenceEqual(TireIceStore.Targets(sample).Select(t => (t.Path,t.Exists,t.Value))), "Restores exact attribute presence and chain values");
        TireIceStore.Save(root, reset); TireIceStore.Apply(root);
        Check(TireIceStore.Preview(root, false).Count == 0, "Reset leaves no further changes");

        var conflictRoot = Path.Combine(root, "conflict");
        Directory.CreateDirectory(Path.Combine(conflictRoot, "initial.pak", "original_files"));
        foreach (var name in new[] { "a.xml", "z.xml" })
        {
            var path = WorkspaceOriginals.GetOriginalPath(conflictRoot, "[media]/classes/wheels/" + name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, sample);
        }
        TireIceStore.Save(conflictRoot, TireIceStore.Preview(conflictRoot, true));
        File.WriteAllText(WorkspaceOriginals.GetOriginalPath(conflictRoot, "[media]/classes/wheels/z.xml"), sample.Replace("Name='one'", "Name='conflict'"));
        try { TireIceStore.Apply(conflictRoot); throw new Exception("Missed second-file conflict"); }
        catch (InvalidDataException) { }
        Check(!Directory.Exists(WorkspaceOriginals.GetUpdatedRoot(conflictRoot)), "Validates all files before any output");
        Check(TireIceStore.Load(conflictRoot).Count == 8, "Conflict retains staged snapshot");

        using (var tab = new VariousEditorTab(() => root))
        {
            tab.EnsureLoaded();
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var toggle = (CheckBox)typeof(VariousEditorTab).GetField("_ignoreIce", flags)!.GetValue(tab)!;
            var stage = (Button)typeof(VariousEditorTab).GetField("_stage", flags)!.GetValue(tab)!;
            Check(toggle.Enabled && !toggle.Checked, "Toggle loads original mixed state");
            toggle.Checked = true; stage.PerformClick();
            Check(TireIceStore.Load(root).Count == 4, "Toggle and stage persist full snapshot");
            toggle.Checked = false;
            Check(TireIceStore.Load(root).Count == 4, "Later preview leaves staged snapshot unchanged");
            using var restarted = new VariousEditorTab(() => root); restarted.EnsureLoaded();
            Check(((CheckBox)typeof(VariousEditorTab).GetField("_ignoreIce", flags)!.GetValue(restarted)!).Checked, "Restart restores staged toggle");
            restarted.Size = new Size(1000, 450); restarted.CreateControl();
            using var bitmap = new Bitmap(restarted.Width, restarted.Height);
            restarted.DrawToBitmap(bitmap, restarted.ClientRectangle);
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, "various-preview.png"));
        }
        Console.WriteLine("PASS: templates, inherited tires, presence restoration, conflict checks, staging, UI toggle and restart.");

        if (args.Length == 0) return;
        var source = args[0]; var real = Path.Combine(root, "real");
        Directory.CreateDirectory(Path.Combine(real, "initial.pak", "original_files"));
        var files = TireIceStore.Files(source).ToArray();
        var hashes = files.ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
        foreach (var file in files)
        {
            var dest = WorkspaceOriginals.GetOriginalPath(real, WorkspaceOriginals.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest);
        }
        var realEdits = TireIceStore.Preview(real, true);
        TireIceStore.Save(real, realEdits); TireIceStore.Apply(real);
        var all = TireIceStore.Files(real).SelectMany(f => TireIceStore.Targets(File.ReadAllText(f))).ToArray();
        Check(all.Length > 100 && all.All(t => t.Exists && t.Value == "true"), "Full real catalog coverage");
        Check(TireIceStore.Preview(real, true).Count == 0, "Real enable idempotent");
        var realReset = TireIceStore.Preview(real, false); TireIceStore.Save(real, realReset); TireIceStore.Apply(real);
        Check(TireIceStore.Preview(real, false).Count == 0, "Full real catalog restoration");
        foreach (var file in files) Check(hashes[file] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), "Source workspace untouched");
        Console.WriteLine($"PASS: real catalog, {files.Length} files, {all.Length} friction targets, {realEdits.Count} edits; enable/reset in isolated copy only.");
    }
}

namespace SnowPakTool.Desktop
{
    internal static class TruckPropertiesStore
    {
        internal static void WriteAtomic(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    }
    internal static class WorkspaceDataCache
    {
        internal static bool TryGetCurrentFiles(string w, string r, string p, SearchOption s, out IEnumerable<string> files) { files = []; return false; }
        internal static void InvalidateFile(string path) { }
    }
}
