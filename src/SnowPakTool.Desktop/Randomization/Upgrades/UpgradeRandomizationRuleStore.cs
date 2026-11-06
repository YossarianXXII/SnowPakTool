using System.Text.Json;

namespace SnowPakTool.Desktop;

internal sealed class UpgradeRandomizationRules
{
    public List<string> Rules { get; set; } =
    [
        "Keep every pickup's original upgrade type. Keep metal detectors at their original locations.",
        "For truck-specific slots, prefer unused upgrades compatible with currently obtainable trucks in that region, including direct garage rewards.",
        "For shared slots, choose from all eligible shared upgrades of the same type. There is no purchase-only restriction and no locality percentage roll.",
        "Try unused shared and other same-type upgrades before reusing a compatible choice. Stock/default parts, unavailable content and objective-locked replacement parts are excluded.",
        "In the first progression region, shared slots get an extra preference for upgrades with no compatible obtainable truck in that region.",
        "Randomize owned regions using enabled content. Stage pickups and exploration flags, then apply from Start → Apply pending changes. Upgrades found on any resulting map require exploration; upgrades absent from maps do not."
    ];
}

internal sealed class UpgradeRandomizationRuleStore
{
    private readonly string _path;

    public UpgradeRandomizationRuleStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnowPakTool.Desktop", "upgrade-randomization-rules.json");

    public UpgradeRandomizationRules Load()
    {
        if (!File.Exists(_path)) return new();
        var rules = JsonSerializer.Deserialize<UpgradeRandomizationRules>(File.ReadAllText(_path))
            ?? throw new JsonException("The saved rule list is empty.");
        Validate(rules);
        for (var i = 0; i < rules.Rules.Count; i++)
            if (rules.Rules[i] is "Randomize owned regions using enabled content. Review the saved preview in Map upgrades; game-file staging and applying are disabled for this trial."
                or "Randomize owned regions using enabled content. Stage pickups and exploration flags, then apply from Start → PAK. Upgrades found on any resulting map require exploration; upgrades absent from maps do not.")
                rules.Rules[i] = new UpgradeRandomizationRules().Rules[^1];
        return rules;
    }

    public void Save(UpgradeRandomizationRules rules)
    {
        Validate(rules);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, overwrite: true);
    }

    private static void Validate(UpgradeRandomizationRules rules)
    {
        if (rules.Rules is null || rules.Rules.Any(string.IsNullOrWhiteSpace))
            throw new JsonException("Rules must be a list of non-empty notes.");
    }
}
