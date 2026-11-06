using System.Text.Json;

namespace SnowPakTool.Desktop;

internal sealed class GroupState
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> TruckCodes { get; set; } = [];
}

internal static class CustomGroupStore
{
    private static readonly string StateFile = Path.Combine(
        Path.Combine(AppContext.BaseDirectory, "test-state"),
        "SnowPakTool.Desktop", "custom-truck-groups.json");
    private static readonly (string Name, string Description)[] DefaultGroups =
    [
        ("Scouting", "Explore routes and locate objectives."),
        ("Recovery", "Rescue stranded trucks and recover cargo."),
        ("Long-distance hauling", "Move cargo efficiently across long routes."),
        ("Heavy hauling", "Transport oversized and high-weight loads."),
        ("Offroad transport", "Carry cargo across rough terrain."),
        ("Utility", "Farming, fuel and other special equipment")
    ];

    public static List<GroupState> Load()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                var saved = JsonSerializer.Deserialize<List<GroupState>>(File.ReadAllText(StateFile));
                if (saved is not null)
                {
                    foreach (var state in saved)
                        state.TruckCodes ??= [];
                    return saved;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        return DefaultGroups.Select(group => new GroupState
        {
            Name = group.Name,
            Description = group.Description
        }).ToList();
    }

    public static void Save(IReadOnlyList<GroupState> groups)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        var temporaryFile = StateFile + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(groups, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryFile, StateFile, overwrite: true);
    }
}
