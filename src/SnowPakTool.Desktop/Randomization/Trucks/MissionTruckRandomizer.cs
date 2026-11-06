using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SnowPakTool.Desktop;

internal static class MissionTruckRandomizer
{
    internal static double Weight(double percentile, MissionTruckDifficulty difficulty) => difficulty switch
    {
        MissionTruckDifficulty.Easy => 3 - 2 * percentile,
        MissionTruckDifficulty.Hard => 1 + 2 * percentile,
        _ => 1
    };

    internal static int TireCategory(int difficulty, Random random)
    {
        var category = difficulty switch { 0 => 0, 1 => 1, 2 or 3 => 2, _ => 3 };
        if (random.NextDouble() < 0.2) category = Math.Clamp(category + (random.Next(2) == 0 ? -1 : 1), 0, 3);
        return category;
    }

    private static string TireLabel(int category) => new[] { "Highway", "All-terrain", "Offroad", "Mudtires" }[category];

    public static void Randomize(string workspace, RegionTruckCatalog catalog, IReadOnlySet<string> ownedRegions,
        IReadOnlyList<CustomTruck> enabledTrucks, IReadOnlyList<GroupState> regionTiers, MissionTruckDifficulty difficulty,
        IProgress<RandomizationLogEntry> log, int? seed = null)
    {
        if (!Enum.IsDefined(difficulty)) throw new ArgumentOutOfRangeException(nameof(difficulty));
        var equipment = catalog.EquipmentCatalog ?? throw new InvalidOperationException("Load map and equipment data before randomizing.");
        void Summary(string message) => log.Report(new(message));
        void Detail(string message) => log.Report(new(message, true));
        var enabled = enabledTrucks.DistinctBy(truck => truck.Code, StringComparer.OrdinalIgnoreCase).ToArray();
        var fleet = enabled.Select(truck => (Truck: truck, Mass: equipment.VehicleMassKg(truck.Code)))
            .Where(item => item.Mass.HasValue).OrderBy(item => item.Mass).ThenBy(item => item.Truck.Code, StringComparer.OrdinalIgnoreCase).ToArray();
        if (fleet.Length == 0) throw new InvalidOperationException("No enabled trucks have readable vehicle mass. Check the workspace vehicle definitions.");
        if (fleet.Length < enabled.Length) Summary($"{enabled.Length - fleet.Length} enabled truck(s) excluded because their vehicle mass could not be read.");
        // Equal masses share an average percentile; XML/file ordering must not influence the bias.
        var percentiles = fleet.Select((item, index) => (item, index)).GroupBy(pair => pair.item.Mass!.Value)
            .SelectMany(group => group.Select(pair => (pair.item.Truck.Code, Percentile: group.Average(pair => pair.index) / Math.Max(1, fleet.Length - 1))))
            .ToDictionary(pair => pair.Code, pair => pair.Percentile, StringComparer.OrdinalIgnoreCase);
        var tierByRegion = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tier in regionTiers)
            foreach (var code in tier.TruckCodes)
                if (RegionProgressionService.TierDifficulty(tier.Name) is { } value) tierByRegion.TryAdd(code, value);
        var assignments = PendingTruckReplacements.LoadAssignments(workspace).ToDictionary(item => item.PlacementKey, StringComparer.OrdinalIgnoreCase);
        var pending = PendingTruckReplacements.Load(workspace);
        var resetKeys = pending.Where(change => change.ResetOriginal).Select(change => change.Original.StablePlacementKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pendingKeys = pending.Where(change => !change.ResetOriginal).Select(change => change.Original.StablePlacementKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pendingOffsets = pending.Where(change => !change.ResetOriginal).Select(change => change.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Merge by placement: a saved assignment and its pending draft/staged copy count once.
        var globalTargets = assignments.Values.Where(item => !resetKeys.Contains(item.PlacementKey))
            .ToDictionary(item => item.PlacementKey, item => item.Target.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var change in pending.Where(change => !change.ResetOriginal)) globalTargets[change.Original.StablePlacementKey] = change.Target.Code;
        foreach (var entry in catalog.Entries.Where(entry => entry.TruckChanged && !resetKeys.Contains(entry.StablePlacementKey)))
            globalTargets.TryAdd(entry.StablePlacementKey, entry.TruckCode);
        var uses = globalTargets.Values.GroupBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var regions = catalog.Entries.Where(entry => ownedRegions.Contains(entry.RegionCode) && entry.Availability == "Mission/task only")
            .GroupBy(entry => entry.RegionCode, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (regions.Length == 0) throw new InvalidOperationException("No mission-only truck placements were found in owned regions.");
        var configurations = new Dictionary<(string Code, int Category), (MapTruckEquipment Equipment, bool Fallback)?>();
        (MapTruckEquipment Equipment, bool Fallback)? Configuration(string code, int category)
        {
            var key = (code, category);
            if (!configurations.TryGetValue(key, out var result))
            {
                result = equipment.TryMissionConfiguration(code, category, out var configuration, out var fallback) ? (configuration, fallback) : null;
                configurations[key] = result;
            }
            return result;
        }
        var runSeed = seed ?? Random.Shared.Next();
        foreach (var region in regions)
        {
            var name = region.First().Region;
            if (!tierByRegion.TryGetValue(region.Key, out var regionTier))
            { Summary($"{name}: unchanged — assign this region a difficulty tier before randomizing mission trucks."); continue; }
            var regionalTargets = assignments.Values.Where(item => item.RegionCode.Equals(region.Key, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(item => item.PlacementKey, item => item.Target.Code, StringComparer.OrdinalIgnoreCase);
            foreach (var change in pending.Where(change => !change.ResetOriginal && change.Original.RegionCode.Equals(region.Key, StringComparison.OrdinalIgnoreCase)))
                regionalTargets[change.Original.StablePlacementKey] = change.Target.Code;
            foreach (var entry in region.Where(entry => entry.TruckChanged && !resetKeys.Contains(entry.StablePlacementKey))) regionalTargets.TryAdd(entry.StablePlacementKey, entry.TruckCode);
            var regionalUses = regionalTargets.Values.GroupBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var counts = new Dictionary<string, int>(uses, StringComparer.OrdinalIgnoreCase);
            var planned = new List<(RegionTruckEntry Entry, CustomTruck Target, MapTruckEquipment Configuration)>();
            var kept = 0; var skipped = 0; var fallbacks = 0;
            foreach (var entry in region.OrderBy(entry => entry.StablePlacementKey, StringComparer.Ordinal))
            {
                var label = $"{name} / {entry.Map} / placement {entry.PlacementIndex + 1}";
                if (!resetKeys.Contains(entry.StablePlacementKey) && (entry.FileEntry is not null || pendingKeys.Contains(entry.StablePlacementKey)
                    || pendingOffsets.Contains(entry.MapCode + "|" + entry.PlacementId + "|" + entry.BinaryOffset)))
                { kept++; Detail($"{label}: kept the existing draft or staged change."); continue; }
                if (assignments.TryGetValue(entry.StablePlacementKey, out var saved))
                {
                    if (entry.TruckCode.Equals(saved.Target.Code, StringComparison.OrdinalIgnoreCase) && PendingTruckReplacements.EquipmentEqual(entry.RawEquipment, saved.Configuration))
                    { kept++; Detail($"{label}: kept the saved assignment."); }
                    else { skipped++; Detail($"{label}: saved assignment differs from the current map; reset this placement before randomizing it again."); }
                    continue;
                }
                if (entry.TruckChanged || !PendingTruckReplacements.EquipmentEqual(entry.RawEquipment, entry.OriginalEquipment))
                { kept++; Detail($"{label}: preserved an existing edit."); continue; }
                if (entry.RawEquipment is not { Parsed: true } || entry.PlacementIndex < 0)
                { skipped++; Detail($"{label}: unchanged — original placement equipment could not be read safely."); continue; }
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"mission|{runSeed}|{entry.StablePlacementKey}")));
                var random = new Random(BinaryPrimitives.ReadInt32LittleEndian(bytes));
                var category = TireCategory(regionTier, random);
                var candidates = fleet.Where(item => !item.Truck.Code.Equals(entry.BaselineTruckCode, StringComparison.OrdinalIgnoreCase)
                    && Configuration(item.Truck.Code, category).HasValue).ToArray();
                if (candidates.Length == 0)
                { skipped++; Detail($"{label}: unchanged — no different enabled truck has usable tires and readable mass."); continue; }
                // Global coverage leads; local variety breaks ties without restarting the pool per region.
                var globalMinimum = candidates.Min(item => counts.GetValueOrDefault(item.Truck.Code));
                candidates = candidates.Where(item => counts.GetValueOrDefault(item.Truck.Code) == globalMinimum).ToArray();
                var minimumUses = candidates.Min(item => regionalUses.GetValueOrDefault(item.Truck.Code));
                candidates = candidates.Where(item => regionalUses.GetValueOrDefault(item.Truck.Code) == minimumUses).ToArray();
                double SelectionWeight(CustomTruck truck) => Weight(percentiles[truck.Code], difficulty);
                var roll = random.NextDouble() * candidates.Sum(item => SelectionWeight(item.Truck));
                var candidate = candidates[^1];
                foreach (var item in candidates) { roll -= SelectionWeight(item.Truck); if (roll <= 0) { candidate = item; break; } }
                var (configuration, fallback) = Configuration(candidate.Truck.Code, category)!.Value;
                planned.Add((entry.FileEntry ?? entry, candidate.Truck, configuration));
                regionalUses[candidate.Truck.Code] = minimumUses + 1;
                counts[candidate.Truck.Code] = counts.GetValueOrDefault(candidate.Truck.Code) + 1;
                Detail($"{label}: selected from a broad weight mix with {difficulty} bias; original class, truck tier and addon requirements were not used."
                    + (globalMinimum > 0 ? " Global reuse was necessary; a least-used model across all regions was selected." : " Selected a model not yet used in saved assignments or drafts across all regions.")
                    + (minimumUses > 0 ? " Regional reuse was necessary after global balancing." : " This model was not yet represented in this region."));
                var actual = equipment.ConfiguredTireCategory(configuration);
                if (fallback) { fallbacks++; Detail($"{label}: tire category {TireLabel(category)} → {actual} (requested category unsupported; nearest compatible category used)."); }
                Detail($"{label}: requested {TireLabel(category)} tires from region difficulty with variance; planned {actual}, smallest size and first variant, with replacement default equipment.");
            }
            try { PendingTruckReplacements.QueueRandomization(workspace, planned, runSeed, missionOnly: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
            { Summary($"{name}: error — the mission plan could not be saved. Previous assignments were preserved."); continue; }
            uses = counts;
            if (skipped > 0) Summary($"{name}: {skipped} unchanged — no valid replacement, unreadable placement, or saved assignment conflict; enable detailed logs for reasons.");
            Summary($"{name}: complete — {planned.Count} mission trucks randomized, {kept} kept, {skipped} unchanged."
                + (fallbacks > 0 ? $" Nearest compatible tire category used for {fallbacks} placement(s)." : ""));
        }
    }
}
