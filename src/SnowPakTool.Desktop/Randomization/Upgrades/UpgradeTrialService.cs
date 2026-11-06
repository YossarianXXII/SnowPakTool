using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SnowPakTool.Desktop;

internal sealed record UpgradeTrialCatalog(string Workspace, UpgradeDistributionInput Input,
    IReadOnlyList<UpgradeDistributionSlot> AllSlots, IReadOnlyDictionary<string, UpgradeTrialPart> Parts,
    string Stamp, IReadOnlyList<string> Warnings);
internal sealed record UpgradeTrialDraft(int Seed, DateTimeOffset CreatedAt, string InputStamp,
    IReadOnlyList<UpgradeDistributionAssignment> Assignments, UpgradeDistributionReport? Report,
    IReadOnlyList<string>? ResetKeys = null);

internal static class UpgradeTrialService
{
    private static readonly StringComparer Codes = StringComparer.OrdinalIgnoreCase;
    public static string Region(string map) => Regex.Match(map, @"^level_((?:us|ru)_\d{2})_", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
    public static string Key(UpgradeDistributionSlot slot) => string.Join('|', slot.Source.Replace('\\', '/'), slot.Map, slot.Zone, slot.RewardIndex);

    public static UpgradeTrialCatalog Prepare(string workspace, RegionTruckCatalog trucks, IReadOnlySet<string> enabled,
        IReadOnlySet<string> ownedRegions, IReadOnlySet<string> ownedDlc, IReadOnlyList<string> progression)
    {
        var slots = new List<UpgradeDistributionSlot>();
        var warnings = new List<string>();
        var sourceHashes = new List<string>();
        var originals = Path.Combine(WorkspaceOriginals.GetOriginalRoot(workspace), "[ps]");
        if (!Directory.Exists(originals)) throw new InvalidOperationException("Original level settings are unavailable in this workspace.");
        foreach (var path in Directory.EnumerateFiles(originals, "level_*.sso", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var map = Path.GetFileNameWithoutExtension(path); var region = Region(map);
            if (region.Length == 0) continue;
            var bytes = File.ReadAllBytes(path);
            sourceHashes.Add(map + ":" + Convert.ToHexString(SHA256.HashData(bytes)));
            var root = SsoDataReader.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
            if (!root.Members.TryGetValue("zoneDesc", out var zones)) continue;
            foreach (var (zone, settings) in zones.Members)
                if (settings.Members.TryGetValue("props", out var props)
                    && props.Members.TryGetValue("ZonePropertyUpgradesGiver", out var giver)
                    && giver.Members.TryGetValue("upgrades", out var upgrades))
                    for (var index = 0; index < upgrades.Items.Count; index++)
                    {
                        var id = upgrades.Items[index].Text;
                        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException($"{map}/{zone}: unreadable original upgrade ID.");
                        slots.Add(new(Path.GetRelativePath(WorkspaceOriginals.GetOriginalRoot(workspace), path), map, region, zone, index, id));
                    }
        }
        if (slots.Count == 0) throw new InvalidOperationException("No upgrade pickups were found in the original level settings.");
        var equipment = trucks.EquipmentCatalog ?? throw new InvalidOperationException("Reload Map trucks to read vehicle compatibility.");
        var parts = equipment.BuildUpgradeSelectionCatalog(enabled, ownedDlc, slots.Select(s => s.OriginalUpgradeId).ToHashSet(Codes));
        warnings.AddRange(equipment.UpgradeCatalogWarnings());
        var homes = trucks.Entries.Where(t => t.Availability.StartsWith("Obtainable", StringComparison.Ordinal) && enabled.Contains(t.TruckCode))
            .Select(t => new UpgradeTruckHome(t.TruckCode, t.RegionCode)).ToList();
        // Direct garage rewards have no physical map placement to find in scenes.
        foreach (var path in WorkspaceOriginals.EnumerateCurrentFiles(workspace, "[ps]", "*.sso"))
        {
            var text = WorkspaceDataCache.ReadAllText(path);
            if (!text.Contains("ObjectiveRewardItem", StringComparison.Ordinal)) continue;
            var root = SsoDataReader.Parse(text);
            foreach (var task in root.DescendantsAndSelf().Where(n => n.Get("__type") is "TaskSettings" or "ContractSettings" or "ContestSettings"))
            {
                var region = task.Get("region");
                if (region.Length == 0) region = Region("level_" + Path.GetFileNameWithoutExtension(path));
                if (region.Length == 0 || !task.Members.TryGetValue("rewards", out var rewards)) continue;
                foreach (var item in rewards.DescendantsAndSelf().Where(n => n.Key == "ObjectiveRewardItem"))
                    if (enabled.Contains(item.Get("itemName"))) homes.Add(new(item.Get("itemName"), region));
            }
        }
        var selected = slots.Where(s => ownedRegions.Contains(s.Region)).ToArray();
        var unknown = selected.Where(s => !parts.ContainsKey(s.OriginalUpgradeId)).Select(s => s.OriginalUpgradeId).Distinct().ToArray();
        if (unknown.Length > 0) throw new InvalidOperationException("Unresolved original upgrade IDs: " + string.Join(", ", unknown));
        var order = progression.Concat(selected.Select(s => s.Region).Order(StringComparer.Ordinal)).Distinct(Codes).ToArray();
        var normalizedHomes = homes.DistinctBy(h => h.Truck + "|" + h.Region, Codes).OrderBy(h => h.Truck, StringComparer.Ordinal).ThenBy(h => h.Region, StringComparer.Ordinal).ToArray();
        var input = new UpgradeDistributionInput(selected, parts.Values.Select(p => p.Definition).ToArray(), normalizedHomes, order);
        var stampText = string.Join('\n', sourceHashes) + string.Join('\n', normalizedHomes.Select(h => h.Truck + "|" + h.Region))
            + string.Join('\n', input.Definitions.OrderBy(d => d.Id, StringComparer.Ordinal).Select(d => d.Id + "|" + d.Kind + "|" + d.Eligible + "|" + string.Join(',', d.CompatibleTrucks)))
            + string.Join('|', order) + string.Join('|', selected.Select(Key));
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stampText)));
        return new(workspace, input, slots, parts, stamp, warnings.Distinct().ToArray());
    }

    public static UpgradeTrialDraft Generate(UpgradeTrialCatalog catalog, IProgress<RandomizationLogEntry> progress, int? seed = null)
    {
        if (catalog.Input.Slots.Count == 0) throw new InvalidOperationException("No upgrade pickups belong to owned regions.");
        progress.Report(new($"Reading {catalog.Parts.Count} resolved upgrade definitions; {catalog.Input.Definitions.Count(d => d.Eligible && !d.KeepInPlace)} eligible replacement IDs."));
        var plan = UpgradeDistributionPlanner.Generate(catalog.Input, seed ?? Random.Shared.Next());
        foreach (var group in plan.Assignments.GroupBy(a => a.Slot.Region))
        {
            progress.Report(new($"{group.Key.ToUpperInvariant()}: {group.Count()} pickups planned · {group.Count(a => a.Fallback)} fallbacks · {group.Count(a => a.Reused)} reused choices."));
            progress.Report(new($"{group.Key.ToUpperInvariant()}: {group.Count(a => a.Path == UpgradeSelectionPath.LocalTruck)} local-compatible primary choices; {group.Count(a => a.Path == UpgradeSelectionPath.Fixed)} fixed pickups.", true));
        }
        foreach (var note in plan.Report.Notes) progress.Report(new(note, true));
        if (catalog.Warnings.Count > 0) progress.Report(new($"{catalog.Warnings.Count} source-read warnings were retained in Map upgrades details.", true));
        var keys = catalog.Input.Slots.Select(Key).ToHashSet(Codes);
        var previous = UpgradeTrialStore.Load(catalog.Workspace);
        var retained = previous?.Assignments.Where(a => !keys.Contains(Key(a.Slot))) ?? [];
        var draft = new UpgradeTrialDraft(plan.Seed, DateTimeOffset.UtcNow, catalog.Stamp, retained.Concat(plan.Assignments).ToArray(), plan.Report,
            previous?.ResetKeys?.Where(k => !keys.Contains(k)).ToArray());
        UpgradeTrialStore.Save(catalog.Workspace, draft);
        progress.Report(new($"Seed {plan.Seed}. Draft saved: {plan.Assignments.Count(a => !Codes.Equals(a.UpgradeId, a.Slot.OriginalUpgradeId))} changed rewards. Stage changes, then Apply pending changes from Start."));
        return draft;
    }
}

internal static class UpgradeTrialStore
{
    private static readonly object Gate = new();
    private static string PathFor(string workspace) => WorkspaceOriginals.GetMetadataPath(workspace, "upgrade-randomization", "draft.json");
    public static UpgradeTrialDraft? Load(string workspace)
    {
        lock (Gate)
        {
            var path = PathFor(workspace);
            if (!File.Exists(path)) return null;
            var draft = JsonSerializer.Deserialize<UpgradeTrialDraft>(File.ReadAllText(path));
            if (draft?.Assignments is null || draft.Assignments.Any(a => a?.Slot is null || string.IsNullOrWhiteSpace(a.UpgradeId))
                || draft.Assignments.Select(a => UpgradeTrialService.Key(a.Slot)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != draft.Assignments.Count)
                throw new InvalidDataException("The saved upgrade preview is invalid; it was not overwritten.");
            return draft;
        }
    }
    public static void Save(string workspace, UpgradeTrialDraft draft)
    {
        lock (Gate)
        {
            var path = PathFor(workspace); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(draft, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
    }
    public static void Reset(string workspace, IEnumerable<UpgradeDistributionSlot> slots)
    {
        lock (Gate)
        {
            var draft = Load(workspace) ?? new UpgradeTrialDraft(0, DateTimeOffset.UtcNow, "", [], null);
            var keys = slots.Select(UpgradeTrialService.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Save(workspace, draft with { Assignments = draft.Assignments.Where(a => !keys.Contains(UpgradeTrialService.Key(a.Slot))).ToArray(),
                ResetKeys = (draft.ResetKeys ?? []).Concat(keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), Report = null });
        }
    }
}
