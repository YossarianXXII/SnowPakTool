using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class Program
{
    static readonly Assembly App = Assembly.Load("SnowPakTool.Desktop");
    static Type T(string name) => App.GetType("SnowPakTool.Desktop." + name)!;
    static object P(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
    static object? Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(value, args);
    static object? Static(string type, string name, params object?[] args) => T(type).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
    static object[] Items(object value) => ((IEnumerable)value).Cast<object>().ToArray();
    static string S(object value, string name) => (string)P(value, name);
    static string Region(string map) => Regex.Match(map, @"level_((?:us|ru)_\d{2})", RegexOptions.IgnoreCase).Groups[1].Value.ToLowerInvariant();
    static string[] Split(string? value) => (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    sealed record Reference(string Map, string Region, string Zone, int Index, string UpgradeId, string Source);
    sealed record Placement(string Truck, string Name, string Region, string Map, string Availability, string PlacementId, string Evidence, string Source);
    sealed record Model(string Truck, string Name, string Class, string[] Regions, string[] UnverifiedRegions, string[] CompatibleRewardIds);
    sealed record Link(string Truck, string Name, string TruckRegion, string UpgradeId, string UpgradeType, string UpgradeName, string UpgradeRegion, string UpgradeMap, string Zone, bool SameRegion);

    static void Main()
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var analysis = Path.Combine(project, ".checks", "UpgradeRegionAnalysis");
        var workspace = Path.Combine(analysis, "source-workspace");
        var installed = @"S:\SteamLibrary\steamapps\common\SnowRunner\preload\paks\client";
        var output = Path.Combine(analysis, "results"); Directory.CreateDirectory(output);
        var warnings = new List<string>();
        var names = (IReadOnlyDictionary<string,string>)Static("WorkspaceDataCache", "GetLocalizedNames", workspace)!;
        var metadata = (IDictionary)Static("ExperienceEditorTab", "LoadRegionMetadata", workspace, names)!;
        var regionNames = metadata.Keys.Cast<string>().ToDictionary(key => key, key => S(metadata[key]!, "Name"), StringComparer.OrdinalIgnoreCase);
        Console.WriteLine("Reading original map placements with no pending edits or availability selections…");
        var availability = Activator.CreateInstance(T("RegionTruckAvailabilityStore"), new object[] { Path.Combine(analysis, "no-overrides.json") })!;
        var catalog = Static("RegionTruckCatalogService", "Load", workspace, installed, names, regionNames, availability, false)!;
        var equipment = P(catalog, "EquipmentCatalog");
        var placements = Items(P(catalog, "Entries")).Select(e => new Placement(S(e,"TruckCode"), S(e,"Truck"), S(e,"RegionCode"), S(e,"MapCode"), S(e,"Availability"), S(e,"PlacementId"), S(e,"Evidence"), S(e,"Source"))).ToArray();
        warnings.AddRange(Items(P(catalog,"Warnings")).Cast<string>());
        var obtainable = placements.Where(p => p.Availability.StartsWith("Obtainable", StringComparison.Ordinal)).ToArray();
        var references = Items(Static("AddonCatalogService", "ReadMapReferences", workspace, warnings)!).Select(r => new Reference(S(r,"Map"), Region(S(r,"Map")), S(r,"Zone"), (int)P(r,"Index"), S(r,"UpgradeId"), S(r,"Source"))).ToArray();
        var rewardIds = references.Select(r => r.UpgradeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var models = (Dictionary<string,(string Name,string Class)>)Static("RegionTruckCatalogService", "LoadTrucks", workspace, names, warnings)!;
        XElement[] Chain(string code) => Items(Call(equipment,"FileChain", code)!).Select(d => (XElement)P(d,"Xml")).ToArray();
        var rewardInfo = new Dictionary<string,(string Type,string Name)>(StringComparer.OrdinalIgnoreCase);
        string Label(XElement e, string fallback)
        {
            var key = (string?)e.Element("GameData")?.Element("UiDesc")?.Attribute("UiName");
            return key is null ? fallback : names.GetValueOrDefault(key,key);
        }
        var allModels = new List<Model>();
        foreach (var (code, info) in models.OrderBy(p => p.Key))
        {
            var chain = Chain(code);
            var compatible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (socket,kind) in new[] { ("EngineSocket","Engine"), ("GearboxSocket","Gearbox"), ("SuspensionSocket","SuspensionSet"), ("WinchUpgradeSocket","Winch") })
            {
                // Socket Type is inherited only when the child does not define it.
                var type = chain.Select(x => (string?)x.Element("TruckData")?.Element(socket)?.Attribute("Type")).FirstOrDefault(x => x is not null);
                foreach (var family in Split(type))
                    foreach (var definition in Chain(family))
                        foreach (var variant in definition.Elements(kind))
                        {
                            var id = (string?)variant.Attribute("Name");
                            if (id is null || !rewardIds.Contains(id)) continue;
                            compatible.Add(id);
                            rewardInfo[id] = (kind == "SuspensionSet" ? "Suspension" : kind, Label(variant,id));
                        }
            }
            var sockets = chain.SelectMany(x => x.Element("GameData")?.Elements("AddonSockets") ?? [])
                .SelectMany(x => x.Elements("Socket")).SelectMany(x => Split((string?)x.Attribute("Names"))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var id in rewardIds)
            {
                var addon = Chain(id);
                if (addon.Length == 0 || addon[0].Name.LocalName != "TruckAddon") continue;
                var install = addon.Select(x => x.Element("GameData")?.Element("InstallSocket")).FirstOrDefault(x => x is not null);
                if (install is null || !Split((string?)install.Attribute("Type")).Any(sockets.Contains)) continue;
                // For map-reward addons (AWD/diff lock), verify the existing
                // compatibility helper's socket/block/required-addon rules too.
                if (!(bool)Call(equipment,"CanInstallAddons",code,new[] { id })!) continue;
                compatible.Add(id);
                var category = addon.Select(x => (string?)x.Element("GameData")?.Attribute("Category")).FirstOrDefault(x => x is not null) ?? "Addon";
                rewardInfo[id] = (category, addon.Select(x => Label(x, "")).FirstOrDefault(x => x.Length > 0) ?? id);
            }
            allModels.Add(new(code, info.Name, info.Class,
                obtainable.Where(p => p.Truck.Equals(code,StringComparison.OrdinalIgnoreCase)).Select(p => p.Region).Distinct().Order().ToArray(),
                placements.Where(p => p.Truck.Equals(code,StringComparison.OrdinalIgnoreCase) && p.Availability == "Unverified").Select(p => p.Region).Distinct().Order().ToArray(),
                compatible.Order().ToArray()));
        }
        var eligible = allModels.Where(m => m.Regions.Length > 0).ToArray();
        // Count each model/reward-location once. Multiple obtainable copies of
        // a model within one region cannot inflate the primary ratio.
        var links = eligible.SelectMany(model => references.Where(r => model.CompatibleRewardIds.Contains(r.UpgradeId,StringComparer.OrdinalIgnoreCase))
            .Select(r => new Link(model.Truck,model.Name,string.Join(",",model.Regions),r.UpgradeId,rewardInfo[r.UpgradeId].Type,rewardInfo[r.UpgradeId].Name,r.Region,r.Map,r.Zone,model.Regions.Contains(r.Region,StringComparer.OrdinalIgnoreCase)))).ToArray();
        var perTruckRegion = links.SelectMany(link => link.TruckRegion.Split(',').Select(region => link with { TruckRegion = region, SameRegion = region == link.UpgradeRegion })).ToArray();
        var sourceMap = (Dictionary<string,string>)Static("RegionTruckCatalogService","MapSources",workspace,installed)!;
        var sourceChecks = new List<object>();
        foreach (var (map,source) in sourceMap)
        {
            var installedSource = Path.Combine(installed,map+".pak");
            if (!File.Exists(installedSource)) { sourceChecks.Add(new {Map=map,Source=source,InstalledSceneMatches=(bool?)null}); continue; }
            var a = (byte[]?)Static("RegionTruckCatalogService","ReadMapData",source);
            var b = (byte[]?)Static("RegionTruckCatalogService","ReadMapData",installedSource);
            sourceChecks.Add(new {Map=map,Source=source,InstalledSceneMatches=(bool?)(a is not null && b is not null && a.AsSpan().SequenceEqual(b))});
        }
        var data = new
        {
            GeneratedAt = DateTimeOffset.Now,
            RegionNames = regionNames,
            MapsRead = P(catalog,"MapsRead"),
            Placements = placements,
            Models = allModels,
            References = references,
            RewardInfo = rewardInfo.ToDictionary(p => p.Key,p => new {p.Value.Type,p.Value.Name}),
            Links = links,
            PerTruckRegionLinks = perTruckRegion,
            UnknownRewardIds = references.Where(r => !rewardInfo.ContainsKey(r.UpgradeId)).Select(r => r.UpgradeId).Distinct().ToArray(),
            SourceChecks = sourceChecks,
            Warnings = warnings.Distinct().ToArray(),
            Summary = new { AllPlacements=placements.Length, ObtainablePlacements=obtainable.Length, ObtainableModels=eligible.Length, ModelRewardPairs=links.Length, SameRegionPairs=links.Count(l=>l.SameRegion), OtherRegionPairs=links.Count(l=>!l.SameRegion) }
        };
        File.WriteAllText(Path.Combine(output,"original-trucks-upgrades.json"),JsonSerializer.Serialize(data,Json));
        Console.WriteLine(JsonSerializer.Serialize(data.Summary,Json));
        Console.WriteLine("Availability: "+string.Join("; ",placements.GroupBy(p=>p.Availability).Select(g=>$"{g.Key}: {g.Count()}")));
        Console.WriteLine("Unknown reward IDs: "+string.Join(", ",data.UnknownRewardIds));
        Console.WriteLine($"Warnings: {data.Warnings.Length}. Exported {output}");
    }
}
