namespace SnowPakTool.Desktop;

internal enum UpgradeKind { Engine, Suspension, Gearbox, DifferentialLock, Awd, Tire, MetalDetector }
internal enum UpgradePlacementRelation { SpecificLocal, SpecificElsewhere, SpecificWithoutHome, SharedLocal, SharedForeign, Fixed }
internal enum UpgradeSelectionPath { Fixed, LocalTruck, Shared, OtherSameType, KeptOriginal }

// Include the full verified compatibility set. Eligible excludes stock/default,
// unavailable or otherwise unwanted replacement candidates without hiding originals.
internal sealed record UpgradeDistributionDefinition(string Id, UpgradeKind Kind,
    IReadOnlyList<string> CompatibleTrucks, bool KeepInPlace = false, bool Eligible = true);
internal sealed record UpgradeDistributionSlot(string Source, string Map, string Region,
    string Zone, int RewardIndex, string OriginalUpgradeId);
internal sealed record UpgradeTruckHome(string Truck, string Region);
internal sealed record UpgradeDistributionInput(IReadOnlyList<UpgradeDistributionSlot> Slots,
    IReadOnlyList<UpgradeDistributionDefinition> Definitions, IReadOnlyList<UpgradeTruckHome> TruckHomes,
    IReadOnlyList<string> RegionOrder);
internal sealed record UpgradeDistributionOptions(double FirstRegionForeignSharedChance = .70);
internal sealed record UpgradeDistributionAssignment(UpgradeDistributionSlot Slot, string UpgradeId,
    UpgradeKind Kind, UpgradePlacementRelation Relation, UpgradeSelectionPath Path, bool Fallback, bool Reused);
internal sealed record UpgradeDistributionReport(int OriginalSpecificSlots, int OriginalSharedSlots,
    int LocalChoicesForSpecificSlots, int Fallbacks, int Reused, int KeptOriginal, string? FirstRegion,
    int FirstRegionSharedSlots, int FirstRegionForeignSharedChoices,
    IReadOnlyDictionary<UpgradeKind, int> OriginalTypes, IReadOnlyDictionary<UpgradeKind, int> PlannedTypes,
    IReadOnlyList<string> Notes);
internal sealed record UpgradeDistributionPlan(int Seed, IReadOnlyList<UpgradeDistributionAssignment> Assignments,
    UpgradeDistributionReport Report);

/// <summary>Pure slot-by-slot reward selection. Preserves each slot's type, not
/// the old reward IDs. No file access, staging, optimizer or locality quota.</summary>
internal static class UpgradeDistributionPlanner
{
    private static readonly StringComparer Codes = StringComparer.OrdinalIgnoreCase;
    private sealed record Part(UpgradeDistributionDefinition Definition, bool Shared, HashSet<string> Homes)
    {
        public string Id => Definition.Id;
        public bool Fixed => Definition.KeepInPlace || Definition.Kind == UpgradeKind.MetalDetector;
    }

    public static UpgradeDistributionPlan Generate(UpgradeDistributionInput input, int seed,
        UpgradeDistributionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new();
        if (!double.IsFinite(options.FirstRegionForeignSharedChance) || options.FirstRegionForeignSharedChance is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        var regions = input.RegionOrder.ToArray();
        if (regions.Any(string.IsNullOrWhiteSpace) || regions.Distinct(Codes).Count() != regions.Length)
            throw new ArgumentException("Region progression must contain unique non-empty codes.");
        var slots = input.Slots.OrderBy(Key, StringComparer.Ordinal).ToArray();
        if (slots.Any(s => Blank(s.Source, s.Map, s.Region, s.Zone, s.OriginalUpgradeId) || s.RewardIndex < 0 || !regions.Contains(s.Region, Codes))
            || slots.Select(Key).Distinct(Codes).Count() != slots.Length)
            throw new ArgumentException("Pickups need unique source/map/zone/index identities and a region in progression.");
        if (input.TruckHomes.Any(h => Blank(h.Truck, h.Region))) throw new ArgumentException("Truck homes need model and region codes.");
        var homes = input.TruckHomes.GroupBy(h => h.Truck, Codes)
            .ToDictionary(g => g.Key, g => g.Select(h => h.Region).ToHashSet(Codes), Codes);
        var parts = new Dictionary<string, Part>(Codes);
        foreach (var definition in input.Definitions)
        {
            if (Blank(definition.Id) || !Enum.IsDefined(definition.Kind) || definition.CompatibleTrucks.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Definitions need valid IDs, types and verified compatibility.");
            var compatible = definition.CompatibleTrucks.Distinct(Codes).ToArray();
            var part = new Part(definition, compatible.Length > 1,
                compatible.Where(homes.ContainsKey).SelectMany(code => homes[code]).ToHashSet(Codes));
            if ((!part.Fixed && compatible.Length == 0) || !parts.TryAdd(part.Id, part))
                throw new ArgumentException($"Missing compatibility or duplicate definition for '{part.Id}'.");
        }
        if (slots.Any(s => !parts.ContainsKey(s.OriginalUpgradeId)))
            throw new ArgumentException("Resolve every original reward ID, including composite tire IDs, before generating.");

        var random = new Random(seed);
        var first = regions.FirstOrDefault();
        var candidates = parts.Values.Where(p => !p.Fixed && p.Definition.Eligible)
            .OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
        var uses = new Dictionary<string, int>(Codes);
        var assignments = new Dictionary<string, UpgradeDistributionAssignment>(Codes);
        var foreignUnavailable = 0;
        void Assign(UpgradeDistributionSlot slot, Part part, UpgradeSelectionPath path, bool fallback)
        {
            var used = uses.GetValueOrDefault(part.Id);
            assignments.Add(Key(slot), new(slot, part.Id, part.Definition.Kind,
                path == UpgradeSelectionPath.Fixed ? UpgradePlacementRelation.Fixed : Relation(part, slot.Region), path, fallback, used > 0));
            uses[part.Id] = used + 1;
        }
        foreach (var slot in slots.Where(s => parts[s.OriginalUpgradeId].Fixed))
            Assign(slot, parts[slot.OriginalUpgradeId], UpgradeSelectionPath.Fixed, false);

        // Protect scarce local choices before generic slots consume shared parts.
        // Ties are seeded; list ordering supplied by the caller does not matter.
        var work = slots.Where(s => !parts[s.OriginalUpgradeId].Fixed).Select(slot =>
        {
            var original = parts[slot.OriginalUpgradeId];
            var localCount = candidates.Count(p => p.Definition.Kind == original.Definition.Kind && p.Homes.Contains(slot.Region));
            return (Slot: slot, Original: original, LocalCount: localCount, Tie: random.NextDouble());
        }).OrderBy(x => x.Original.Shared ? 1 : 0)
          .ThenBy(x => x.Original.Shared ? Codes.Equals(x.Slot.Region, first) ? 0 : 1 : x.LocalCount == 0 ? int.MaxValue : x.LocalCount)
          .ThenBy(x => x.Tie).ToArray();

        foreach (var (slot, original, _, _) in work)
        {
            var sameType = candidates.Where(p => p.Definition.Kind == original.Definition.Kind).ToArray();
            var local = sameType.Where(p => p.Homes.Contains(slot.Region)).ToArray();
            var shared = sameType.Where(p => p.Shared).ToArray();
            var preferForeign = original.Shared && Codes.Equals(slot.Region, first)
                && random.NextDouble() < options.FirstRegionForeignSharedChance;
            Part? Pick(Part[] pool, bool unused, bool foreignPreference = false)
            {
                var available = pool.Where(p => !unused || uses.GetValueOrDefault(p.Id) == 0).ToArray();
                if (foreignPreference)
                {
                    var foreign = available.Where(p => !p.Homes.Contains(slot.Region)).ToArray();
                    if (foreign.Length > 0) available = foreign;
                }
                if (available.Length == 0) return null;
                if (!unused)
                {
                    var leastUsed = available.Min(p => uses.GetValueOrDefault(p.Id));
                    available = available.Where(p => uses.GetValueOrDefault(p.Id) == leastUsed).ToArray();
                }
                return available[random.Next(available.Length)];
            }

            // Unused rewards are tried across fallback pools before any reuse.
            var tiers = original.Shared
                ? new[] { (shared, true, UpgradeSelectionPath.Shared), (sameType, true, UpgradeSelectionPath.OtherSameType),
                    (shared, false, UpgradeSelectionPath.Shared), (sameType, false, UpgradeSelectionPath.OtherSameType) }
                : new[] { (local, true, UpgradeSelectionPath.LocalTruck), (shared, true, UpgradeSelectionPath.Shared),
                    (sameType, true, UpgradeSelectionPath.OtherSameType), (local, false, UpgradeSelectionPath.LocalTruck),
                    (shared, false, UpgradeSelectionPath.Shared), (sameType, false, UpgradeSelectionPath.OtherSameType) };
            var selected = false;
            for (var index = 0; index < tiers.Length; index++)
            {
                var (pool, unused, path) = tiers[index];
                var choice = Pick(pool, unused, preferForeign && path == UpgradeSelectionPath.Shared);
                if (choice is null) continue;
                Assign(slot, choice, path, index > 0);
                if (preferForeign && (!choice.Shared || choice.Homes.Contains(slot.Region))) foreignUnavailable++;
                selected = true;
                break;
            }
            if (!selected) Assign(slot, original, UpgradeSelectionPath.KeptOriginal, true);
        }

        var result = slots.Select(s => assignments[Key(s)]).ToArray();
        if (result.Any(a => a.Kind != parts[a.Slot.OriginalUpgradeId].Definition.Kind
            || (parts[a.Slot.OriginalUpgradeId].Fixed && a.UpgradeId != a.Slot.OriginalUpgradeId)))
            throw new InvalidOperationException("A pickup type or fixed reward changed unexpectedly.");
        var specific = result.Where(a => !parts[a.Slot.OriginalUpgradeId].Fixed && !parts[a.Slot.OriginalUpgradeId].Shared).ToArray();
        var generic = result.Where(a => !parts[a.Slot.OriginalUpgradeId].Fixed && parts[a.Slot.OriginalUpgradeId].Shared).ToArray();
        var firstGeneric = generic.Where(a => Codes.Equals(a.Slot.Region, first)).ToArray();
        var notes = new List<string>();
        if (result.Any(a => a.Fallback)) notes.Add($"{result.Count(a => a.Fallback)} slots used a fallback pool or reused a reward.");
        if (result.Any(a => a.Path == UpgradeSelectionPath.KeptOriginal)) notes.Add("Some slots kept their original reward because no eligible same-type replacement exists.");
        if (foreignUnavailable > 0) notes.Add($"{foreignUnavailable} first-region shared slots could not satisfy their foreign preference while preserving unused-first selection.");
        return new(seed, result, new(specific.Length, generic.Length,
            specific.Count(a => parts[a.UpgradeId].Homes.Contains(a.Slot.Region)), result.Count(a => a.Fallback),
            result.Count(a => a.Reused), result.Count(a => a.Path == UpgradeSelectionPath.KeptOriginal), first,
            firstGeneric.Length, firstGeneric.Count(a => a.Relation == UpgradePlacementRelation.SharedForeign),
            Counts(slots.Select(s => parts[s.OriginalUpgradeId].Definition.Kind)), Counts(result.Select(a => a.Kind)), notes));
    }

    private static UpgradePlacementRelation Relation(Part part, string region) => part.Shared
        ? part.Homes.Contains(region) ? UpgradePlacementRelation.SharedLocal : UpgradePlacementRelation.SharedForeign
        : part.Homes.Count == 0 ? UpgradePlacementRelation.SpecificWithoutHome
            : part.Homes.Contains(region) ? UpgradePlacementRelation.SpecificLocal : UpgradePlacementRelation.SpecificElsewhere;
    private static Dictionary<UpgradeKind, int> Counts(IEnumerable<UpgradeKind> values)
    {
        var counts = values.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        return Enum.GetValues<UpgradeKind>().ToDictionary(k => k, k => counts.GetValueOrDefault(k));
    }
    private static bool Blank(params string[] values) => values.Any(string.IsNullOrWhiteSpace);
    private static string Key(UpgradeDistributionSlot slot) => string.Join('\u001f', slot.Source.Replace('\\', '/'), slot.Map, slot.Zone,
        slot.RewardIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
