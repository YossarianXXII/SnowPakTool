using System.Text.Json;

namespace SnowPakTool.Desktop;

internal sealed record TruckUnlockChoice(string RelativeFile, int Rank, bool Starter);
internal sealed record UnlockStarterContext(string RegionCode, string RegionName, string Country, string Note = "");
internal sealed record TruckUnlockReportRow(string Code, string Name, string GameClass, string Tier, decimal? Before,
    decimal? After, decimal? InFiles, decimal? Original, string Reason)
{
    public string State => After is null ? "Skipped" : Before == After ? "Unchanged" : "Changed";
}
internal sealed record TruckUnlockPlan(List<TruckUnlockChoice> Choices, int SoftCap, int Unranked, List<string> MissingCoverage,
    UnlockStarterContext StarterContext, List<TruckUnlockReportRow> Report, int Seed);

internal static class TruckUnlockRankGenerator
{
    internal static readonly string[] GameClasses = ["HIGHWAY", "HEAVY_DUTY", "OFFROAD", "HEAVY", "SCOUT"];
    internal static int Quality(string tier) => tier switch { "FX" => 0, "D" => 1, "C" => 2, "B" => 3, "A" => 4, "S" => 5, "SS" => 6, _ => -1 };
    internal static int SoftCap(ProgressionDifficulty difficulty) => difficulty switch
    {
        ProgressionDifficulty.Easy => 15, ProgressionDifficulty.Normal => 20, ProgressionDifficulty.Hard => 25,
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty))
    };
    internal static string[] Countries(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(code => code.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static TruckUnlockPlan Generate(IReadOnlyList<TruckPropertyRow> rows, IReadOnlyList<GroupState> tiers,
        IReadOnlySet<string> enabledCodes, ProgressionDifficulty difficulty, int seed, UnlockStarterContext? starterContext = null)
    {
        var cap = SoftCap(difficulty);
        var qualities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tier in tiers)
            foreach (var code in tier.TruckCodes)
                if (Quality(tier.Name) is var quality && quality >= 0) qualities.TryAdd(code, quality);
        var tierNames = tiers.SelectMany(tier => tier.TruckCodes.Select(code => (code, tier.Name))).GroupBy(pair => pair.code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
        starterContext ??= new("", "", "", "No first region was supplied; starter coverage skipped.");
        var candidates = rows.Where(row => enabledCodes.Contains(row.Truck.Code) && row.Draft.ContainsKey("UnlockByRank")
            && row.Current.ContainsKey("Price") && row.Draft.GetValueOrDefault("UnlockByExploration", row.RequiresExploration ? 1 : 0) == 0 && row.UnlockObjective.Length == 0 && !row.Locked
            && row.Country.Length > 0 && GameClasses.Contains(row.GameClass.ToUpperInvariant())).ToArray();
        var random = new Random(seed);
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var availableCountries = rows.ToDictionary(row => row.RelativeFile, row => Countries(row.Country), StringComparer.OrdinalIgnoreCase);
        var reasons = rows.ToDictionary(row => row.RelativeFile, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var unranked = 0;
        foreach (var row in rows.OrderBy(row => row.RelativeFile, StringComparer.OrdinalIgnoreCase))
        {
            if (!row.Draft.TryGetValue("UnlockByRank", out var current))
            {
                reasons[row.RelativeFile].Add("UnlockByRank is missing/inherited, so no rank was changed."); continue;
            }
            if (!qualities.TryGetValue(row.Truck.Code, out var quality))
            {
                ranks[row.RelativeFile] = (int)current; unranked++;
                reasons[row.RelativeFile].Add("No assigned tier; kept the existing preview rank."); continue;
            }
            var fraction = quality / 6.0;
            var exponent = difficulty switch { ProgressionDifficulty.Easy => 1.15, ProgressionDifficulty.Hard => 0.85, _ => 1.0 };
            var spread = difficulty switch { ProgressionDifficulty.Easy => 8.0, ProgressionDifficulty.Hard => 4.0, _ => 5.5 };
            var target = 1 + (cap - 1) * Math.Pow(fraction, exponent);
            var variation = (random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * spread * (0.5 + fraction * 0.5);
            // Easy's broad spread chiefly rewards early access, with a smaller late tail around its soft cap.
            if (difficulty == ProgressionDifficulty.Easy && variation > 0) variation *= 0.5;
            ranks[row.RelativeFile] = Math.Clamp((int)Math.Round(target + variation, MidpointRounding.AwayFromZero), 1, 30);
            reasons[row.RelativeFile].Add($"Tier {tierNames[row.Truck.Code]} / {difficulty} independent roll"
                + (ranks[row.RelativeFile] == current ? " returned the same rank." : "."));
        }
        var starters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        if (starterContext.Country.Length == 0)
            missing.Add("Starter coverage skipped: " + starterContext.Note);
        else foreach (var gameClass in GameClasses)
        {
            var limit = gameClass is "SCOUT" or "OFFROAD" ? 1 : 3;
            var pool = candidates.Where(row => row.GameClass.Equals(gameClass, StringComparison.OrdinalIgnoreCase)
                    && availableCountries[row.RelativeFile].Contains(starterContext.Country, StringComparer.OrdinalIgnoreCase))
                .OrderBy(row => qualities.GetValueOrDefault(row.Truck.Code, int.MaxValue))
                .ThenBy(row => row.Draft.GetValueOrDefault("Price", decimal.MaxValue))
                .ThenBy(row => row.Truck.Code, StringComparer.OrdinalIgnoreCase).ToArray();
            var starter = pool.FirstOrDefault();
            if (starter is null)
            {
                missing.Add($"{starterContext.Country} / {gameClass} by rank {limit}: no owned, directly purchasable candidate."); continue;
            }
            var starterRank = limit == 1 ? 1 : random.Next(1, 4);
            var previousRoll = ranks[starter.RelativeFile];
            ranks[starter.RelativeFile] = starterRank; starters.Add(starter.RelativeFile);
            reasons[starter.RelativeFile].Add($"Selected {gameClass} starter for {starterContext.RegionName} ({starterContext.Country}), required by rank {limit}; "
                + (previousRoll == starterRank ? $"rank {starterRank} already matched." : $"adjusted rolled rank {previousRoll} to {starterRank}."));
        }
        var report = rows.OrderBy(row => row.Truck.Name, StringComparer.CurrentCultureIgnoreCase).Select(row =>
        {
            var exclusions = new List<string>();
            if (!enabledCodes.Contains(row.Truck.Code)) exclusions.Add("not owned/enabled");
            if (row.Draft.GetValueOrDefault("UnlockByExploration", row.RequiresExploration ? 1 : 0) == 1) exclusions.Add("UnlockByExploration=true: discover it before buying; changing rank does not clear this lock");
            if (row.UnlockObjective.Length > 0) exclusions.Add("requires objective " + row.UnlockObjective);
            if (row.Locked) exclusions.Add("locked unconditionally");
            if (!row.Current.ContainsKey("Price")) exclusions.Add("no explicit store price");
            if (row.Country.Length == 0) exclusions.Add("no explicit store country");
            else if (starterContext.Country.Length > 0 && !availableCountries[row.RelativeFile].Contains(starterContext.Country, StringComparer.OrdinalIgnoreCase))
                exclusions.Add($"not sold in the first region's store country {starterContext.Country} (sold in {row.Country})");
            if (!GameClasses.Contains(row.GameClass.ToUpperInvariant())) exclusions.Add("original game class is unknown/unsupported");
            if (exclusions.Count > 0) reasons[row.RelativeFile].Add("Not eligible as a starter: " + string.Join("; ", exclusions) + ".");
            return new TruckUnlockReportRow(row.Truck.Code, row.Truck.Name, row.GameClass, tierNames.GetValueOrDefault(row.Truck.Code, "Unsorted"),
                row.Draft.TryGetValue("UnlockByRank", out var before) ? before : null,
                ranks.TryGetValue(row.RelativeFile, out var after) ? after : null,
                row.Current.TryGetValue("UnlockByRank", out var inFiles) ? inFiles : null,
                row.Original.TryGetValue("UnlockByRank", out var original) ? original : null, string.Join(" ", reasons[row.RelativeFile]));
        }).ToList();
        return new(rows.Where(row => ranks.ContainsKey(row.RelativeFile)).Select(row => new TruckUnlockChoice(row.RelativeFile,
            ranks[row.RelativeFile], starters.Contains(row.RelativeFile))).ToList(), cap, unranked, missing, starterContext, report, seed);
    }

    internal static UnlockStarterContext ResolveFirstRegion(string workspace, RegionProgressionState progression)
    {
        if (progression.Order.Count == 0) return new("", "", "", "Choose a first region in Region progression before checking starter availability.");
        var code = progression.Order[0];
        var path = WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[ps]", "zone_desc_storage.sso"));
        if (!File.Exists(path)) return new(code, code, "", "Region/store definitions are missing from this workspace.");
        var root = SsoDataReader.Parse(File.ReadAllText(path));
        var region = root.Members.GetValueOrDefault("regions")?.Items.FirstOrDefault(item => item.Get("name").Equals(code, StringComparison.OrdinalIgnoreCase));
        if (region is null) return new(code, code, "", "The first region was not found in this workspace's region definitions.");
        var country = region.Get("country").ToUpperInvariant();
        // These built-in Russian regions omit the game's default RU value in the saved SSO.
        if (country.Length == 0 && new[] { "ru_02", "ru_03", "ru_05", "ru_06" }.Contains(code, StringComparer.OrdinalIgnoreCase)) country = "RU";
        var nameKey = region.Members.GetValueOrDefault("uiInfo")?.Get("uiName") ?? "";
        var name = WorkspaceDataCache.GetLocalizedNames(workspace).GetValueOrDefault(nameKey, code);
        return new(code, name, country, country.Length == 0 ? "The first region has no resolvable store country; starter coverage skipped."
            : progression.IsFinalized ? "Using finalized region progression." : "Using the current saved region order (not finalized).");
    }
}

internal static class TruckUnlockRankSettings
{
    private static string PathFor(string workspace) => WorkspaceOriginals.GetMetadataPath(workspace, "truck-properties", "unlock-rank-settings.json");
    internal static ProgressionDifficulty Load(string workspace)
    {
        var path = PathFor(workspace);
        if (!File.Exists(path)) return ProgressionDifficulty.Normal;
        var value = JsonSerializer.Deserialize<ProgressionDifficulty>(File.ReadAllText(path));
        if (!Enum.IsDefined(value)) throw new InvalidDataException("Invalid unlock-rank difficulty setting.");
        return value;
    }
    internal static void Save(string workspace, ProgressionDifficulty value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
        TruckPropertiesStore.WriteAtomic(PathFor(workspace), JsonSerializer.Serialize(value));
    }
}
