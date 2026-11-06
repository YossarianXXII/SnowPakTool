using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace SnowPakTool.Desktop;

internal static class TruckThumbnailProvider
{
    private static readonly Assembly AppAssembly = Assembly.GetExecutingAssembly();
    private static readonly Lazy<Dictionary<string, string>> Aliases = new(LoadAliases);
    private static readonly Lazy<Image> Placeholder = new(LoadPlaceholder);
    private static readonly ConcurrentDictionary<string, Lazy<Task<Image>>> Images = new(StringComparer.OrdinalIgnoreCase);

    public static Image PlaceholderImage => Placeholder.Value;

    public static Task<Image> LoadAsync(CustomTruck truck)
    {
        var nameSlug = Normalize(truck.Name);
        var candidates = new List<string>();
        if (Aliases.Value.TryGetValue(nameSlug, out var alias))
            candidates.Add(alias);
        candidates.Add(nameSlug);
        candidates.Add(Normalize(truck.Code));

        var key = string.Join("|", candidates.Distinct(StringComparer.OrdinalIgnoreCase));
        return Images.GetOrAdd(key, _ => new Lazy<Task<Image>>(() => Task.Run(() => LoadCore(candidates))))
            .Value;
    }

    private static Image LoadCore(IEnumerable<string> candidates)
    {
        foreach (var slug in candidates)
        {
            using var stream = AppAssembly.GetManifestResourceStream($"SnowPakTool.Desktop.Assets.Trucks.{slug}.png");
            if (stream is null)
                continue;
            using var source = Image.FromStream(stream);
            return new Bitmap(source);
        }

        return Placeholder.Value;
    }

    private static Dictionary<string, string> LoadAliases()
    {
        using var stream = AppAssembly.GetManifestResourceStream("SnowPakTool.Desktop.Assets.Trucks.aliases.json");
        if (stream is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
               ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static Image LoadPlaceholder()
    {
        using var stream = AppAssembly.GetManifestResourceStream("SnowPakTool.Desktop.Assets.aac-58dw.png")
            ?? throw new InvalidOperationException("The AAC-58DW tile image is missing from the application resources.");
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}
