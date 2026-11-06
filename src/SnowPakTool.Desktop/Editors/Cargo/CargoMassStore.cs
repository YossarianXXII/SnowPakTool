using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace SnowPakTool.Desktop;

internal sealed record CargoMassEdit(string RelativeFile, string BodyPath, string ModelFrame, string Structure,
    string Name, string? Before, string? After);
internal sealed record CargoRawBody(string Path, string Frame, string Template, string? Mass, int Offset, int Length);
internal sealed class CargoMassRow(CargoMassTarget target, string structure, bool hasOriginal, string? original)
{
    public CargoMassTarget Target { get; } = target;
    public string Structure { get; } = structure;
    public bool HasOriginal { get; } = hasOriginal;
    public string? Original { get; } = original;
    public string? Current { get; } = target.RawMass;
    public string? Draft { get; set; } = target.RawMass;
}

internal static class CargoMassStore
{
    private static string PendingPath(string workspace) => WorkspaceOriginals.GetMetadataPath(workspace, "cargo-mass", "pending-changes.json");
    internal static string Key(string file, string body) => file.Replace('\\', '/') + "|" + body;
    internal static string Key(CargoMassTarget target) => Key(target.RelativeFile, target.BodyPath);
    internal static bool Equal(string? left, string? right) => left == right || left is not null && right is not null
        && decimal.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
        && decimal.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a == b;
    internal static bool Valid(string? value) => value is null || decimal.TryParse(value, NumberStyles.Float,
        CultureInfo.InvariantCulture, out var number) && number >= 0 && number <= 1000000;

    // XML reader supplies exact opening-tag offsets; only the selected Mass attribute is patched.
    internal static List<CargoRawBody> Bodies(string text)
    {
        var lines = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') lines.Add(i + 1);
        var result = new List<CargoRawBody>();
        var stack = new List<string>();
        var counters = new List<Dictionary<string, int>> { new(StringComparer.Ordinal) };
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        { ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            var depth = reader.Depth;
            while (stack.Count > depth) stack.RemoveAt(stack.Count - 1);
            while (counters.Count > depth + 1) counters.RemoveAt(counters.Count - 1);
            var siblings = counters[depth];
            siblings.TryGetValue(reader.Name, out var index); siblings[reader.Name] = ++index;
            var part = reader.Name + "[" + index + "]";
            var path = string.Join("/", stack.Append(part));
            if (reader.LocalName == "Body" && stack.Count >= 2
                && (stack[0] == "TruckAddon[1]" || stack[0] == "ModelBrand[1]") && stack[1] == "PhysicsModel[1]")
            {
                var info = (IXmlLineInfo)reader;
                var offset = lines[info.LineNumber - 1] + info.LinePosition - 2;
                var end = offset; var quote = '\0';
                for (; end < text.Length; end++)
                {
                    var ch = text[end];
                    if (quote != '\0') { if (ch == quote) quote = '\0'; }
                    else if (ch is '\'' or '"') quote = ch;
                    else if (ch == '>') break;
                }
                if (offset < 0 || end == text.Length || text[offset] != '<') throw new InvalidDataException("Could not locate cargo body.");
                result.Add(new(path, reader.GetAttribute("ModelFrame") ?? "", reader.GetAttribute("_template") ?? "",
                    reader.GetAttribute("Mass"), offset, end - offset + 1));
            }
            if (!reader.IsEmptyElement) { stack.Add(part); counters.Add(new(StringComparer.Ordinal)); }
        }
        return result;
    }
    internal static string Structure(IReadOnlyList<CargoRawBody> bodies) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\n", bodies.Select(body => body.Path + "|" + body.Frame + "|" + body.Template)))));

    internal static Dictionary<string, CargoMassRow> ReadRows(string workspace, CargoCatalog catalog)
    {
        var result = new Dictionary<string, CargoMassRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in catalog.Entries.SelectMany(entry => entry.MassTargets).GroupBy(target => target.RelativeFile, StringComparer.OrdinalIgnoreCase))
        {
            var current = Bodies(WorkspaceDataCache.ReadAllText(WorkspaceOriginals.GetCurrentPath(workspace, group.Key)));
            var originalPath = WorkspaceOriginals.GetOriginalReadPath(workspace, group.Key);
            var originals = File.Exists(originalPath) ? Bodies(File.ReadAllText(originalPath)) : [];
            var originalStructureMatches = Structure(originals) == Structure(current);
            foreach (var target in group.DistinctBy(Key, StringComparer.OrdinalIgnoreCase))
            {
                var original = originalStructureMatches ? originals.SingleOrDefault(body => body.Path == target.BodyPath && body.Frame == target.ModelFrame) : null;
                result[Key(target)] = new(target, Structure(current), original is not null, original?.Mass);
            }
        }
        return result;
    }

    private static void Validate(IReadOnlyList<CargoMassEdit> edits)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            var path = edit?.RelativeFile?.Replace('\\', '/');
            if (edit is null || path is null || Path.IsPathRooted(path) || path.Contains(':')
                || path.Split('/').Any(part => part is ".." or "." or "")
                || !path.StartsWith("[media]/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || !(path.Contains("/classes/trucks/", StringComparison.OrdinalIgnoreCase) || path.Contains("/classes/models/", StringComparison.OrdinalIgnoreCase))
                || !Path.GetFileName(path).StartsWith("cargo_", StringComparison.OrdinalIgnoreCase)
                || edit.BodyPath is null || !Regex.IsMatch(edit.BodyPath, @"^(TruckAddon|ModelBrand)\[1\]/PhysicsModel\[1\](/Body\[[1-9][0-9]*\])+$")
                || edit.ModelFrame is null || edit.Name is null || edit.Structure is null || edit.Structure.Length != 64
                || !Valid(edit.Before) || !Valid(edit.After) || !seen.Add(Key(path, edit.BodyPath)))
                throw new InvalidDataException("Invalid or duplicate cargo mass target.");
        }
    }
    internal static List<CargoMassEdit> Load(string workspace)
    {
        var path = PendingPath(workspace);
        var edits = File.Exists(path) ? JsonSerializer.Deserialize<List<CargoMassEdit>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Missing cargo mass edits.") : [];
        Validate(edits); return edits;
    }
    internal static void Save(string workspace, List<CargoMassEdit> edits)
    {
        Validate(edits);
        TruckPropertiesStore.WriteAtomic(PendingPath(workspace), JsonSerializer.Serialize(edits, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static IEnumerable<WorkspaceChange> Pending(string workspace) => Load(workspace).Select(edit => new WorkspaceChange(
        WorkspaceChange.Pending, WorkspaceOriginals.GetCurrentPath(workspace, edit.RelativeFile), edit.Name + " / " + edit.BodyPath + "/@Mass",
        edit.Before ?? "Not present", edit.After ?? "Not present"));

    internal static string Patch(string text, IReadOnlyList<CargoMassEdit> edits)
    {
        Validate(edits);
        var bodies = Bodies(text);
        var structure = Structure(bodies);
        var patches = new List<(int Offset, int Length, string Text)>();
        foreach (var edit in edits)
        {
            var body = bodies.SingleOrDefault(body => body.Path == edit.BodyPath && body.Frame == edit.ModelFrame);
            if (body is null || structure != edit.Structure || !Equal(body.Mass, edit.Before) && !Equal(body.Mass, edit.After))
                throw new InvalidDataException($"{edit.Name}: cargo body or mass changed since staging. Reload and stage again.");
            if (Equal(body.Mass, edit.After)) continue;
            var tag = text.Substring(body.Offset, body.Length);
            var attribute = Regex.Match(tag, "\\s+Mass\\s*=\\s*(?<quote>[\"'])(?<value>[^\"']*)\\k<quote>");
            if (attribute.Success)
            {
                var match = edit.After is null ? (Group)attribute : attribute.Groups["value"];
                patches.Add((body.Offset + match.Index, match.Length, edit.After ?? ""));
            }
            else if (edit.After is not null)
            {
                var index = tag.EndsWith("/>", StringComparison.Ordinal) ? tag.Length - 2 : tag.Length - 1;
                patches.Add((body.Offset + index, 0, " Mass=\"" + edit.After + "\""));
            }
        }
        foreach (var patch in patches.OrderByDescending(patch => patch.Offset)) text = text.Remove(patch.Offset, patch.Length).Insert(patch.Offset, patch.Text);
        return text;
    }
    internal static void Apply(string workspace)
    {
        var outputs = Load(workspace).GroupBy(edit => edit.RelativeFile, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var source = WorkspaceOriginals.GetCurrentPath(workspace, group.Key);
            return (Source: source, Text: Patch(File.ReadAllText(source), group.ToArray()));
        }).ToArray(); // Validate every file before writing any output.
        foreach (var output in outputs)
        {
            var path = WorkspaceOriginals.PrepareForEdit(workspace, output.Source);
            TruckPropertiesStore.WriteAtomic(path, output.Text); WorkspaceDataCache.InvalidateFile(path);
        }
        Save(workspace, []);
    }
}
