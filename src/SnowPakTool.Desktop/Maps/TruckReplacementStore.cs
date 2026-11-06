using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SnowPakTool.Desktop;

internal sealed record SavedTruckReplacement(string OutputPath, string OriginalBackupPath, string? PreviousBackupPath, string TruckName);

/// <summary>Writes validated map copies; input map archives and workspace originals are never overwritten.</summary>
internal static class TruckReplacementStore
{
    private const string SceneEntry = "[scene]/data_for_ssl.bin";
    private static readonly object SaveGate = new();

    private static string Root(string workspace) => WorkspaceOriginals.GetMetadataPath(workspace, "region-maps");

    public static IEnumerable<(string Map, string Source)> SavedMaps(string workspace)
    {
        var updated = Path.Combine(Root(workspace), "updated");
        if (!Directory.Exists(updated)) yield break;
        foreach (var file in Directory.EnumerateFiles(updated, "level_*.pak"))
            yield return (Path.GetFileNameWithoutExtension(file), file);
        foreach (var directory in Directory.EnumerateDirectories(updated, "level_*"))
        {
            var file = Path.Combine(directory, "[scene]", "data_for_ssl.bin");
            if (File.Exists(file)) yield return (Path.GetFileName(directory), file);
        }
    }

    public static SavedTruckReplacement Save(string workspace, RegionTruckEntry current, ReplacementTruck replacement)
    {
        lock (SaveGate) return SaveCore(workspace, current, replacement);
    }

    internal const string RepairRequired = "Invalid scene boundaries — repair required";

    public static int RepairSavedMaps(string workspace)
    {
        lock (SaveGate)
        {
            var repaired = 0;
            foreach (var (map, path) in SavedMaps(workspace))
            {
                var data = RegionTruckCatalogService.ReadMapData(path) ?? throw new InvalidDataException("Missing scene for " + map);
                try { ValidateScene(data); continue; } catch (InvalidDataException) { }
                var archive = path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);
                var backups = Path.Combine(Root(workspace), "backups", map);
                var original = Path.Combine(backups, archive ? "original.pak" : "original.bin");
                var baseline = RegionTruckCatalogService.ReadMapData(original) ?? throw new InvalidDataException("Missing original scene for " + map);
                ValidateScene(baseline);
                var delta = data.Length - baseline.Length;
                var oldBoundary = BinaryPrimitives.ReadInt32LittleEndian(baseline.AsSpan(4, 4));
                var newBoundary = (long)oldBoundary + delta;
                // Recognize only the old writer's failure: unchanged envelope and unchanged data after the header.
                if (data.Length < 12 || !data.AsSpan(0, 12).SequenceEqual(baseline.AsSpan(0, 12))
                    || newBoundary < 12 || newBoundary > data.Length
                    || !baseline.AsSpan(oldBoundary).SequenceEqual(data.AsSpan((int)newBoundary)))
                    throw new InvalidDataException("Cannot safely repair " + map + ": its changes do not match the old writer's format.");
                var sourceHash = FileHash(path); var originalHash = FileHash(original);
                if (archive) VerifyOtherEntries(original, path);
                var fixedData = (byte[])data.Clone();
                BinaryPrimitives.WriteInt32LittleEndian(fixedData.AsSpan(4, 4), (int)newBoundary);
                BinaryPrimitives.WriteInt32LittleEndian(fixedData.AsSpan(8, 4), fixedData.Length);
                ValidateScene(fixedData);
                var previous = Path.Combine(backups, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N") + (archive ? ".pak" : ".bin"));
                Backup(path, previous);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    // Use the original archive layout, preserving the replacement scene and every other entry.
                    if (archive) MapArchiveWriter.Write(original, temporary, fixedData); else File.WriteAllBytes(temporary, fixedData);
                    var written = archive ? ReadArchiveMetadata(temporary) : File.ReadAllBytes(temporary);
                    if (!written.AsSpan().SequenceEqual(fixedData) || sourceHash != FileHash(path) || originalHash != FileHash(original))
                        throw new IOException("The repaired map did not pass verification, or its source changed.");
                    File.Move(temporary, path, overwrite: true); repaired++;
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return repaired;
        }
    }

    private static void VerifyOtherEntries(string original, string edited)
    {
        using var a = ZipFile.OpenRead(original); using var b = ZipFile.OpenRead(edited);
        var entries = b.Entries.ToDictionary(entry => entry.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
        if (a.Entries.Count != entries.Count) throw new InvalidDataException("Map repair would change unrelated archive entries.");
        foreach (var entry in a.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!entries.TryGetValue(name, out var other)) throw new InvalidDataException("Map repair would remove an archive entry.");
            if (name.Equals(SceneEntry, StringComparison.OrdinalIgnoreCase)) continue;
            using var first = entry.Open(); using var second = other.Open();
            if (entry.Length != other.Length || !SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second)))
                throw new InvalidDataException("Map repair found unrelated changes. Restore or review those entries first.");
        }
    }

    public static SavedTruckReplacement SaveBatch(string workspace, IReadOnlyList<(RegionTruckEntry Original, ReplacementTruck Replacement)> changes)
    {
        lock (SaveGate)
        {
            if (changes.Count == 0) throw new ArgumentException("No map changes were selected.");
            var first = changes[0].Original;
            var data = RegionTruckCatalogService.ReadMapData(first.Source) ?? throw new InvalidDataException("The map metadata is missing.");
            var hash = Hash(data);
            if (changes.Any(change => change.Original.MapCode != first.MapCode || change.Original.Source != first.Source || change.Original.MapDataHash != hash))
                throw new InvalidDataException("The map changed since these replacements were queued. Reload and review the pending changes.");
            foreach (var change in changes.OrderByDescending(change => change.Original.BinaryOffset))
                data = Patch(data, change.Original, change.Replacement);
            return SaveCore(workspace, first, changes[0].Replacement, data);
        }
    }

    private static SavedTruckReplacement SaveCore(string workspace, RegionTruckEntry current, ReplacementTruck replacement, byte[]? prepared = null)
    {
        if (!Regex.IsMatch(current.MapCode, @"^level_(?:us|ru)_\d{2}_\d{2}(?:_[a-z0-9]+)*$", RegexOptions.IgnoreCase))
            throw new InvalidDataException("The map has no valid map identifier.");
        if (replacement.Configuration is null && replacement.Truck.Code.Equals(current.TruckCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a different replacement truck.");
        if (replacement.Equipment.InstalledAddons is null)
            throw new InvalidDataException("The replacement truck's default equipment could not be resolved.");
        var source = Path.GetFullPath(current.Source);
        var sourceHash = FileHash(source);
        var data = RegionTruckCatalogService.ReadMapData(source) ?? throw new InvalidDataException("The source map has no scene metadata.");
        if (current.MapDataHash.Length == 0 || current.MapDataHash != Hash(data))
            throw new InvalidDataException("The map changed since it was scanned. Reload maps before replacing this truck.");
        var patched = prepared ?? Patch(data, current, replacement);
        var root = Root(workspace);
        var archive = source.EndsWith(".pak", StringComparison.OrdinalIgnoreCase);
        if (!archive && !source.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsupported map file.");
        var output = archive ? Path.Combine(root, "updated", current.MapCode + ".pak")
            : Path.Combine(root, "updated", current.MapCode, "[scene]", "data_for_ssl.bin");
        var sameOutput = source.Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase);
        if (File.Exists(output) && !sameOutput)
            throw new IOException("A saved version of this map already exists. Reload maps to edit that version.");
        var backups = Path.Combine(root, "backups", current.MapCode);
        Directory.CreateDirectory(backups);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var original = Path.Combine(backups, archive ? "original.pak" : "original.bin");
        if (!File.Exists(original)) Backup(source, original);
        string? previous = null;
        if (sameOutput)
        {
            previous = Path.Combine(backups, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N") + (archive ? ".pak" : ".bin"));
            Backup(source, previous);
        }
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (archive)
            {
                MapArchiveWriter.Write(source, temporary, patched);
            }
            else File.WriteAllBytes(temporary, patched);
            // Read back the staged output before publishing it atomically.
            var written = archive ? ReadArchiveMetadata(temporary) : File.ReadAllBytes(temporary);
            if (!written.AsSpan().SequenceEqual(patched)) throw new IOException("The saved map did not pass verification.");
            if (sourceHash != FileHash(source)) throw new IOException("The source map changed during saving. Reload and try again.");
            if (!sameOutput && File.Exists(output)) throw new IOException("A new edited map appeared while saving. Reload maps.");
            File.Move(temporary, output, overwrite: sameOutput);
            return new(output, original, previous, replacement.Truck.Name);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] ReadArchiveMetadata(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.Entries.Single(entry => entry.FullName.Replace('\\', '/').Equals(SceneEntry, StringComparison.OrdinalIgnoreCase));
        using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }

    private static void Backup(string source, string destination)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            using (var input = File.OpenRead(source))
            using (var copy = File.OpenRead(temporary))
                if (!SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(copy))) throw new IOException("The map backup did not pass verification.");
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static byte[] Patch(byte[] data, RegionTruckEntry current, ReplacementTruck replacement)
    {
        ValidateScene(data);
        var headerEnd = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4));
        var offset = current.BinaryOffset;
        if (offset < 0 || !RegionTruckCatalogService.TryString(data, offset, out var model, out var idOffset)
            || model != current.TruckCode || !RegionTruckCatalogService.TryString(data, idOffset, out var id, out var transform)
            || id != current.PlacementId || current.Transform is not { Count: 16 } || transform + 93 > data.Length)
            throw new InvalidDataException("The selected placement no longer matches the map. Reload maps.");
        for (var i = 0; i < 16; i++)
            if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(transform + i * 4, 4)) != BitConverter.SingleToInt32Bits(current.Transform[i]))
                throw new InvalidDataException("The placement transform changed. Reload maps.");
        var addonsStart = transform + 64 + 1 + 24;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(addonsStart, 4));
        if (count > 128 || data[transform + 64] > 1) throw new InvalidDataException("Unsupported placement equipment record.");
        var end = addonsStart + 4;
        for (var i = 0; i < count + 7; i++)
            if (!RegionTruckCatalogService.TryString(data, end, out _, out end)) throw new InvalidDataException("Incomplete placement equipment record.");
        if (end + 4 > data.Length) throw new InvalidDataException("Missing wheel scale in placement record.");
        end += 4;
        if (offset < 12 || end > headerEnd) throw new InvalidDataException("The placement lies outside the supported scene header.");
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write(data.AsSpan(0, offset));
        WriteString(writer, replacement.Truck.Code);
        writer.Write(data.AsSpan(idOffset, addonsStart - idOffset)); // ID, transform, flags and bounds stay byte-identical.
        var configuration = replacement.Configuration;
        var addons = configuration?.Addons ?? replacement.Equipment.InstalledAddons?.Select(addon => addon.Id).ToArray()
            ?? throw new InvalidDataException("Replacement addons are unavailable.");
        if (addons.Count > 128) throw new InvalidDataException("Too many default addons for this placement format.");
        writer.Write((uint)addons.Count);
        foreach (var addon in addons) WriteString(writer, addon);
        foreach (var value in new[] { configuration?.Engine ?? "", configuration?.Gearbox ?? "", configuration?.Winch ?? "",
                     configuration?.Suspension ?? "", configuration?.WheelType ?? "", configuration?.Rim ?? "", configuration?.Tire ?? "" })
            WriteString(writer, value);
        writer.Write(configuration?.WheelScale ?? 0f);
        writer.Write(data.AsSpan(end)); // Preserve the untouched placement suffix and all following data.
        writer.Flush();
        var patched = output.ToArray();
        // The scene envelope stores the end of its variable-length header and the full file length.
        // Vehicle records belong to that header; moving its tail requires updating both boundaries.
        BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan(4, 4), checked(headerEnd + patched.Length - data.Length));
        BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan(8, 4), patched.Length);
        ValidateScene(patched);
        ValidatePlacements(data, patched, current, replacement);
        return patched;
    }

    internal static void ValidateScene(byte[] data)
    {
        if (data.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(data) is not (20190654u or 20190655u))
            throw new InvalidDataException("Unsupported map scene format; no map was written.");
        var headerEnd = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4));
        if (headerEnd < 12 || headerEnd > data.Length || BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8, 4)) != data.Length)
            throw new InvalidDataException("The map scene has invalid size boundaries. Restore the original map or repair the saved workspace map before editing it.");
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        if (value.Length > 255 || value.Any(character => character is < ' ' or > '~')) throw new InvalidDataException("Unsupported non-ASCII or oversized truck/part identifier.");
        writer.Write((ushort)(value.Length + 1)); writer.Write(Encoding.ASCII.GetBytes(value)); writer.Write((byte)0);
    }

    private static void ValidatePlacements(byte[] before, byte[] after, RegionTruckEntry current, ReplacementTruck replacement)
    {
        // Every other placement of either model must survive the variable-length splice.
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.TruckCode, replacement.Truck.Code };
        var oldRows = RegionTruckCatalogService.ReadPlacements(before, codes);
        var newRows = RegionTruckCatalogService.ReadPlacements(after, codes);
        var targetFound = false;
        if (oldRows.Count != newRows.Count) throw new InvalidDataException("Replacement changed the placement count.");
        for (var i = 0; i < oldRows.Count; i++)
        {
            var original = oldRows[i]; var updated = newRows[i];
            if (original.Id != updated.Id || !original.Transform!.SequenceEqual(updated.Transform!)) throw new InvalidDataException("Replacement changed placement identity or position.");
            if (original.Offset == current.BinaryOffset)
            {
                targetFound = true;
                if (updated.TruckCode != replacement.Truck.Code || !updated.Equipment.Parsed
                    || !updated.Equipment.Addons.SequenceEqual(replacement.Configuration?.Addons ?? replacement.Equipment.InstalledAddons!.Select(addon => addon.Id))
                    || replacement.Configuration is { } expected && !EquipmentEqual(updated.Equipment, expected with { WheelScale = expected.WheelScale ?? 0f }))
                    throw new InvalidDataException("Replacement truck did not pass re-read verification.");
            }
            else if (original.TruckCode != updated.TruckCode || !EquipmentEqual(original.Equipment, updated.Equipment))
                throw new InvalidDataException("Replacement changed another vehicle's equipment.");
        }
        if (!targetFound) throw new InvalidDataException("The selected placement was not found during verification.");
    }

    private static bool EquipmentEqual(MapTruckEquipment a, MapTruckEquipment b) => a.Parsed == b.Parsed && a.Addons.SequenceEqual(b.Addons)
        && a.Engine == b.Engine && a.Gearbox == b.Gearbox && a.Winch == b.Winch && a.Suspension == b.Suspension
        && a.WheelType == b.WheelType && a.Tire == b.Tire && a.Rim == b.Rim && a.WheelScale == b.WheelScale;
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static string FileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
