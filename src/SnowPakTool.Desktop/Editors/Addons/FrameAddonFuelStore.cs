using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SnowPakTool.Desktop;

internal sealed record FrameAddonFuelEdit(string RelativeFile, string Name, decimal Before, decimal After);
internal sealed class FrameAddonFuelRow(string name, decimal original, decimal current)
{
    public string Name { get; } = name;
    public decimal Original { get; } = original;
    public decimal Current { get; } = current;
    public decimal Draft { get; set; } = current;
}

internal static class FrameAddonFuelStore
{
    internal static decimal RoundFuel(decimal value) => Math.Clamp(decimal.Round(value / 5m, 0, MidpointRounding.AwayFromZero) * 5m, 0, 10000);
    private static string PendingPath(string workspace) => WorkspaceOriginals.GetMetadataPath(workspace, "frame-addon-fuel", "pending-changes.json");
    private static Match Root(string text) => Regex.Match(text, @"<TruckAddon(?=[\s>])[\s\S]*?</TruckAddon\s*>");
    internal static decimal? Read(string text)
    {
        var root = Root(text);
        if (!root.Success) return null;
        var xml = XElement.Parse(root.Value);
        return decimal.TryParse((string?)xml.Element("TruckData")?.Attribute("FuelCapacity"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    internal static List<FrameAddonFuelEdit> Load(string workspace)
    {
        var path = PendingPath(workspace);
        var edits = File.Exists(path) ? JsonSerializer.Deserialize<List<FrameAddonFuelEdit>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Missing frame addon fuel edits.") : [];
        Validate(edits);
        return edits;
    }

    private static void Validate(IReadOnlyList<FrameAddonFuelEdit> edits)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            var path = edit?.RelativeFile?.Replace('\\', '/');
            if (edit is null || path is null || edit.Name is null || Path.IsPathRooted(path) || path.Contains(':')
                || path.Split('/').Any(part => part is ".." or "." or "")
                || !path.StartsWith("[media]/", StringComparison.OrdinalIgnoreCase)
                || !path.Contains("/classes/trucks/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || !seen.Add(path) || edit.After < 0 || edit.After > 10000 || decimal.Truncate(edit.After) != edit.After)
                throw new InvalidDataException("Invalid or duplicate frame addon fuel edit.");
        }
    }

    internal static void Save(string workspace, List<FrameAddonFuelEdit> edits)
    {
        Validate(edits);
        TruckPropertiesStore.WriteAtomic(PendingPath(workspace), JsonSerializer.Serialize(edits, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static IEnumerable<WorkspaceChange> Pending(string workspace) => Load(workspace).Select(edit =>
        new WorkspaceChange(WorkspaceChange.Pending, WorkspaceOriginals.GetCurrentPath(workspace, edit.RelativeFile),
            edit.Name + " / Fuel capacity (L)", edit.Before.ToString(CultureInfo.InvariantCulture), edit.After.ToString(CultureInfo.InvariantCulture)));

    internal static string Patch(string text, FrameAddonFuelEdit edit)
    {
        Validate([edit]);
        var root = Root(text);
        var current = Read(text);
        if (current is null || current != edit.Before && current != edit.After)
            throw new InvalidDataException($"{edit.Name}: fuel capacity changed since staging. Reload and stage again.");
        var xml = XElement.Parse(root.Value);
        if (!string.Equals((string?)xml.Element("GameData")?.Attribute("Category"), "frame_addons", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{edit.Name}: expected an explicitly defined frame addon category.");
        var tags = Regex.Matches(root.Value, @"<TruckData\b[^>]*>");
        if (tags.Count != 1) throw new InvalidDataException($"{edit.Name}: ambiguous TruckData element.");
        var attribute = Regex.Match(tags[0].Value, "\\bFuelCapacity\\s*=\\s*(?<quote>[\"'])(?<value>[^\"']*)\\k<quote>");
        if (!attribute.Success) throw new InvalidDataException($"{edit.Name}: missing explicit fuel capacity.");
        var value = attribute.Groups["value"];
        var offset = root.Index + tags[0].Index + value.Index;
        return text.Remove(offset, value.Length).Insert(offset, edit.After.ToString(CultureInfo.InvariantCulture));
    }

    internal static void Apply(string workspace)
    {
        var outputs = Load(workspace).Select(edit =>
        {
            var path = WorkspaceOriginals.GetCurrentPath(workspace, edit.RelativeFile);
            return (Path: path, Text: Patch(File.ReadAllText(path), edit));
        }).ToArray();
        foreach (var output in outputs)
        {
            var path = WorkspaceOriginals.PrepareForEdit(workspace, output.Path);
            TruckPropertiesStore.WriteAtomic(path, output.Text);
            WorkspaceDataCache.InvalidateFile(path);
        }
        Save(workspace, []);
    }
}
