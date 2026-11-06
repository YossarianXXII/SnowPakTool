using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace SnowPakTool.Desktop;

internal sealed record MapTruckEquipment(bool Parsed, IReadOnlyList<string> Addons, string WheelType, string Tire, string Rim,
    string Engine = "", string Gearbox = "", string Winch = "", string Suspension = "", float? WheelScale = null)
{
    public static readonly MapTruckEquipment Unknown = new(false, [], "", "", "");
}
internal sealed record InstalledAddonInfo(string Id, string Name, string Category, string SourcePath, bool IsVisual, string Origin)
{
    // User-defined UI grouping: "visual addons" means *_tuning folders,
    // including functional parts that the game stores in those folders.
    public bool IsFrameAddon => Category.Equals("frame_addons", StringComparison.OrdinalIgnoreCase);
    public string Group => IsVisual ? "Visual addons (_tuning)" : IsFrameAddon ? "Frame addons" : "Other addons";
}
internal sealed record TruckEquipmentSummary(string Tires, string TireDetails, string Addons, string AddonDetails,
    IReadOnlyList<InstalledAddonInfo>? InstalledAddons = null, string FrameAddons = "Unknown", string FrameAddonDetails = "",
    IReadOnlyList<InternalEquipmentInfo>? InternalEquipment = null);
internal sealed record InternalEquipmentInfo(string Slot, string Id, string Name, string Origin, string SourcePath, string DefinitionDetails);

/// <summary>Resolves per-placement equipment against the active workspace's vehicle defaults and wheel definitions.</summary>
internal sealed partial class TruckEquipmentCatalog
{
    private sealed record Definition(string Path, XElement Xml, string Parent);
    private sealed record Tire(string Identity, string Name, string Category);
    private sealed record AddonOption(string Name, string Category);
    private readonly object _gate = new();
    private readonly IReadOnlyDictionary<string, string> _names;
    private readonly string _workspace;
    private readonly List<string> _warnings;
    private readonly Dictionary<string, string> _files;
    private readonly HashSet<string> _filePaths;
    private readonly Dictionary<string, Definition[]> _chains = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, Tire>> _tires = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _sockets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _addonNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TruckEquipmentSummary> _defaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _comparisonProperties = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<AddonOption>>? _optionsBySocket;
    private readonly long _version;
    private readonly Dictionary<string, Definition?> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _tireUsers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(Definition Definition, XElement Variant)>> _components = new(StringComparer.OrdinalIgnoreCase);
    private bool _componentsIndexed;

    public TruckEquipmentCatalog(string workspace, IReadOnlyDictionary<string, string> names, List<string> warnings)
    {
        _names = names;
        _workspace = workspace;
        _version = WorkspaceDataCache.ContentVersion;
        _warnings = warnings;
        _files = WorkspaceOriginals.EnumerateCurrentFiles(workspace, "[media]", "*.xml")
            .Where(path => Regex.IsMatch(path.Replace('\\', '/'), @"/classes/(trucks|wheels|engines|gearboxes|winches|suspensions)/", RegexOptions.IgnoreCase))
            .GroupBy(path => Path.GetFileNameWithoutExtension(path)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        _filePaths = _files.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (truck, path) in _files.Where(pair => string.Equals(Path.GetFileName(Path.GetDirectoryName(pair.Value)), "trucks", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            var chain = Chain(path).ToArray();
            if (!chain.Any(definition => definition.Xml.Name.LocalName == "Truck")) continue;
            var types = chain.SelectMany(definition => definition.Xml.Descendants("CompatibleWheels"))
                .Select(element => (string?)element.Attribute("Type") ?? "")
                .Append(DefaultWheel(chain, "DefaultWheelType")).Where(type => type.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var type in types)
                foreach (var tire in Tires(type).Values)
                {
                    if (!_tireUsers.TryGetValue(tire.Identity, out var users)) _tireUsers[tire.Identity] = users = new(StringComparer.OrdinalIgnoreCase);
                    users.Add(truck);
                }
        }
    }

    public TruckEquipmentSummary Resolve(string truck, MapTruckEquipment equipment)
    {
        lock (_gate) return ResolveCore(truck, equipment);
    }

    public bool IsCurrent(string workspace) => _version == WorkspaceDataCache.ContentVersion
        && Path.GetFullPath(_workspace).Equals(Path.GetFullPath(workspace), StringComparison.OrdinalIgnoreCase);

    public TruckEquipmentSummary ResolveDefault(string truck)
    {
        lock (_gate)
        {
            if (!_defaults.TryGetValue(truck, out var summary))
                _defaults[truck] = summary = ResolveCore(truck, new(true, [], "", "", ""));
            return summary;
        }
    }

    public TruckEquipmentSummary ResolveConfigured(string truck, MapTruckEquipment equipment)
    {
        lock (_gate) return ResolveCore(truck, equipment, includeDefaults: false);
    }

    private TruckEquipmentSummary ResolveCore(string truck, MapTruckEquipment equipment, bool includeDefaults = true)
    {
        if (!equipment.Parsed) return new("Unknown", "The map equipment record could not be read; vehicle defaults were not assumed.",
            "Unknown", "The map equipment record could not be read.");
        var chain = FileChain(truck).ToArray();
        var wheelType = equipment.WheelType.Length > 0 ? equipment.WheelType : DefaultWheel(chain, "DefaultWheelType");
        var tireId = equipment.Tire.Length > 0 ? equipment.Tire : DefaultWheel(chain, "DefaultTire");
        var rim = equipment.Rim.Length > 0 ? equipment.Rim : DefaultWheel(chain, "DefaultRim");
        Tires(wheelType).TryGetValue(tireId, out var tire);
        var custom = tire is not null && _tireUsers.TryGetValue(tire.Identity, out var users) && users.Count == 1 && users.Contains(truck);
        var inferredCategory = tire is null ? Category(tireId.Replace("allterain", "allterrain", StringComparison.OrdinalIgnoreCase)) : null;
        var category = custom ? $"Custom ({tire!.Category})" : tire?.Category ?? inferredCategory ?? "Unknown";
        var tireDetails = $"{tire?.Name ?? tireId}\r\nWheel family: {wheelType}\r\nTire ID: {tireId}\r\nRim: {rim}"
            + $"\r\nWheel scale: {(equipment.WheelScale is > 0 ? equipment.WheelScale.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " [map override]" : "vehicle default")}"
            + $"\r\nWheel family: {(equipment.WheelType.Length > 0 ? "map override" : "vehicle default")}; tire: {(equipment.Tire.Length > 0 ? "map override" : "vehicle default")}."
            + (custom ? $"\r\nCustom: this tire definition is exclusive to this truck in the current workspace. Underlying category: {tire!.Category}." : "")
            + (tire is null ? "\r\nThe stored tire ID has no matching definition in this wheel family. "
                + (inferredCategory is not null ? "Category inferred from the stored ID; the exact variant could not be validated." : "The installed category could not be resolved.") : "");

        var installed = equipment.Addons.Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mapAddons = installed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var occupied = installed.SelectMany(SocketTypes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var addon in includeDefaults ? DefaultAddons(chain) : [])
        {
            if (installed.Contains(addon, StringComparer.OrdinalIgnoreCase) || SocketTypes(addon).Any(occupied.Contains)) continue;
            if (installed.All(id => SocketTypes(id).Any()) && !CanInstallAddons(truck, installed.Append(addon).ToArray())) continue;
            installed.Add(addon);
        }
        var addonLabels = installed.Select(id => (Id: id, Name: AddonName(id))).ToArray();
        var addonInfo = addonLabels.Select(item =>
        {
            var definitions = FileChain(item.Id).ToArray();
            var path = definitions.FirstOrDefault()?.Path;
            var source = path is null ? "" : WorkspaceOriginals.GetRelativePath(_workspace, path);
            var visual = source.Replace('\\', '/').Split('/').Any(part => part.EndsWith("_tuning", StringComparison.OrdinalIgnoreCase));
            var gameCategory = definitions.Select(definition => (string?)definition.Xml.Element("GameData")?.Attribute("Category"))
                .FirstOrDefault(value => value is not null) ?? "";
            return new InstalledAddonInfo(item.Id, item.Name, gameCategory, source, visual, mapAddons.Contains(item.Id) ? "map" : "vehicle default");
        }).ToArray();
        // Hide the user-defined visual group first, including tuning-folder
        // parts with functional categories. Separate the remaining frame gear.
        var visible = addonInfo.Where(item => !item.IsVisual).ToArray();
        var frame = visible.Where(item => item.IsFrameAddon).ToArray();
        var other = visible.Where(item => !item.IsFrameAddon).ToArray();
        var internalEquipment = new List<InternalEquipmentInfo>
        {
            ResolveComponent(chain, "Engine", "EngineSocket", equipment.Engine),
            ResolveComponent(chain, "Gearbox", "GearboxSocket", equipment.Gearbox),
            ResolveComponent(chain, "Winch", "WinchUpgradeSocket", equipment.Winch),
            ResolveComponent(chain, "SuspensionSet", "SuspensionSocket", equipment.Suspension)
        };
        var drivetrain = chain.Select(definition => (Definition: definition, Data: definition.Xml.Element("TruckData")))
            .FirstOrDefault(item => item.Data?.Attribute("DiffLockType") is not null);
        if (drivetrain.Data?.Attribute("DiffLockType") is { } mode)
            internalEquipment.Add(new("Drivetrain base mode", mode.Value, mode.Value, "vehicle definition",
                WorkspaceOriginals.GetRelativePath(_workspace, drivetrain.Definition.Path), "DiffLockType=" + mode.Value));
        if (chain.FirstOrDefault() is { } vehicleDefinition)
        {
            var properties = chain.SelectMany(definition => new[] { "TruckData", "GameData" }
                    .SelectMany(section => (definition.Xml.Element(section)?.Attributes() ?? [])
                        .Where(attribute => attribute.Name.LocalName != "TruckType")
                        .Select(attribute => (Key: section + "." + attribute.Name.LocalName, attribute.Value))))
                .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
            internalEquipment.Add(new("Vehicle definition", truck, AddonName(truck), "vehicle definition",
                WorkspaceOriginals.GetRelativePath(_workspace, vehicleDefinition.Path),
                "Definition properties, not current save-state values (templates may add further values):\r\n"
                    + string.Join("\r\n", properties.Select(item => item.Key + "=" + item.Value))));
        }
        foreach (var addon in addonInfo.Where(item => item.Category is "awd" or "diff_lock" or "snorkel"))
        {
            var data = FileChain(addon.Id).Select(item => item.Xml.Element("TruckData")).FirstOrDefault(element => element is not null);
            var settings = data is null ? "No explicit TruckData settings in this addon definition."
                : string.Join("\r\n", data.Attributes().Select(attribute => attribute.Name.LocalName + "=" + attribute.Value));
            internalEquipment.Add(new(addon.Category switch { "awd" => "AWD / transfer box", "diff_lock" => "Differential lock", _ => "Snorkel" },
                addon.Id, addon.Name, addon.Origin, addon.SourcePath, settings));
        }
        return new(category, tireDetails, FormatNames(other), FormatDetails(other), addonInfo,
            FormatNames(frame), FormatDetails(frame), internalEquipment);
    }

    public IReadOnlyDictionary<string, string> ComparisonProperties(string truck)
    {
        lock (_gate)
        {
            if (!_comparisonProperties.TryGetValue(truck, out var properties))
                _comparisonProperties[truck] = properties = BuildComparisonProperties(truck);
            return properties;
        }
    }

    public bool TryGetComparisonProperties(string truck, out IReadOnlyDictionary<string, string> properties)
    {
        lock (_gate) return _comparisonProperties.TryGetValue(truck, out properties!);
    }

    private IReadOnlyDictionary<string, string> BuildComparisonProperties(string truck)
    {
        var chain = FileChain(truck).ToArray();
        var properties = chain.SelectMany(definition => new[] { "TruckData", "GameData" }
                .SelectMany(section => (definition.Xml.Element(section)?.Attributes() ?? [])
                    .Where(attribute => attribute.Name.LocalName != "TruckType")
                    .Select(attribute => (Key: section + " / " + attribute.Name.LocalName, attribute.Value))))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        var wheels = chain.SelectMany(definition => definition.Xml.Descendants("CompatibleWheels"))
            .Select(element => new { Type = (string?)element.Attribute("Type") ?? "", Scale = (string?)element.Attribute("Scale") ?? "" })
            .Distinct().ToArray();
        properties["Wheels / Compatible families and scale"] = string.Join("\r\n", wheels.Select(wheel => wheel.Type + (wheel.Scale.Length > 0 ? " (scale " + wheel.Scale + ")" : "")));
        foreach (var type in wheels.Select(wheel => wheel.Type).Distinct(StringComparer.OrdinalIgnoreCase))
            properties["Wheels / " + type + " tires"] = string.Join("\r\n", Tires(type).Values
                .Select(tire => tire.Name + " (" + tire.Category + ")").Distinct().Order(StringComparer.CurrentCultureIgnoreCase));
        var sockets = chain.SelectMany(definition => definition.Xml.Element("GameData")?.Elements("AddonSockets") ?? [])
            .SelectMany(element => element.Elements("Socket")).SelectMany(socket => Split((string?)socket.Attribute("Names")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IndexAddonOptions();
        var supported = sockets.Where(socket => _optionsBySocket!.ContainsKey(socket)).ToArray();
        properties["Addons / Supported sockets"] = string.Join(", ", supported.Order(StringComparer.CurrentCultureIgnoreCase));
        foreach (var group in supported.SelectMany(socket => _optionsBySocket![socket]).Distinct().GroupBy(option => option.Category))
            foreach (var name in group.Select(option => option.Name).Distinct().Order(StringComparer.CurrentCultureIgnoreCase))
                properties["Addon socket options / " + group.Key.Replace('_', ' ') + " / " + name] = "Supported socket match";
        return properties;
    }

    private void IndexAddonOptions()
    {
        if (_optionsBySocket is not null) return;
        _optionsBySocket = new(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _files.Keys)
        {
            var chain = FileChain(id).ToArray();
            if ((chain.FirstOrDefault()?.Path ?? "").Replace('\\', '/').Split('/').Any(part => part.EndsWith("_tuning", StringComparison.OrdinalIgnoreCase))) continue;
            var sockets = SocketTypes(id).ToArray();
            if (sockets.Length == 0) continue;
            var category = chain.Select(definition => (string?)definition.Xml.Element("GameData")?.Attribute("Category"))
                .FirstOrDefault(value => value is not null) ?? "Uncategorized";
            var option = new AddonOption(AddonName(id), category);
            foreach (var socket in sockets)
            {
                if (!_optionsBySocket.TryGetValue(socket, out var options)) _optionsBySocket[socket] = options = [];
                options.Add(option);
            }
        }
    }

    private InternalEquipmentInfo ResolveComponent(IReadOnlyList<Definition> truck, string kind, string socket, string mapId)
    {
        var sockets = truck.Select(definition => definition.Xml.Element("TruckData")?.Element(socket)).Where(element => element is not null).ToArray();
        var id = mapId.Length > 0 ? mapId : sockets.Select(element => (string?)element!.Attribute("Default")).FirstOrDefault(value => value is not null) ?? "";
        var origin = mapId.Length > 0 ? "map override" : "vehicle default";
        var label = kind == "SuspensionSet" ? "Suspension" : kind;
        if (id.Length == 0) return new(label, "", "Not specified", origin, "", "No component ID was specified in the map or vehicle defaults.");
        IndexComponents();
        var types = sockets.SelectMany(element => Split((string?)element!.Attribute("Type"))).ToArray();
        (Definition Definition, XElement Variant)? found = null;
        foreach (var type in types)
        {
            foreach (var definition in FileChain(type))
            {
                var variant = definition.Xml.Elements(kind).FirstOrDefault(element => string.Equals((string?)element.Attribute("Name"), id, StringComparison.OrdinalIgnoreCase));
                if (variant is not null) { found = (definition, variant); break; }
            }
            if (found.HasValue) break;
        }
        if (found is null && _components.TryGetValue(kind + "|" + id, out var candidates)) found = candidates[0];
        if (found is not { } result) return new(label, id, id, origin, "", "The selected component ID has no matching definition in the active workspace.");
        var key = (string?)result.Variant.Element("GameData")?.Element("UiDesc")?.Attribute("UiName") ?? "";
        var name = _names.GetValueOrDefault(key, id);
        var settings = result.Variant.DescendantsAndSelf().SelectMany(element => element.Attributes()
            .Where(attribute => attribute.Name.LocalName is not ("Name" or "UiName" or "UiDesc") && !attribute.Name.LocalName.StartsWith("UiIcon", StringComparison.Ordinal))
            .Select(attribute => element.Name.LocalName
                + (element.Parent?.Elements(element.Name).Skip(1).Any() == true ? $"[{element.ElementsBeforeSelf(element.Name).Count() + 1}]" : "")
                + "." + attribute.Name.LocalName + "=" + attribute.Value));
        return new(label, id, name, origin, WorkspaceOriginals.GetRelativePath(_workspace, result.Definition.Path),
            "Explicit definition settings (template defaults may add further values):\r\n" + string.Join("\r\n", settings));
    }

    private void IndexComponents()
    {
        if (_componentsIndexed) return;
        _componentsIndexed = true;
        foreach (var path in _files.Values.Where(path => Regex.IsMatch(path.Replace('\\', '/'), @"/classes/(engines|gearboxes|winches|suspensions)/", RegexOptions.IgnoreCase)))
        {
            var definition = Read(path);
            if (definition is null) continue;
            foreach (var variant in definition.Xml.Elements().Where(element => element.Name.LocalName is "Engine" or "Gearbox" or "Winch" or "SuspensionSet"))
            {
                if ((string?)variant.Attribute("Name") is not { Length: > 0 } id) continue;
                var key = variant.Name.LocalName + "|" + id;
                if (!_components.TryGetValue(key, out var items)) _components[key] = items = [];
                items.Add((definition, variant));
            }
        }
    }

    private static string FormatNames(IReadOnlyList<InstalledAddonInfo> items)
    {
        if (items.Count == 0) return "None";
        var duplicateNames = items.GroupBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        return string.Join(", ", items.Select(item => duplicateNames.Contains(item.Name) ? $"{item.Name} ({item.Id})" : item.Name));
    }

    private static string FormatDetails(IReadOnlyList<InstalledAddonInfo> items) => items.Count == 0
        ? "No installed addons in this group after excluding visual (_tuning) parts."
        : string.Join("\r\n", items.Select(item => $"{item.Name} — {item.Id} [{item.Origin}; {item.Group}; category: {item.Category}]\r\n  {item.SourcePath}"));

    private static string DefaultWheel(IEnumerable<Definition> chain, string attribute) => chain
        .Select(definition => (string?)definition.Xml.Element("TruckData")?.Element("Wheels")?.Attribute(attribute))
        .FirstOrDefault(value => value is not null) ?? "";

    private static IEnumerable<string> DefaultAddons(IReadOnlyList<Definition> chain)
    {
        var sockets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in chain.Reverse())
            foreach (var addonSockets in definition.Xml.Element("GameData")?.Elements("AddonSockets") ?? [])
            {
                if (addonSockets.Attribute("DefaultAddon") is not { } addon) continue;
                var types = addonSockets.Elements("Socket").SelectMany(socket => Split((string?)socket.Attribute("Names"))).Order().ToArray();
                sockets[types.Length == 0 ? addon.Value : string.Join(",", types)] = addon.Value;
            }
        return sockets.Values.Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private string AddonName(string id)
    {
        if (_addonNames.TryGetValue(id, out var name)) return name;
        var key = FileChain(id).Select(definition => (string?)definition.Xml.Element("GameData")?.Element("UiDesc")?.Attribute("UiName"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return _addonNames[id] = key is not null ? _names.GetValueOrDefault(key, id) : id;
    }

    private IEnumerable<string> SocketTypes(string id)
    {
        if (!_sockets.TryGetValue(id, out var sockets))
            _sockets[id] = sockets = FileChain(id)
                .SelectMany(definition => definition.Xml.Element("GameData")?.Elements("InstallSocket") ?? [])
                .SelectMany(socket => Split((string?)socket.Attribute("Type"))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return sockets;
    }

    private Dictionary<string, Tire> Tires(string type)
    {
        if (_tires.TryGetValue(type, out var cached)) return cached;
        var tires = new Dictionary<string, Tire>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in FileChain(type).Reverse())
            foreach (var element in definition.Xml.Element("TruckTires")?.Elements("TruckTire") ?? [])
            {
                var id = (string?)element.Attribute("Name");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var ui = element.Element("GameData")?.Element("UiDesc");
                var key = (string?)ui?.Attribute("UiName") ?? "";
                var description = (string?)ui?.Attribute("UiDesc") ?? "";
                var friction = (string?)element.Element("WheelFriction")?.Attribute("_template") ?? "";
                var template = (string?)element.Attribute("_template") ?? "";
                // UI category descriptions are more reliable than template names reused by unique tires.
                var category = Category(description) ?? Category(friction) ?? Category(template) ?? Category(id) ?? "Unknown";
                tires[id] = new(definition.Path + "|" + id, _names.GetValueOrDefault(key, id), category);
            }
        return _tires[type] = tires;
    }

    private static string? Category(string value)
    {
        value = value.Replace("_", "").Replace("-", "").ToLowerInvariant();
        if (value.Contains("chain") || value == "ice") return "Ice";
        if (value.Contains("mudtire")) return "Mudtires";
        if (value.Contains("allterrain")) return "All-terrain";
        if (value.Contains("offroad")) return "Offroad";
        if (value.Contains("highway")) return "Highway";
        return null;
    }

    private IEnumerable<Definition> FileChain(string code) => _files.TryGetValue(code, out var path) ? Chain(path) : [];

    private Definition[] Chain(string path)
    {
        if (_chains.TryGetValue(path, out var cached)) return cached;
        var start = path;
        var result = new List<Definition>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (visited.Count < 32 && visited.Add(path))
        {
            var definition = Read(path);
            if (definition is null) break;
            result.Add(definition);
            if (definition.Parent.Length == 0) break;
            var sibling = Path.Combine(Path.GetDirectoryName(path)!, definition.Parent + ".xml");
            if (_filePaths.Contains(sibling)) path = sibling;
            else if (!_files.TryGetValue(definition.Parent, out path!)) break;
        }
        return _chains[start] = result.ToArray();
    }

    private Definition? Read(string path)
    {
        if (_definitions.TryGetValue(path, out var cached)) return cached;
        try
        {
            var text = WorkspaceDataCache.ReadAllText(path);
            // Game XML uses fragments and undeclared region-prefixed tags.
            text = Regex.Replace(text, @"<\?xml[\s\S]*?\?>", "");
            text = Regex.Replace(text, @"<(/?)([A-Za-z_][\w.-]*):", "<$1$2_");
            var root = XElement.Parse("<Root>" + text + "</Root>");
            var element = root.Elements().FirstOrDefault(element => element.Name.LocalName is "Truck" or "TruckAddon" or "TruckWheels"
                or "EngineVariants" or "GearboxVariants" or "WinchVariants" or "SuspensionSetVariants");
            var definition = element is null ? null : new Definition(path, element, (string?)root.Element("_parent")?.Attribute("File") ?? "");
            _definitions[path] = definition;
            return definition;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            _warnings.Add($"Equipment definition {Path.GetFileName(path)}: {ex.Message}");
            _definitions[path] = null;
            return null;
        }
    }

    private static IEnumerable<string> Split(string? value) => (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
