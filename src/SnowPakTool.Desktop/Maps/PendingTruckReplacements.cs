using System.Text.Json;

namespace SnowPakTool.Desktop;

internal sealed record PendingTruckReplacement(string Key, RegionTruckEntry Original, CustomTruck Target, DateTimeOffset QueuedAt,
    MapTruckEquipment? Configuration = null, bool ResetOriginal = false, bool Randomized = false, bool Staged = true);
internal sealed record TruckRandomizationAssignment(string PlacementKey, string RegionCode, CustomTruck Target,
    MapTruckEquipment Configuration, int Seed, DateTimeOffset CreatedAt, string OriginalClass = "", bool MissionOnly = false);

/// <summary>Drafts, staged intent and assignments share one atomic file. Only Start applies staged map writes.</summary>
internal static class PendingTruckReplacements
{
    private sealed class State
    {
        public State() { }
        public List<PendingTruckReplacement> Pending { get; set; } = [];
        public List<TruckRandomizationAssignment> Assignments { get; set; } = [];
    }
    private static readonly object Gate = new();
    private static string PathFor(string workspace) => WorkspaceOriginals.GetMetadataPath(workspace, "region-maps", "pending-replacements.json");
    private static string Key(RegionTruckEntry entry) => entry.MapCode + "|" + entry.PlacementId + "|" + entry.BinaryOffset;

    private static State Read(string workspace)
    {
        var path = PathFor(workspace);
        if (!File.Exists(path)) return new();
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var document = JsonDocument.Parse(input);
        // Previous releases stored only a JSON array of manual pending changes.
        var state = document.RootElement.ValueKind == JsonValueKind.Array
            ? new State { Pending = document.RootElement.Deserialize<List<PendingTruckReplacement>>()! }
            : document.RootElement.TryGetProperty("Pending", out _)
                ? document.RootElement.Deserialize<State>() : null;
        if (state?.Pending is null || state.Assignments is null || state.Pending.Any(item => item?.Original is null || item.Target is null
                || string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Original.TruckCode) || !ConfigurationShape(item.Configuration))
            || state.Assignments.Any(item => item?.Configuration is null || item.Target is null || string.IsNullOrWhiteSpace(item.PlacementKey)
                || !ConfigurationShape(item.Configuration))
            || state.Pending.Select(item => item.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Pending.Count
            || state.Assignments.Select(item => item.PlacementKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Assignments.Count)
            throw new InvalidDataException("The saved map change plan is invalid; it was not overwritten.");
        return state;
    }

    private static bool ConfigurationShape(MapTruckEquipment? equipment)
    {
        if (equipment is null) return true;
        static bool Identifier(string value) => value is not null && value.Length <= 255 && value.All(character => character is >= ' ' and <= '~');
        return equipment.Parsed && equipment.Addons is not null && equipment.Addons.Count <= 128 && equipment.Addons.All(Identifier)
            && new[] { equipment.Engine, equipment.Gearbox, equipment.Winch, equipment.Suspension, equipment.WheelType, equipment.Tire, equipment.Rim }.All(Identifier)
            && (equipment.WheelScale is null || float.IsFinite(equipment.WheelScale.Value) && equipment.WheelScale is >= 0 and <= 10);
    }

    public static IReadOnlyList<PendingTruckReplacement> Load(string workspace) { lock (Gate) return Read(workspace).Pending; }
    public static IReadOnlyList<PendingTruckReplacement> LoadStaged(string workspace) => Load(workspace).Where(change => change.Staged).ToArray();
    public static bool HasUnstagedChanges(string workspace) => Load(workspace).Any(change => !change.Staged);
    public static int Stage(string workspace)
    {
        lock (Gate)
        {
            var state = Read(workspace);
            state.Pending = state.Pending.Select(change => change with { Staged = true }).ToList();
            Write(workspace, state);
            return state.Pending.Count;
        }
    }
    public static IReadOnlyList<TruckRandomizationAssignment> LoadAssignments(string workspace) { lock (Gate) return Read(workspace).Assignments; }

    public static PendingTruckReplacement Queue(string workspace, RegionTruckEntry displayed, CustomTruck target)
    {
        lock (Gate)
        {
            var original = displayed.FileEntry ?? displayed;
            if (original.MapDataHash.Length == 0) throw new InvalidDataException("Reload maps before queuing a replacement.");
            var item = new PendingTruckReplacement(Key(original), original, target, DateTimeOffset.UtcNow, Staged: false);
            var state = Read(workspace);
            state.Pending.RemoveAll(change => change.Key == item.Key);
            state.Assignments.RemoveAll(change => change.PlacementKey == original.StablePlacementKey);
            if (!original.TruckCode.Equals(target.Code, StringComparison.OrdinalIgnoreCase)) state.Pending.Add(item);
            Write(workspace, state);
            return item;
        }
    }

    public static void QueueRandomization(string workspace,
        IReadOnlyList<(RegionTruckEntry Entry, CustomTruck Target, MapTruckEquipment Configuration)> changes, int seed, bool missionOnly = false)
    {
        lock (Gate)
        {
            var state = Read(workspace);
            foreach (var (entry, target, configuration) in changes)
            {
                if (entry.PlacementIndex < 0 || entry.MapDataHash.Length == 0 || !configuration.Parsed)
                    throw new InvalidDataException("Reload maps before randomizing.");
                var existing = state.Pending.Where(item => item.Key == Key(entry) || item.Original.StablePlacementKey == entry.StablePlacementKey).ToArray();
                if (state.Assignments.Any(item => item.PlacementKey == entry.StablePlacementKey) || existing.Any(item => !item.ResetOriginal))
                    throw new InvalidOperationException("A placement changed while the region was being randomized. Reload and retry.");
                // A user-requested reset may be replaced by a fresh roll without an intermediate map write.
                state.Pending.RemoveAll(item => existing.Contains(item));
                state.Pending.Add(new(Key(entry), entry, target, DateTimeOffset.UtcNow, configuration, Randomized: true, Staged: false));
                state.Assignments.Add(new(entry.StablePlacementKey, entry.RegionCode, target, configuration, seed, DateTimeOffset.UtcNow,
                    entry.OriginalClass.Length > 0 ? entry.OriginalClass : entry.Class, missionOnly));
            }
            if (changes.Count > 0) Write(workspace, state);
        }
    }

    public static void Reset(string workspace, IReadOnlyList<RegionTruckEntry> entries)
    {
        lock (Gate)
        {
            var state = Read(workspace);
            foreach (var displayed in entries)
            {
                var current = displayed.FileEntry ?? displayed;
                var configuration = current.OriginalEquipment;
                var assignment = state.Assignments.FirstOrDefault(item => item.PlacementKey == current.StablePlacementKey);
                var isPending = state.Pending.Any(item => item.Key == Key(current));
                if (!isPending && assignment is null && !current.TruckChanged && (configuration is null || EquipmentEqual(current.RawEquipment, configuration))) continue;
                if (assignment is not null && !isPending && RegionTruckCatalogService.OriginalMapPath(workspace, current.MapCode) is null)
                    throw new InvalidDataException("The preserved original map is missing. Restore its backup before resetting this assignment.");
                if (configuration is not { Parsed: true } || configuration.WheelScale is null)
                    throw new InvalidDataException("Original equipment is unavailable. Reload the original map before resetting.");
                state.Pending.RemoveAll(item => item.Key == Key(current) || item.Original.StablePlacementKey == current.StablePlacementKey);
                state.Assignments.RemoveAll(item => item.PlacementKey == current.StablePlacementKey);
                if (current.TruckChanged || !EquipmentEqual(current.RawEquipment, configuration))
                    state.Pending.Add(new(Key(current), current, new(current.BaselineTruckCode, current.BaselineTruck,
                        current.OriginalClass.Length > 0 ? current.OriginalClass : current.Class), DateTimeOffset.UtcNow, configuration, true, Staged: false));
            }
            Write(workspace, state);
        }
    }

    private static void Write(string workspace, State state)
    {
        var path = PathFor(workspace); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static IReadOnlyList<RegionTruckEntry> Preview(string workspace, IReadOnlyList<RegionTruckEntry> entries, TruckEquipmentCatalog catalog)
    {
        var pending = Load(workspace).ToDictionary(change => change.Key, StringComparer.OrdinalIgnoreCase);
        return entries.Select(entry =>
        {
            if (!pending.TryGetValue(Key(entry), out var change) || entry.MapDataHash != change.Original.MapDataHash) return entry;
            var equipment = change.Configuration is { } configuration
                ? change.ResetOriginal ? catalog.Resolve(change.Target.Code, configuration) : catalog.ResolveConfigured(change.Target.Code, configuration)
                : catalog.ResolveDefault(change.Target.Code);
            return entry with { TruckCode = change.Target.Code, Truck = change.Target.Name, Class = change.Target.Class,
                Tires = equipment.Tires, TireDetails = equipment.TireDetails, Addons = equipment.Addons, AddonDetails = equipment.AddonDetails,
                FrameAddons = equipment.FrameAddons, FrameAddonDetails = equipment.FrameAddonDetails,
                InstalledAddons = equipment.InstalledAddons, InternalEquipment = equipment.InternalEquipment,
                RawEquipment = change.Configuration, FileEntry = entry };
        }).ToArray();
    }

    public static IReadOnlyList<SavedTruckReplacement> ApplyAll(string workspace)
    {
        lock (Gate)
        {
            var state = Read(workspace);
            if (!state.Pending.Any(change => change.Staged)) return [];
            var catalog = new TruckEquipmentCatalog(workspace, WorkspaceDataCache.GetLocalizedNames(workspace), []);
            var saved = new List<SavedTruckReplacement>();
            foreach (var group in state.Pending.Where(change => change.Staged).GroupBy(change => change.Original.MapCode).ToArray())
            {
                foreach (var change in group)
                    if (!change.ResetOriginal && change.Configuration is { } configured && !catalog.IsConfigurationSupported(change.Target.Code, configured))
                        throw new InvalidDataException("Staged randomization equipment is no longer compatible. Reset the affected region and randomize again.");
                var replacements = group.Select(change => (change.Original, new ReplacementTruck(change.Target,
                    change.Configuration is { } configuration
                        ? change.ResetOriginal ? catalog.Resolve(change.Target.Code, configuration) : catalog.ResolveConfigured(change.Target.Code, configuration)
                        : catalog.ResolveDefault(change.Target.Code),
                    catalog.ComparisonProperties(change.Target.Code), change.Configuration))).ToArray();
                saved.Add(TruckReplacementStore.SaveBatch(workspace, replacements));
                var keys = group.Select(change => change.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                state.Pending.RemoveAll(change => keys.Contains(change.Key));
                Write(workspace, state);
                // Staged and draft edits may share a map. Model lengths move byte offsets;
                // retain later drafts against the newly written file using stable placement IDs.
                if (state.Pending.Any(change => change.Original.MapCode.Equals(group.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    var names = WorkspaceDataCache.GetLocalizedNames(workspace);
                    var entries = RegionTruckCatalogService.Load(workspace, "", names, new Dictionary<string, string>(), includePending: false)
                        .Entries.Where(entry => entry.MapCode.Equals(group.Key, StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(entry => entry.StablePlacementKey, StringComparer.OrdinalIgnoreCase);
                    state.Pending = state.Pending.Select(change =>
                        change.Original.MapCode.Equals(group.Key, StringComparison.OrdinalIgnoreCase)
                            && entries.TryGetValue(change.Original.StablePlacementKey, out var current)
                            ? change with { Key = Key(current), Original = current } : change).ToList();
                    Write(workspace, state);
                }
            }
            return saved;
        }
    }

    public static bool EquipmentEqual(MapTruckEquipment? a, MapTruckEquipment? b) => a is not null && b is not null
        && a.Parsed == b.Parsed && a.Addons.SequenceEqual(b.Addons) && a.WheelType == b.WheelType && a.Tire == b.Tire && a.Rim == b.Rim
        && a.Engine == b.Engine && a.Gearbox == b.Gearbox && a.Winch == b.Winch && a.Suspension == b.Suspension
        && (a.WheelScale ?? 0f) == (b.WheelScale ?? 0f);

    public static void Discard(string workspace, string key) => DiscardMany(workspace, [key]);

    public static int DiscardMany(string workspace, IReadOnlyCollection<string> keys, bool stagedOnly = false)
    {
        lock (Gate)
        {
            var state = Read(workspace);
            var selected = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removed = state.Pending.Where(change => selected.Contains(change.Key) && (!stagedOnly || change.Staged)).ToArray();
            if (removed.Length == 0) return 0;
            var removedKeys = removed.Select(change => change.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var placements = removed.Select(change => change.Original.StablePlacementKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            state.Pending.RemoveAll(change => removedKeys.Contains(change.Key));
            state.Assignments.RemoveAll(change => placements.Contains(change.PlacementKey));
            Write(workspace, state);
            return removed.Length;
        }
    }
}
