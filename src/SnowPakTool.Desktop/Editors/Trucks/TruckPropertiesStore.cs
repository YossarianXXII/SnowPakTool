using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SnowPakTool.Desktop;

internal sealed record TruckPropertySpec(string Element, string Attribute, string Label, decimal Step, decimal Minimum, decimal Maximum, bool IsBoolean = false);
internal sealed record TruckPropertyEdit(string RelativeFile, string TruckName, string Attribute, decimal Before, decimal After, bool RemoveAttribute = false);
internal sealed record TruckPropertyPreset(int Version, List<TruckPropertyEdit> Values);
internal sealed record TruckPropertyRow(string RelativeFile, CustomTruck Truck, string Country, string DiffLock,
    Dictionary<string, decimal> Original, Dictionary<string, decimal> Current, Dictionary<string, decimal> Draft,
    string GameClass = "", bool RequiresExploration = false, string UnlockObjective = "", bool Locked = false,
    bool OriginalExplorationExplicit = true, bool CurrentExplorationExplicit = true);

internal static class TruckPropertiesStore
{
    internal static decimal RoundFuel(decimal value) => Math.Clamp(decimal.Round(value / 5m, 0, MidpointRounding.AwayFromZero) * 5m, 5, 100000);
    internal static readonly TruckPropertySpec[] Specs =
    [
        new("TruckData", "FuelCapacity", "Fuel capacity (L)", 10, 1, 100000),
        new("GameData", "UnlockByRank", "Unlock rank", 1, 0, 30),
        new("GameData", "Price", "Price", 1000, 0, 100000000),
        new("GameData", "UnlockByExploration", "Unlock by exploration", 1, 0, 1, true)
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static string PendingPath(string workspace) => WorkspaceOriginals.GetMetadataPath(workspace, "truck-properties", "pending-changes.json");
    private static Match Root(string text) => Regex.Match(text, @"<Truck(?=[\s>])[\s\S]*?</Truck\s*>", RegexOptions.IgnoreCase);
    private static Match Tag(string text, string element) => Regex.Match(text, @"<" + element + @"\b[^>]*>", RegexOptions.IgnoreCase);
    private static Match Attribute(string text, string attribute) => Regex.Match(text, @"\b" + attribute + "\\s*=\\s*\"(?<value>[^\"]*)\"", RegexOptions.IgnoreCase);
    internal static string Read(string text, string element, string attribute) => Attribute(Tag(Root(text).Value, element).Value, attribute).Groups["value"].Value;
    internal static Dictionary<string, decimal> Values(string text) => Specs
        .Select(spec => (spec.Attribute, Value: spec.IsBoolean
            ? (Read(text, spec.Element, spec.Attribute).Equals("true", StringComparison.OrdinalIgnoreCase) ? "1" : "0")
            : Read(text, spec.Element, spec.Attribute)))
        .Where(pair => decimal.TryParse(pair.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        .ToDictionary(pair => pair.Attribute, pair => decimal.Parse(pair.Value, CultureInfo.InvariantCulture));

    internal static List<TruckPropertyRow> LoadRows(string workspace)
    {
        var names = WorkspaceDataCache.GetLocalizedNames(workspace);
        var result = new List<TruckPropertyRow>();
        foreach (var file in WorkspaceOriginals.EnumerateCurrentFiles(workspace, Path.Combine("[media]", "classes", "trucks"), "*.xml")
                     .Concat(WorkspaceOriginals.EnumerateCurrentFiles(workspace, Path.Combine("[media]", "_dlc"), "*.xml")))
        {
            var relative = WorkspaceOriginals.GetRelativePath(workspace, file).Replace('\\', '/');
            if (!relative.Contains("/classes/trucks/", StringComparison.OrdinalIgnoreCase)) continue;
            var text = File.ReadAllText(file);
            if (!Root(text).Success) continue;
            var code = Path.GetFileNameWithoutExtension(file);
            var key = Read(text, "UiDesc", "UiName");
            var truckClass = Read(text, "TruckData", "TruckType");
            if (truckClass.Length == 0) continue;
            var current = Values(text);
            var originalFile = WorkspaceOriginals.GetOriginalReadPath(workspace, relative);
            var original = File.Exists(originalFile) ? Values(File.ReadAllText(originalFile)) : new(current);
            result.Add(new(relative, new(code, names.GetValueOrDefault(key, code), TruckClasses.ForTruck(code, truckClass)),
                Read(text, "GameData", "Country"), Read(text, "TruckData", "DiffLockType"), original, current, new(current),
                truckClass, Read(text, "GameData", "UnlockByExploration").Equals("true", StringComparison.OrdinalIgnoreCase),
                Read(text, "GameData", "UnlockByObjective"), Read(text, "GameData", "LockedUnconditionally").Equals("true", StringComparison.OrdinalIgnoreCase),
                File.Exists(originalFile) ? Read(File.ReadAllText(originalFile), "GameData", "UnlockByExploration").Length > 0 : Read(text, "GameData", "UnlockByExploration").Length > 0,
                Read(text, "GameData", "UnlockByExploration").Length > 0));
        }
        return result;
    }

    internal static List<TruckPropertyEdit> LoadPending(string workspace)
    {
        return LoadSnapshot(workspace).Values;
    }

    internal static TruckPropertyPreset LoadSnapshot(string workspace)
    {
        var path = PendingPath(workspace);
        if (!File.Exists(path)) return new(2, []);
        var json = File.ReadAllText(path);
        return json.TrimStart().StartsWith('[') ? new(2, ReadEdits(json)) : ReadPresetSnapshot(json);
    }

    internal static List<TruckPropertyEdit> ReadEdits(string json)
    {
        var edits = JsonSerializer.Deserialize<List<TruckPropertyEdit>>(json) ?? throw new InvalidDataException("Missing truck property values.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            if (edit is null || string.IsNullOrWhiteSpace(edit.RelativeFile) || edit.TruckName is null)
                throw new InvalidDataException("Invalid truck property entry.");
            var spec = Specs.FirstOrDefault(spec => spec.Attribute == edit.Attribute);
            var path = edit.RelativeFile.Replace('\\', '/');
            if (spec is null || edit.After < spec.Minimum || edit.After > spec.Maximum || decimal.Truncate(edit.After) != edit.After
                || edit.RemoveAttribute && (edit.Attribute != "UnlockByExploration" || edit.After != 0)
                || Path.IsPathRooted(path) || path.Split('/').Any(part => part is ".." or "" or ".")
                || !path.StartsWith("[media]/", StringComparison.OrdinalIgnoreCase)
                || !path.Contains("/classes/trucks/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || !keys.Add(path + "|" + edit.Attribute))
                throw new InvalidDataException("Invalid or duplicate truck property value.");
        }
        return edits.Select(edit => edit with { RelativeFile = edit.RelativeFile.Replace('\\', '/') }).ToList();
    }

    internal static void SavePending(string workspace, List<TruckPropertyEdit> edits)
    {
        var json = SerializeSnapshot(edits);
        ReadPresetSnapshot(json);
        WriteAtomic(PendingPath(workspace), json);
    }

    internal static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static string Patch(string text, IEnumerable<TruckPropertyEdit> edits)
    {
        foreach (var edit in edits)
        {
            var spec = Specs.Single(spec => spec.Attribute == edit.Attribute);
            var root = Root(text);
            var tag = Tag(root.Value, spec.Element);
            var attribute = Attribute(tag.Value, spec.Attribute);
            var rawValue = attribute.Groups["value"].Value;
            decimal current;
            var valid = spec.IsBoolean ? rawValue.Length == 0 || bool.TryParse(rawValue, out _) : decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
            current = spec.IsBoolean ? (rawValue.Equals("true", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                : decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : 0;
            if (!root.Success || !tag.Success || !valid
                || current != edit.Before && current != edit.After)
                throw new InvalidDataException($"{edit.TruckName}: {spec.Label} changed since staging. Reload and stage again.");
            var replacement = spec.IsBoolean ? (edit.After == 1 ? "true" : "false") : edit.After.ToString(CultureInfo.InvariantCulture);
            if (edit.RemoveAttribute)
            {
                if (!attribute.Success) continue;
                var remove = Regex.Match(tag.Value, @"\s+" + Regex.Escape(spec.Attribute) + "\\s*=\\s*\"[^\"]*\"", RegexOptions.IgnoreCase);
                if (!remove.Success) throw new InvalidDataException("Cannot locate the introduced exploration attribute.");
                text = text.Remove(root.Index + tag.Index + remove.Index, remove.Length);
                continue;
            }
            if (!attribute.Success && spec.IsBoolean)
            {
                var insert = root.Index + tag.Index + tag.Length - (tag.Value.EndsWith("/>") ? 2 : 1);
                text = text.Insert(insert, $" {spec.Attribute}=\"{replacement}\"");
                continue;
            }
            var value = attribute.Groups["value"];
            var offset = root.Index + tag.Index + value.Index;
            text = text.Remove(offset, value.Length).Insert(offset, replacement);
        }
        return text;
    }

    internal static void Apply(string workspace)
    {
        var pending = LoadSnapshot(workspace);
        // Validate every target before writing any output, preserving edits made by other editors.
        var outputs = pending.Values.Select(edit => edit.RelativeFile)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(relative =>
        {
            var path = WorkspaceOriginals.GetCurrentPath(workspace, relative);
            var text = Patch(File.ReadAllText(path), pending.Values.Where(edit => edit.RelativeFile.Equals(relative, StringComparison.OrdinalIgnoreCase)));
            return (Path: path, Text: text);
        }).ToArray();
        foreach (var output in outputs)
        {
            var path = WorkspaceOriginals.PrepareForEdit(workspace, output.Path);
            WriteAtomic(path, output.Text);
            WorkspaceDataCache.InvalidateFile(path);
        }
        SavePending(workspace, []);
    }

    internal static string SerializePreset(List<TruckPropertyEdit> values) => JsonSerializer.Serialize(new TruckPropertyPreset(1, values), JsonOptions);
    internal static string SerializeSnapshot(List<TruckPropertyEdit> values) => JsonSerializer.Serialize(new TruckPropertyPreset(2, values), JsonOptions);
    internal static List<TruckPropertyEdit> ReadPreset(string json) => ReadPresetSnapshot(json).Values;

    internal static TruckPropertyPreset ReadPresetSnapshot(string json)
    {
        var preset = JsonSerializer.Deserialize<TruckPropertyPreset>(json);
        if (preset?.Version is not (1 or 2) || preset.Values is null)
            throw new InvalidDataException("Unsupported truck property preset.");
        var values = ReadEdits(JsonSerializer.Serialize(preset.Values));
        // Ignore the short-lived Country payload in older version-2 plans/presets.
        // Country availability is read-only; only validated supported properties can be applied.
        return new(2, values);
    }
}
