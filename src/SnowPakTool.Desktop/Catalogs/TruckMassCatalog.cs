using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SnowPakTool.Desktop;

internal sealed partial class TruckEquipmentCatalog
{
    private readonly Dictionary<string, double?> _vehicleMasses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XElement?> _massDocuments = new(StringComparer.OrdinalIgnoreCase);

    // Bare vehicle physics bodies, including mirrors and other template bodies; no cargo or frame addons.
    public double? VehicleMassKg(string truck)
    {
        lock (_gate)
        {
            if (_vehicleMasses.TryGetValue(truck, out var known)) return known;
            var masses = new Dictionary<string, double?>(StringComparer.Ordinal);
            foreach (var definition in FileChain(truck).Reverse())
            {
                var physics = definition.Xml.Element("PhysicsModel");
                if (physics is null) continue;
                if ((string?)physics.Attribute("_noinherit") == "true") masses.Clear();
                var templates = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void AddTemplates(string path)
                {
                    if (!visited.Add(path) || visited.Count > 32) return;
                    var document = MassDocument(path);
                    var section = document?.Element("_templates");
                    if (section is null) return;
                    foreach (var include in Split((string?)section.Attribute("Include")))
                        AddTemplates(WorkspaceOriginals.GetCurrentPath(_workspace, Path.Combine("[media]", "_templates", include + ".xml")));
                    foreach (var template in section.Element("Body")?.Elements() ?? []) templates[template.Name.LocalName] = template;
                }
                AddTemplates(definition.Path);
                double? Mass(XElement body, HashSet<string> seen)
                {
                    if (body.Attribute("Mass") is { } attribute)
                        return double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mass)
                            && double.IsFinite(mass) && mass >= 0 ? mass : null;
                    var template = (string?)body.Attribute("_template");
                    return template is not null && seen.Add(template) && templates.TryGetValue(template, out var source) ? Mass(source, seen) : null;
                }
                foreach (var body in physics.Descendants("Body"))
                {
                    var key = string.Join("/", body.AncestorsAndSelf().TakeWhile(element => element != physics).Reverse()
                        .Where(element => element.Name.LocalName == "Body")
                        .Select(element => (string?)element.Attribute("ModelFrame") ?? "#" + element.ElementsBeforeSelf("Body").Count()));
                    if ((string?)body.Attribute("_noinherit") == "true")
                        foreach (var previous in masses.Keys.Where(previous => previous == key || previous.StartsWith(key + "/", StringComparison.Ordinal)).ToArray()) masses.Remove(previous);
                    var value = Mass(body, new(StringComparer.OrdinalIgnoreCase));
                    if (value.HasValue || !masses.ContainsKey(key) || body.Attribute("Mass") is not null || body.Attribute("_template") is not null) masses[key] = value;
                }
            }
            var total = masses.Count > 0 && masses.Values.All(value => value.HasValue) ? masses.Values.Sum(value => value!.Value) : 0;
            return _vehicleMasses[truck] = total > 0 && double.IsFinite(total) ? total : null;
        }
    }

    private XElement? MassDocument(string path)
    {
        if (_massDocuments.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path)) return _massDocuments[path] = null;
        try
        {
            var text = Regex.Replace(WorkspaceDataCache.ReadAllText(path), @"<\?xml[\s\S]*?\?>", "");
            text = Regex.Replace(text, @"<(/?)([A-Za-z_][\w.-]*):", "<$1$2_");
            return _massDocuments[path] = XElement.Parse("<Root>" + text + "</Root>");
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        { return _massDocuments[path] = null; }
    }
}
