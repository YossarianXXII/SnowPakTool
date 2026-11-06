using System.Globalization;
using System.Xml.Linq;

namespace SnowPakTool.Desktop;

internal sealed record UpgradeTrialPart(UpgradeDistributionDefinition Definition, string Name, string Source,
    string Element = "", string Variant = "");

internal sealed partial class TruckEquipmentCatalog
{
    public IReadOnlyList<string> UpgradeCatalogWarnings() { lock (_gate) return _warnings.Distinct().ToArray(); }
    private sealed class UpgradeBuilder(string id, UpgradeKind kind, string name, string source, bool allowed, string element, string variant)
    {
        public readonly string Id = id, Name = name, Source = source;
        public readonly string Element = element, Variant = variant;
        public readonly UpgradeKind Kind = kind;
        public bool Allowed = allowed;
        public readonly HashSet<string> Trucks = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Defaults = new(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, UpgradeTrialPart> BuildUpgradeSelectionCatalog(
        IReadOnlySet<string> enabledTrucks, IReadOnlySet<string> ownedDlc, IReadOnlySet<string> originalRewardIds)
    {
        lock (_gate)
        {
            var parts = new Dictionary<string, UpgradeBuilder>(StringComparer.OrdinalIgnoreCase);
            var tireLicenses = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var library = WorkspaceOriginals.GetCurrentPath(_workspace, Path.Combine("[ps]", "dlc_library.sso"));
            if (File.Exists(library))
            {
                var root = SsoDataReader.Parse(WorkspaceDataCache.ReadAllText(library));
                if (root.Members.TryGetValue("dlcInfos", out var infos))
                    foreach (var (code, info) in infos.Members)
                        if (info.Members.TryGetValue("tires", out var tires))
                            foreach (var tire in tires.Items)
                            {
                                if (!tireLicenses.TryGetValue(tire.Text, out var licenses)) tireLicenses[tire.Text] = licenses = new(StringComparer.OrdinalIgnoreCase);
                                licenses.Add(code);
                            }
            }
            bool Allowed(XElement? game) => game is not null
                && !string.Equals((string?)game.Attribute("LockedUnconditionally"), "true", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty((string?)game.Attribute("UnlockByObjective"))
                && !string.Equals((string?)game.Attribute("AddonUnlockByObjective"), "true", StringComparison.OrdinalIgnoreCase)
                && decimal.TryParse((string?)game.Attribute("Price"), NumberStyles.Float, CultureInfo.InvariantCulture, out var price) && price > 0;
            void Add(string id, UpgradeKind kind, string truck, Definition definition, XElement element, bool isDefault, bool licensed = true)
            {
                var game = element.Element("GameData");
                var key = (string?)game?.Element("UiDesc")?.Attribute("UiName") ?? id;
                if (!parts.TryGetValue(id, out var part))
                    parts[id] = part = new(id, kind, _names.GetValueOrDefault(key, key), WorkspaceOriginals.GetRelativePath(_workspace, definition.Path), Allowed(game) && licensed,
                        element.Name.LocalName, (string?)element.Attribute("Name") ?? "");
                if (part.Kind != kind) throw new InvalidDataException($"Upgrade ID '{id}' is ambiguous across component types.");
                part.Allowed &= Allowed(game) && licensed;
                part.Trucks.Add(truck);
                if (isDefault) part.Defaults.Add(truck);
            }
            var trucks = _files.Where(pair => string.Equals(Path.GetFileName(Path.GetDirectoryName(pair.Value)), "trucks", StringComparison.OrdinalIgnoreCase))
                .Where(pair => FileChain(pair.Key).Any(d => d.Xml.Name.LocalName == "Truck")).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
            foreach (var truck in trucks)
            {
                var chain = FileChain(truck).ToArray();
                foreach (var (socket, elementName, kind) in new[] { ("EngineSocket", "Engine", UpgradeKind.Engine),
                             ("GearboxSocket", "Gearbox", UpgradeKind.Gearbox), ("SuspensionSocket", "SuspensionSet", UpgradeKind.Suspension) })
                {
                    var sockets = chain.Select(d => d.Xml.Element("TruckData")?.Element(socket)).Where(e => e is not null).ToArray();
                    var type = sockets.Select(e => (string?)e!.Attribute("Type")).FirstOrDefault(v => v is not null);
                    var defaultId = sockets.Select(e => (string?)e!.Attribute("Default")).FirstOrDefault(v => v is not null);
                    foreach (var family in Split(type))
                    {
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var definition in FileChain(family))
                            foreach (var element in definition.Xml.Elements(elementName))
                                if ((string?)element.Attribute("Name") is { Length: > 0 } id && seen.Add(id))
                                    Add(id, kind, truck, definition, element, id.Equals(defaultId, StringComparison.OrdinalIgnoreCase));
                    }
                }
                var defaultFamily = DefaultWheel(chain, "DefaultWheelType");
                var defaultTire = DefaultWheel(chain, "DefaultTire");
                foreach (var wheel in chain.SelectMany(d => d.Xml.Element("TruckData")?.Elements("CompatibleWheels") ?? []))
                {
                    var family = (string?)wheel.Attribute("Type") ?? "";
                    if (!decimal.TryParse((string?)wheel.Attribute("Scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) || scale <= 0) continue;
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var definition in FileChain(family))
                        foreach (var element in definition.Xml.Element("TruckTires")?.Elements("TruckTire") ?? [])
                            if ((string?)element.Attribute("Name") is { Length: > 0 } name && seen.Add(name))
                            {
                                var id = family + "_" + name + "_" + scale.ToString("G29", CultureInfo.InvariantCulture);
                                var licensed = !tireLicenses.TryGetValue(name, out var licenses) || licenses.Any(ownedDlc.Contains);
                                Add(id, UpgradeKind.Tire, truck, definition, element,
                                    family.Equals(defaultFamily, StringComparison.OrdinalIgnoreCase) && name.Equals(defaultTire, StringComparison.OrdinalIgnoreCase), licensed);
                            }
                }
            }
            foreach (var (id, _) in _files.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var chain = FileChain(id).ToArray();
                if (chain.FirstOrDefault()?.Xml.Name.LocalName != "TruckAddon") continue;
                var game = chain.Select(d => d.Xml.Element("GameData")).FirstOrDefault(g => g is not null);
                var category = (string?)game?.Attribute("Category");
                var kind = id.Equals("frame_addon_metal_detector", StringComparison.OrdinalIgnoreCase) ? UpgradeKind.MetalDetector
                    : category == "awd" ? UpgradeKind.Awd : category == "diff_lock" ? UpgradeKind.DifferentialLock : (UpgradeKind?)null;
                if (kind is null) continue;
                foreach (var truck in trucks)
                {
                    if (!CanInstallAddons(truck, [id])) continue;
                    Add(id, kind.Value, truck, chain[0], chain[0].Xml, DefaultAddons(FileChain(truck).ToArray()).Contains(id, StringComparer.OrdinalIgnoreCase));
                }
            }
            return parts.Values.ToDictionary(p => p.Id, p => new UpgradeTrialPart(
                new(p.Id, p.Kind, p.Trucks.Order(StringComparer.Ordinal).ToArray(),
                    KeepInPlace: p.Kind == UpgradeKind.MetalDetector,
                    Eligible: p.Allowed && p.Trucks.Any(enabledTrucks.Contains)
                        && (p.Defaults.Count == 0 || originalRewardIds.Contains(p.Id))), p.Name, p.Source, p.Element, p.Variant), StringComparer.OrdinalIgnoreCase);
        }
    }
}
