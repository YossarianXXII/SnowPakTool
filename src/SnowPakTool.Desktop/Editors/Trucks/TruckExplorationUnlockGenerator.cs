namespace SnowPakTool.Desktop;

internal sealed record TruckExplorationUnlockPlan(Dictionary<string, decimal> Values, List<string> Log);

internal static class TruckExplorationUnlockGenerator
{
    internal static TruckExplorationUnlockPlan Generate(IReadOnlyList<TruckPropertyRow> rows, RegionTruckCatalog catalog)
    {
        var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var log = new List<string> { "EXPLORATION UNLOCK UPDATE — current obtainable map placements, including randomized/pending replacements; mission-only placements do not count." };
        var obtainable = catalog.Entries.Where(entry => entry.Availability.StartsWith("Obtainable", StringComparison.Ordinal))
            .Select(entry => entry.TruckCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uncertain = catalog.Entries.Where(entry => entry.Availability == "Unverified")
            .Select(entry => entry.TruckCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        log.Add($"{catalog.MapsRead} maps read; {catalog.Warnings.Count} read warnings. Stage map changes as well when using replacement previews.");
        foreach (var warning in catalog.Warnings) log.Add("MAP WARNING: " + warning);
        foreach (var row in rows.OrderBy(row => row.Truck.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var before = row.Draft.GetValueOrDefault("UnlockByExploration", row.RequiresExploration ? 1 : 0);
            var onMap = obtainable.Contains(row.Truck.Code);
            string? skip = catalog.MapsRead == 0 ? "no maps were read"
                : !onMap && catalog.Warnings.Count > 0 ? "map read warnings prevent confirming absence"
                : !onMap && uncertain.Contains(row.Truck.Code) ? "map placement obtainability is unverified" : null;
            if (skip is not null)
            {
                log.Add($"SKIPPED | {row.Truck.Name} [{row.Truck.Code}] | UnlockByExploration stays {Flag(before)}: {skip}.");
                continue;
            }
            var after = onMap ? 1m : 0m;
            values[row.RelativeFile] = after;
            log.Add($"{(before == after ? "UNCHANGED" : "CHANGED")} | {row.Truck.Name} [{row.Truck.Code}] | UnlockByExploration {Flag(before)} → {Flag(after)} | "
                + (onMap ? "obtainable on a current map." : "no obtainable placement in the scanned maps."));
        }
        return new(values, log);
    }

    private static string Flag(decimal value) => value == 1 ? "true" : "false";
}
