using System.Text.Json;

namespace SnowPakTool.Desktop;

internal static class TierListStore
{
    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnowPakTool.Desktop", "truck-tiers.json");
    private static readonly string[] Names = ["SS", "S", "A", "B", "C", "D", "FX"];

    public static Color ColorFor(string name) => AppColors.Get($"Tier{name}");
    public static Color BackgroundFor(string name)
    {
        var color = ColorFor(name);
        var background = AppColors.Get("Surface");
        return Color.FromArgb((background.R * 4 + color.R) / 5, (background.G * 4 + color.G) / 5, (background.B * 4 + color.B) / 5);
    }

    public static string DescriptionFor(string name) => name switch
    {
        "SS" => "Completely overpowered",
        "S" => "Excellent picks, fleet mainstays",
        "A" => "Great in most areas, all-rounders and brutes",
        "B" => "Good in most areas or as early trucks",
        "C" => "Can be decent but will lag behind quickly",
        "D" => "There are better options",
        "FX" => "Screw these. Very circumstantial. Use only if you like torture.",
        _ => string.Empty
    };

    public static List<GroupState> Load()
    {
        List<GroupState>? saved = null;
        try
        {
            if (File.Exists(StateFile))
                saved = JsonSerializer.Deserialize<List<GroupState>>(File.ReadAllText(StateFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return Names.Select(name => new GroupState
        {
            Name = name,
            Description = DescriptionFor(name),
            TruckCodes = saved?.FirstOrDefault(tier => tier.Name == name)?.TruckCodes ?? []
        }).ToList();
    }

    public static void Save(IReadOnlyList<GroupState> tiers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        var temporaryFile = StateFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(tiers, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryFile, StateFile, overwrite: true);
    }
}
