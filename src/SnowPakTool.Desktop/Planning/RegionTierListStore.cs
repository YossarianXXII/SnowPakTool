using System.Text.Json;

namespace SnowPakTool.Desktop;

/// <summary>Region rankings are independent of truck tiers and custom truck groups.</summary>
internal static class RegionTierListStore
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnowPakTool.Desktop", "region-tiers.json");
    private static readonly string[] Names = ["S", "A", "B", "C", "D"];

    // Regions run from hardest/red to easiest/blue; truck quality tiers keep their own colors.
    public static Color ColorFor(string name) => name switch
    {
        "S" => AppColors.Get("TierFX"),
        "A" => AppColors.Get("TierD"),
        "B" => AppColors.Get("TierC"),
        "C" => AppColors.Get("TierA"),
        "D" => AppColors.Get("TierS"),
        _ => Color.FromArgb(215, 220, 225)
    };

    public static Color BackgroundFor(string name)
    {
        var color = ColorFor(name);
        if (!Names.Contains(name)) return color;
        var surface = AppColors.Get("Surface");
        return Color.FromArgb((surface.R * 4 + color.R) / 5,
            (surface.G * 4 + color.G) / 5, (surface.B * 4 + color.B) / 5);
    }

    private sealed class RegionTierState
    {
        public string Name { get; set; } = string.Empty;
        public List<string> RegionCodes { get; set; } = [];
    }

    public static List<GroupState> Load()
    {
        List<RegionTierState>? saved = null;
        try
        {
            if (File.Exists(StateFile))
                saved = JsonSerializer.Deserialize<List<RegionTierState>>(File.ReadAllText(StateFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        // The shared tile board uses TruckCodes internally for both kinds of tile.
        return Names.Select(name => new GroupState
        {
            Name = name,
            TruckCodes = saved?.FirstOrDefault(tier => tier.Name == name)?.RegionCodes ?? []
        }).ToList();
    }

    public static void Save(IReadOnlyList<GroupState> tiers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        var temporaryFile = StateFile + ".tmp";
        var saved = Names.Select(name => new RegionTierState
        {
            Name = name,
            RegionCodes = tiers.FirstOrDefault(tier => tier.Name == name)?.TruckCodes ?? []
        });
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryFile, StateFile, overwrite: true);
    }
}
