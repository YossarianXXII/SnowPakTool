using System.Buffers.Binary;
using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace SnowPakTool.Desktop;

internal sealed record RegionTruckEntry(string RegionCode, string Region, string MapCode, string Map,
    string TruckCode, string Truck, string Class, string Availability, string Task, string PlacementId,
    float X, float Y, float Z, string Evidence, string Source,
    string Tires = "Unknown", string TireDetails = "", string Addons = "Unknown", string AddonDetails = "", bool AvailabilityEditable = false,
    IReadOnlyList<InstalledAddonInfo>? InstalledAddons = null, string FrameAddons = "Unknown", string FrameAddonDetails = "",
    IReadOnlyList<InternalEquipmentInfo>? InternalEquipment = null, IReadOnlyList<float>? Transform = null, int BinaryOffset = -1,
    string MapDataHash = "", [property: System.Text.Json.Serialization.JsonIgnore] RegionTruckEntry? FileEntry = null,
    string OriginalTruckCode = "", string OriginalTruck = "", string OriginalClass = "",
    MapTruckEquipment? RawEquipment = null, MapTruckEquipment? OriginalEquipment = null, int PlacementIndex = -1)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string BaselineTruckCode => !string.IsNullOrEmpty(OriginalTruckCode) ? OriginalTruckCode : FileEntry?.TruckCode ?? TruckCode;
    [System.Text.Json.Serialization.JsonIgnore]
    public string BaselineTruck => !string.IsNullOrEmpty(OriginalTruck) ? OriginalTruck : FileEntry?.Truck ?? Truck;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool TruckChanged => !TruckCode.Equals(BaselineTruckCode, StringComparison.OrdinalIgnoreCase);
    [System.Text.Json.Serialization.JsonIgnore]
    public string StablePlacementKey => FormattableString.Invariant($"{MapCode}|{PlacementId}|{X:R}|{Y:R}|{Z:R}|{PlacementIndex}");
}
internal sealed record RegionTruckCatalog(IReadOnlyList<RegionTruckEntry> Entries, IReadOnlyList<string> Warnings, int MapsRead,
    TruckEquipmentCatalog? EquipmentCatalog = null);
internal sealed record MapTruckPlacement(string TruckCode, string Id, int Offset, float X, float Y, float Z, MapTruckEquipment Equipment, IReadOnlyList<float>? Transform = null);

internal static class RegionTruckCatalogService
{
    private sealed record TaskLink(string Name, string Map, string Action, string Source);
    private static readonly Regex MapCode = new(@"level_(?<region>(?:us|ru)_\d{2})_\d{2}(?:_[a-z0-9]+)*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TruckRoot = new(@"<Truck(?=[\s>])[\s\S]*?</Truck\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static RegionTruckCatalog Load(string workspace, string mapDirectory,
        IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<string, string> regionNames, RegionTruckAvailabilityStore? availabilityStore = null,
        bool includePending = true)
    {
        var warnings = new List<string>();
        availabilityStore ??= new RegionTruckAvailabilityStore();
        var trucks = LoadTrucks(workspace, names, warnings);
        if (trucks.Count == 0) throw new InvalidDataException("No vehicle definitions were found in the selected workspace.");
        var tasks = LoadTasks(workspace, names, warnings);
        var equipmentCatalog = new TruckEquipmentCatalog(workspace, names, warnings);
        var models = new TruckModelIndex(trucks.Keys);
        var sources = MapSources(workspace, mapDirectory);
        var entries = new List<RegionTruckEntry>();
        var mapsRead = 0;
        foreach (var (map, source) in sources.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var data = ReadMapData(source);
                if (data is null) { warnings.Add($"{map}: no [scene]/data_for_ssl.bin in archive."); continue; }
                var regionCode = MapCode.Match(map).Groups["region"].Value.ToLowerInvariant();
                var region = regionNames.GetValueOrDefault(regionCode, regionCode);
                var mapName = MapName(data, map, names);
                var mapHash = Convert.ToHexString(SHA256.HashData(data));
                var originals = ReadOriginalTrucks(workspace, map, source, models, warnings);
                var originalSource = OriginalMapPath(workspace, map);
                var comparingBackup = originalSource is not null && !Path.GetFullPath(originalSource).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);
                mapsRead++;
                var placementIndex = 0;
                foreach (var placement in ReadPlacements(data, models))
                {
                    var metadata = trucks[placement.TruckCode];
                    var links = placement.Id.Length == 0 ? [] : tasks.GetValueOrDefault(placement.Id, [])
                        .Where(link => SameMap(link.Map, map) || link.Map.Length == 0).Distinct().ToArray();
                    var actions = links.Select(link => link.Action).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    string availability;
                    string reason;
                    if (RegionTruckPlacementOverrides.GetMissionOnlyReason(map, placement.Id) is { } confirmedReason)
                    {
                        availability = "Mission/task only";
                        reason = confirmedReason;
                    }
                    else if (actions.Contains("REWARD") && actions.Contains("DESTROY"))
                    {
                        availability = "Unverified";
                        reason = "Both reward and removal rules reference this placement; review task stages.";
                    }
                    else if (actions.Contains("DESTROY"))
                    {
                        availability = "Mission/task only";
                        reason = "Task removes the vehicle (DESTROY).";
                    }
                    else if (actions.Contains("REWARD"))
                    {
                        availability = "Obtainable — task reward";
                        reason = "Task explicitly awards the vehicle (REWARD).";
                    }
                    else if (placement.Id.Length == 0)
                    {
                        availability = "Obtainable — discovery/start";
                        reason = "Inferred: unnamed world placement; no task ID. Includes starting vehicles.";
                    }
                    else
                    {
                        availability = "Unverified";
                        reason = links.Length > 0
                            ? "Task vehicle; no explicit award/removal rule. " + string.Join(", ", actions)
                            : "Named placement with no matching task ownership rule in this workspace.";
                    }
                    var evidence = reason + $" Placement at 0x{placement.Offset:X}."
                        + (links.Length == 0 ? "" : " Task sources: " + string.Join("; ", links.Select(link => link.Source).Distinct()));
                    var equipment = equipmentCatalog.Resolve(placement.TruckCode, placement.Equipment);
                    var originalPlacement = placement;
                    var originalFound = !comparingBackup;
                    if (originals.TryGetValue((placement.Id, placement.X, placement.Y, placement.Z), out var originalCodes) && originalCodes.Count > 0)
                    {
                        originalPlacement = originalCodes.Dequeue();
                        originalFound = true;
                    }
                    var originalCode = originalPlacement.TruckCode;
                    var originalName = trucks.TryGetValue(originalCode, out var originalMetadata) ? originalMetadata.Name : originalCode;
                    entries.Add(new(regionCode, region, map, mapName, placement.TruckCode, metadata.Name,
                        metadata.Class, availability, string.Join("; ", links.Select(link => link.Name).Distinct()),
                        placement.Id, placement.X, placement.Y, placement.Z, evidence, source,
                        equipment.Tires, equipment.TireDetails, equipment.Addons, equipment.AddonDetails, InstalledAddons: equipment.InstalledAddons,
                        FrameAddons: equipment.FrameAddons, FrameAddonDetails: equipment.FrameAddonDetails,
                        InternalEquipment: equipment.InternalEquipment, Transform: placement.Transform, BinaryOffset: placement.Offset, MapDataHash: mapHash,
                        OriginalTruckCode: originalCode, OriginalTruck: originalName, OriginalClass: originalMetadata.Class ?? metadata.Class,
                        RawEquipment: placement.Equipment, OriginalEquipment: originalFound ? originalPlacement.Equipment : null, PlacementIndex: placementIndex++));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { warnings.Add($"{map}: {ex.Message}"); }
        }
        var resolved = entries.Select(availabilityStore.Apply).ToArray();
        return new(includePending ? PendingTruckReplacements.Preview(workspace, resolved, equipmentCatalog) : resolved, warnings.ToArray(), mapsRead, equipmentCatalog);
    }

    internal static string? OriginalMapPath(string workspace, string map)
    {
        var directory = WorkspaceOriginals.GetMetadataPath(workspace, "region-maps", "backups", map);
        var pak = Path.Combine(directory, "original.pak");
        if (File.Exists(pak)) return pak;
        var bin = Path.Combine(directory, "original.bin");
        return File.Exists(bin) ? bin : null;
    }

    private static Dictionary<(string Id, float X, float Y, float Z), Queue<MapTruckPlacement>> ReadOriginalTrucks(
        string workspace, string map, string source, TruckModelIndex models, List<string> warnings)
    {
        var result = new Dictionary<(string, float, float, float), Queue<MapTruckPlacement>>();
        var original = OriginalMapPath(workspace, map);
        if (original is null || Path.GetFullPath(original).Equals(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) return result;
        try
        {
            var data = ReadMapData(original);
            if (data is null) throw new InvalidDataException("Original map has no scene metadata.");
            // Replacements can shift binary offsets. IDs and transforms remain unchanged.
            // Queues distinguish repeated/unnamed placements at the same coordinates by scene order.
            foreach (var placement in ReadPlacements(data, models))
            {
                var key = (placement.Id, placement.X, placement.Y, placement.Z);
                if (!result.TryGetValue(key, out var codes)) result[key] = codes = new();
                codes.Enqueue(placement);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { warnings.Add($"{map}: could not compare trucks with the preserved original: {ex.Message}"); }
        return result;
    }

    internal static Dictionary<string, string> MapSources(string workspace, string mapDirectory)
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // The selected folder supplies maps that have not yet been backed up in this workspace.
        if (Directory.Exists(mapDirectory))
        {
            foreach (var pak in Directory.EnumerateFiles(mapDirectory, "level_*.pak"))
                if (MapCode.IsMatch(Path.GetFileNameWithoutExtension(pak))) sources[Path.GetFileNameWithoutExtension(pak)] = pak;
            foreach (var directory in Directory.EnumerateDirectories(mapDirectory, "level_*").Append(mapDirectory))
            {
                var match = MapCode.Match(Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                var bin = Path.Combine(directory, "[scene]", "data_for_ssl.bin");
                if (match.Success && File.Exists(bin)) sources[match.Value] = bin;
            }
        }
        // A preserved original is the baseline even when the installed game map has since changed.
        var backups = WorkspaceOriginals.GetMetadataPath(workspace, "region-maps", "backups");
        if (Directory.Exists(backups))
            foreach (var directory in Directory.EnumerateDirectories(backups, "level_*"))
            {
                var map = Path.GetFileName(directory);
                if (!MapCode.IsMatch(map)) continue;
                var original = Path.Combine(directory, "original.pak");
                if (!File.Exists(original)) original = Path.Combine(directory, "original.bin");
                if (File.Exists(original)) sources[map] = original;
            }
        foreach (var (map, source) in TruckReplacementStore.SavedMaps(workspace)) sources[map] = source;
        return sources;
    }

    internal static string SourceStamp(string workspace, string mapDirectory)
    {
        var paths = MapSources(workspace, mapDirectory).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(pair => new[] { pair.Value, OriginalMapPath(workspace, pair.Key) })
            .Where(path => path is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Append(WorkspaceOriginals.GetMetadataPath(workspace, "region-maps", "pending-replacements.json"));
        return string.Join("\n", paths.Select(path =>
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : file.FullName + "|missing";
        }));
    }

    internal static IReadOnlyList<MapTruckPlacement> ReadPlacements(byte[] data, IReadOnlySet<string> truckCodes)
        => ReadPlacements(data, new TruckModelIndex(truckCodes));

    private sealed class TruckModelIndex
    {
        public readonly Dictionary<int, byte[][]> Candidates;
        public readonly int MinLength;
        public readonly int MaxLength;
        public readonly SearchValues<byte> LengthBytes;

        public TruckModelIndex(IEnumerable<string> codes)
        {
            var bytes = codes.Where(code => code.Length is > 0 and < 256 && code.All(character => character is >= ' ' and <= '~'))
                .Select(Encoding.ASCII.GetBytes).ToArray();
            Candidates = bytes.GroupBy(code => Key(code.Length + 1, code[0])).ToDictionary(group => group.Key, group => group.ToArray());
            MinLength = bytes.Length == 0 ? 1 : bytes.Min(code => code.Length + 1);
            MaxLength = bytes.Length == 0 ? 0 : bytes.Max(code => code.Length + 1);
            LengthBytes = SearchValues.Create(bytes.Select(code => (byte)(code.Length + 1)).Distinct().ToArray());
        }

        public static int Key(int length, byte first) => length | (first is >= (byte)'A' and <= (byte)'Z' ? first + 32 : first) << 9;
    }

    private static IReadOnlyList<MapTruckPlacement> ReadPlacements(byte[] data, TruckModelIndex models)
    {
        var result = new List<MapTruckPlacement>();
        for (var offset = 0; offset + 3 < data.Length; offset++)
        {
            var candidateOffset = data.AsSpan(offset, data.Length - offset - 3).IndexOfAny(models.LengthBytes);
            if (candidateOffset < 0) break;
            offset += candidateOffset;
            // Every model ID is a short ASCII Pascal string. Reject binary bytes
            // before decoding or walking arbitrary strings at every map offset.
            var length = data[offset] | data[offset + 1] << 8;
            if (length < models.MinLength || length > models.MaxLength || offset + 2 + length > data.Length
                || data[offset + 1 + length] != 0
                || !models.Candidates.TryGetValue(TruckModelIndex.Key(length, data[offset + 2]), out var candidates)) continue;
            var encoded = data.AsSpan(offset + 2, length - 1);
            var found = false;
            foreach (var candidate in candidates)
                if (Ascii.EqualsIgnoreCase(encoded, candidate)) { found = true; break; }
            if (!found) continue;
            var model = Encoding.ASCII.GetString(encoded);
            var next = offset + 2 + length;
            if (!TryString(data, next, out var id, out var transform) || !IsTransform(data, transform)) continue;
            result.Add(new(model, id, offset, ReadFloat(data, transform + 48), ReadFloat(data, transform + 52), ReadFloat(data, transform + 56), ReadEquipment(data, transform),
                Enumerable.Range(0, 16).Select(index => ReadFloat(data, transform + index * 4)).ToArray()));
            offset = transform + 63;
        }
        return result;
    }

    private static MapTruckEquipment ReadEquipment(byte[] data, int transform)
    {
        // Placement suffix: transform, flag, six bounds floats, addon count and
        // addon IDs, then engine/gearbox/winch/suspension/wheel-family/rim/tire.
        var position = transform + 64 + 1 + 6 * sizeof(float);
        if (position + sizeof(uint) > data.Length || data[transform + 64] > 1) return MapTruckEquipment.Unknown;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, sizeof(uint)));
        if (count > 128) return MapTruckEquipment.Unknown;
        position += sizeof(uint);
        var addons = new List<string>();
        for (var i = 0; i < count; i++)
        {
            if (!TryString(data, position, out var addon, out position)) return MapTruckEquipment.Unknown;
            addons.Add(addon);
        }
        var fields = new string[7];
        for (var i = 0; i < fields.Length; i++)
            if (!TryString(data, position, out fields[i], out position)) return MapTruckEquipment.Unknown;
        var scale = position + sizeof(float) <= data.Length ? ReadFloat(data, position) : (float?)null;
        return new(true, addons, fields[4], fields[6], fields[5], fields[0], fields[1], fields[2], fields[3],
            scale is >= 0 and <= 10 ? scale : null);
    }

    private static bool IsTransform(byte[] data, int offset)
    {
        if (offset + 64 > data.Length) return false;
        for (var i = 0; i < 16; i++)
        {
            var value = ReadFloat(data, offset + i * 4);
            if (!float.IsFinite(value)) return false;
            if (i < 12 && Math.Abs(value) > 1.1f) return false;
            if (i is 3 or 7 or 11 && Math.Abs(value) > 0.0001f) return false;
        }
        return Math.Abs(ReadFloat(data, offset + 60) - 1f) < 0.0001f
            && Math.Abs(ReadFloat(data, offset)) + Math.Abs(ReadFloat(data, offset + 4)) + Math.Abs(ReadFloat(data, offset + 8)) > 0.5f;
    }

    private static float ReadFloat(byte[] data, int offset) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4));

    internal static bool TryString(byte[] data, int offset, out string value, out int next)
    {
        value = "";
        next = offset;
        if (offset + 2 > data.Length) return false;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
        if (length is < 1 or > 256 || offset + 2 + length > data.Length || data[offset + 1 + length] != 0) return false;
        for (var i = offset + 2; i < offset + 1 + length; i++) if (data[i] is < 32 or > 126) return false;
        value = Encoding.ASCII.GetString(data, offset + 2, length - 1);
        next = offset + 2 + length;
        return true;
    }

    internal static byte[]? ReadMapData(string source)
    {
        if (source.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(source);
        using var archive = ZipFile.OpenRead(source);
        var entry = archive.Entries.FirstOrDefault(entry => entry.FullName.Replace('\\', '/').Equals("[scene]/data_for_ssl.bin", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        if (entry.Length > 128 * 1024 * 1024) throw new InvalidDataException("Map metadata is larger than supported (128 MB).");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static string MapName(byte[] data, string map, IReadOnlyDictionary<string, string> names)
    {
        for (var i = 0; i < Math.Min(256, data.Length); i++)
            if (TryString(data, i, out var key, out _) && key.EndsWith("_NAME", StringComparison.OrdinalIgnoreCase)
                && names.TryGetValue(key, out var name)) return name;
        var keyName = Regex.Replace(map, "^level_", "", RegexOptions.IgnoreCase).ToUpperInvariant() + "_NAME";
        return names.GetValueOrDefault(keyName, map);
    }

    internal static Dictionary<string, (string Name, string Class)> LoadTrucks(string workspace, IReadOnlyDictionary<string, string> names, List<string> warnings)
    {
        var trucks = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in WorkspaceOriginals.EnumerateCurrentFiles(workspace, "[media]", "*.xml")
                     .Where(file => string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), "trucks", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var root = TruckRoot.Match(WorkspaceDataCache.ReadAllText(file));
                if (!root.Success) continue;
                var key = Regex.Match(root.Value, "\\bUiName\\s*=\\s*\"([^\"]+)\"").Groups[1].Value;
                var truckClass = Regex.Match(root.Value, "\\bTruckType\\s*=\\s*\"([^\"]+)\"").Groups[1].Value;
                var code = Path.GetFileNameWithoutExtension(file);
                trucks[code] = (names.GetValueOrDefault(key, code), TruckClasses.ForTruck(code, truckClass));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Add($"{file}: {ex.Message}"); }
        }
        return trucks;
    }

    private static Dictionary<string, List<TaskLink>> LoadTasks(string workspace, IReadOnlyDictionary<string, string> names, List<string> warnings)
    {
        var links = new Dictionary<string, List<TaskLink>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in WorkspaceOriginals.EnumerateCurrentFiles(workspace, "[ps]", "*.sso"))
        {
            try
            {
                var text = WorkspaceDataCache.ReadAllText(file);
                if (!text.Contains("truckId", StringComparison.OrdinalIgnoreCase) && !text.Contains("truckUid", StringComparison.OrdinalIgnoreCase)) continue;
                var root = SsoDataReader.Parse(text);
                foreach (var task in root.DescendantsAndSelf().Where(node => node.Get("__type") is "TaskSettings" or "ContractSettings" or "ContestSettings"))
                {
                    var nodes = task.DescendantsAndSelf().ToArray();
                    var key = task.Get("uiDesc");
                    var titleKeys = new[] { task.Key, Path.GetFileNameWithoutExtension(file).ToUpperInvariant(), Regex.Replace(key, "_DESC.*$", "", RegexOptions.IgnoreCase) };
                    var name = titleKeys.Where(title => title.Length > 0 && names.ContainsKey(title)).Select(title => names[title]).FirstOrDefault()
                        ?? (task.Key.Length > 0 ? task.Key : Path.GetFileNameWithoutExtension(file));
                    // Scope reused placement IDs to the task's region; deliveries can cross maps.
                    var map = task.Members.TryGetValue("taskGiverZone", out var giver) ? MapCode.Match(giver.Get("globalZoneId")).Value : "";
                    if (map.Length == 0 && Regex.IsMatch(task.Get("region"), @"^(?:us|ru)_\d{2}$", RegexOptions.IgnoreCase))
                        map = "level_" + task.Get("region") + "_00";
                    if (map.Length == 0) map = MapCode.Match("level_" + Path.GetFileNameWithoutExtension(file).Replace("level_", "")).Value;
                    foreach (var node in nodes)
                    {
                        var id = node.Get("truckId");
                        if (id.Length == 0) id = node.Get("truckUid");
                        if (id.Length == 0) continue;
                        var action = node.Get("afterStageFinished");
                        if (node.Get("mustDestroyAfterSubstage").Equals("True", StringComparison.OrdinalIgnoreCase)) action = "DESTROY";
                        if (action.Length == 0) action = "Unspecified";
                        if (!links.TryGetValue(id, out var list)) links[id] = list = [];
                        list.Add(new(name, map, action, WorkspaceOriginals.GetRelativePath(workspace, file)));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            { warnings.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
        }
        return links;
    }

    private static bool SameMap(string first, string second) =>
        MapCode.Match(first).Groups["region"].Value.Equals(MapCode.Match(second).Groups["region"].Value, StringComparison.OrdinalIgnoreCase);

    public static string FindMapDirectory(string inputPak)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(inputPak)) candidates.Add(Path.GetDirectoryName(Path.GetFullPath(inputPak))!);
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed))
            foreach (var library in new[] { "SteamLibrary", "Steam", "Program Files (x86)/Steam" })
                candidates.Add(Path.Combine(drive.RootDirectory.FullName, library, "steamapps/common/SnowRunner/preload/paks/client"));
        foreach (var candidate in candidates)
        {
            try { if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "level_*.pak").Any()) return candidate; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return "";
    }
}
