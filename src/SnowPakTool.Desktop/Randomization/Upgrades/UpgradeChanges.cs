using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SnowPakTool.Desktop;

internal sealed record UpgradeFileEdit(string Source, string Zone, int RewardIndex,
    string Element, string Variant, string? Before, string After);
internal sealed record UpgradeChangeSnapshot(IReadOnlyList<UpgradeFileEdit> Edits,
    IReadOnlyList<UpgradeFileEdit> Pickups);

/// <summary>Stages exact pickup/flag edits independently of the mutable randomizer draft.</summary>
internal static class UpgradeChanges
{
    private static readonly object Gate = new();
    private static readonly StringComparer Codes = StringComparer.OrdinalIgnoreCase;
    private static string PendingPath(string workspace) => WorkspaceOriginals.GetMetadataPath(workspace, "upgrade-randomization", "staged.json");
    private static UpgradeChangeSnapshot Load(string workspace) => File.Exists(PendingPath(workspace))
        ? JsonSerializer.Deserialize<UpgradeChangeSnapshot>(File.ReadAllText(PendingPath(workspace)))
            ?? throw new InvalidDataException("Invalid staged upgrade changes.") : new([], []);

    internal static SsoValue Pickup(string text, string zone, int index)
    {
        var root = SsoDataReader.Parse(text);
        if (!root.Members.TryGetValue("zoneDesc", out var zones) || !zones.Members.TryGetValue(zone, out var settings)
            || !settings.Members.TryGetValue("props", out var props)
            || !props.Members.TryGetValue("ZonePropertyUpgradesGiver", out var giver)
            || !giver.Members.TryGetValue("upgrades", out var list) || index < 0 || index >= list.Items.Count)
            throw new InvalidDataException($"Upgrade pickup no longer exists: {zone}/{index}.");
        return list.Items[index];
    }

    // Parse for identity, retain line positions to edit only the original attribute text.
    private static (XElement Part, XElement? Game, int Offset) XmlPart(string text, string element, string variant)
    {
        var clean = Regex.Replace(text, @"<\?xml[\s\S]*?\?>", m => Regex.Replace(m.Value, @"[^\r\n]", " "));
        clean = Regex.Replace(clean, @"(?<=[\w]):(?=[\w])", "_");
        var document = XDocument.Parse("<Root>\n" + clean + "\n</Root>", LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        var parts = document.Descendants(element).Where(e => !e.Ancestors().Any(a => a.Name.LocalName == "_templates")
            && (variant.Length == 0 || Codes.Equals((string?)e.Attribute("Name"), variant))).ToArray();
        if (parts.Length != 1) throw new InvalidDataException($"Cannot uniquely locate {element}/{variant}.");
        var part = parts[0]; var game = part.Element("GameData");
        var info = (IXmlLineInfo)(game ?? part);
        var offset = 0;
        for (var line = 1; line < info.LineNumber - 1; line++)
        {
            offset = text.IndexOf('\n', offset) + 1;
            if (offset == 0) throw new InvalidDataException("Invalid upgrade XML location.");
        }
        offset += info.LinePosition - 2; // XmlReader positions the name immediately after '<'.
        return (part, game, offset);
    }

    private static string? Read(string text, UpgradeFileEdit edit) => edit.Zone.Length > 0
        ? Pickup(text, edit.Zone, edit.RewardIndex).Text
        : (string?)XmlPart(text, edit.Element, edit.Variant).Game?.Attribute("UnlockByExploration");

    internal static string? ReadExploration(string text, string element, string variant) =>
        (string?)XmlPart(text, element, variant).Game?.Attribute("UnlockByExploration");

    internal static Dictionary<string, string> ReadAddonProperties(string text, string element, string variant) =>
        XmlPart(text, element, variant).Game?.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value) ?? new();

    internal static string PatchAddonProperty(string text, string element, string variant, string attribute, string? before, string? after)
    {
        var current = ReadAddonProperties(text, element, variant).GetValueOrDefault(attribute);
        if (current == after) return text;
        if (current != before) throw new InvalidDataException($"{attribute} changed since staging. Reload and stage again.");
        var (part, game, offset) = XmlPart(text, element, variant);
        var tag = Regex.Match(text[offset..], @"^<[^>]+>").Value;
        if (tag.Length == 0) throw new InvalidDataException("Cannot locate addon opening tag.");
        if (game is null)
        {
            var addition = $"<GameData {attribute}=\"{System.Security.SecurityElement.Escape(after)}\" />";
            text = part.IsEmpty ? text.Remove(offset, tag.Length).Insert(offset, tag[..^2] + ">" + addition + $"</{part.Name.LocalName}>")
                : text.Insert(offset + tag.Length, addition);
        }
        else
        {
            var attr = Regex.Match(tag, "\\s+" + Regex.Escape(attribute) + "\\s*=\\s*([\"'])(?<value>.*?)\\1");
            if (after is null)
            {
                if (!attr.Success) throw new InvalidDataException("Cannot locate addon property.");
                text = text.Remove(offset + attr.Index, attr.Length);
            }
            else if (attr.Success)
            {
                var value = attr.Groups["value"];
                text = text.Remove(offset + value.Index, value.Length).Insert(offset + value.Index, System.Security.SecurityElement.Escape(after)!);
            }
            else text = text.Insert(offset + tag.Length - (tag.EndsWith("/>") ? 2 : 1), $" {attribute}=\"{System.Security.SecurityElement.Escape(after)}\"");
        }
        if (ReadAddonProperties(text, element, variant).GetValueOrDefault(attribute) != after) throw new InvalidDataException("Addon property verification failed.");
        return text;
    }

    internal static string PatchExploration(string text, string element, string variant, string? before, string? after)
    {
        if (after is not null) return Patch(text, new("Addon exploration", "", -1, element, variant, before, after));
        var current = ReadExploration(text, element, variant);
        if (current is null) return text;
        if (current != before) throw new InvalidDataException("Exploration flag changed since staging. Reload and stage again.");
        var (_, _, offset) = XmlPart(text, element, variant);
        var tag = Regex.Match(text[offset..], @"^<[^>]+>").Value;
        var attribute = Regex.Match(tag, "\\s+UnlockByExploration\\s*=\\s*([\"'])(?<value>.*?)\\1");
        if (!attribute.Success) throw new InvalidDataException("Cannot locate explicit exploration flag.");
        text = text.Remove(offset + attribute.Index, attribute.Length);
        if (ReadExploration(text, element, variant) is not null) throw new InvalidDataException("Exploration reset verification failed.");
        return text;
    }

    internal static string Patch(string text, UpgradeFileEdit edit)
    {
        var current = Read(text, edit);
        if (current == edit.After) return text;
        if (current != edit.Before) throw new InvalidDataException($"{edit.Source}: an upgrade changed since staging. Reload and stage again.");
        if (edit.Zone.Length > 0)
        {
            var value = Pickup(text, edit.Zone, edit.RewardIndex);
            text = text.Remove(value.ValueStart, value.ValueLength).Insert(value.ValueStart, JsonSerializer.Serialize(edit.After));
        }
        else
        {
            var (part, game, offset) = XmlPart(text, edit.Element, edit.Variant);
            var tag = Regex.Match(text[offset..], @"^<[^>]+>").Value;
            if (tag.Length == 0) throw new InvalidDataException("Cannot locate upgrade opening tag.");
            if (game is null)
            {
                var addition = $"<GameData UnlockByExploration=\"{edit.After}\" />";
                text = part.IsEmpty ? text.Remove(offset, tag.Length).Insert(offset, tag[..^2] + ">" + addition + $"</{part.Name.LocalName}>")
                    : text.Insert(offset + tag.Length, addition);
            }
            else
            {
                var attr = Regex.Match(tag, "\\bUnlockByExploration\\s*=\\s*([\"'])(?<value>.*?)\\1");
                if (attr.Success)
                {
                    var value = attr.Groups["value"];
                    text = text.Remove(offset + value.Index, value.Length).Insert(offset + value.Index, edit.After);
                }
                else text = text.Insert(offset + tag.Length - (tag.EndsWith("/>") ? 2 : 1), $" UnlockByExploration=\"{edit.After}\"");
            }
        }
        if (Read(text, edit) != edit.After) throw new InvalidDataException("Upgrade patch verification failed.");
        return text;
    }

    private static (string Element, string Variant) Selector(UpgradeTrialPart part)
    {
        if (part.Element.Length > 0) return (part.Element, part.Variant);
        var id = part.Definition.Id;
        return part.Definition.Kind switch
        {
            UpgradeKind.Engine => ("Engine", id), UpgradeKind.Gearbox => ("Gearbox", id),
            UpgradeKind.Suspension => ("SuspensionSet", id),
            UpgradeKind.Tire => ("TruckTire", id[(Path.GetFileNameWithoutExtension(part.Source).Length + 1)..id.LastIndexOf('_')]),
            _ => ("TruckAddon", "")
        };
    }

    private static string Identity(UpgradeFileEdit edit) => string.Join('|', edit.Source.Replace('\\', '/'), edit.Zone, edit.RewardIndex);

    private static UpgradeFileEdit[] CurrentPickups(string workspace)
    {
        var pickups = new List<UpgradeFileEdit>();
        var sources = new[] { WorkspaceOriginals.GetUpdatedRoot(workspace), WorkspaceOriginals.GetOriginalRoot(workspace) }
            .Select(root => Path.Combine(root, "[ps]")).Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "level_*.sso", SearchOption.AllDirectories))
            .Select(path => WorkspaceOriginals.GetRelativePath(workspace, path)).Distinct(Codes).Order(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var path = WorkspaceOriginals.GetCurrentPath(workspace, source);
            if (UpgradeTrialService.Region(Path.GetFileNameWithoutExtension(path)).Length == 0) continue;
            var root = SsoDataReader.Parse(File.ReadAllText(path));
            if (!root.Members.TryGetValue("zoneDesc", out var zones)) continue;
            foreach (var (zone, settings) in zones.Members)
                if (settings.Members.TryGetValue("props", out var props)
                    && props.Members.TryGetValue("ZonePropertyUpgradesGiver", out var giver)
                    && giver.Members.TryGetValue("upgrades", out var upgrades))
                    for (var i = 0; i < upgrades.Items.Count; i++)
                    {
                        var id = upgrades.Items[i].Text;
                        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("An upgrade pickup is unreadable. Reload level settings.");
                        pickups.Add(new(WorkspaceOriginals.GetRelativePath(workspace, path), zone, i, "", "", id, id));
                    }
        }
        if (pickups.Count == 0) throw new InvalidDataException("No current upgrade pickups were read; exploration locks were not changed.");
        return pickups.ToArray();
    }

    internal static UpgradeChangeSnapshot Build(UpgradeTrialCatalog catalog)
    {
        var draft = UpgradeTrialStore.Load(catalog.Workspace);
        if (draft is not null && draft.Assignments.Count > 0 && draft.InputStamp != catalog.Stamp)
            throw new InvalidOperationException("Upgrade inputs changed. Randomize again before staging.");
        var assignments = draft?.Assignments.ToDictionary(a => UpgradeTrialService.Key(a.Slot), Codes) ?? new();
        var resets = draft?.ResetKeys?.ToHashSet(Codes) ?? [];
        var allKeys = catalog.AllSlots.Select(UpgradeTrialService.Key).ToHashSet(Codes);
        if (assignments.Keys.Any(k => !allKeys.Contains(k))) throw new InvalidDataException("A saved pickup no longer exists. Reload upgrades.");
        var texts = new Dictionary<string, string>(Codes);
        string Text(string source) => texts.TryGetValue(source, out var text) ? text
            : texts[source] = File.ReadAllText(WorkspaceOriginals.GetCurrentPath(catalog.Workspace, source));
        var pickupsByKey = CurrentPickups(catalog.Workspace).ToDictionary(Identity, Codes);
        var ownedKeys = catalog.Input.Slots.Select(UpgradeTrialService.Key).ToHashSet(Codes);
        foreach (var slot in catalog.AllSlots)
        {
            var key = string.Join('|', slot.Source.Replace('\\', '/'), slot.Zone, slot.RewardIndex);
            if (!pickupsByKey.TryGetValue(key, out var current)) throw new InvalidDataException("An original pickup no longer exists. Reload level settings.");
            if (assignments.TryGetValue(UpgradeTrialService.Key(slot), out var pending) && pending.UpgradeId != current.After)
            {
                if (!ownedKeys.Contains(UpgradeTrialService.Key(slot))) throw new InvalidOperationException("A draft pickup belongs to a region that is no longer owned. Reset it before staging.");
                if (!catalog.Parts.TryGetValue(pending.UpgradeId, out var part) || !part.Definition.Eligible && pending.UpgradeId != slot.OriginalUpgradeId)
                    throw new InvalidOperationException("A replacement upgrade is no longer eligible. Randomize again or reset it before staging.");
            }
            pickupsByKey[key] = current with { After = assignments.TryGetValue(UpgradeTrialService.Key(slot), out var assignment) ? assignment.UpgradeId
                : resets.Contains(UpgradeTrialService.Key(slot)) ? slot.OriginalUpgradeId : current.After };
        }
        var pickups = pickupsByKey.Values.ToArray();
        var edits = pickups.Where(e => e.Before != e.After).ToList();
        var found = pickups.Select(e => e.After).ToHashSet(Codes);
        if (found.Any(id => !catalog.Parts.ContainsKey(id))) throw new InvalidDataException("A resulting pickup has an unresolved upgrade definition. Exploration locks cannot be safely staged.");
        // A tire pickup encodes family + variant + scale; exploration is stored on the
        // shared family/variant definition, so any discoverable scale locks that variant.
        foreach (var group in catalog.Parts.Values.GroupBy(p => { var s = Selector(p); return p.Source + "|" + s.Element + "|" + s.Variant; }, Codes))
        {
            var part = group.First(); var (element, variant) = Selector(part);
            var edit = new UpgradeFileEdit(part.Source, "", -1, element, variant, null, group.Any(p => found.Contains(p.Definition.Id)) ? "true" : "false");
            var before = Read(Text(part.Source), edit);
            if (before != edit.After) edits.Add(edit with { Before = before });
        }
        return new(edits, pickups);
    }

    public static void Stage(UpgradeTrialCatalog catalog)
    {
        lock (Gate)
        {
            // Prepare reads every level setting and fails on unreadable pickup lists;
            // unrelated component-catalog warnings do not invalidate that complete scan.
            var snapshot = Build(catalog);
            Validate(catalog.Workspace, snapshot);
            TruckPropertiesStore.WriteAtomic(PendingPath(catalog.Workspace), JsonSerializer.Serialize(snapshot));
        }
    }

    public static IReadOnlyList<WorkspaceChange> Pending(string workspace)
    {
        lock (Gate) return Load(workspace).Edits.GroupBy(e => e.Source, Codes).Select(g =>
            new WorkspaceChange(WorkspaceChange.Pending, WorkspaceOriginals.GetCurrentPath(workspace, g.Key),
                $"Upgrade randomization: {g.Count(e => e.Zone.Length > 0)} pickups; {g.Count(e => e.Zone.Length == 0)} exploration flags",
                "Current settings", "Staged — details in Map upgrades")).ToArray();
    }

    public static bool HasDraft(UpgradeTrialCatalog catalog)
    {
        lock (Gate)
        {
            if (UpgradeTrialStore.Load(catalog.Workspace) is null) return false;
            var desired = Build(catalog);
            var staged = Load(catalog.Workspace);
            return (desired.Edits.Count > 0 || staged.Edits.Count > 0) && JsonSerializer.Serialize(desired) != JsonSerializer.Serialize(staged);
        }
    }

    public static string State(string workspace, UpgradeDistributionSlot slot, string desired)
    {
        var current = Pickup(File.ReadAllText(WorkspaceOriginals.GetCurrentPath(workspace, slot.Source)), slot.Zone, slot.RewardIndex).Text;
        if (current == desired) return current == slot.OriginalUpgradeId ? "Original" : "In files";
        return Load(workspace).Pickups.Any(e => Codes.Equals(e.Source.Replace('\\', '/'), slot.Source.Replace('\\', '/')) && Codes.Equals(e.Zone, slot.Zone)
            && e.RewardIndex == slot.RewardIndex && e.After == desired) ? "Staged" : "Draft";
    }

    public static string CurrentId(string workspace, UpgradeDistributionSlot slot) =>
        Pickup(File.ReadAllText(WorkspaceOriginals.GetCurrentPath(workspace, slot.Source)), slot.Zone, slot.RewardIndex).Text;

    private static Dictionary<string, string> Validate(string workspace, UpgradeChangeSnapshot snapshot)
    {
        var outputs = new Dictionary<string, string>(Codes);
        if (snapshot.Pickups.Count == 0 && snapshot.Edits.Count == 0) return outputs;
        var currentPickups = CurrentPickups(workspace).ToDictionary(Identity, Codes);
        if (!currentPickups.Keys.ToHashSet(Codes).SetEquals(snapshot.Pickups.Select(Identity)))
            throw new InvalidDataException("The set of map upgrade pickups changed since staging. Reload and stage again.");
        foreach (var pickup in snapshot.Pickups)
        {
            var value = currentPickups[Identity(pickup)].After;
            if (value != pickup.Before && value != pickup.After) throw new InvalidDataException("Map upgrades changed since staging. Reload and stage again.");
        }
        foreach (var group in snapshot.Edits.GroupBy(e => e.Source, Codes))
        {
            var text = File.ReadAllText(WorkspaceOriginals.GetCurrentPath(workspace, group.Key));
            foreach (var edit in group) text = Patch(text, edit);
            outputs[group.Key] = text;
        }
        return outputs;
    }

    public static void Apply(string workspace)
    {
        lock (Gate)
        {
            var snapshot = Load(workspace);
            var outputs = Validate(workspace, snapshot); // Validate every file before any write.
            foreach (var (relative, text) in outputs)
            {
                var path = WorkspaceOriginals.PrepareForEdit(workspace, WorkspaceOriginals.GetCurrentPath(workspace, relative));
                TruckPropertiesStore.WriteAtomic(path, text);
                WorkspaceDataCache.InvalidateFile(path);
            }
            TruckPropertiesStore.WriteAtomic(PendingPath(workspace), JsonSerializer.Serialize(new UpgradeChangeSnapshot([], [])));
        }
    }
}
