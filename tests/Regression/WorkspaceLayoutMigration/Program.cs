using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 3 || args[0] != "--apply") throw new ArgumentException("Usage: --apply <workspace-root> <report-path>");
var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
var mappings = new[] { (".snowpaktool", ""), ("original_files", "initial.pak/original_files"), ("updated", "initial.pak/updated") };
var before = mappings.SelectMany(pair =>
{
    var source = Path.Combine(root, pair.Item1); var destination = Path.Combine(root, pair.Item2);
    return !Directory.Exists(source) ? Enumerable.Empty<(string Source, string Destination, string Hash)>()
        : Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Select(file => (Source: file, Destination: Path.Combine(destination, Path.GetRelativePath(source, file)), Hash: Hash(file)));
}).ToArray();
var assembly = Assembly.Load("SnowPakTool.Desktop");
var result = assembly.GetType("SnowPakTool.Desktop.WorkspaceLayout")!.GetMethod("Migrate")!.Invoke(null, [root])!;
var rewritten = ((IEnumerable<string>)result.GetType().GetProperty("RewrittenMetadata")!.GetValue(result)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
foreach (var file in before)
{
    if (!File.Exists(file.Destination)) throw new IOException("A migrated file is missing: " + file.Destination);
    if (!rewritten.Contains(file.Destination) && file.Hash != Hash(file.Destination)) throw new IOException("Migrated file content changed: " + file.Destination);
}
File.WriteAllText(args[2], JsonSerializer.Serialize(new { Migration = JsonSerializer.SerializeToElement(result, result.GetType()), VerifiedFileCount = before.Length, Files = before },
    new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
Console.WriteLine($"Migrated workspace: {root}");
Console.WriteLine($"Verified byte-identical content for {before.Length - rewritten.Count} files; remapped {rewritten.Count} metadata documents.");
Console.WriteLine($"Report: {Path.GetFullPath(args[2])}");
