using System.Collections.Concurrent;

namespace SnowPakTool.Desktop;

internal static class RegionTileAssets
{
    // Base-game regions, then the regions introduced in seasons 1–19.
    // Region IDs match dlc_library.sso; chronology follows the original releases,
    // including seasons 1–3 that predate SnowRunner's Steam launch.
    private static readonly string[] ReleaseOrder =
    [
        "us_01", "us_02", "ru_02", "ru_03", "us_04", "us_03", "ru_04", "ru_05",
        "us_06", "us_07", "ru_08", "us_09", "us_10", "us_11", "us_12", "ru_13",
        "us_14", "us_15", "us_16", "ru_17", "us_18", "us_19"
    ];
    private static readonly ConcurrentDictionary<string, Lazy<Image?>> Images = new(StringComparer.OrdinalIgnoreCase);

    public static int GetReleaseOrder(string code)
    {
        var index = Array.FindIndex(ReleaseOrder, item => item.Equals(code, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    public static string GetReleaseLabel(string code)
    {
        var order = GetReleaseOrder(code);
        return order == int.MaxValue ? "Region" : order < 3 ? "Base game" : $"Season {order - 2}";
    }

    public static string GetMapNames(string code, IReadOnlyDictionary<string, string> names)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            @"^(?:LEVEL_)?(?<map>" + System.Text.RegularExpressions.Regex.Escape(code) + @"_\d{2})(?:_NEW)?_NAME$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return string.Join(Environment.NewLine, names
            .Select(pair => (Match: pattern.Match(pair.Key), pair.Value))
            .Where(item => item.Match.Success && !string.IsNullOrWhiteSpace(item.Value))
            .GroupBy(item => item.Match.Groups["map"].Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Value));
    }

    public static Image? GetThumbnail(string code) => Images.GetOrAdd(code, key => new Lazy<Image?>(() =>
    {
        using var stream = typeof(RegionTileAssets).Assembly.GetManifestResourceStream($"SnowPakTool.Desktop.Assets.Regions.{key.ToLowerInvariant()}.png");
        if (stream is null) return null;
        using var source = Image.FromStream(stream);
        return new Bitmap(source);
    })).Value;
}
