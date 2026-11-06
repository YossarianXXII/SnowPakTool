using System.Text.Json;

namespace SnowPakTool.Desktop;

internal static class AppColors
{
    internal sealed record ColorEntry(string Key, string Name, string Usage, Color DefaultColor);
    private const int SettingsVersion = 3;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnowPakTool.Desktop", "colors.json");
    private static readonly Dictionary<string, ColorEntry> Entries = CreateEntries();
    private static readonly Dictionary<string, Color> Overrides = LoadOverrides();

    public static event EventHandler? Changed;

    public static Color Get(string key)
    {
        key = NormalizeKey(key);
        if (Overrides.TryGetValue(key, out var color)) return color;
        if (Entries.TryGetValue(key, out var entry)) return entry.DefaultColor;
        throw new KeyNotFoundException(key);
    }

    public static IReadOnlyList<ColorEntry> GetEntries() => Entries.Values.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    public static void Set(string key, Color color)
    {
        key = NormalizeKey(key);
        if (!Entries.ContainsKey(key)) return;
        Overrides[key] = Color.FromArgb(255, color.R, color.G, color.B);
        SaveOverrides();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static Dictionary<string, ColorEntry> CreateEntries() => new(StringComparer.Ordinal)
    {
        ["TierSS"] = new("TierSS", "SS — Cyan", "Tier SS background and header", Color.FromArgb(0, 210, 225)),
        ["TierS"] = new("TierS", "S — Blue", "Tier S background and header", Color.FromArgb(55, 117, 173)),
        ["TierA"] = new("TierA", "A — Green", "Tier A background and header", Color.FromArgb(45, 180, 75)),
        ["TierB"] = new("TierB", "B — Lime", "Tier B background and header", Color.FromArgb(180, 220, 115)),
        ["TierC"] = new("TierC", "C — Yellow", "Tier C background and header", Color.FromArgb(255, 220, 65)),
        ["TierD"] = new("TierD", "D — Orange", "Tier D background and header", Color.FromArgb(220, 125, 40)),
        ["TierFX"] = new("TierFX", "FX — Red", "Tier FX background and header", Color.FromArgb(225, 65, 65)),
        ["TruckClassHeavy"] = new("TruckClassHeavy", "Heavy", "Truck tile title and class background", Color.FromArgb(38, 38, 38)),
        ["TruckClassHeavyDuty"] = new("TruckClassHeavyDuty", "Heavy-duty", "Truck tile title and class background", Color.FromArgb(78, 82, 86)),
        ["TruckClassOffroad"] = new("TruckClassOffroad", "Offroad", "Truck tile title and class background", Color.FromArgb(66, 105, 74)),
        ["TruckClassScout"] = new("TruckClassScout", "Scout", "Truck tile title and class background", Color.FromArgb(61, 88, 125)),
        ["TruckClassHighway"] = new("TruckClassHighway", "Highway", "Truck tile title and class background", Color.FromArgb(132, 115, 48)),
        ["Surface"] = new("Surface", "Surface", "Table and graph backgrounds", Color.White),
        ["TextArea"] = new("TextArea", "Text areas", "Logs, details and randomization rule backgrounds", Color.FromArgb(248, 248, 248)),
        ["AlternateRow"] = new("AlternateRow", "Alternate row", "Alternating table rows", Color.FromArgb(248, 248, 248)),
        ["SelectionBlue"] = new("SelectionBlue", "Selected cell", "Selected compatibility cells", Color.FromArgb(205, 226, 247)),
        ["SearchActive"] = new("SearchActive", "Active search result", "Currently targeted search match", Color.FromArgb(255, 160, 65)),
        ["EngineMatch"] = new("EngineMatch", "Engine compatibility match", "Truck rows matching the selected engine class", Color.FromArgb(255, 241, 168)),
        ["GroupRow"] = new("GroupRow", "Group row", "Engine and truck group rows", Color.FromArgb(225, 233, 241)),
        ["CompatibilitySeparator"] = new("CompatibilitySeparator", "Compatibility separator", "Section separator in compatibility matrices", Color.FromArgb(115, 125, 135)),
        ["DAF8E0"] = new("DAF8E0", "DAF8E0", "Added text; Base game owned and region; selected conflict choices; increased and selected values", Color.FromArgb(218, 248, 224)),
        ["FFDCDC"] = new("FFDCDC", "FFDCDC", "Removed text; decreased and removed values", Color.FromArgb(255, 220, 220)),
        ["GraphAxis"] = new("GraphAxis", "Graph axis", "Experience graph axes", Color.FromArgb(180, 180, 180)),
        ["GraphPreviewAxis"] = new("GraphPreviewAxis", "Curve preview axis", "XP curve preview axes", Color.FromArgb(170, 170, 170)),
        ["GraphCurve"] = new("GraphCurve", "Curve preview line", "XP curve preview", Color.FromArgb(55, 85, 115)),
        ["GraphBorder"] = new("GraphBorder", "Graph border", "XP curve preview border", Color.FromArgb(80, 80, 80)),
        ["GraphGridMajor"] = new("GraphGridMajor", "Graph major grid", "Major experience graph grid lines", Color.FromArgb(160, 160, 160)),
        ["GraphGridMinor"] = new("GraphGridMinor", "Graph minor grid", "Minor experience graph grid lines", Color.FromArgb(225, 225, 225)),
        ["GraphOriginal"] = new("GraphOriginal", "Graph original values", "Original experience progression curve", Color.FromArgb(55, 117, 173)),
        ["GraphModified"] = new("GraphModified", "Graph modified values", "Modified experience progression curve", Color.FromArgb(220, 125, 40)),
        ["WarningText"] = new("WarningText", "Warning text", "Unlocalized truck warnings", Color.Red)
    };

    private static Dictionary<string, Color> LoadOverrides()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new(StringComparer.Ordinal);
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = document.RootElement;
            var hasVersion = root.TryGetProperty("Version", out var version);
            var formatVersion = hasVersion ? version.GetInt32() : 0;
            var hasColors = root.TryGetProperty("Colors", out var colors);
            var isCurrentFormat = formatVersion >= SettingsVersion && hasColors;
            var values = hasColors
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(colors.GetRawText())
                : JsonSerializer.Deserialize<Dictionary<string, string>>(root.GetRawText());
            if (values is null) return new(StringComparer.Ordinal);
            var result = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (var (key, value) in values)
            {
                var normalized = NormalizeKey(key);
                if (!Entries.ContainsKey(normalized)) continue;
                // Older settings stored these roles independently. Apply the unified requested defaults once.
                if (formatVersion < SettingsVersion && normalized is "DAF8E0" or "FFDCDC") continue;
                if (key != normalized) continue;
                result[normalized] = ColorTranslator.FromHtml(value);
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static void SaveOverrides()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var colors = Overrides.ToDictionary(pair => pair.Key, pair => ColorTranslator.ToHtml(pair.Value));
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { Version = SettingsVersion, Colors = colors }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string NormalizeKey(string key)
    {
        return key switch
        {
            "BaseGameOwned" or "BaseGameRegion" or "ConflictSelected" or "ConflictSelectedFocus" or "ConflictNewText" or "ChangedPositive" => "DAF8E0",
            "ConflictOldText" or "ChangedNegative" => "FFDCDC",
            _ => key
        };
    }
}
