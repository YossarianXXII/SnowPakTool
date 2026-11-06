using System.Text.Json;
using System.Xml.Linq;

namespace SnowPakTool.Desktop;

internal sealed record TrailerEdit(string File, string Path, string Before, string After);
internal static class TrailerStore
{
    internal static string PendingPath(string w) => WorkspaceOriginals.GetMetadataPath(w, "trailers", "pending-changes.json");
    internal static bool IsTrailer(string file) => file.Replace('\\', '/').Contains("/classes/trucks/trailers/", StringComparison.OrdinalIgnoreCase)
        && file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    internal static IEnumerable<string> Files(string w) => WorkspaceOriginals.EnumerateCurrentFiles(w, "[media]", "*.xml").Where(IsTrailer);
    internal static XElement Parse(string text) => XElement.Parse("<Source>" + text + "</Source>", LoadOptions.PreserveWhitespace);
    internal static string PathOf(XElement node) => node.Parent?.Name == "Source" ? node.Name + "[1]"
        : PathOf(node.Parent!) + "/" + node.Name + "[" + (node.ElementsBeforeSelf(node.Name).Count() + 1) + "]";
    internal static Dictionary<string, XAttribute> Attributes(XElement root) => root.Descendants().SelectMany(e => e.Attributes())
        .Where(a => !a.Name.LocalName.StartsWith('_') && a.Name.LocalName != "Type")
        .ToDictionary(a => PathOf(a.Parent!) + "/@" + a.Name, a => a);
    internal static List<TrailerEdit> Load(string w) => File.Exists(PendingPath(w)) ? JsonSerializer.Deserialize<List<TrailerEdit>>(File.ReadAllText(PendingPath(w))) ?? [] : [];
    internal static void Save(string w, List<TrailerEdit> edits) => TruckPropertiesStore.WriteAtomic(PendingPath(w), JsonSerializer.Serialize(edits));
    internal static IEnumerable<WorkspaceChange> Changes(string w, IEnumerable<TrailerEdit> edits) => edits.Select(e => new WorkspaceChange(WorkspaceChange.Pending,
        WorkspaceOriginals.GetCurrentPath(w, e.File), e.Path, e.Before, e.After));
    internal static IEnumerable<WorkspaceChange> Pending(string w) => Changes(w, Load(w));
    internal static void Apply(string w)
    {
        var outputs = Load(w).GroupBy(e => e.File).Select(g =>
        {
            if (!IsTrailer(g.Key) || g.Key.Replace('\\', '/').Split('/').Any(p => p is ".." or ".") || g.Key.Contains(':')) throw new InvalidDataException("Invalid trailer path.");
            var source = WorkspaceOriginals.GetCurrentPath(w, g.Key);
            var root = Parse(File.ReadAllText(source)); var attributes = Attributes(root);
            foreach (var e in g)
            {
                if (!attributes.TryGetValue(e.Path, out var a) || a.Value != e.Before && a.Value != e.After)
                    throw new InvalidDataException("Trailer property changed since staging: " + e.File + " " + e.Path);
                a.Value = e.After;
            }
            return (source, text: string.Concat(root.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting))));
        }).ToArray();
        foreach (var output in outputs)
        {
            var path = WorkspaceOriginals.PrepareForEdit(w, output.source);
            TruckPropertiesStore.WriteAtomic(path, output.text); WorkspaceDataCache.InvalidateFile(path);
        }
        Save(w, []);
    }
}
