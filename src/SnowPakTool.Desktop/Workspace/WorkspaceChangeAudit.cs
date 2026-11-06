using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SnowPakTool.Desktop;

internal static class WorkspaceChangeAudit
{
    public static IReadOnlyList<WorkspaceChange> PakFiles(string workspace)
    {
        var rows = new List<WorkspaceChange>();
        foreach (var folder in new[] { "[media]", "[ps]", "[ps_common]", "[strings]" })
        {
            var updated = Path.Combine(WorkspaceOriginals.GetUpdatedRoot(workspace), folder);
            if (!Directory.Exists(updated)) continue;
            foreach (var path in Directory.EnumerateFiles(updated, "*", SearchOption.AllDirectories))
            {
                var relative = WorkspaceOriginals.GetRelativePath(workspace, path);
                var original = WorkspaceOriginals.GetOriginalReadPath(workspace, relative);
                if (!File.Exists(original)) { rows.Add(new(WorkspaceChange.InFiles, relative, "Added file", "Not present", "Added")); continue; }
                var before = File.ReadAllBytes(original); var after = File.ReadAllBytes(path);
                if (before.AsSpan().SequenceEqual(after)) continue;
                if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var a = XmlValues(File.ReadAllText(original)); var b = XmlValues(File.ReadAllText(path));
                        foreach (var key in a.Keys.Concat(b.Keys).Distinct())
                            if (a.GetValueOrDefault(key) != b.GetValueOrDefault(key))
                                rows.Add(new(WorkspaceChange.InFiles, relative, key, a.GetValueOrDefault(key, "Not present"), b.GetValueOrDefault(key, "Not present")));
                        // Equal parsed values mean formatting/encoding differences only.
                        continue;
                    }
                    catch (System.Xml.XmlException) { }
                }
                if (Path.GetFileName(path).Equals("rank_settings.sso", StringComparison.OrdinalIgnoreCase))
                {
                    var beforeText = File.ReadAllText(original); var afterText = File.ReadAllText(path);
                    if (RankProgressionChanges.TryRead(beforeText, out var oldRanks) &&
                        RankProgressionChanges.TryRead(afterText, out var newRanks))
                    {
                        var rankChange = RankProgressionChanges.Create(WorkspaceChange.InFiles, relative, oldRanks, newRanks);
                        if (rankChange is not null) rows.Add(rankChange);
                        // Other settings in this file still need their own audit entry.
                        if (RankProgressionChanges.WithoutValues(beforeText) == RankProgressionChanges.WithoutValues(afterText)) continue;
                    }
                }
                if (new[] { ".sso", ".str", ".txt", ".xml" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                {
                    var a = File.ReadAllLines(original); var b = File.ReadAllLines(path);
                    // Keep a file-level row and the changed line preview for non-XML formats.
                    var first = Enumerable.Range(0, Math.Min(a.Length, b.Length)).FirstOrDefault(i => a[i] != b[i], -1);
                    if (first < 0) first = Math.Min(a.Length, b.Length);
                    rows.Add(new(WorkspaceChange.InFiles, relative, $"Text changed (first difference: line {first + 1})",
                        first < a.Length ? a[first] : "End of file", first < b.Length ? b[first] : "End of file"));
                }
                else rows.Add(new(WorkspaceChange.InFiles, relative, "Binary file changed", $"{before.Length:N0} bytes", $"{after.Length:N0} bytes"));
            }
        }
        return rows;
    }

    private static Dictionary<string, string> XmlValues(string text)
    {
        text = Regex.Replace(text, @"<\?xml[\s\S]*?\?>", "");
        text = Regex.Replace(text, @"<(/?)([A-Za-z_][\w.-]*):", "<$1$2_");
        var root = XElement.Parse("<Root>" + text + "</Root>");
        var values = new Dictionary<string, string>();
        void Visit(XElement node, string path)
        {
            foreach (var attr in node.Attributes()) values[path + "/@" + attr.Name] = attr.Value;
            if (!node.HasElements && !string.IsNullOrWhiteSpace(node.Value)) values[path + "/text"] = node.Value;
            var counts = new Dictionary<string, int>();
            foreach (var child in node.Elements())
            {
                var tag = child.Name.LocalName; counts[tag] = counts.GetValueOrDefault(tag) + 1;
                var id = (string?)child.Attribute("Name");
                Visit(child, path + "/" + tag + (id is null ? $"[{counts[tag]}]" : "[" + id + "]"));
            }
        }
        Visit(root, ""); return values;
    }

    public static IReadOnlyList<WorkspaceChange> MapFiles(string workspace)
    {
        var rows = new List<WorkspaceChange>();
        var workingChanges = PendingTruckReplacements.Load(workspace);
        var pendingChanges = workingChanges.Where(change => change.Staged).ToArray();
        var hiddenPlacements = PendingTruckReplacements.LoadAssignments(workspace).Select(item => item.PlacementKey)
            .Concat(workingChanges.Where(item => item.ResetOriginal || item.Randomized).Select(item => item.Original.StablePlacementKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = WorkspaceDataCache.GetLocalizedNames(workspace);
        var trucks = RegionTruckCatalogService.LoadTrucks(workspace, names, []);
        var codes = trucks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (map, path) in TruckReplacementStore.SavedMaps(workspace))
        {
            var backup = WorkspaceOriginals.GetMetadataPath(workspace, "region-maps", "backups", map,
                path.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ? "original.pak" : "original.bin");
            if (!File.Exists(backup)) { rows.Add(new(WorkspaceChange.InFiles, map, "Edited map (original backup unavailable)", "Unknown", path)); continue; }
            var before = RegionTruckCatalogService.ReadMapData(backup); var after = RegionTruckCatalogService.ReadMapData(path);
            if (before is null || after is null) throw new InvalidDataException("Cannot read map metadata for " + map);
            try { TruckReplacementStore.ValidateScene(after); }
            catch (InvalidDataException) { rows.Add(new(WorkspaceChange.InFiles, map, TruckReplacementStore.RepairRequired, "Original scene", "Use Repair saved maps")); }
            if (before.AsSpan().SequenceEqual(after)) continue;
            var originalRows = RegionTruckCatalogService.ReadPlacements(before, codes).ToList();
            var old = originalRows.ToLookup(Identity);
            var updated = RegionTruckCatalogService.ReadPlacements(after, codes).ToLookup(Identity);
            var start = rows.Count;
            foreach (var key in old.Select(group => group.Key).Concat(updated.Select(group => group.Key)).Distinct())
            {
                var a = old[key].ToArray(); var b = updated[key].ToArray();
                for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
                {
                    var first = i < a.Length ? a[i] : null; var second = i < b.Length ? b[i] : null;
                    if (first is not null && second is not null && hiddenPlacements.Contains(FormattableString.Invariant(
                            $"{map}|{first.Id}|{first.X:R}|{first.Y:R}|{first.Z:R}|{originalRows.IndexOf(first)}")))
                    {
                        if (first.TruckCode != second.TruckCode || !PendingTruckReplacements.EquipmentEqual(first.Equipment, second.Equipment))
                            rows.Add(new(WorkspaceChange.InFiles, map, "Truck randomization", "Original placement", "Saved — details in Map trucks"));
                        continue;
                    }
                    var label = first?.Id is { Length: > 0 } id ? id : $"Placement at {first?.X ?? second!.X:0.#}; {first?.Z ?? second!.Z:0.#}";
                    if (first?.TruckCode != second?.TruckCode)
                        rows.Add(new(WorkspaceChange.InFiles, map, label + " / Truck", Name(first?.TruckCode), Name(second?.TruckCode)));
                    else if (first is not null && second is not null && Equipment(first.Equipment) != Equipment(second.Equipment))
                        rows.Add(new(WorkspaceChange.InFiles, map, label + " / Installed equipment", Equipment(first.Equipment), Equipment(second.Equipment)));
                }
            }
            if (rows.Count == start) rows.Add(new(WorkspaceChange.InFiles, map, "Other map metadata changed", "Original metadata", "Modified metadata"));
            string Name(string? code) => code is null ? "Not present" : trucks.TryGetValue(code, out var truck) ? truck.Name : code;
        }
        foreach (var change in pendingChanges)
            rows.Add(change.Randomized || change.ResetOriginal
                ? new(WorkspaceChange.Pending, change.Original.MapCode, change.ResetOriginal ? "Reset to original" : "Truck randomization",
                    "Current placement", change.ResetOriginal ? "Original truck and equipment" : "Queued — details in Map trucks", change.Key)
                : new(WorkspaceChange.Pending, change.Original.MapCode,
                (change.Original.PlacementId.Length > 0 ? change.Original.PlacementId : $"Placement at {change.Original.X:0.#}; {change.Original.Z:0.#}") + " / Truck",
                change.Original.Truck, change.Target.Name, change.Key));
        return rows;
    }

    private static string Identity(MapTruckPlacement row) => row.Id + "|" + string.Join(",", row.Transform!.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
    private static string Equipment(MapTruckEquipment equipment) => string.Join(", ", equipment.Addons) + " | "
        + string.Join(" / ", equipment.Engine, equipment.Gearbox, equipment.Winch, equipment.Suspension, equipment.WheelType, equipment.Tire, equipment.Rim, equipment.WheelScale);
}
