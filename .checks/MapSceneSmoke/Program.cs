using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

var assembly = Assembly.Load("SnowPakTool.Desktop");
object? Call(string type, string method, params object?[] values) => assembly.GetType("SnowPakTool.Desktop." + type)!
    .GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, values);
var workspace = Path.GetFullPath(args[0]);
var scratch = Path.Combine(AppContext.BaseDirectory, "real-map-" + Guid.NewGuid().ToString("N"));
var root = Path.Combine(scratch, "region-maps");
Directory.CreateDirectory(Path.Combine(root, "backups", "level_us_01_01"));
Directory.CreateDirectory(Path.Combine(root, "updated"));
var baseline = Path.Combine(root, "backups", "level_us_01_01", "original.pak");
var output = Path.Combine(root, "updated", "level_us_01_01.pak");
var source = Path.Combine(workspace, "region-maps", "updated", "level_us_01_01.pak");
string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
byte[] Scene(string path) { using var zip = ZipFile.OpenRead(path); using var input = zip.Entries.Single(e => e.FullName.Replace('\\', '/') == "[scene]/data_for_ssl.bin").Open(); using var memory = new MemoryStream(); input.CopyTo(memory); return memory.ToArray(); }
var sourceHash = Hash(source);
File.Copy(Path.Combine(workspace, "region-maps", "backups", "level_us_01_01", "original.pak"), baseline);
File.Copy(source, output);
var broken = Scene(output); var original = Scene(baseline); var originalHash = Hash(baseline);
if ((int)Call("TruckReplacementStore", "RepairSavedMaps", scratch)! != 1) throw new Exception("Expected one repaired map.");
var fixedScene = Scene(output); Call("TruckReplacementStore", "ValidateScene", fixedScene);
if (!broken.AsSpan(12).SequenceEqual(fixedScene.AsSpan(12))) throw new Exception("Repair changed truck or scene payload.");
if (Hash(source) != sourceHash || Hash(baseline) != originalHash) throw new Exception("Repair changed an input map.");
if ((int)Call("TruckReplacementStore", "RepairSavedMaps", scratch)! != 0) throw new Exception("Repair is not idempotent.");
Console.WriteLine(JsonSerializer.Serialize(new { Output = output, Original = baseline, HeaderEndBefore = BinaryPrimitives.ReadInt32LittleEndian(broken.AsSpan(4, 4)),
    HeaderEndAfter = BinaryPrimitives.ReadInt32LittleEndian(fixedScene.AsSpan(4, 4)), StoredLength = BinaryPrimitives.ReadInt32LittleEndian(fixedScene.AsSpan(8, 4)),
    ActualLength = fixedScene.Length, OriginalLength = original.Length, SourceUnchanged = true }, new JsonSerializerOptions { WriteIndented = true }));
