using System.Text.Json;

namespace SnowPakTool.Desktop;

internal sealed class TruckRandomizationRules
{
    public List<string> Obtainable { get; set; } = [.. TruckRandomizationTab.DefaultObtainableRules];
    public List<string> Missions { get; set; } = [.. TruckRandomizationTab.DefaultMissionRules];
    public bool MissionRulesInitialized { get; set; }
    public MissionTruckDifficulty MissionDifficulty { get; set; } = MissionTruckDifficulty.Moderate;
}

internal sealed class TruckRandomizationRuleStore
{
    private readonly string _path;

    public TruckRandomizationRuleStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnowPakTool.Desktop", "truck-randomization-rules.json");

    public TruckRandomizationRules Load()
    {
        if (!File.Exists(_path)) return new() { MissionRulesInitialized = true };
        var rules = JsonSerializer.Deserialize<TruckRandomizationRules>(File.ReadAllText(_path))
            ?? throw new JsonException("The saved rule list is empty.");
        Validate(rules);
        const string previousTireRule = "Choose tires using the player-defined region difficulty tier list: Highway in easier regions, then Allterrain, Offroad, and Mudtires in the hardest regions, where compatible. Allow some variation and use the first (smallest) tires in the chosen group.";
        var tireRuleIndex = rules.Obtainable.IndexOf(previousTireRule);
        if (tireRuleIndex >= 0) rules.Obtainable[tireRuleIndex] = TruckRandomizationTab.DefaultObtainableRules[4];
        const string previousProgressionTireRule = "Roll tires separately for each truck using finalized region progression, region difficulty tier and the separate tire difficulty. The first region requests 99% Highway / 1% All-terrain; better tires become more likely later. Hard still allows 30% Mudtires in the final S-tier region. Use the nearest compatible category, smallest size and first variant.";
        tireRuleIndex = rules.Obtainable.IndexOf(previousProgressionTireRule);
        if (tireRuleIndex >= 0) rules.Obtainable[tireRuleIndex] = TruckRandomizationTab.DefaultObtainableRules[4];
        if (!rules.MissionRulesInitialized)
        {
            if (rules.Missions.Count == 0) rules.Missions.AddRange(TruckRandomizationTab.DefaultMissionRules);
            rules.MissionRulesInitialized = true;
            Save(rules);
        }
        var missionNotesChanged = false;
        var missionRule3 = rules.Missions.IndexOf("Keep a mixture of light and heavy vehicles based on actual vehicle physics mass: Easy favors lighter vehicles, Moderate is balanced, and Hard favors heavier vehicles. All weights remain possible in every mode.");
        if (missionRule3 >= 0) { rules.Missions[missionRule3] = TruckRandomizationTab.DefaultMissionRules[3]; missionNotesChanged = true; }
        var missionRule4 = rules.Missions.IndexOf("Prefer different models within each region. Use models not yet represented before repeating, then choose among the least-used models while retaining the selected weight bias.");
        if (missionRule4 >= 0) { rules.Missions[missionRule4] = TruckRandomizationTab.DefaultMissionRules[4]; missionNotesChanged = true; }
        if (missionNotesChanged) Save(rules);
        return rules;
    }

    public void Save(TruckRandomizationRules rules)
    {
        Validate(rules);
        rules.MissionRulesInitialized = true;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporaryFile = _path + ".tmp";
        File.WriteAllText(temporaryFile, JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryFile, _path, overwrite: true);
    }

    private static void Validate(TruckRandomizationRules rules)
    {
        if (!Enum.IsDefined(rules.MissionDifficulty) || rules.Obtainable is null || rules.Missions is null ||
            rules.Obtainable.Concat(rules.Missions).Any(string.IsNullOrWhiteSpace))
            throw new JsonException("Each pane must contain a list of non-empty rules.");
    }
}
