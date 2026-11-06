namespace SnowPakTool.Desktop;

internal enum ProgressionDifficulty { Easy = -1, Normal = 0, Hard = 1 }

internal sealed record ProgressionRegion(string Code, int? Difficulty);

internal static class RegionProgressionService
{
    public static int? TierDifficulty(string tier) => tier switch
    {
        "S" => 4, "A" => 3, "B" => 2, "C" => 1, "D" => 0, _ => null
    };

    public static List<string> Generate(IReadOnlyList<ProgressionRegion> regions, ProgressionDifficulty difficulty, int seed)
    {
        // Canonical ordering makes the result independent of ordering inside tier panes.
        var unique = regions.DistinctBy(region => region.Code, StringComparer.OrdinalIgnoreCase).ToList();
        var remaining = unique.Where(region => region.Difficulty.HasValue)
            .OrderBy(region => region.Code, StringComparer.OrdinalIgnoreCase).ToList();
        var result = new List<string>();
        var random = new Random(seed);
        var count = remaining.Count;
        if (count > 0)
        {
            var easiest = remaining.Min(region => region.Difficulty!.Value);
            var hardest = remaining.Max(region => region.Difficulty!.Value);
            void Take(ProgressionRegion region) { result.Add(region.Code); remaining.Remove(region); }
            var starters = remaining.Where(region => region.Difficulty == easiest).ToArray();
            Take(starters[random.Next(starters.Length)]);
            while (remaining.Count > 0)
            {
                var progress = (double)result.Count / (count - 1);
                var setting = Math.Clamp((int)difficulty, -1, 1);
                var target = easiest + (hardest - easiest) * Math.Pow(progress, Math.Pow(2, -setting));
                var spread = 0.8 + 0.25 * setting;
                var tiers = remaining.GroupBy(region => region.Difficulty!.Value).OrderBy(group => group.Key)
                    .Select(group => (Regions: group.ToArray(), Weight: Math.Exp(-Math.Pow(group.Key - target, 2) / (2 * spread * spread))))
                    .ToArray();
                var roll = random.NextDouble() * tiers.Sum(tier => tier.Weight);
                var chosen = tiers[^1];
                foreach (var tier in tiers)
                {
                    roll -= tier.Weight;
                    if (roll <= 0) { chosen = tier; break; }
                }
                Take(chosen.Regions[random.Next(chosen.Regions.Length)]);
            }
        }
        result.AddRange(unique.Where(region => !region.Difficulty.HasValue).Select(region => region.Code));
        return result;
    }

    // Never move an existing placement when regions or their tier assignments change.
    public static List<string> AppendNew(IEnumerable<string> saved, IEnumerable<string> available) =>
        saved.Concat(available).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static List<string> Move(IReadOnlyList<string> saved, IReadOnlyList<string> visible, string code, int insertionIndex)
    {
        var reordered = visible.Where(item => !item.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();
        reordered.Insert(Math.Clamp(insertionIndex, 0, reordered.Count), code);
        var visibleSet = visible.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = 0;
        // Hidden regions keep their saved positions when editing another workspace's visible subset.
        return saved.Select(item => visibleSet.Contains(item) ? reordered[next++] : item).ToList();
    }
}
