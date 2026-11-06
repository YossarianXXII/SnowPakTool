namespace SnowPakTool.Desktop;

/// <summary>Application classifications; game TruckType attributes remain untouched.</summary>
internal static class TruckClasses
{
    public const string Logging = "Logging";
    public const string FarmingTractor = "Farming Tractor";
    private static readonly HashSet<string> FarmingTractors = new(StringComparer.OrdinalIgnoreCase)
    {
        "ankatra_1160", "futom_7290ra", "kirovets_k700", "kirovets_k7m"
    };

    public static string ForTruck(string code, string gameClass) =>
        code.Equals("aramatsu_forester", StringComparison.OrdinalIgnoreCase) ? Logging :
        FarmingTractors.Contains(code) ? FarmingTractor : gameClass;
}
