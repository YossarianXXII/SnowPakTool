using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace SnowPakTool.Desktop;

internal sealed record WorkspaceLayoutMove(string Source, string Destination);
internal sealed record WorkspaceLayoutResult(string Root, IReadOnlyList<WorkspaceLayoutMove> Moves, IReadOnlyList<string> RewrittenMetadata);

internal static class WorkspaceLayout
{
    public static WorkspaceLayoutResult Migrate(string workspace)
    {
        var root = WorkspaceOriginals.GetWorkspaceRoot(workspace);
        var moves = new List<WorkspaceLayoutMove>(); var rewritten = new List<string>();
        if (!Directory.Exists(root)) return new(root, moves, rewritten);
        var initial = Path.Combine(root, "initial.pak");
        if (File.Exists(initial)) throw new IOException("The initial.pak workspace folder conflicts with a file: " + initial);
        var metadata = Path.Combine(root, ".snowpaktool");
        var metadataJson = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(metadata))
            foreach (var file in Directory.EnumerateFiles(metadata, "*.json", SearchOption.AllDirectories)) metadataJson.Add(Path.Combine(root, Path.GetRelativePath(metadata, file)));
        var maps = Path.Combine(root, "region-maps");
        if (Directory.Exists(maps)) foreach (var file in Directory.EnumerateFiles(maps, "*.json", SearchOption.AllDirectories)) metadataJson.Add(file);
        metadataJson.Add(Path.Combine(root, "progression-settings.json"));
        var mappings = new[] { (metadata, root), (Path.Combine(root, "original_files"), Path.Combine(initial, "original_files")),
            (Path.Combine(root, "updated"), Path.Combine(initial, "updated")) };
        var backupRoot = Path.Combine(root, "layout-backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));

        void Check(string path)
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Layout migration path is outside the workspace: " + full);
            for (var parent = full; parent is not null && !parent.Equals(root, StringComparison.OrdinalIgnoreCase); parent = Path.GetDirectoryName(parent))
                if ((File.Exists(parent) || Directory.Exists(parent)) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Layout migration does not follow linked paths: " + parent);
        }

        bool EqualFiles(string first, string second)
        {
            if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
            using var a = File.OpenRead(first); using var b = File.OpenRead(second);
            return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
        }

        void Move(string source, string destination)
        {
            Check(source); Check(destination);
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Layout migration does not follow linked paths: " + source);
            if (Directory.Exists(source))
            {
                if (!Directory.Exists(destination) && !File.Exists(destination))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!); Directory.Move(source, destination);
                    moves.Add(new(source, destination)); return;
                }
                if (File.Exists(destination)) PreserveConflict(destination);
                Directory.CreateDirectory(destination);
                foreach (var item in Directory.EnumerateFileSystemEntries(source).ToArray()) Move(item, Path.Combine(destination, Path.GetFileName(item)));
                Directory.Delete(source, recursive: false); return;
            }
            if (File.Exists(destination) && EqualFiles(source, destination))
            { File.Delete(source); moves.Add(new(source, destination)); return; }
            if (File.Exists(destination) || Directory.Exists(destination)) PreserveConflict(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Move(source, destination);
            moves.Add(new(source, destination));
        }

        void PreserveConflict(string destination)
        {
            var backup = Path.Combine(backupRoot, Path.GetRelativePath(root, destination));
            Check(destination); Check(backup); Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            if (Directory.Exists(destination)) Directory.Move(destination, backup); else File.Move(destination, backup);
            moves.Add(new(destination, backup));
        }

        if (Directory.Exists(metadata))
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(metadata).ToArray()) Move(item, Path.Combine(root, Path.GetFileName(item)));
            Directory.Delete(metadata, recursive: false);
        }
        foreach (var name in new[] { "original_files", "updated" })
        {
            var source = Path.Combine(root, name);
            if (Directory.Exists(source)) Move(source, Path.Combine(initial, name));
        }
        foreach (var file in metadataJson.Where(File.Exists))
        {
            var node = JsonNode.Parse(File.ReadAllText(file)); var changed = false;
            JsonNode? Rewrite(JsonNode? value)
            {
                if (value is JsonObject obj)
                { foreach (var key in obj.Select(pair => pair.Key).ToArray()) { var before = obj[key]; var after = Rewrite(before); if (!ReferenceEquals(before, after)) obj[key] = after; } }
                else if (value is JsonArray array)
                { for (var i = 0; i < array.Count; i++) { var before = array[i]; var after = Rewrite(before); if (!ReferenceEquals(before, after)) array[i] = after; } }
                else if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && Path.IsPathRooted(text))
                    foreach (var (oldPath, newPath) in mappings)
                    {
                        var normalized = text.Replace('/', Path.DirectorySeparatorChar);
                        if (normalized.Equals(oldPath, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(oldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        { changed = true; return JsonValue.Create(newPath + normalized[oldPath.Length..]); }
                    }
                return value;
            }
            Rewrite(node);
            if (changed)
            {
                var backup = Path.Combine(backupRoot, "metadata", Path.GetRelativePath(root, file));
                Check(backup); Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(file, backup, overwrite: false);
                var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temporary, node!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, file, overwrite: true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                rewritten.Add(file);
            }
        }
        return new(root, moves, rewritten);
    }
}
