using System.Globalization;
using System.Xml.Linq;

namespace SnowPakTool.Desktop;

internal sealed partial class TruckEquipmentCatalog
{
    private sealed record RandomizationSocket(int Group, string Type, HashSet<string> Blocks);
    private sealed record RandomizationTire(string WheelType, string Tire, string Rim, float Scale, int Category, int Order, float Size);
    private readonly Dictionary<string, RandomizationSocket[]> _randomSockets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RandomizationTire[]> _randomTires = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string[]>? _randomAddons;
    private static bool MapIdentifier(string value) => value is not null && value.Length <= 255 && value.All(character => character is >= ' ' and <= '~');

    private string? RequiredAddonKind(string id)
    {
        var key = string.Join(" ", FileChain(id).Select(definition =>
            (string?)definition.Xml.Element("GameData")?.Element("UiDesc")?.Attribute("UiName"))) + " " + id + " " + AddonName(id);
        key = new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (key.Contains("sideboard") && !key.Contains("trailer"))
        {
            var capacity = FileChain(id).Select(definition => (string?)definition.Xml.Element("GameData")?.Element("AddonSlots")?.Attribute("Quantity"))
                .FirstOrDefault(value => value is not null) ?? "unspecified";
            return "Sideboard Bed/" + capacity;
        }
        if (key.Contains("saddlehigh")) return "Saddle High";
        if (key.Contains("saddlelow")) return "Saddle Low";
        if (key.Contains("bigcraneru") || key.Contains("avto23")) return "Avto-23 Heavy Crane";
        if (key.Contains("kung") || key.Contains("vanbody")) return "Van Body Addon";
        if (key.Contains("bunklogaddon") || key.Contains("logcarrierfront")) return "Log Carrier Front";
        if (key.Contains("shortlogging") || key.Contains("logshort") || key.Contains("shortlog")) return "Short logging frame";
        if (key.Contains("seismic")) return "Seismic Vibrator Module";
        if (key.Contains("metaldetector")) return "Metal Detector Module Frame";
        return null;
    }

    private RandomizationSocket[] RandomizationSockets(string truck)
    {
        if (_randomSockets.TryGetValue(truck, out var cached)) return cached;
        var groups = new Dictionary<string, XElement[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in FileChain(truck).Reverse())
            foreach (var group in definition.Xml.Element("GameData")?.Elements("AddonSockets") ?? [])
            {
                var sockets = group.Elements("Socket").ToArray();
                var key = string.Join(",", sockets.SelectMany(socket => Split((string?)socket.Attribute("Names"))).Order(StringComparer.OrdinalIgnoreCase));
                if (key.Length > 0) groups[key] = sockets;
            }
        return _randomSockets[truck] = groups.Values.SelectMany((group, index) => group.SelectMany(socket =>
            Split((string?)socket.Attribute("Names")).Select(type => new RandomizationSocket(index, type,
                Split((string?)socket.Attribute("NamesBlock")).ToHashSet(StringComparer.OrdinalIgnoreCase))))).ToArray();
    }

    private bool CanInstallAddons(string truck, IReadOnlyList<string> addons)
    {
        var sockets = RandomizationSockets(truck);
        var options = addons.Select(id => (Id: id, Slots: sockets.Where(socket => SocketTypes(id).Contains(socket.Type, StringComparer.OrdinalIgnoreCase)).ToArray()))
            .OrderBy(item => item.Slots.Length).ToArray();
        if (options.Any(item => item.Slots.Length == 0)) return false;
        var chosen = new List<RandomizationSocket>();
        var attempts = 0;
        bool Place(int index)
        {
            if (++attempts > 4096) return false;
            if (index == options.Length) return true;
            foreach (var slot in options[index].Slots)
            {
                if (chosen.Any(other => other.Group == slot.Group || other.Blocks.Contains(slot.Type) || slot.Blocks.Contains(other.Type))) continue;
                chosen.Add(slot);
                if (Place(index + 1)) return true;
                chosen.RemoveAt(chosen.Count - 1);
            }
            return false;
        }
        if (!Place(0)) return false;
        var installed = addons.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installedTypes = addons.SelectMany(SocketTypes).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in addons)
            foreach (var definition in FileChain(id))
                foreach (var condition in definition.Xml.Element("GameData")?.Elements()
                             .Where(element => element.Name.LocalName is "RequiredAddon" or "ExcludedAddon") ?? [])
                {
                    var tokens = condition.Attributes().SelectMany(attribute => Split(attribute.Value)).ToArray();
                    if (tokens.Length == 0) return false;
                    var found = tokens.Any(token => installed.Contains(token) || installedTypes.Contains(token));
                    if (condition.Name.LocalName == "RequiredAddon" ? !found : found) return false;
                }
        return true;
    }

    private static int TireCategory(string category) => category switch
    {
        "Highway" => 0, "All-terrain" => 1, "Offroad" => 2, "Mudtires" => 3, _ => -1
    };

    private RandomizationTire[] RandomizationTires(string truck)
    {
        if (_randomTires.TryGetValue(truck, out var cached)) return cached;
        var chain = FileChain(truck).ToArray();
        var result = new List<RandomizationTire>();
        var defaultRim = DefaultWheel(chain, "DefaultRim");
        var defaultSuspension = chain.Select(definition => (string?)definition.Xml.Element("TruckData")?.Element("SuspensionSocket")?.Attribute("Default"))
            .FirstOrDefault(value => value is not null) ?? "";
        foreach (var wheel in chain.SelectMany(definition => definition.Xml.Descendants("CompatibleWheels")))
        {
            var type = (string?)wheel.Attribute("Type") ?? "";
            if (!float.TryParse((string?)wheel.Attribute("Scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) || scale <= 0 || scale > 10) continue;
            var suspension = (string?)wheel.Attribute("Suspension");
            if (!string.IsNullOrEmpty(suspension) && !Split(suspension).Contains(defaultSuspension, StringComparer.OrdinalIgnoreCase)) continue;
            var rims = FileChain(type).SelectMany(definition => definition.Xml.Element("TruckRims")?.Elements("TruckRim") ?? [])
                .Select(rim => (string?)rim.Attribute("Name") ?? "").Where(id => id.Length > 0).ToArray();
            var rim = rims.Length == 0 || rims.Contains(defaultRim, StringComparer.OrdinalIgnoreCase) ? defaultRim : rims[0];
            var radiusText = FileChain(type).Select(definition => (string?)definition.Xml.Attribute("Radius")).FirstOrDefault(value => value is not null);
            var radius = float.TryParse(radiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRadius) && parsedRadius > 0 ? parsedRadius : 1f;
            var order = 0;
            foreach (var (id, tire) in Tires(type))
            {
                var category = TireCategory(tire.Category);
                if (category >= 0) result.Add(new(type, id, rim, scale, category, order, scale * radius));
                order++;
            }
        }
        return _randomTires[truck] = result.ToArray();
    }

    public bool TryRandomizationConfiguration(RegionTruckEntry original, string truck, int requestedTires,
        out MapTruckEquipment configuration, out bool tireFallback)
        => TryRandomizationConfigurationCore(original, truck, requestedTires, out configuration, out tireFallback);

    public bool TryMissionConfiguration(string truck, int requestedTires, out MapTruckEquipment configuration, out bool tireFallback)
        => TryRandomizationConfigurationCore(null, truck, requestedTires, out configuration, out tireFallback);

    private bool TryRandomizationConfigurationCore(RegionTruckEntry? original, string truck, int requestedTires,
        out MapTruckEquipment configuration, out bool tireFallback)
    {
        lock (_gate)
        {
            configuration = MapTruckEquipment.Unknown; tireFallback = false;
            var baseline = original?.OriginalEquipment ?? original?.RawEquipment;
            if (original is not null && baseline is not { Parsed: true }) return false;
            var required = original is null ? [] : ResolveCore(original.BaselineTruckCode, baseline!).InstalledAddons!
                .Select(addon => (addon.Id, Kind: RequiredAddonKind(addon.Id))).Where(item => item.Kind is not null)
                .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase).Select(item => (Kind: item.Kind!, Original: item.Id)).ToArray();
            _randomAddons ??= _files.Keys.Where(id => FileChain(id).FirstOrDefault()?.Xml.Name.LocalName == "TruckAddon")
                .Select(id => (Id: id, Kind: RequiredAddonKind(id))).Where(item => item.Kind is not null)
                .GroupBy(item => item.Kind!).ToDictionary(group => group.Key, group => group.Select(item => item.Id).Order(StringComparer.OrdinalIgnoreCase).ToArray());
            var selected = new List<string>();
            var attempts = 0;
            bool Select(int index)
            {
                if (++attempts > 1024) return false;
                if (index == required.Length) return CanInstallAddons(truck, selected);
                if (!_randomAddons.TryGetValue(required[index].Kind, out var options)) return false;
                foreach (var id in options.OrderBy(id => id.Equals(required[index].Original, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                {
                    if (selected.Contains(id, StringComparer.OrdinalIgnoreCase)) continue;
                    if (!SocketTypes(id).Any(type => RandomizationSockets(truck).Any(socket => socket.Type.Equals(type, StringComparison.OrdinalIgnoreCase)))) continue;
                    selected.Add(id);
                    if (Select(index + 1)) return true;
                    selected.RemoveAt(selected.Count - 1);
                }
                return false;
            }
            if (!Select(0)) return false;
            foreach (var addon in DefaultAddons(FileChain(truck).ToArray()))
                if (!selected.Contains(addon, StringComparer.OrdinalIgnoreCase) && CanInstallAddons(truck, selected.Append(addon).ToArray())) selected.Add(addon);
            var tires = RandomizationTires(truck).OrderBy(tire => Math.Abs(tire.Category - requestedTires))
                .ThenBy(tire => tire.Size).ThenBy(tire => tire.Order).ThenBy(tire => tire.WheelType, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (tires is null || selected.Count > 128 || !MapIdentifier(truck) || selected.Any(id => !MapIdentifier(id))
                || !MapIdentifier(tires.WheelType) || !MapIdentifier(tires.Tire) || !MapIdentifier(tires.Rim)) return false;
            configuration = new(true, selected, tires.WheelType, tires.Tire, tires.Rim, WheelScale: tires.Scale);
            tireFallback = tires.Category != requestedTires;
            return true;
        }
    }

    public bool IsConfigurationSupported(string truck, MapTruckEquipment configuration)
    {
        lock (_gate) return MapIdentifier(truck) && truck.Length > 0 && configuration.Parsed && configuration.Addons is not null
            && configuration.Addons.Count <= 128 && configuration.Addons.All(MapIdentifier)
            && configuration.Engine == "" && configuration.Gearbox == "" && configuration.Winch == "" && configuration.Suspension == ""
            && RandomizationTires(truck).Any(tire => tire.WheelType == configuration.WheelType && tire.Tire == configuration.Tire
                && tire.Rim == configuration.Rim && tire.Scale == configuration.WheelScale)
            && CanInstallAddons(truck, configuration.Addons);
    }

    public string ConfiguredTireCategory(MapTruckEquipment configuration)
    {
        lock (_gate) return Tires(configuration.WheelType).TryGetValue(configuration.Tire, out var tire) ? tire.Category : "Unknown";
    }
}
