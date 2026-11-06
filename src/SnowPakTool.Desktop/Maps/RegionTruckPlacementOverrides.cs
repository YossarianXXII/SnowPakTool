namespace SnowPakTool.Desktop;

/// <summary>Confirmed ownership facts that incomplete task settings cannot establish.</summary>
internal static class RegionTruckPlacementOverrides
{
    // Confirmed by the user on 2026-11-01: these 17 previously unverified
    // placements are mission/task-only and are not rewarded to the player.
    // Scope by map AND placement ID, never by truck model: other copies of
    // the same model may be collectible. Compiled with the app so these facts
    // survive restarts, workspace changes, and cache/map reloads.
    private static readonly HashSet<string> MissionOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "level_ru_04_02|DELIVERY_TRUCK_RECOVERY", // Knee Deep — ZiKZ 5368
        "level_ru_04_03|lost_b", // Left Behind — DON 71
        "level_ru_08_03|TASK_CARGO_DELIVERY_TRUCK", // Abandoned Gear — KHAN Lo4F
        "level_ru_08_03|TASK_TRANSPORT_EVACUATION", // Emergency Retrieval — ANK MK38
        "level_ru_08_04|fishing_01", // Gone Fishing — TUZ 166
        "level_ru_08_04|LOST_1", // Cutting Losses — Royal BM17
        "level_ru_08_04|SPECIAL_01", // When Luck Runs Out — KHAN Lo4F
        "level_us_04_01|US_04_01_CATTH357", // Flooded Foothills — Caterpillar TH357
        "level_us_07_01|RIDER_01", // Lost Souls — YAR 87
        "level_us_07_01|RIDER_02", // Lost Souls — Chevrolet Kodiak C70
        "level_us_07_01|RIDER_03", // Lost Souls — Scout 800
        "level_us_07_01|RIDER_04", // Lost Souls — Chevrolet CK1500
        "level_us_07_01|EVA_01", // Bad Take — Chevrolet CK1500
        "level_us_07_01|EVA_02", // Bad Take — Tayga 6436
        "level_us_07_01|EVA_03", // Bad Take — Voron Grad
        "level_us_07_01|CAREER_01", // Wise Old Bird — Hummer H2
        "level_us_07_01|CAREER_02" // Wise Old Bird — TUZ 166
    };

    public static string? GetMissionOnlyReason(string map, string placementId) =>
        MissionOnly.Contains(map + "|" + placementId)
            ? "User-confirmed mission/task-only placement: this vehicle is not rewarded to the player (confirmed 2026-11-01)."
            : null;
}
