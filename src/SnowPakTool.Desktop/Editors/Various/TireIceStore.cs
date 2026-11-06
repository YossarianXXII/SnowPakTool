using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace SnowPakTool.Desktop;

internal sealed record TireIceTarget(string Path, string Identity, bool Exists, string? Value, int Offset, int Length);
internal sealed record TireIceEdit(string RelativeFile, string Path, string Identity, bool BeforeExists, string? Before,
    bool AfterExists, string? After);

internal static class TireIceStore
{
    private static readonly Regex Attribute = new("\\s+IsIgnoreIce\\s*=\\s*(?<q>[\"'])(?<value>[^\"']*)\\k<q>");
    private static string PendingPath(string workspace) => WorkspaceOriginals.GetMetadataPath(workspace, "various", "pending-changes.json");
    private sealed record Node(string Name, string Path, string Identity, int Offset, int Length, bool Empty);

    // Read XML fragments (the game's templates and definitions are sibling roots), retaining exact tag offsets.
    internal static List<TireIceTarget> Targets(string text)
    {
        var lines = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') lines.Add(i + 1);
        var stack = new List<Node>();
        var counts = new List<Dictionary<string, int>> { new(StringComparer.Ordinal) };
        var targets = new List<TireIceTarget>();
        var tires = new List<Node>();
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        { ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            while (stack.Count > reader.Depth) stack.RemoveAt(stack.Count - 1);
            while (counts.Count > reader.Depth + 1) counts.RemoveAt(counts.Count - 1);
            var siblings = counts[reader.Depth];
            siblings.TryGetValue(reader.Name, out var index); siblings[reader.Name] = ++index;
            var path = string.Join("/", stack.Select(n => n.Path.Split('/')[^1]).Append(reader.Name + "[" + index + "]"));
            var identity = string.Join("/", stack.Select(n => n.Identity.Split('\n')[^1])) + "\n" + reader.Name + "|"
                + reader.GetAttribute("Name") + "|" + reader.GetAttribute("Mesh") + "|" + reader.GetAttribute("_template");
            var info = (IXmlLineInfo)reader;
            var offset = lines[info.LineNumber - 1] + info.LinePosition - 2;
            var end = offset; var quote = '\0';
            for (; end < text.Length; end++)
            {
                var c = text[end];
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c is '\'' or '"') quote = c;
                else if (c == '>') break;
            }
            if (offset < 0 || end == text.Length || text[offset] != '<') throw new InvalidDataException("Could not locate tire XML tag.");
            var node = new Node(reader.Name, path, identity, offset, end - offset + 1, reader.IsEmptyElement);
            var tire = reader.Name == "TruckTire" && stack.Select(n => n.Name).SequenceEqual(new[] { "TruckWheels", "TruckTires" });
            if (tire) tires.Add(node);
            var direct = reader.Name == "WheelFriction" && stack.Select(n => n.Name).SequenceEqual(new[] { "TruckWheels", "TruckTires", "TruckTire" });
            var template = stack.Count == 2 && stack[0].Name == "_templates" && stack[1].Name == "WheelFriction";
            var tireTemplate = reader.Name == "WheelFriction" && stack.Count == 3
                && stack[0].Name == "_templates" && stack[1].Name == "TruckTire";
            if (direct || template || tireTemplate)
                targets.Add(new(path, direct ? stack[^1].Identity : identity, true, reader.GetAttribute("IsIgnoreIce"), offset, node.Length));
            if (!reader.IsEmptyElement) { stack.Add(node); counts.Add(new(StringComparer.Ordinal)); }
        }
        foreach (var tire in tires)
        {
            var path = tire.Path + "/WheelFriction[1]";
            if (targets.Any(t => t.Path == path)) continue;
            // Missing local friction inherits from templates. Add an explicit override on each tire.
            targets.Add(new(path, tire.Identity, false, null, tire.Offset, tire.Length));
        }
        if (targets.Select(t => t.Path).Distinct().Count() != targets.Count) throw new InvalidDataException("Duplicate tire friction target.");
        return targets;
    }

    internal static IEnumerable<string> Files(string workspace) => WorkspaceOriginals.EnumerateCurrentFiles(workspace, "[media]", "*.xml")
        .Where(file => { var p = WorkspaceOriginals.GetRelativePath(workspace, file).Replace('\\', '/');
            return p.Contains("/classes/wheels/", StringComparison.OrdinalIgnoreCase) || p.Contains("/_templates/", StringComparison.OrdinalIgnoreCase); });

    internal static List<TireIceEdit> Preview(string workspace, bool enabled)
    {
        var edits = new List<TireIceEdit>();
        foreach (var file in Files(workspace))
        {
            var relative = WorkspaceOriginals.GetRelativePath(workspace, file);
            var current = Targets(File.ReadAllText(file));
            var originalPath = WorkspaceOriginals.GetOriginalReadPath(workspace, relative);
            var original = File.Exists(originalPath) ? Targets(File.ReadAllText(originalPath)).ToDictionary(t => t.Path) : [];
            foreach (var target in current)
            {
                if (!enabled && (!original.TryGetValue(target.Path, out var baseline) || baseline.Identity != target.Identity))
                    throw new InvalidDataException($"Cannot restore original tire friction: {relative} / {target.Path}");
                var afterExists = enabled || original[target.Path].Exists;
                var after = enabled ? "true" : original[target.Path].Value;
                if (target.Exists != afterExists || target.Value != after)
                    edits.Add(new(relative, target.Path, target.Identity, target.Exists, target.Value, afterExists, after));
            }
        }
        return edits;
    }

    private static void Validate(IEnumerable<TireIceEdit> edits)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in edits)
        {
            var path = edit.RelativeFile.Replace('\\', '/');
            if (!path.StartsWith("[media]/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || path.Split('/').Any(p => p is "" or "." or "..") || path.Contains(':')
                || !(path.Contains("/classes/wheels/", StringComparison.OrdinalIgnoreCase) || path.Contains("/_templates/", StringComparison.OrdinalIgnoreCase))
                || string.IsNullOrEmpty(edit.Path) || string.IsNullOrEmpty(edit.Identity)
                || !seen.Add(path + "|" + edit.Path) || !edit.BeforeExists && edit.Before is not null || !edit.AfterExists && edit.After is not null
                || edit.After is not null && edit.After is not ("true" or "false"))
                throw new InvalidDataException("Invalid or duplicate tire ice edit.");
        }
    }
    internal static List<TireIceEdit> Load(string workspace)
    {
        var path = PendingPath(workspace);
        var edits = File.Exists(path) ? JsonSerializer.Deserialize<List<TireIceEdit>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Missing tire ice edits.") : [];
        Validate(edits); return edits;
    }
    internal static void Save(string workspace, List<TireIceEdit> edits)
    {
        Validate(edits);
        TruckPropertiesStore.WriteAtomic(PendingPath(workspace), JsonSerializer.Serialize(edits, new JsonSerializerOptions { WriteIndented = true }));
    }
    internal static IEnumerable<WorkspaceChange> Changes(string workspace, IEnumerable<TireIceEdit> edits) => edits.Select(e => new WorkspaceChange(
        WorkspaceChange.Pending, WorkspaceOriginals.GetCurrentPath(workspace, e.RelativeFile), e.Path + "/@IsIgnoreIce", e.Before ?? "Not present", e.After ?? "Not present"));
    internal static IEnumerable<WorkspaceChange> Pending(string workspace) => Changes(workspace, Load(workspace));

    internal static string Patch(string text, IReadOnlyList<TireIceEdit> edits)
    {
        Validate(edits);
        var targets = Targets(text).ToDictionary(t => t.Path);
        var patches = new List<(int Offset, int Length, string Text)>();
        foreach (var edit in edits)
        {
            if (!targets.TryGetValue(edit.Path, out var target) || target.Identity != edit.Identity)
                throw new InvalidDataException("Tire definition changed since staging: " + edit.Path);
            if (target.Exists == edit.AfterExists && target.Value == edit.After) continue;
            if (target.Exists != edit.BeforeExists || target.Value != edit.Before)
                throw new InvalidDataException("Tire ice setting changed since staging: " + edit.Path);
            var tag = text.Substring(target.Offset, target.Length);
            if (!target.Exists)
            {
                if (!edit.AfterExists) continue;
                var child = "<WheelFriction IsIgnoreIce=\"" + edit.After + "\" />";
                if (tag.EndsWith("/>", StringComparison.Ordinal))
                    patches.Add((target.Offset, target.Length, tag[..^2] + ">" + child + "</TruckTire>"));
                else patches.Add((target.Offset + target.Length, 0, child));
                continue;
            }
            var attribute = Attribute.Match(tag);
            if (!edit.AfterExists)
            {
                var remainder = Attribute.Replace(tag, "");
                // Only remove the introduced empty override. Preserve any later, unrelated friction edits.
                if (!Regex.IsMatch(remainder, @"^<WheelFriction\s*/>$"))
                    throw new InvalidDataException("The added tire friction override has other properties. Reload before restoring: " + edit.Path);
                patches.Add((target.Offset, target.Length, ""));
            }
            else if (attribute.Success)
            {
                var match = edit.After is null ? (Group)attribute : attribute.Groups["value"];
                patches.Add((target.Offset + match.Index, match.Length, edit.After ?? ""));
            }
            else if (edit.After is not null)
                patches.Add((target.Offset + tag.Length - (tag.EndsWith("/>", StringComparison.Ordinal) ? 2 : 1), 0, " IsIgnoreIce=\"" + edit.After + "\""));
        }
        foreach (var patch in patches.OrderByDescending(p => p.Offset)) text = text.Remove(patch.Offset, patch.Length).Insert(patch.Offset, patch.Text);
        _ = Targets(text);
        return text;
    }
    internal static void Apply(string workspace)
    {
        var outputs = Load(workspace).GroupBy(e => e.RelativeFile, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var source = WorkspaceOriginals.GetCurrentPath(workspace, group.Key);
            return (Source: source, Text: Patch(File.ReadAllText(source), group.ToArray()));
        }).ToArray();
        foreach (var output in outputs)
        {
            var path = WorkspaceOriginals.PrepareForEdit(workspace, output.Source);
            TruckPropertiesStore.WriteAtomic(path, output.Text); WorkspaceDataCache.InvalidateFile(path);
        }
        Save(workspace, []);
    }
}
