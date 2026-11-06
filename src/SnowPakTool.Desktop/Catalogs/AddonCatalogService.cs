using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SnowPakTool.Desktop;

internal sealed record UpgradeMapReference(string Map, string Zone, int Index, string UpgradeId, string Source);
internal sealed record AddonCatalogProperty(string Source, string Property, string Value);
internal sealed record FrameAddonSummary(decimal? CargoSlots, decimal? Fuel, decimal? Water, decimal? Repairs, decimal? SpareWheels);
internal sealed record AddonCatalogEntry(string Name, string Type, string Category, string Id, string Family,
    string Content, decimal? Price, decimal? UnlockRank, string Exploration, string Description, string Source,
    IReadOnlyList<UpgradeMapReference> MapReferences, IReadOnlyList<AddonCatalogProperty> Properties, FrameAddonSummary? Frame = null);
internal sealed record AddonCatalog(IReadOnlyList<AddonCatalogEntry> Entries,
    IReadOnlyList<UpgradeMapReference> MapReferences, IReadOnlyList<string> Warnings);

internal static class AddonCatalogService
{
    private sealed record Definition(string Path, string Relative, XElement Root, XElement Xml);

    public static AddonCatalog Load(string workspace, IReadOnlyDictionary<string, string> names)
    {
        var warnings = new List<string>();
        var references = ReadMapReferences(workspace, warnings);
        var referencesById = references.GroupBy(item => item.UpgradeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<UpgradeMapReference>)group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var definitions = new List<Definition>();
        foreach (var path in WorkspaceOriginals.EnumerateCurrentFiles(workspace, "[media]", "*.xml"))
        {
            var relative = WorkspaceOriginals.GetRelativePath(workspace, path).Replace('\\', '/');
            if (!new[] { "trucks", "engines", "gearboxes", "suspensions", "winches", "wheels" }
                    .Any(folder => relative.Contains("/classes/" + folder + "/", StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var root = new XElement("Root");
                using var reader = XmlReader.Create(new StringReader(WorkspaceDataCache.ReadAllText(path)),
                    new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit });
                while (!reader.EOF)
                {
                    if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                    root.Add(XNode.ReadFrom(reader));
                }
                var xml = root.Elements().FirstOrDefault(element => !element.Name.LocalName.StartsWith('_'));
                if (xml is not null) definitions.Add(new(path, relative, root, xml));
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            { warnings.Add($"{relative}: {ex.Message}"); }
        }
        var byPath = definitions.ToDictionary(item => Path.GetFullPath(item.Path), StringComparer.OrdinalIgnoreCase);
        var byName = definitions.GroupBy(item => Path.GetFileNameWithoutExtension(item.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var entries = new List<AddonCatalogEntry>();
        foreach (var definition in definitions)
        {
            var kind = definition.Xml.Name.LocalName;
            if (kind == "TruckAddon") Add(definition.Xml, "Addon", Path.GetFileNameWithoutExtension(definition.Path));
            else
                foreach (var variant in definition.Xml.Descendants().Where(element => element.Name.LocalName is
                             "Engine" or "Gearbox" or "SuspensionSet" or "Winch" or "TruckTire"))
                {
                    var id = (string?)variant.Attribute("Name");
                    if (!string.IsNullOrEmpty(id)) Add(variant, variant.Name.LocalName == "SuspensionSet" ? "Suspension" :
                        variant.Name.LocalName == "TruckTire" ? "Tire" : variant.Name.LocalName, id);
                }

            void Add(XElement xml, string type, string id)
            {
                // Follow file parents for explicitly defined metadata. Shared/local template
                // expansion and complete installation compatibility are deliberately not inferred.
                var chain = new List<(Definition Definition, XElement Xml)> { (definition, xml) };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { definition.Path };
                var current = definition;
                while ((string?)current.Root.Element("_parent")?.Attribute("File") is { Length: > 0 } parent)
                {
                    var local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current.Path)!, parent + ".xml"));
                    if (!byPath.TryGetValue(local, out var next))
                    {
                        var code = Path.GetFileNameWithoutExtension(parent);
                        if (!byName.TryGetValue(code, out var candidates) || candidates.Length != 1)
                        { warnings.Add($"{current.Relative}: missing or ambiguous parent {parent}."); break; }
                        next = candidates[0];
                    }
                    if (!seen.Add(next.Path)) { warnings.Add($"{definition.Relative}: cyclic file parent."); break; }
                    var inherited = type == "Addon" ? next.Xml : next.Xml.Descendants()
                        .FirstOrDefault(element => element.Name == xml.Name && (string?)element.Attribute("Name") == id);
                    if (inherited is null) break;
                    chain.Add((next, inherited));
                    current = next;
                }
                string? Field(string path, string attribute)
                {
                    foreach (var item in chain)
                    {
                        XElement? node = item.Xml;
                        var blocked = (string?)node.Attribute("_noinherit") == "true";
                        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                        {
                            node = node?.Element(part);
                            blocked |= (string?)node?.Attribute("_noinherit") == "true";
                        }
                        if ((string?)node?.Attribute(attribute) is { } value) return value;
                        if (blocked) return null;
                    }
                    return null;
                }
                string Label(string? key, string fallback) => key is null ? fallback : names.TryGetValue(key, out var value) ? value : key;
                var properties = new List<AddonCatalogProperty>();
                foreach (var item in chain)
                {
                    Collect(item.Definition.Relative, item.Xml, "", properties);
                    if (item.Definition.Root.Element("_parent") is { } parent) Collect(item.Definition.Relative, parent, "", properties);
                    if (item.Definition.Root.Element("_templates") is { } templates) Collect(item.Definition.Relative, templates, "", properties);
                }
                var parts = definition.Relative.Split('/');
                var dlc = Array.FindIndex(parts, part => part.Equals("_dlc", StringComparison.OrdinalIgnoreCase));
                entries.Add(new(Label(Field("GameData/UiDesc", "UiName"), id), type, Field("GameData", "Category") ?? "",
                    id, type == "Addon" ? Field("GameData/InstallSocket", "Type") ?? "" : Path.GetFileNameWithoutExtension(definition.Path),
                    dlc >= 0 && dlc + 1 < parts.Length ? parts[dlc + 1] : "Base game",
                    Number(Field("GameData", "Price")), Number(Field("GameData", "UnlockByRank")), Field("GameData", "UnlockByExploration") ?? "",
                    Label(Field("GameData/UiDesc", "UiDesc"), ""), definition.Relative,
                    referencesById.GetValueOrDefault(id) ?? [], properties,
                    type == "Addon" ? new FrameAddonSummary(Number(Field("GameData/AddonSlots", "Quantity")),
                        Number(Field("TruckData", "FuelCapacity")), Number(Field("TruckData", "WaterCapacity")),
                        Number(Field("TruckData", "RepairsCapacity")), Number(Field("TruckData", "WheelRepairsCapacity"))) : null));
            }
        }
        var known = entries.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in referencesById.Where(pair => !known.Contains(pair.Key)))
        {
            warnings.Add($"Upgrade ID '{group.Key}' is referenced by level settings but has no catalog definition.");
            entries.Add(new(group.Key, "Unresolved map upgrade", "", group.Key, "", "Unknown", null, null, "", "No definition found in this workspace.", "", group.Value, []));
        }
        return new(entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(entry => entry.Id)
            .ThenBy(entry => entry.Source).ToArray(), references, warnings.Distinct().ToArray());
    }

    internal static IReadOnlyList<UpgradeMapReference> ReadMapReferences(string workspace, List<string> warnings)
    {
        var result = new List<UpgradeMapReference>();
        foreach (var path in WorkspaceOriginals.EnumerateCurrentFiles(workspace, "[ps]", "*.sso")
                     .Where(path => Path.GetFileName(path).StartsWith("level_", StringComparison.OrdinalIgnoreCase)))
        {
            var relative = WorkspaceOriginals.GetRelativePath(workspace, path);
            try
            {
                var root = SsoDataReader.Parse(WorkspaceDataCache.ReadAllText(path));
                if (!root.Members.TryGetValue("zoneDesc", out var zones)) continue;
                foreach (var (zone, settings) in zones.Members)
                {
                    if (!settings.Members.TryGetValue("props", out var props)
                        || !props.Members.TryGetValue("ZonePropertyUpgradesGiver", out var giver)
                        || !giver.Members.TryGetValue("upgrades", out var upgrades)) continue;
                    for (var index = 0; index < upgrades.Items.Count; index++)
                    {
                        var id = upgrades.Items[index].Text;
                        if (string.IsNullOrWhiteSpace(id)) { warnings.Add($"{relative}: {zone} upgrade {index} has no scalar ID."); continue; }
                        result.Add(new(Path.GetFileNameWithoutExtension(path), zone, index, id, relative));
                    }
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            { warnings.Add($"{relative}: {ex.Message}"); }
        }
        return result;
    }

    private static decimal? Number(string? value) => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static void Collect(string source, XElement element, string parent, List<AddonCatalogProperty> result)
    {
        var siblings = element.Parent?.Elements(element.Name).ToArray();
        var suffix = siblings is { Length: > 1 } ? $"[{Array.IndexOf(siblings, element) + 1}]" : "";
        var path = parent + element.Name.LocalName + suffix;
        foreach (var attribute in element.Attributes()) result.Add(new(source, path + "/@" + attribute.Name, attribute.Value));
        foreach (var child in element.Elements()) Collect(source, child, path + "/", result);
    }
}
