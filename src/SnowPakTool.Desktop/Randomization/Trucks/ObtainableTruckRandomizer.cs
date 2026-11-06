using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SnowPakTool.Desktop;

internal static class ObtainableTruckRandomizer
{
    private static int TruckQuality(string tier) => tier switch
    {
        "FX" => 0, "D" => 1, "C" => 2, "B" => 3, "A" => 4, "S" => 5, "SS" => 6, _ => -1
    };

    public static void Randomize(string workspace, RegionTruckCatalog catalog, IReadOnlySet<string> ownedRegions,
        IReadOnlyList<CustomTruck> ownedTrucks, RegionProgressionState progression, IReadOnlyList<GroupState> truckTiers,
        IReadOnlyList<GroupState> regionTiers, IProgress<string> progress, int? seed = null,
        ProgressionDifficulty tireDifficulty = ProgressionDifficulty.Hard)
        => Randomize(workspace, catalog, ownedRegions, ownedTrucks, progression, truckTiers, regionTiers,
            new RandomizationLogProgress(entry => { if (!entry.IsDetail) progress.Report(entry.Message); }), seed, tireDifficulty);

    public static void Randomize(string workspace, RegionTruckCatalog catalog, IReadOnlySet<string> ownedRegions,
        IReadOnlyList<CustomTruck> ownedTrucks, RegionProgressionState progression, IReadOnlyList<GroupState> truckTiers,
        IReadOnlyList<GroupState> regionTiers, IProgress<RandomizationLogEntry> log, int? seed = null,
        ProgressionDifficulty tireDifficulty = ProgressionDifficulty.Hard)
    {
        void Summary(string message) => log.Report(new(message));
        void Detail(string message) => log.Report(new(message, IsDetail: true));
        if (!progression.IsFinalized || progression.Order.Count == 0)
            throw new InvalidOperationException("Finalize Region progression before randomizing obtainable trucks.");
        if (!Enum.IsDefined(tireDifficulty)) throw new ArgumentOutOfRangeException(nameof(tireDifficulty));
        var equipment = catalog.EquipmentCatalog ?? throw new InvalidOperationException("Load map and equipment data before randomizing.");
        var quality = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tier in truckTiers)
            foreach (var code in tier.TruckCodes)
                if (TruckQuality(tier.Name) >= 0) quality.TryAdd(code, TruckQuality(tier.Name));
        var trucks = ownedTrucks.Where(truck => quality.ContainsKey(truck.Code)).DistinctBy(truck => truck.Code, StringComparer.OrdinalIgnoreCase)
            .OrderBy(truck => truck.Code, StringComparer.OrdinalIgnoreCase).ToArray();
        if (trucks.Length == 0) throw new InvalidOperationException("Rank your owned trucks in the Truck tier list before randomizing.");
        var difficulty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tier in regionTiers)
            foreach (var code in tier.TruckCodes)
                if (RegionProgressionService.TierDifficulty(tier.Name) is { } value) difficulty.TryAdd(code, value);
        var order = progression.Order.Select((code, index) => (code, index))
            .ToDictionary(item => item.code, item => item.index, StringComparer.OrdinalIgnoreCase);
        var assignments = PendingTruckReplacements.LoadAssignments(workspace).ToDictionary(item => item.PlacementKey, StringComparer.OrdinalIgnoreCase);
        var pendingChanges = PendingTruckReplacements.Load(workspace);
        var pending = pendingChanges.Select(item => item.Original.StablePlacementKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pendingOffsets = pendingChanges.Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resetKeys = pendingChanges.Where(item => item.ResetOriginal).Select(item => item.Original.StablePlacementKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uses = assignments.Values.GroupBy(item => item.Target.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        // Persist source class for new assignments; older plans can recover it from loaded placements.
        var sourceClasses = catalog.Entries.Concat(pendingChanges.Select(change => change.Original))
            .GroupBy(entry => entry.StablePlacementKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => SourceClass(group.First()), StringComparer.OrdinalIgnoreCase);
        var heavyAssignments = assignments.Values.Where(item => !item.MissionOnly && SameClass(
            string.IsNullOrEmpty(item.OriginalClass) ? sourceClasses.GetValueOrDefault(item.PlacementKey, "") : item.OriginalClass, "HEAVY_DUTY")).ToArray();
        var heavyCount = heavyAssignments.Length;
        var offroadCount = heavyAssignments.Count(item => SameClass(item.Target.Class, "OFFROAD"));
        var regions = catalog.Entries.Where(entry => ownedRegions.Contains(entry.RegionCode) && entry.Availability.StartsWith("Obtainable", StringComparison.Ordinal))
            .GroupBy(entry => entry.RegionCode).OrderBy(group => order.GetValueOrDefault(group.Key, int.MaxValue)).ThenBy(group => group.Key).ToArray();
        if (regions.Length == 0) throw new InvalidOperationException("No obtainable placements were found in owned regions.");
        var runSeed = seed ?? Random.Shared.Next();
        foreach (var region in regions)
        {
            var name = region.First().Region;
            if (!difficulty.TryGetValue(region.Key, out var tier) || !order.TryGetValue(region.Key, out var position))
            {
                Summary($"{name}: unchanged — assign a difficulty tier and include this region in the finalized progression first.");
                continue;
            }
            var planned = new List<(RegionTruckEntry Entry, CustomTruck Target, MapTruckEquipment Configuration)>();
            var counts = new Dictionary<string, int>(uses, StringComparer.OrdinalIgnoreCase);
            // Include saved placements even when only part of a region is loaded or rerolled.
            var regionalTargets = assignments.Values.Where(item => item.RegionCode.Equals(region.Key, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(item => item.PlacementKey, item => item.Target.Code, StringComparer.OrdinalIgnoreCase);
            foreach (var change in pendingChanges.Where(item => !item.ResetOriginal && item.Original.RegionCode.Equals(region.Key, StringComparison.OrdinalIgnoreCase)))
                regionalTargets[change.Original.StablePlacementKey] = change.Target.Code;
            foreach (var existing in region.Where(entry => entry.TruckChanged && !resetKeys.Contains(entry.StablePlacementKey)))
                regionalTargets.TryAdd(existing.StablePlacementKey, existing.TruckCode);
            var regionalUses = regionalTargets.Values.GroupBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var kept = 0; var skipped = 0; var fallbacks = 0;
            var regionHeavyCount = heavyCount;
            var regionOffroadCount = offroadCount;
            var errors = new Dictionary<string, int>();
            void Skip(string reason) { skipped++; errors[reason] = errors.GetValueOrDefault(reason) + 1; }
            var work = region.OrderBy(entry => trucks.Count(truck => SameClass(truck.Class, entry.OriginalClass.Length > 0 ? entry.OriginalClass : entry.Class)))
                .ThenByDescending(entry => entry.OriginalEquipment?.Addons.Count ?? 0).ThenBy(entry => entry.StablePlacementKey, StringComparer.Ordinal).ToArray();
            foreach (var entry in work)
            {
                var label = $"{name} / {entry.Map} / placement {entry.PlacementIndex + 1}";
                if (!resetKeys.Contains(entry.StablePlacementKey) && (entry.FileEntry is not null || pending.Contains(entry.StablePlacementKey)
                    || pendingOffsets.Contains(entry.MapCode + "|" + entry.PlacementId + "|" + entry.BinaryOffset)))
                { kept++; Detail($"{label}: kept the saved preview."); continue; }
                if (assignments.TryGetValue(entry.StablePlacementKey, out var assigned))
                {
                    if (entry.TruckCode.Equals(assigned.Target.Code, StringComparison.OrdinalIgnoreCase) && PendingTruckReplacements.EquipmentEqual(entry.RawEquipment, assigned.Configuration))
                    { kept++; Detail($"{label}: kept the saved assignment."); }
                    else { Skip("saved assignment differs from the current map; reset that placement before randomizing it again"); Detail($"{label}: saved assignment no longer matches the map; reset is required."); }
                    continue;
                }
                if (entry.TruckChanged || !PendingTruckReplacements.EquipmentEqual(entry.RawEquipment, entry.OriginalEquipment))
                { kept++; Detail($"{label}: preserved an existing edit."); continue; }
                if (entry.RawEquipment is not { Parsed: true } || entry.PlacementIndex < 0) { Skip("placement equipment could not be read"); continue; }
                var random = PlacementRandom(runSeed, entry.StablePlacementKey);
                var tireWeights = TruckTireRandomization.Weights(position, progression.Order.Count, tier, tireDifficulty);
                var requested = TruckTireRandomization.Draw(tireWeights, random.NextDouble());
                var heavyDuty = SameClass(SourceClass(entry), "HEAVY_DUTY");
                var targetQuality = position / (double)Math.Max(1, progression.Order.Count - 1) * 6;
                var candidates = trucks.Where(truck => SameClass(truck.Class, SourceClass(entry))
                    && !truck.Code.Equals(entry.BaselineTruckCode, StringComparison.OrdinalIgnoreCase)).ToList();
                var rejected = 0;
                var configurations = new Dictionary<string, (MapTruckEquipment Equipment, bool Fallback)>(StringComparer.OrdinalIgnoreCase);
                bool Compatible(CustomTruck truck)
                {
                    if (configurations.ContainsKey(truck.Code)) return true;
                    if (!equipment.TryRandomizationConfiguration(entry, truck.Code, requested, out var configuration, out var fallback))
                    {
                        rejected++;
                        Detail($"{label}: candidate rejected — required addons or compatible tires are unavailable; trying another eligible truck.");
                        return false;
                    }
                    configurations.Add(truck.Code, (configuration, fallback));
                    return true;
                }
                List<CustomTruck>? offroadCandidates = null;
                if (heavyDuty)
                {
                    // Count real choices after addon/tire validation, rather than nominal class members.
                    candidates = candidates.Where(Compatible).ToList();
                    var nearby = candidates.Where(truck => Math.Abs(quality[truck.Code] - targetQuality) <= 1).ToList();
                    var scarce = nearby.Count < 3 || nearby.All(truck => regionalUses.GetValueOrDefault(truck.Code) > 0);
                    if (scarce && regionOffroadCount < (regionHeavyCount + 1) / 5 && random.NextDouble() < 0.18)
                    {
                        offroadCandidates = trucks.Where(truck => SameClass(truck.Class, "OFFROAD")
                            && !truck.Code.Equals(entry.BaselineTruckCode, StringComparison.OrdinalIgnoreCase)
                            && Math.Abs(quality[truck.Code] - targetQuality) <= 1 && Compatible(truck)).ToList();
                    }
                    // An exhausted narrow band must not force the same low-tier truck repeatedly.
                    var wider = candidates.Where(truck => Math.Abs(quality[truck.Code] - targetQuality) <= 2).ToList();
                    int LeastUsed(List<CustomTruck> pool) => pool.Count == 0 ? int.MaxValue : pool.Min(truck => regionalUses.GetValueOrDefault(truck.Code));
                    if (wider.Count > 0 && LeastUsed(wider) < LeastUsed(nearby))
                    {
                        candidates = wider;
                        if (nearby.Count > 0) Detail($"{label}: widened the HEAVY_DUTY tier band by one tier to avoid regional duplication.");
                    }
                    else candidates = nearby.Count > 0 ? nearby : wider.Count > 0 ? wider : candidates;
                    if (offroadCandidates is { Count: > 0 } && LeastUsed(offroadCandidates) <= LeastUsed(candidates))
                        candidates = offroadCandidates;
                    if (candidates.Count > 0)
                    {
                        var leastUsed = LeastUsed(candidates);
                        candidates = candidates.Where(truck => regionalUses.GetValueOrDefault(truck.Code) == leastUsed).ToList();
                        if (leastUsed > 0) Detail($"{label}: regional reuse is necessary — all compatible trucks in the permitted tier band are already represented; choosing a least-used model.");
                    }
                }
                var accepted = false;
                while (candidates.Count > 0)
                {
                    double Weight(CustomTruck truck)
                    {
                        var expected = quality[truck.Code] / 6d * Math.Max(1, progression.Order.Count - 1);
                        var distance = expected - position;
                        var spread = distance > 0 ? 1.6 : Math.Max(2, progression.Order.Count * 0.18);
                        return Math.Max(1e-8, Math.Exp(-0.5 * Math.Pow(distance / spread, 2))) / Math.Pow(1 + counts.GetValueOrDefault(truck.Code), heavyDuty ? 3 : 2)
                            / (heavyDuty ? 1 : Math.Pow(100, Math.Min(6, regionalUses.GetValueOrDefault(truck.Code))));
                    }
                    var roll = random.NextDouble() * candidates.Sum(Weight);
                    var candidate = candidates[^1];
                    foreach (var truck in candidates) { roll -= Weight(truck); if (roll <= 0) { candidate = truck; break; } }
                    candidates.Remove(candidate);
                    if (!Compatible(candidate)) continue;
                    var (configuration, fallback) = configurations[candidate.Code];
                    if (heavyDuty)
                    {
                        regionHeavyCount++;
                        if (SameClass(candidate.Class, "OFFROAD"))
                        {
                            regionOffroadCount++;
                            Detail($"{label}: limited HEAVY_DUTY → OFFROAD exception used to improve variety; within one target tier and the one-in-five saved assignment cap.");
                        }
                    }
                    Detail($"{label}: requested {TireLabel(requested)} tires (progression {position + 1}/{progression.Order.Count}, region tier {new[] { "D", "C", "B", "A", "S" }[tier]}, tire difficulty {tireDifficulty}; "
                        + string.Join(", ", tireWeights.Select((weight, category) => $"{TireLabel(category)} {weight.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%")) + ").");
                    var actualTires = equipment.ConfiguredTireCategory(configuration);
                    if (fallback)
                        Detail($"{label}: tire category {TireLabel(requested)} → {actualTires} (requested category is not supported by the selected truck; nearest compatible category used).");
                    Detail($"{label}: planned {actualTires} tires, smallest supported size (scale {configuration.WheelScale?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}), first variant."
                        + (rejected > 0 ? $" {rejected} incompatible candidate(s) rejected." : ""));
                    planned.Add((entry.FileEntry ?? entry, candidate, configuration));
                    counts[candidate.Code] = counts.GetValueOrDefault(candidate.Code) + 1;
                    regionalUses[candidate.Code] = regionalUses.GetValueOrDefault(candidate.Code) + 1;
                    if (fallback) fallbacks++;
                    accepted = true;
                    break;
                }
                if (!accepted)
                {
                    Skip("no different owned, ranked truck in the permitted classes supports the required addon configuration and tires");
                    Detail($"{label}: unchanged — no valid candidate remained.");
                }
            }
            try { PendingTruckReplacements.QueueRandomization(workspace, planned, runSeed); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
            {
                Summary($"{name}: error — the region plan could not be saved. Its previous assignments were preserved.");
                continue;
            }
            uses = counts;
            heavyCount = regionHeavyCount;
            offroadCount = regionOffroadCount;
            foreach (var (reason, count) in errors) Summary($"{name}: {count} unchanged — {reason}.");
            Summary($"{name}: complete — {planned.Count} randomized, {kept} kept, {skipped} unchanged."
                + (fallbacks > 0 ? $" Nearest compatible tire category used for {fallbacks} placement(s)." : ""));
        }
    }

    private static string SourceClass(RegionTruckEntry entry) => entry.OriginalClass.Length > 0 ? entry.OriginalClass : entry.Class;

    private static string TireLabel(int category) => category switch
    {
        0 => "Highway", 1 => "All-terrain", 2 => "Offroad", _ => "Mudtires"
    };

    private static bool SameClass(string first, string second)
    {
        var a = string.Concat(first.Where(char.IsLetterOrDigit));
        var b = string.Concat(second.Where(char.IsLetterOrDigit));
        return a.Length > 0 && !a.Equals("Unknown", StringComparison.OrdinalIgnoreCase) && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    private static Random PlacementRandom(int seed, string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + key));
        return new Random(BinaryPrimitives.ReadInt32LittleEndian(bytes));
    }
}

/// <summary>Per-placement tire odds: play order leads, region tier nudges, and tire difficulty controls the reward curve.</summary>
internal static class TruckTireRandomization
{
    public static double[] Weights(int position, int regionCount, int regionDifficulty, ProgressionDifficulty difficulty)
    {
        if (regionCount < 1 || position < 0 || position >= regionCount || regionDifficulty is < 0 or > 4 || !Enum.IsDefined(difficulty))
            throw new ArgumentOutOfRangeException(nameof(position));
        // The first region keeps these exact odds in every difficulty, including single-region progressions.
        if (position == 0) return [99, 1, 0, 0];
        var progress = position / (double)(regionCount - 1);
        // Region difficulty contributes gradually so an early S region cannot bypass the starting tire progression.
        var strength = 0.8 * progress + 0.2 * (regionDifficulty / 4.0) * Math.Sqrt(progress);
        double[][] anchors = difficulty switch
        {
            ProgressionDifficulty.Easy => [[99, 1, 0, 0], [25, 45, 25, 5], [5, 15, 45, 35], [0, 5, 25, 70]],
            ProgressionDifficulty.Hard => [[99, 1, 0, 0], [60, 30, 9, 1], [25, 35, 30, 10], [10, 25, 35, 30]],
            _ => [[99, 1, 0, 0], [35, 50, 14, 1], [10, 25, 50, 15], [3, 12, 40, 45]]
        };
        var scaled = Math.Clamp(strength, 0, 1) * 3;
        var lower = Math.Min((int)scaled, 2);
        var fraction = scaled - lower;
        return Enumerable.Range(0, 4).Select(category => anchors[lower][category] * (1 - fraction) + anchors[lower + 1][category] * fraction).ToArray();
    }

    public static int Draw(double[] weights, double roll)
    {
        if (weights.Length != 4 || weights.Any(weight => !double.IsFinite(weight) || weight < 0)
            || Math.Abs(weights.Sum() - 100) > 0.000001 || !double.IsFinite(roll) || roll is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(weights));
        var remaining = roll * 100;
        for (var category = 0; category < weights.Length; category++)
        {
            if (remaining < weights[category]) return category;
            remaining -= weights[category];
        }
        return Array.FindLastIndex(weights, weight => weight > 0);
    }
}
