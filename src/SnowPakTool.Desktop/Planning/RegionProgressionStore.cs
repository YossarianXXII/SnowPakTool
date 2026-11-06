using System.Text.Json;

namespace SnowPakTool.Desktop;

internal sealed class RegionProgressionState
{
    public List<string> Order { get; set; } = [];
    public ProgressionDifficulty Difficulty { get; set; } = ProgressionDifficulty.Normal;
    public bool HasRandomized { get; set; }
    public bool IsFinalized { get; set; }
    public int? Seed { get; set; }
}

internal sealed class RegionProgressionStore
{
    private readonly string _path;
    public RegionProgressionStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnowPakTool.Desktop", "region-progression.json");

    public RegionProgressionState Load()
    {
        if (!File.Exists(_path)) return new();
        var state = JsonSerializer.Deserialize<RegionProgressionState>(File.ReadAllText(_path))
            ?? throw new JsonException("The saved progression is empty.");
        Validate(state);
        return state;
    }

    public void Save(RegionProgressionState state)
    {
        Validate(state);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, overwrite: true);
    }

    private static void Validate(RegionProgressionState state)
    {
        if (state.Order is null || state.Order.Any(string.IsNullOrWhiteSpace) ||
            state.Order.Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Order.Count ||
            !Enum.IsDefined(state.Difficulty))
            throw new JsonException("The saved region progression is invalid.");
    }
}
