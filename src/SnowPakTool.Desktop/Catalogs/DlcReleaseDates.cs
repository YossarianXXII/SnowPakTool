namespace SnowPakTool.Desktop;

/// <summary>Steam release dates for SnowRunner DLC entries in dlc_library.sso.</summary>
internal static class DlcReleaseDates
{
    // Dates are keyed by dlc_library.sso section names so they do not depend on UI language.
    // Source: SteamDB's SnowRunner DLC listing and Year Pass package records.
    private static readonly IReadOnlyDictionary<string, string> Dates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["search_and_recover"] = "2021-05-18",
        ["expand_and_explore"] = "2021-05-18",
        ["locate_and_deliver"] = "2021-05-18",
        ["expansion"] = "2021-05-18",
        ["rostov_region"] = "2021-09-09",
        ["haul_and_hustle"] = "2021-12-07",
        ["compete_and_conquer"] = "2022-05-31",
        ["grand_harvest"] = "2022-10-13",
        ["phase_9"] = "2023-02-28",
        ["fix_and_connect"] = "2023-07-18",
        ["lights_and_cameras"] = "2023-10-19",
        ["phase_12"] = "2024-01-31",
        ["phase_13"] = "2024-06-04",
        ["phase_14"] = "2024-09-26",
        ["oil_and_dirt"] = "2025-04-10",
        ["phase_16"] = "2025-09-04",
        ["phase_17"] = "2026-01-27",
        ["phase_18"] = "2026-06-25",
        ["phase_19"] = "2026-10-20",
        ["sabertooth"] = "2021-05-17",
        ["pre_order"] = "2021-05-17",
        ["gmc_brigadier8000"] = "2021-05-18",
        ["khan39marshall"] = "2021-05-17",
        ["white_western_star4964skin"] = "2021-05-17",
        ["chevroletck1500skin"] = "2021-05-17",
        ["chevrolet_kodiakc70skin"] = "2021-05-18",
        ["westernstar49x"] = "2021-05-18",
        ["chevroletapache"] = "2021-05-18",
        ["cat681_hx520"] = "2021-05-18",
        ["tatra805_tatra_t813"] = "2021-06-22",
        ["jeep_wrangler_jeep_cj7"] = "2021-09-09",
        ["step_3364_crocodile"] = "2022-03-01",
        ["landrover_defender"] = "2022-05-31",
        ["western_star_47X_57X"] = "2022-09-08",
        ["rezvani_hercules"] = "2022-10-13",
        ["burning_bright_vinyl_skins"] = "2021-05-18",
        ["instant_reward"] = "2022-12-08",
        ["mastodont"] = "2023-05-25",
        ["kenworth_w990"] = "2023-11-22",
        ["wheels_pack"] = "2023-11-22",
        ["year_4_instant_reward"] = "2024-01-31",
        ["azov_atom"] = "2024-04-02",
        ["rezvani_tank"] = "2024-07-30",
        ["kenworth_c500_t880"] = "2024-12-03",
        ["mack_pinnacle_terrapro"] = "2025-02-11",
        ["ws6900xd_freightliner120"] = "2025-06-12",
        ["year_5_instant_reward"] = "2025-09-04",
        ["avenhorn_a15_padera_std4"] = "2025-11-06",
        ["mercedes_actros_mercedes_zetros"] = "2026-03-19",
        ["mercedes_3850_mercedes_mamute"] = "2026-09-02",
        ["seasonpass_1"] = "2021-05-17",
        ["seasonpass_2"] = "2021-05-17",
        ["seasonpass_3"] = "2022-12-08",
        ["seasonpass_4"] = "2024-01-31",
        ["seasonpass_5"] = "2025-09-04"
    };

    public static DateOnly? Get(string dlcCode) => Dates.TryGetValue(dlcCode, out var value)
        ? DateOnly.ParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
        : null;
}
