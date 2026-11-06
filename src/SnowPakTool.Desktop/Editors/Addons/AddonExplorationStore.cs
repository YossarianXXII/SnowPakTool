using System.Text.Json;
using System.Globalization;

namespace SnowPakTool.Desktop;

internal sealed record AddonExplorationEdit(string Source, string Element, string Variant, string Name, string? Before, string? After, string Attribute = "UnlockByExploration");
internal sealed class AddonExplorationRow(AddonExplorationEdit identity, string? original, bool hasOriginal)
{
    public AddonExplorationEdit Identity { get; } = identity;
    public string? Original { get; } = original;
    public bool HasOriginal { get; } = hasOriginal;
    public string? Current => Identity.Before;
    public string? Draft { get; set; } = identity.Before;
}

internal static class AddonExplorationStore
{
    private static string PathFor(string workspace) => WorkspaceOriginals.GetMetadataPath(workspace, "upgrade-randomization", "addon-exploration.json");
    internal static readonly string[] Attributes = ["Price", "UnlockByRank", "UnlockByExploration"];
    internal static string Column(string attribute) => attribute switch { "Price" => "Price", "UnlockByRank" => "Unlock rank", _ => "Exploration unlock" };
    internal static string Key(string source, string element, string variant, string attribute = "UnlockByExploration") => source.Replace('\\', '/') + "|" + element + "|" + variant + "|" + attribute;
    internal static string Key(AddonCatalogEntry entry, string attribute = "UnlockByExploration") => Key(entry.Source, Element(entry), entry.Type == "Addon" ? "" : entry.Id, attribute);
    private static string Element(AddonCatalogEntry entry) => entry.Type switch { "Addon" => "TruckAddon", "Suspension" => "SuspensionSet", "Tire" => "TruckTire", _ => entry.Type };
    internal static string Display(string? flag) => flag ?? "Inherited / unspecified";

    internal static Dictionary<string, AddonExplorationRow> ReadRows(string workspace, AddonCatalog catalog)
    {
        var result = new Dictionary<string, AddonExplorationRow>(StringComparer.OrdinalIgnoreCase);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Text(string path) => texts.TryGetValue(path, out var value) ? value : texts[path] = File.ReadAllText(path);
        foreach (var entry in catalog.Entries.Where(e => e.Source.Length > 0 && e.Type != "Unresolved map upgrade"))
        {
            try
            {
                var element = Element(entry); var variant = entry.Type == "Addon" ? "" : entry.Id;
                var values = UpgradeChanges.ReadAddonProperties(Text(WorkspaceOriginals.GetCurrentPath(workspace, entry.Source)), element, variant);
                var originalPath = WorkspaceOriginals.GetOriginalReadPath(workspace, entry.Source);
                Dictionary<string, string>? originals = null;
                if (File.Exists(originalPath))
                    try { originals = UpgradeChanges.ReadAddonProperties(Text(originalPath), element, variant); }
                    catch (InvalidDataException) { }
                foreach (var attribute in Attributes)
                {
                    var current = values.GetValueOrDefault(attribute); var original = originals?.GetValueOrDefault(attribute);
                    if (current is not null && attribute != "UnlockByExploration"
                        && !decimal.TryParse(current, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)) continue;
                    var identity = new AddonExplorationEdit(entry.Source, element, variant, entry.Name, current, current, attribute);
                    result[Key(entry, attribute)] = new(identity, original, originals is not null && ValidValue(attribute, original));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException) { /* Ambiguous definitions stay read-only. */ }
        }
        return result;
    }

    internal static List<AddonExplorationEdit> Load(string workspace)
    {
        var path = PathFor(workspace);
        var edits = File.Exists(path) ? JsonSerializer.Deserialize<List<AddonExplorationEdit>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid addon exploration snapshot.") : [];
        Validate(edits); return edits;
    }
    private static void Validate(IReadOnlyList<AddonExplorationEdit> edits)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in edits)
        {
            var path = e?.Source?.Replace('\\', '/');
            if (e is null || path is null || e.Name is null || e.Variant is null || Path.IsPathRooted(path) || path.Contains(':')
                || !path.StartsWith("[media]/", StringComparison.OrdinalIgnoreCase) || !path.Contains("/classes/", StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || path.Split('/').Any(p => p is ".." or "." or "")
                || e.Element is not ("TruckAddon" or "Engine" or "Gearbox" or "SuspensionSet" or "Winch" or "TruckTire")
                || e.Element != "TruckAddon" && e.Variant.Length == 0
                || !Attributes.Contains(e.Attribute) || !ValidValue(e.Attribute, e.After)
                || !seen.Add(Key(path, e.Element, e.Variant, e.Attribute))) throw new InvalidDataException("Invalid or duplicate addon property edit.");
        }
    }
    internal static bool ValidValue(string attribute, string? value) => value is null || (attribute == "UnlockByExploration"
        ? value is "true" or "false" : decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            && decimal.Truncate(number) == number && number >= 0 && number <= (attribute == "Price" ? 100000000 : 30));
    internal static void Save(string workspace, List<AddonExplorationEdit> edits)
    {
        Validate(edits); TruckPropertiesStore.WriteAtomic(PathFor(workspace), JsonSerializer.Serialize(edits));
    }
    internal static IEnumerable<WorkspaceChange> Pending(string workspace) => Load(workspace).Select(e =>
        new WorkspaceChange(WorkspaceChange.Pending, WorkspaceOriginals.GetCurrentPath(workspace, e.Source),
            e.Name + " / " + Column(e.Attribute), Display(e.Before), Display(e.After)));
    internal static void Apply(string workspace)
    {
        var outputs = Load(workspace).GroupBy(e => e.Source, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var path = WorkspaceOriginals.GetCurrentPath(workspace, group.Key); var text = File.ReadAllText(path);
            foreach (var e in group) text = UpgradeChanges.PatchAddonProperty(text, e.Element, e.Variant, e.Attribute, e.Before, e.After);
            return (Path: path, Text: text);
        }).ToArray();
        foreach (var output in outputs)
        {
            var path = WorkspaceOriginals.PrepareForEdit(workspace, output.Path);
            TruckPropertiesStore.WriteAtomic(path, output.Text); WorkspaceDataCache.InvalidateFile(path);
        }
        Save(workspace, []);
    }
}
