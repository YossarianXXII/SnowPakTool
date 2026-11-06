using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace SnowPakTool.Desktop;

internal sealed record CargoProperty(string Source, string Property, string Value);
internal sealed record CargoMassTarget(string Role, string RelativeFile, string BodyPath, string ModelFrame, string? RawMass, decimal? EffectiveMass, decimal? InheritedMass);
internal sealed record CargoEntry(string Name, string Type, string Variant, string Content,
    decimal? Slots, decimal? LooseMassKg, decimal? PackedMassKg, decimal? LoadValue, string Description,
    string TypeFile, string AddonFile, IReadOnlyList<CargoProperty> Properties, IReadOnlyList<CargoMassTarget> MassTargets);
internal sealed record CargoCatalog(IReadOnlyList<CargoEntry> Entries, IReadOnlyList<string> Warnings);

internal static class CargoCatalogService
{
    public static CargoCatalog Load(string workspace, IReadOnlyDictionary<string, string> names)
    {
        var warnings = new List<string>();
        var types = new Dictionary<string, (string Path, XElement Xml)>(StringComparer.OrdinalIgnoreCase);
        var addons = new List<(string Path, XElement Xml, XElement Slot)>();
        var units = new List<(string Path, XElement Xml, IReadOnlyDictionary<string, XElement> Templates)>();
        foreach (var path in WorkspaceOriginals.EnumerateCurrentFiles(workspace, "[media]", "*.xml"))
        {
            var relative = WorkspaceOriginals.GetRelativePath(workspace, path).Replace('\\', '/');
            var isType = relative.Contains("/classes/cargo_types/", StringComparison.OrdinalIgnoreCase);
            var isAddon = relative.Contains("/classes/trucks/", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(path).StartsWith("cargo_", StringComparison.OrdinalIgnoreCase);
            var isUnit = relative.Contains("/classes/models/", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(path).StartsWith("cargo_unit_", StringComparison.OrdinalIgnoreCase);
            if (!isType && !isAddon && !isUnit) continue;
            try
            {
                // Game XML may contain several top-level elements (templates and the definition).
                using var reader = XmlReader.Create(new StringReader(WorkspaceDataCache.ReadAllText(path)),
                    new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit });
                while (!reader.EOF)
                {
                    if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                    var element = (XElement)XNode.ReadFrom(reader);
                    if (isType && element.Name.LocalName == "CargoType")
                        types[Path.GetFileNameWithoutExtension(path)] = (relative, element);
                    if (isAddon && element.Name.LocalName == "TruckAddon")
                        foreach (var slot in element.Descendants("InstallSlot").Where(slot => slot.Attribute("CargoType") is not null))
                            addons.Add((relative, element, slot));
                    if (isUnit && element.Name.LocalName == "ModelBrand"
                        && (string?)element.Attribute("DynamicModel") == "true"
                        && element.Element("GameData")?.Attribute("LoadType") is not null)
                        units.Add((relative, element, ReadBodyTemplates(path)));
                }
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{relative}: {ex.Message}");
            }
        }

        var entries = new List<CargoEntry>();
        foreach (var type in types.Keys.Concat(addons.Select(addon => (string)addon.Slot.Attribute("CargoType")!))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            types.TryGetValue(type, out var definition);
            var ui = definition.Xml?.Element("UiDesc");
            var nameKey = (string?)ui?.Attribute("UiName");
            var descKey = (string?)ui?.Attribute("UiDesc");
            var name = Resolve(nameKey, type, names);
            var description = Resolve(descKey, "", names);
            var matching = addons.Where(addon => string.Equals((string?)addon.Slot.Attribute("CargoType"), type, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length == 0) AddEntry(null, null, null);
            else foreach (var addon in matching) AddEntry(addon.Path, addon.Xml, addon.Slot);

            void AddEntry(string? addonPath, XElement? addonXml, XElement? slot)
            {
                var properties = new List<CargoProperty>();
                var targets = new List<CargoMassTarget>();
                void AddTargets(string role, string source, XElement xml, IReadOnlyDictionary<string, XElement> templates)
                {
                    foreach (var body in xml.Element("PhysicsModel")?.Descendants("Body") ?? [])
                    {
                        var path = string.Join("/", body.AncestorsAndSelf().Reverse().Select(element =>
                            element.Name.LocalName + "[" + (element.ElementsBeforeSelf(element.Name).Count() + 1) + "]"));
                        var inherited = new XElement(body);
                        inherited.Attribute("Mass")?.Remove();
                        targets.Add(new(role, source, path, (string?)body.Attribute("ModelFrame") ?? "",
                            (string?)body.Attribute("Mass"), ResolveBodyMass(body, templates, new(StringComparer.OrdinalIgnoreCase)),
                            ResolveBodyMass(inherited, templates, new(StringComparer.OrdinalIgnoreCase))));
                    }
                }
                if (definition.Xml is not null) CollectProperties(definition.Path, definition.Xml, "", properties);
                if (addonXml is not null) CollectProperties(addonPath!, addonXml, "", properties);
                if (addonXml is not null) AddTargets("Packed", addonPath!, addonXml, new Dictionary<string, XElement>());
                var looseUnits = units.Where(unit => string.Equals((string?)unit.Xml.Element("GameData")?.Attribute("LoadType"),
                    type, StringComparison.OrdinalIgnoreCase)
                    && (slot is null || Number((string?)unit.Xml.Element("GameData")?.Attribute("PackSlotsNumber"))
                        == Number((string?)slot.Attribute("CargoLength")))).ToArray();
                foreach (var unit in looseUnits)
                {
                    CollectProperties(unit.Path, unit.Xml, "", properties);
                    AddTargets("Loose", unit.Path, unit.Xml, unit.Templates);
                    var visitedTemplates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    void AddTemplate(string? name)
                    {
                        if (name is null || !visitedTemplates.Add(name) || !unit.Templates.TryGetValue(name, out var template)) return;
                        CollectProperties(template.Annotation<string>()!, template, "_templates/Body/", properties);
                        AddTemplate((string?)template.Attribute("_template"));
                    }
                    foreach (var body in unit.Xml.Descendants("Body")) AddTemplate((string?)body.Attribute("_template"));
                }
                var looseMasses = looseUnits.Select(unit => BodyMass(unit.Xml, unit.Templates)).ToArray();
                // A cargo type can have several loose models. Do not choose an arbitrary weight.
                decimal? looseMass = looseMasses.Length > 0 && looseMasses.All(value => value.HasValue)
                    && looseMasses.Distinct().Count() == 1 ? looseMasses[0] : null;
                var massAttributes = addonXml?.Element("PhysicsModel")?.Descendants("Body")
                    .Select(body => (string?)body.Attribute("Mass")).ToArray() ?? [];
                var masses = massAttributes.Select(Number).ToArray();
                // Only report an explicit body sum; missing/template masses are unknown, not zero.
                decimal? mass = masses.Length > 0 && masses.All(value => value.HasValue)
                    ? masses.Sum(value => value!.Value) : null;
                var contentPath = definition.Path ?? addonPath ?? "";
                var parts = contentPath.Split('/');
                var dlc = Array.FindIndex(parts, part => part.Equals("_dlc", StringComparison.OrdinalIgnoreCase));
                entries.Add(new CargoEntry(name, type, addonPath is null ? "—" : Path.GetFileNameWithoutExtension(addonPath),
                    dlc >= 0 && dlc + 1 < parts.Length ? parts[dlc + 1] : "Base game",
                    Number((string?)slot?.Attribute("CargoLength")), looseMass, mass, Number((string?)slot?.Attribute("CargoValue")),
                    description, definition.Path ?? "", addonPath ?? "", properties, targets));
            }
        }
        return new CargoCatalog(entries.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Type).ThenBy(entry => entry.Variant).ToArray(), warnings);

        IReadOnlyDictionary<string, XElement> ReadBodyTemplates(string file)
        {
            var templates = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Read(string source)
            {
                if (!visited.Add(source)) return;
                using var reader = XmlReader.Create(new StringReader(WorkspaceDataCache.ReadAllText(source)),
                    new XmlReaderSettings { ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit });
                while (!reader.EOF)
                {
                    if (reader.NodeType != XmlNodeType.Element) { reader.Read(); continue; }
                    var section = (XElement)XNode.ReadFrom(reader);
                    if (section.Name.LocalName != "_templates") continue;
                    foreach (var include in ((string?)section.Attribute("Include") ?? "").Split(
                                 new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        Read(WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[media]", "_templates", include + ".xml")));
                    foreach (var template in section.Element("Body")?.Elements() ?? [])
                    {
                        template.AddAnnotation(WorkspaceOriginals.GetRelativePath(workspace, source).Replace('\\', '/'));
                        templates[template.Name.LocalName] = template;
                    }
                }
            }
            Read(file);
            return templates;
        }
    }

    private static decimal? BodyMass(XElement definition, IReadOnlyDictionary<string, XElement> templates)
    {
        var masses = definition.Element("PhysicsModel")?.Descendants("Body")
            .Select(body => ResolveBodyMass(body, templates, new(StringComparer.OrdinalIgnoreCase))).ToArray() ?? [];
        return masses.Length > 0 && masses.All(mass => mass.HasValue && mass >= 0)
            ? masses.Sum(mass => mass!.Value) : null;
    }

    private static decimal? ResolveBodyMass(XElement body, IReadOnlyDictionary<string, XElement> templates, HashSet<string> seen)
    {
        if (body.Attribute("Mass") is { } mass) return Number(mass.Value);
        var name = (string?)body.Attribute("_template");
        return name is not null && seen.Add(name) && templates.TryGetValue(name, out var template)
            && !template.Descendants("Body").Any() ? ResolveBodyMass(template, templates, seen) : null;
    }

    private static string Resolve(string? key, string fallback, IReadOnlyDictionary<string, string> names) =>
        key is null ? fallback : names.TryGetValue(key, out var value) ? value : key;

    private static decimal? Number(string? value) =>
        decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static void CollectProperties(string source, XElement element, string parent, List<CargoProperty> result)
    {
        var siblings = element.Parent?.Elements(element.Name).ToArray();
        var suffix = siblings is { Length: > 1 } ? $"[{Array.IndexOf(siblings, element) + 1}]" : "";
        var path = parent + element.Name.LocalName + suffix;
        foreach (var attribute in element.Attributes())
            result.Add(new CargoProperty(source, path + "/@" + attribute.Name, attribute.Value));
        foreach (var child in element.Elements()) CollectProperties(source, child, path + "/", result);
    }
}
