using System.IO.Compression;
using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SnowPakTool.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--pak-summaries-only")) { PakSummaryChecks.Run(); return; }
        if (args.Contains("--region-batches-only")) { RegionBatchChecks.Run(); return; }
        if (args.Contains("--missions-only")) { MissionRandomizerChecks.Run(); return; }
        if (args.Contains("--map-staging-only")) { MapStagingChecks.Run(); return; }
        if (args.Contains("--changed-trucks-only")) { ChangedTruckChecks.Run(); return; }
        if (args.Contains("--randomizer-only")) { ObtainableRandomizerChecks.Run(); return; }
        if (args.Length > 1 && args[0] == "--randomizer-equipment") { RealRandomizerEquipmentChecks.Run(Path.GetFullPath(args[1])); return; }
        PopupRegression.Run();
        SharedSearchRegression.Run();
        WorkspaceLayoutRegression();
        var workspace = Path.GetFullPath(args[0]);
        var maps = Path.GetFullPath(args[1]);
        var parsed = SsoDataReader.Parse("// comment\n obj = { uiDesc = \"braces { inside }\" nested = [ { truckId = \"A\" }, { truckId = \"B\" } ] }");
        Check(parsed.DescendantsAndSelf().Count(node => node.Get("truckId").Length > 0) == 2, "Nested settings and strings");
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixture-" + Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(fixture, "availability-" + Guid.NewGuid().ToString("N") + ".json");
        var availabilityStore = new RegionTruckAvailabilityStore(statePath);
        var media = Path.Combine(fixture, "original_files", "[media]", "classes", "trucks");
        var ps = Path.Combine(fixture, "original_files", "[ps]");
        var mapRoot = Path.Combine(fixture, "maps");
        Directory.CreateDirectory(media); Directory.CreateDirectory(ps); Directory.CreateDirectory(mapRoot);
        File.WriteAllText(Path.Combine(media, "test_truck.xml"), "<Truck><TruckData TruckType=\"SCOUT\"><Wheels DefaultWheelType=\"wheels_test\" DefaultTire=\"highway_1\" DefaultRim=\"rim_1\"/></TruckData><GameData><UiDesc UiName=\"TEST_TRUCK\"/><CompatibleWheels Type=\"wheels_private\"/><AddonSockets DefaultAddon=\"test_stock\"><Socket Names=\"TestSocket\"/></AddonSockets></GameData></Truck>");
        File.WriteAllText(Path.Combine(media, "test_truck_second.xml"), "<Truck><TruckData TruckType=\"SCOUT\"><Wheels DefaultWheelType=\"wheels_test\" DefaultTire=\"highway_1\"/></TruckData></Truck>");
        var truckFile = Path.Combine(media, "test_truck.xml");
        File.WriteAllText(truckFile, File.ReadAllText(truckFile).Replace("<Wheels ", "<EngineSocket Default=\"engine_default\" Type=\"e_test\"/><GearboxSocket Default=\"gear_default\" Type=\"g_test\"/><WinchUpgradeSocket Default=\"winch_default\" Type=\"w_test\"/><SuspensionSocket Default=\"susp_default\" Type=\"s_test\"/><Wheels "));
        foreach (var (directory, file, rootName, kind, prefix) in new[]
                 { ("engines", "e_test", "EngineVariants", "Engine", "engine"), ("gearboxes", "g_test", "GearboxVariants", "Gearbox", "gear"),
                   ("winches", "w_test", "WinchVariants", "Winch", "winch"), ("suspensions", "s_test", "SuspensionSetVariants", "SuspensionSet", "susp") })
        {
            var componentFolder = Path.Combine(fixture, "original_files", "[media]", "classes", directory);
            Directory.CreateDirectory(componentFolder);
            File.WriteAllText(Path.Combine(componentFolder, file + ".xml"), $"<{rootName}><{kind} Name=\"{prefix}_default\" TestValue=\"100\"/><{kind} Name=\"{prefix}_override\" TestValue=\"200\"/></{rootName}>");
        }
        var wheelDirectory = Path.Combine(fixture, "original_files", "[media]", "classes", "wheels");
        Directory.CreateDirectory(wheelDirectory);
        File.WriteAllText(Path.Combine(wheelDirectory, "wheels_test.xml"), "<TruckWheels><TruckTires>" + string.Join("", new[] { "highway", "allterrain", "offroad", "mudtires", "chains" }.Select(category => $"<TruckTire Name=\"{category}_1\"><WheelFriction _template=\"{category}\"/></TruckTire>")) + "</TruckTires></TruckWheels>");
        File.WriteAllText(Path.Combine(wheelDirectory, "wheels_private.xml"), "<TruckWheels><TruckTires><TruckTire Name=\"offroad_1\"><WheelFriction _template=\"Offroad\"/></TruckTire></TruckTires></TruckWheels>");
        foreach (var id in new[] { "test_stock", "test_upgrade" }) File.WriteAllText(Path.Combine(media, id + ".xml"), "<TruckAddon><GameData><InstallSocket Type=\"TestSocket\"/></GameData></TruckAddon>");
        Directory.CreateDirectory(Path.Combine(media, "addons"));
        Directory.CreateDirectory(Path.Combine(media, "test_truck_tuning"));
        File.WriteAllText(Path.Combine(media, "addons", "test_frame_bed.xml"), "<TruckAddon><GameData Category=\"frame_addons\"><InstallSocket Type=\"FrameAddon\"/></GameData></TruckAddon>");
        File.WriteAllText(Path.Combine(media, "test_truck_tuning", "test_visual_frame.xml"), "<TruckAddon><GameData Category=\"frame_addons\"><InstallSocket Type=\"RoofRack\"/></GameData></TruckAddon>");
        File.WriteAllText(Path.Combine(ps, "us_01_01_test.sso"), "tasksSettings = { TASK_A = { uiDesc = \"Award task\" stages = [ { repairTruck = [ { truckId = \"AWARD\" afterStageFinished = \"DO_NOTHING\" } ] }, { truckDelivery = [ { truckId = \"AWARD\" afterStageFinished = \"REWARD\" } ] } ] __type = \"TaskSettings\" } TASK_B = { uiDesc = \"Remove task\" stages = [ { truckDelivery = [ { truckId = \"REMOVE\" afterStageFinished = \"DESTROY\" } ] } ] __type = \"TaskSettings\" } TASK_C = { stages = [ { repairTruck = [ { truckId = \"UNKNOWN\" afterStageFinished = \"DO_NOTHING\" } ] } ] __type = \"TaskSettings\" } }");
        var bin = BuildMap("", "AWARD", "REMOVE", "UNKNOWN", "UNLINKED");
        using (var file = File.Create(Path.Combine(mapRoot, "level_us_01_01.pak")))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        using (var stream = archive.CreateEntry("[scene]\\data_for_ssl.bin").Open()) stream.Write(bin);
        var sample = RegionTruckCatalogService.Load(fixture, mapRoot, new Dictionary<string, string>(), new Dictionary<string, string> { ["us_01"] = "Michigan" }, availabilityStore);
        Check(sample.Entries.Count == 5 && sample.Warnings.Count == 0, "Five placements; no false-positive model reference");
        Check(sample.Entries.Single(entry => entry.PlacementId == "AWARD").Availability == "Obtainable — task reward", "Task reward after repair stage");
        Check(sample.Entries.Single(entry => entry.PlacementId == "REMOVE").Availability == "Mission/task only", "Task removal");
        Check(sample.Entries.Single(entry => entry.PlacementId == "UNKNOWN").Availability == "Unverified", "No assumed reward for DO_NOTHING");
        Check(sample.Entries.Single(entry => entry.PlacementId == "UNLINKED").Availability == "Unverified", "Unlinked ID remains unverified");
        Check(sample.Entries.Single(entry => entry.PlacementId == "").Availability == "Obtainable — discovery/start", "Discovery placement");
        Check(sample.Entries.All(entry => entry.X == 100 && entry.Z == 200), "Transform positions");
        Check(sample.Entries.All(entry => entry.Transform is { Count: 16 } && entry.BinaryOffset >= 0), "Full placement transform and record offset retained");
        Check(RegionTruckCatalogService.ReadPlacements(bin, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "TEST_TRUCK" }).Count == 5
            && RegionTruckCatalogService.ReadPlacements(bin, new HashSet<string>()).Count == 0
            && RegionTruckCatalogService.ReadPlacements(bin[..50], new HashSet<string> { "test_truck" }).Count == 0,
            "Fast model scan preserves case-insensitive IDs and ignores unknown/truncated records");
        Check(sample.Entries.Single(entry => entry.PlacementId == "").InternalEquipment!.Single(item => item.Slot == "Engine") is { Id: "engine_default", Origin: "vehicle default" }, "Default engine resolved from vehicle socket");
        var overrideEquipment = sample.Entries.Single(entry => entry.PlacementId == "AWARD").InternalEquipment!;
        Check(overrideEquipment.Take(4).All(item => item.Id.EndsWith("_override") && item.Origin == "map override" && item.DefinitionDetails.Contains("TestValue=200")), "All four map component fields resolve independently of vehicle defaults");
        Check(sample.Entries.Single(entry => entry.PlacementId == "").Tires == "Highway", "Empty map fields resolve vehicle default tires");
        Check(sample.Entries.Single(entry => entry.PlacementId == "AWARD").Tires == "Ice", "Installed chained tire maps to Ice");
        Check(sample.Entries.Single(entry => entry.PlacementId == "REMOVE").Tires == "Custom (Offroad)", "Truck-exclusive tire classification includes its underlying class");
        Check(sample.Entries.Single(entry => entry.PlacementId == "UNKNOWN").Tires == "All-terrain", "Map override belongs to the correct placement");
        Check(sample.Entries.Single(entry => entry.PlacementId == "UNLINKED").Tires == "Mudtires", "Installed mud tire classification");
        Check(new TruckEquipmentCatalog(fixture, new Dictionary<string, string>(), []).Resolve("test_truck", new(true, [], "wheels_test", "offroad_1", "")).Tires == "Offroad", "Installed offroad tire classification");
        Check(sample.Entries.Single(entry => entry.PlacementId == "AWARD").AddonDetails.Contains("test_upgrade") && !sample.Entries.Single(entry => entry.PlacementId == "AWARD").AddonDetails.Contains("test_stock"), "Map addon replaces the stock addon in the same socket");
        Check(sample.Entries.Single(entry => entry.PlacementId == "").AddonDetails.Contains("test_stock"), "Default addon retained without a map replacement");
        var separated = new TruckEquipmentCatalog(fixture, new Dictionary<string, string>(), []).Resolve("test_truck", new(true, ["test_frame_bed", "test_visual_frame"], "", "", ""));
        Check(separated.FrameAddons == "test_frame_bed" && separated.Addons == "test_stock", "Frame addons separated from other addons with tuning parts hidden first");
        Check(!separated.FrameAddonDetails.Contains("test_visual_frame") && !separated.AddonDetails.Contains("test_visual_frame") && separated.InstalledAddons!.Any(addon => addon.Id == "test_visual_frame" && addon.IsVisual), "Hidden visual frame equipment retains its source metadata");
        File.WriteAllText(Path.Combine(media, "test_truck_tuning", "test_visual_option.xml"), "<TruckAddon><GameData Category=\"bumper\"><InstallSocket Type=\"TestSocket\"/></GameData></TruckAddon>");
        var replacementCatalog = new TruckEquipmentCatalog(fixture, new Dictionary<string, string>(), []);
        var replacementDefaults = new ReplacementTruck(new CustomTruck("test_truck", "Test truck", "Scout"),
            replacementCatalog.Resolve("test_truck", new(true, [], "", "", "")), replacementCatalog.ComparisonProperties("test_truck"));
        var replacementRows = TruckReplacementComparison.Build(sample.Entries.Single(entry => entry.PlacementId == "AWARD"), replacementDefaults, replacementDefaults);
        Check(replacementRows.Any(row => row.Property == "Internal / Engine" && row.Current == "engine_override" && row.Replacement == "engine_default")
            && replacementRows.Where(row => row.Property.StartsWith("Wheels / ")).All(row => row.Property == "Wheels / Compatible families and scale")
            && replacementRows.Any(row => row.Property.StartsWith("Installed addons") && row.Current.Contains("test_upgrade") && row.Replacement.Contains("test_stock"))
            && !replacementRows.Any(row => row.Property.StartsWith("Addon socket options")),
            "Replacement compares map equipment to defaults and always hides matching socket options");
        Check(!replacementDefaults.Properties.Keys.Any(key => key.Contains("test_visual_option")), "Tuning-folder options are excluded at the definition source");
        var differentOptions = replacementDefaults.Properties.Where(pair => !pair.Key.Contains("test_upgrade")).ToDictionary(pair => pair.Key, pair => pair.Value);
        var differentRows = TruckReplacementComparison.Build(sample.Entries.Single(entry => entry.PlacementId == "AWARD"), replacementDefaults,
            replacementDefaults with { Properties = differentOptions });
        Check(differentRows.Any(row => row.Property.Contains("test_upgrade") && row.Current != row.Replacement), "Unmatched nonvisual socket options remain visible");
        var cleanRows = TruckReplacementComparison.Build(sample.Entries[0] with { InstalledAddons = separated.InstalledAddons }, replacementDefaults,
            replacementDefaults with { Equipment = separated });
        Check(cleanRows.All(row => row.Property is not ("Truck" or "Class" or "Truck ID") && !row.Property.StartsWith("Internal settings")
            && !row.Property.StartsWith("Internal resolution") && !row.Current.Contains("test_visual_frame") && !row.Replacement.Contains("test_visual_frame")),
            "Comparison omits identity, component settings and installed visual addons");
        var hidden = new[] { "EngineMarkerOffset", "ShadowHideDistance", "HideDistance", "ExhaustStartTime", "EngineStartDelay", "EngineIconScale", "EngineIconMesh", "SteerSpeed", "TruckImage" };
        var noisyProperties = replacementDefaults.Properties.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var property in hidden) noisyProperties["TruckData / " + property] = "hidden value";
        noisyProperties["Addon socket options / hood ornaments / Test ornament"] = "Supported socket match";
        noisyProperties["Addon socket options / miscellenious / Test horn"] = "Supported socket match";
        noisyProperties["Wheels / wheels_test tires"] = "Individual tire variants";
        var noisyEquipment = separated with { InstalledAddons = [.. separated.InstalledAddons!, new("ornament", "Test ornament", "hood_ornaments", "", false, "map"), new("horn", "Test horn", "miscellenious", "", false, "map")] };
        var filteredRows = TruckReplacementComparison.Build(sample.Entries[0] with { InstalledAddons = noisyEquipment.InstalledAddons }, replacementDefaults,
            replacementDefaults with { Properties = noisyProperties, Equipment = noisyEquipment });
        Check(filteredRows.All(row => !hidden.Any(property => row.Property.EndsWith(" / " + property)) && !row.Property.Contains("hood ornaments")
            && !row.Property.Contains("miscellenious") && !row.Current.Contains("Test ornament") && !row.Replacement.Contains("Test horn")
            && (!row.Property.StartsWith("Wheels / ") || row.Property == "Wheels / Compatible families and scale")), "Replacement table hides queued cosmetic categories, technical fields and tire variants");
        Check(new[] { "ankatra_1160", "futom_7290ra", "kirovets_k700", "kirovets_k7m" }.All(code => TruckClasses.ForTruck(code, "HEAVY") == "Farming Tractor")
            && TruckClasses.ForTruck("gmc_9500", "HIGHWAY") == "HIGHWAY", "Farming Tractor is a separate application class for exactly the four requested trucks");
        Check(ReferenceEquals(replacementCatalog.ResolveDefault("test_truck"), replacementCatalog.ResolveDefault("TEST_TRUCK"))
            && ReferenceEquals(replacementCatalog.ComparisonProperties("test_truck"), replacementCatalog.ComparisonProperties("TEST_TRUCK")),
            "Default summaries and comparison options are reused by model ID");
        Check(replacementCatalog.IsCurrent(fixture), "Catalog belongs to the active workspace version");
        WorkspaceDataCache.ContentVersion++;
        Check(!replacementCatalog.IsCurrent(fixture), "Workspace changes invalidate reusable equipment catalogs");
        ReplacementSaveRegression(fixture, mapRoot, sample, replacementCatalog);

        var catalog = RegionTruckCatalogService.Load(workspace, maps, WorkspaceDataCache.GetLocalizedNames(workspace), new Dictionary<string, string>(), availabilityStore);
        Check(catalog.MapsRead > 50 && catalog.Entries.Count > 100, "Installed map coverage");
        Check(catalog.Entries.Any(entry => entry.PlacementId == "OLD_TRUCK" && entry.Availability == "Obtainable — task reward"), "Real White Western Star task reward");
        Check(catalog.Entries.Any(entry => entry.PlacementId == "FARMER_TRUCK_01" && entry.Availability == "Mission/task only"), "Real drowned truck mission removal");
        Check(catalog.Entries.Any(entry => entry.PlacementId == "REP_TRUCK" && entry.Availability == "Mission/task only"), "Real repair-only mission truck");
        Check(catalog.Entries.Any(entry => entry.PlacementId == "US_18_02_NEW_TRUCK" && entry.Availability == "Obtainable — task reward"), "Reward from embedded multi-stage task");
        Check(catalog.Entries.Select(entry => entry.Truck).Any(name => name.Contains(' ')), "Localized truck names");
        Check(catalog.Entries.All(entry => entry.Tires != "Unknown" && entry.Addons != "Unknown" && entry.FrameAddons != "Unknown"), "All real map equipment records readable");
        Check(catalog.Entries.Any(entry => entry.TruckCode == "gmc_9500" && entry.PlacementId == "" && entry.MapCode == "level_us_01_01" && entry.Tires == "Highway" && entry.FrameAddonDetails.Contains("frame_addon_sideboard_2")), "Black River GMC default tires and separate installed sideboard");
        Check(catalog.Entries.Any(entry => entry.PlacementId == "US_03_01_TSK_06_TRUCK" && entry.Tires == "Offroad" && entry.TireDetails.Contains("offroad_3")), "Real Loadstar override without leaking adjacent truck configuration");
        var gmc = catalog.Entries.Single(entry => entry.MapCode == "level_us_01_01" && entry.TruckCode == "gmc_9500" && entry.PlacementId == "");
        Check(gmc.InternalEquipment!.Single(item => item.Slot == "Engine") is { Id: "us_truck_old_engine_gmc9500", SourcePath.Length: > 0 } engine && engine.Name != engine.Id,
            "Real installed engine localized and linked to its definition");
        Check(gmc.InternalEquipment!.Any(item => item.Slot == "Differential lock" && item.DefinitionDetails.Contains("DiffLockInstalled=false"))
            && gmc.InternalEquipment.Any(item => item.Slot == "AWD / transfer box" && item.DefinitionDetails.Contains("AllWheelDriveInstalled=false")), "Installed drivetrain addon settings exposed");
        var detailText = TruckDetailsFormatter.Format(gmc);
        Check(detailText.Contains("VISUAL ADDONS") && detailText.Contains(gmc.InstalledAddons!.First(addon => addon.IsVisual).Name) && detailText.Contains("INTERNAL EQUIPMENT")
            && detailText.Contains(gmc.InternalEquipment!.First(item => item.Slot == "Engine").Name) && !detailText.Contains("World transform") && detailText.Contains("TIRE / WHEEL DETAILS")
            && detailText.Contains("FuelCapacity = 240") && !detailText.Contains("TruckData.") && !detailText.Contains("GameData.") && !detailText.Contains("gmc_9500_bumper_default")
            && !detailText.Contains("us_truck_old_engine_gmc9500") && !detailText.Contains("Engine.Torque=")
            && !detailText.Contains("PLACEMENT") && !detailText.Contains("AVAILABILITY EVIDENCE") && !detailText.Contains("FRAME ADDONS")
            && !detailText.Contains("OTHER ADDONS") && !detailText.Contains(gmc.Truck + " — " + gmc.Map), "Bottom pane omits redundant sections and cleans up vehicle properties");
        var visualSection = detailText.Split("VISUAL ADDONS")[1].Split("VEHICLE PROPERTIES")[0];
        Check(!visualSection.Contains("• RWD") && !visualSection.Contains("• Unlocked")
            && detailText.Contains("AWD / transfer box: RWD")
            && gmc.InstalledAddons!.Where(addon => addon.IsVisual && addon.Category is not ("awd" or "diff_lock") && addon.Category.Length > 0)
                .All(addon => visualSection.Contains(addon.Name + " (" + addon.Category.Replace('_', ' ') + ")")),
            "Visual addon names include categories and exclude drivetrain components");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "gmc-details-preview.txt"), detailText);
        ReplacementPopupRegression(workspace, gmc, catalog.EquipmentCatalog!);
        RealMapReplacementRegression(catalog, gmc, workspace);
        Check(gmc.InstalledAddons!.Single(addon => addon.Id == "gmc_9500_diff_lock_default") is { IsVisual: true, Category: "diff_lock" }, "User's visual group follows tuning folders even for functional diff locks");
        Check(gmc.InstalledAddons!.Single(addon => addon.Id == "frame_addon_sideboard_2") is { IsVisual: false, Category: "frame_addons" }, "Shared bed is outside the tuning group");
        Check(gmc.Addons == "None" && gmc.FrameAddons == "Sideboard Bed" && !gmc.AddonDetails.Contains("gmc_9500_diff_lock_default")
            && gmc.InstalledAddons!.All(addon => addon.SourcePath.Length > 0), "Visual addons hidden while original metadata and frame bed remain");
        var confirmed = catalog.Entries.Where(entry => entry.Evidence.StartsWith("User-confirmed", StringComparison.Ordinal)).ToArray();
        Check(confirmed.Length == 17 && confirmed.All(entry => entry.Availability == "Mission/task only"), "All 17 confirmed placements use persistent mission-only exceptions");
        Check(catalog.Entries.Count(entry => entry.Availability == "Mission/task only") == 166 && catalog.Entries.All(entry => entry.Availability != "Unverified"), "Real scan has 166 mission-only placements and no remaining unverified placements");
        Check(RegionTruckPlacementOverrides.GetMissionOnlyReason("LEVEL_US_07_01", "rider_01") is not null, "Placement exceptions ignore identifier casing");
        Check(RegionTruckPlacementOverrides.GetMissionOnlyReason("level_us_01_01", "RIDER_01") is null, "Same placement ID on a different map is unaffected");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "catalog.json"), JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Installed maps: {catalog.MapsRead}; placements: {catalog.Entries.Count}; regions: {catalog.Entries.Select(entry => entry.RegionCode).Distinct().Count()}; warnings: {catalog.Warnings.Count}");
        foreach (var group in catalog.Entries.GroupBy(entry => entry.Availability)) Console.WriteLine($"  {group.Key}: {group.Count()}");
        foreach (var warning in catalog.Warnings.Take(8)) Console.WriteLine("WARNING " + warning);

        using var host = new Form();
        using var tab = new RegionsTrucksTab(() => workspace, () => "", availabilityStore);
        host.Controls.Add(tab); host.CreateControl(); tab.CreateControl();
        SetField(tab, "_catalog", catalog);
        Invoke(tab, "BuildRegionTiles"); Invoke(tab, "ApplyFilter");
        var tiles = Field<Dictionary<string, Button>>(tab, "_tiles");
        Check(tiles.Keys.Take(6).SequenceEqual(new[] { "us_01", "us_02", "ru_02", "ru_03", "us_04", "us_03" }), "Base regions, Kola, Yukon, Wisconsin release order");
        Check(tiles.Values.All(tile => tile.Size == new Size(150, 210) && tile is RegionTileButton { Thumbnail: not null }), "Unchanged tile sizes and all embedded thumbnails");
        Check(tiles.Values.Cast<RegionTileButton>().All(tile => !tile.Summary.Contains("obtainable") && !tile.Summary.Contains("mission/task") && !tile.Summary.Contains("unverified")), "Resolved region tiles omit all availability count lines");
        var names = WorkspaceDataCache.GetLocalizedNames(workspace);
        var metadata = System.Text.RegularExpressions.Regex.Matches(WorkspaceDataCache.ReadAllText(WorkspaceOriginals.GetCurrentPath(workspace, "[ps]/zone_desc_storage.sso")), "uiName\\s*=\\s*\"(?<key>UI_REGION_[^\"]+_NAME)\"[\\s\\S]{0,300}?name\\s*=\\s*\"(?<code>(?:us|ru)_\\d{2})\"")
            .ToDictionary(match => match.Groups["code"].Value, match => names.GetValueOrDefault(match.Groups["key"].Value, match.Groups["code"].Value));
        using var preview = new Bitmap(6 * 150, 4 * 210);
        using (var graphics = Graphics.FromImage(preview))
        {
            graphics.Clear(Color.FromArgb(240, 240, 240));
            var index = 0;
            foreach (var (code, button) in tiles)
            {
                var original = (RegionTileButton)button;
                using var tile = new RegionTileButton
                {
                    Size = new Size(150, 210), RegionName = metadata.GetValueOrDefault(code, code),
                    ReleaseLabel = original.ReleaseLabel, Summary = original.Summary, Thumbnail = original.Thumbnail,
                    FlatStyle = FlatStyle.Flat, BackColor = index == 0 ? AppColors.Get("SelectionBlue") : Color.FromArgb(32, 38, 45),
                    ForeColor = index == 0 ? Color.Black : Color.White
                };
                tile.FlatAppearance.BorderSize = index == 0 ? 3 : 1;
                tile.FlatAppearance.BorderColor = index == 0 ? SystemColors.Highlight : Color.FromArgb(70, 80, 90);
                using var bitmap = new Bitmap(150, 210);
                tile.DrawToBitmap(bitmap, new Rectangle(0, 0, 150, 210));
                graphics.DrawImageUnscaled(bitmap, index % 6 * 150, index / 6 * 210);
                index++;
            }
        }
        preview.Save(Path.Combine(AppContext.BaseDirectory, "region-tiles-preview.png"));
        var selected = Field<HashSet<string>>(tab, "_selectedRegions");
        var first = selected.Single();
        var second = tiles.Keys.First(key => key != first);
        var grid = Field<DataGridView>(tab, "_trucks");
        Check(grid.Rows.Cast<DataGridViewRow>().All(row => row.Cells["Truck"] is TruckReplacementCell), "Every Truck cell includes a replacement action");
        var invokedRow = -1;
        var action = new TruckReplacementCell { Open = row => invokedRow = row };
        var cloned = (TruckReplacementCell)action.Clone();
        var key = new KeyEventArgs(Keys.F4);
        typeof(TruckReplacementCell).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cloned, [key, 7]);
        Check(invokedRow == 7 && key.Handled, "Cloned replacement cell retains its keyboard action and row identity");
        var firstTruckCell = (TruckReplacementCell)grid.Rows[0].Cells["Truck"];
        var previousAction = firstTruckCell.Open; firstTruckCell.Open = row => invokedRow = row;
        var mouseClick = typeof(TruckReplacementCell).GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
        invokedRow = -1;
        mouseClick.Invoke(firstTruckCell, [new DataGridViewCellMouseEventArgs(firstTruckCell.ColumnIndex, 0, 5, 10, new MouseEventArgs(MouseButtons.Left, 1, 5, 10, 0))]);
        Check(invokedRow == -1, "Clicking a truck name leaves the replacement popup closed");
        var buttonX = firstTruckCell.Size.Width - 15;
        mouseClick.Invoke(firstTruckCell, [new DataGridViewCellMouseEventArgs(firstTruckCell.ColumnIndex, 0, buttonX, 10, new MouseEventArgs(MouseButtons.Left, 1, buttonX, 10, 0))]);
        Check(invokedRow == 0, "Right-edge replacement button opens the correct row"); firstTruckCell.Open = previousAction;
        using (var gridPreview = new Bitmap(grid.Width, grid.Height))
        { grid.DrawToBitmap(gridPreview, grid.ClientRectangle); gridPreview.Save(Path.Combine(AppContext.BaseDirectory, "replacement-cell-preview.png")); }
        Check(Field<TextBox>(tab, "_details").Parent?.Parent is SplitContainer, "Details pane is resizable against the truck table");
        host.Controls.Remove(tab);
        tab.BindingContext = new BindingContext();
        tab.CreateControl();
        using var search = new CurrentTabSearch(() => tab);
        Check(grid.Columns["Frame addons"] is { ReadOnly: true }, "Separate read-only Frame addons column");
        Check(grid.Rows.Count == catalog.Entries.Count(entry => entry.RegionCode == first), "Initial region table");
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tiles[second], [EventArgs.Empty]);
        Check(selected.Count == 2 && tiles[second].FlatAppearance.BorderSize == 3, "Region multi-selection highlight");
        Check(grid.Rows.Count == catalog.Entries.Count(entry => selected.Contains(entry.RegionCode)), "Selected region union");
        var visibleBeforeSearch = grid.Rows.Count;
        Check(search.Search("no-such-vehicle-123456") == 0 && grid.Rows.Count == visibleBeforeSearch, "Overlay search leaves nonmatching rows visible");
        var matchCount = search.Search("chevrolet");
        Check(matchCount > 0 && grid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells.Cast<DataGridViewCell>().Any(cell => cell.Style.BackColor == AppColors.Get("EngineMatch"))), "Shared search highlights matching cells");
        foreach (var query in new[] { "Mission/task only", "vehicle default", "diff_lock", "DESTROY" })
            Check(search.Search(query) > 0,
                "Search matches arbitrary table/detail text: " + query);
        Check(search.Search("o") == 0, "Minimum 3 characters on region content");
        Check(search.Find("Season 19")
            && ((RegionTileButton)tiles["us_19"]).SearchMatch && selected.Count == 2, "Search finds region labels outside the current table without changing region selection");
        search.Clear();
        TableHeaderBehavior.Attach(grid).AddFilter("Availability").SelectValues(new[] { "Mission" });
        Check(grid.Rows.Count == catalog.Entries.Count(entry => selected.Contains(entry.RegionCode) && entry.Availability == "Mission/task only"), "Mission filter");
        if (grid.Rows.Count > 0) grid.CurrentCell = grid.Rows[0].Cells[0];
        Check(!Field<TextBox>(tab, "_details").Text.Contains("AVAILABILITY EVIDENCE")
            && Field<TextBox>(tab, "_details").Text.Contains("INTERNAL EQUIPMENT"), "Details pane shows equipment without classification evidence");
        grid.Sort(grid.Columns["Tires"]!, System.ComponentModel.ListSortDirection.Descending);
        var tooltip = (DataGridViewCellToolTipTextNeededEventArgs)Activator.CreateInstance(typeof(DataGridViewCellToolTipTextNeededEventArgs),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [grid.Columns["Tires"]!.Index, 0, ""], null)!;
        typeof(RegionsTrucksTab).GetMethod("EquipmentToolTipNeeded", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tab, [grid, tooltip]);
        var firstEntry = (RegionTruckEntry)((System.Data.DataRowView)grid.Rows[0].DataBoundItem!)["Entry"];
        Check(tooltip.ToolTipText == firstEntry.TireDetails && tooltip.ToolTipText.Contains("Tire ID:"), "Specific tire tooltip follows sorted row");
        var frameTooltip = (DataGridViewCellToolTipTextNeededEventArgs)Activator.CreateInstance(typeof(DataGridViewCellToolTipTextNeededEventArgs),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [grid.Columns["Frame addons"]!.Index, 0, ""], null)!;
        typeof(RegionsTrucksTab).GetMethod("EquipmentToolTipNeeded", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tab, [grid, frameTooltip]);
        Check(frameTooltip.ToolTipText == firstEntry.FrameAddonDetails, "Frame addon tooltip follows the sorted row");
        Check(grid.Rows.Cast<DataGridViewRow>().All(row => row.Cells["Availability"].ReadOnly), "Known placements remain read-only");

        // Unknown fixture placements remain visible when both filters are off and can be resolved in the table.
        SetField(tab, "_catalog", sample);
        selected.Clear(); selected.Add("us_01");
        TableHeaderBehavior.Attach(grid).AddFilter("Availability").SelectValues(Array.Empty<string>());
        Invoke(tab, "ApplyFilter");
        Invoke(tab, "UpdateTileSummaries");
        Check(grid.Rows.Count == 2 && grid.Rows.Cast<DataGridViewRow>().All(row => row.Cells["Availability"] is DataGridViewComboBoxCell && !row.Cells["Availability"].ReadOnly), "Unknown rows stay visible with editable availability dropdowns");
        Check(((RegionTileButton)tiles["us_01"]).Summary.Contains("2 unverified"), "Only nonzero unverified count shown");
        var target = (RegionTruckEntry)((System.Data.DataRowView)grid.Rows[0].DataBoundItem!)["Entry"];
        grid.Rows[0].Cells["Availability"].Value = "Mission/task only";
        Application.DoEvents();
        Check(grid.Rows.Count == 1 && ((RegionTileButton)tiles["us_01"]).Summary.Contains("1 unverified"), "Resolving an unknown refreshes tile count and respects mission checkbox");
        var reopenedStore = new RegionTruckAvailabilityStore(statePath);
        var resolved = reopenedStore.Apply(target);
        Check(resolved.Availability == "Mission/task only" && resolved.AvailabilityEditable && resolved.Evidence.Contains("User-selected"), "Selection persists through a new store instance");
        Check(reopenedStore.Apply(target with { MapCode = "level_us_02_01" }).Availability == "Unverified", "Manual choices are scoped by map and placement ID");
        TableHeaderBehavior.Attach(grid).AddFilter("Availability").SelectValues(new[] { "Mission" });
        Check(grid.Rows.Count == 3 && grid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells["Availability"] is DataGridViewComboBoxCell && Convert.ToString(row.Cells["Availability"].Value) == "Mission/task only"), "Manual resolutions can be amended later");
        Console.WriteLine("PASS: map equipment, confirmed exceptions, checkbox filters, overlay search, tile counts, editable unknowns and persistent availability choices.");
    }

    private static void RealMapReplacementRegression(RegionTruckCatalog catalog, RegionTruckEntry discovery, string workspace)
    {
        var equipment = catalog.EquipmentCatalog!;
        var replacement = new ReplacementTruck(new("tayga_6436", "Tayga 6436", "Offroad"), equipment.ResolveDefault("tayga_6436"), equipment.ComparisonProperties("tayga_6436"));
        var task = catalog.Entries.First(entry => entry.Availability == "Obtainable — task reward" && entry.TruckCode != replacement.Truck.Code);
        var codes = catalog.Entries.Select(entry => entry.TruckCode).Append(replacement.Truck.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var current in new[] { discovery, task })
        {
            var data = RegionTruckCatalogService.ReadMapData(current.Source)!;
            // An installed/overlaid map can still be an old-writer output. Exercise the preserved original instead.
            try { TruckReplacementStore.ValidateScene(data); }
            catch (InvalidDataException)
            {
                data = RegionTruckCatalogService.ReadMapData(WorkspaceOriginals.GetMetadataPath(workspace, "region-maps", "backups", current.MapCode, "original.pak"))!;
            }
            var patched = TruckReplacementStore.Patch(data, current, replacement);
            CheckSceneSplice(data, patched);
            var oldRows = RegionTruckCatalogService.ReadPlacements(data, codes); var rows = RegionTruckCatalogService.ReadPlacements(patched, codes);
            Check(oldRows.Count == rows.Count && oldRows.Zip(rows).All(pair => pair.First.Id == pair.Second.Id && pair.First.Transform!.SequenceEqual(pair.Second.Transform!)),
                "Real map patch preserves all placement IDs, positions and orientations");
            var index = oldRows.ToList().FindIndex(row => row.Offset == current.BinaryOffset);
            Check(rows[index].TruckCode == replacement.Truck.Code && rows.Where((_, row) => row != index).Select(row => row.TruckCode)
                .SequenceEqual(oldRows.Where((_, row) => row != index).Select(row => row.TruckCode)), "Real map patch changes only the selected discovery/task truck");
        }
        Console.WriteLine("PASS: real discovery and task map replacements re-read correctly with all other placements preserved (in memory only).");
    }

    private static void ReplacementSaveRegression(string workspace, string maps, RegionTruckCatalog before, TruckEquipmentCatalog equipment)
    {
        var current = before.Entries.Single(entry => entry.PlacementId == "AWARD");
        var target = new ReplacementTruck(new("test_truck_second", "Second truck", "Scout"), equipment.ResolveDefault("test_truck_second"), equipment.ComparisonProperties("test_truck_second"));
        // Each run uses independent map copies; live game files are never written.
        var run = Path.Combine(AppContext.BaseDirectory, "save-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
        var pak = Path.Combine(run, "level_us_01_01.pak"); File.Copy(current.Source, pak);
        using (var zip = ZipFile.Open(pak, ZipArchiveMode.Update))
        { using var writer = new StreamWriter(zip.CreateEntry("unrelated_payload.bin", CompressionLevel.NoCompression).Open()); writer.Write("unrelated payload remains unchanged"); }
        var originalBytes = File.ReadAllBytes(pak);
        var saveWorkspace = Path.Combine(run, "workspace"); Directory.CreateDirectory(saveWorkspace);
        current = current with { Source = pak };
        var saved = TruckReplacementStore.Save(saveWorkspace, current, target);
        CheckSceneSplice(RegionTruckCatalogService.ReadMapData(pak)!, RegionTruckCatalogService.ReadMapData(saved.OutputPath)!);
        CheckArchiveLayout(pak, saved.OutputPath);
        Check(File.ReadAllBytes(pak).SequenceEqual(originalBytes) && File.ReadAllBytes(saved.OriginalBackupPath).SequenceEqual(originalBytes), "Input archive stays untouched and original backup is byte-identical");
        using (var zip = ZipFile.OpenRead(saved.OutputPath))
        { using var reader = new StreamReader(zip.GetEntry("unrelated_payload.bin")!.Open()); Check(reader.ReadToEnd() == "unrelated payload remains unchanged", "Saving preserves unrelated archive entries"); }
        var data = RegionTruckCatalogService.ReadMapData(saved.OutputPath)!;
        var codes = new HashSet<string> { "test_truck", "test_truck_second" };
        var rows = RegionTruckCatalogService.ReadPlacements(data, codes);
        var changed = rows.Single(entry => entry.Id == "AWARD");
        Check(rows.Count == 5 && changed.TruckCode == "test_truck_second" && changed.Equipment.Engine == "" && changed.Equipment.Addons.Count == 0
            && changed.Transform!.SequenceEqual(current.Transform!), "Named task placement becomes the new model with defaults, retaining ID and transform");
        Check(rows.Where(entry => entry.Id != "AWARD").All(entry => entry.TruckCode == "test_truck"), "Other copies of the same truck are preserved");
        Check(TruckReplacementStore.SavedMaps(saveWorkspace).Single().Source == saved.OutputPath, "Saved map overlay is discoverable after reopening");
        var currentSaved = current with { TruckCode = changed.TruckCode, BinaryOffset = changed.Offset, Source = saved.OutputPath,
            MapDataHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)) };
        var back = new ReplacementTruck(new("test_truck", "Test truck", "Scout"), equipment.ResolveDefault("test_truck"), equipment.ComparisonProperties("test_truck"));
        var previousBytes = File.ReadAllBytes(saved.OutputPath);
        var second = TruckReplacementStore.Save(saveWorkspace, currentSaved, back);
        Check(File.ReadAllBytes(saved.OriginalBackupPath).SequenceEqual(originalBytes) && second.PreviousBackupPath is not null
            && File.ReadAllBytes(second.PreviousBackupPath).SequenceEqual(previousBytes), "Repeat saves retain the first original and back up the preceding edited version");
        var staleRejected = false;
        try { TruckReplacementStore.Save(saveWorkspace, currentSaved, back); } catch (InvalidDataException) { staleRejected = true; }
        Check(staleRejected, "Stale placement data cannot overwrite a newer edit");
        var binFolder = Path.Combine(run, "level_us_01_01", "[scene]"); Directory.CreateDirectory(binFolder);
        var bin = Path.Combine(binFolder, "data_for_ssl.bin"); File.WriteAllBytes(bin, RegionTruckCatalogService.ReadMapData(pak)!);
        var binWorkspace = Path.Combine(run, "bin-workspace"); Directory.CreateDirectory(binWorkspace);
        var unnamed = before.Entries.Single(entry => entry.PlacementId.Length == 0) with { Source = bin };
        var binSaved = TruckReplacementStore.Save(binWorkspace, unnamed, target);
        var binPlacements = RegionTruckCatalogService.ReadPlacements(File.ReadAllBytes(binSaved.OutputPath), codes);
        Check(binPlacements.Single(entry => entry.Id.Length == 0).TruckCode == target.Truck.Code && File.ReadAllBytes(bin).SequenceEqual(File.ReadAllBytes(binSaved.OriginalBackupPath)),
            "Unnamed discovery placements and extracted map metadata can be replaced and backed up");
        var invalidRejected = false;
        try { TruckReplacementStore.Patch(File.ReadAllBytes(bin), unnamed with { BinaryOffset = unnamed.BinaryOffset + 1 }, target); }
        catch (InvalidDataException) { invalidRejected = true; }
        Check(invalidRejected, "Invalid placement boundaries are rejected");
        var malformed = File.ReadAllBytes(bin); BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(8, 4), malformed.Length - 1);
        var malformedRejected = false;
        try { TruckReplacementStore.Patch(malformed, unnamed, target); } catch (InvalidDataException) { malformedRejected = true; }
        Check(malformedRejected, "Invalid scene envelopes are rejected before any save");
        RepairRegression(run, pak, current, target);
        var overlayRoot = WorkspaceOriginals.GetMetadataPath(workspace, "region-maps", "updated"); Directory.CreateDirectory(overlayRoot);
        File.Copy(saved.OutputPath, Path.Combine(overlayRoot, "level_us_01_01.pak"), overwrite: true);
        var reloaded = RegionTruckCatalogService.Load(workspace, maps, new Dictionary<string, string>(), new Dictionary<string, string>(), new RegionTruckAvailabilityStore(Path.Combine(run, "availability.json")));
        Check(reloaded.Entries.Single(entry => entry.PlacementId == "AWARD").Source.StartsWith(overlayRoot), "Region catalog loads saved maps ahead of input archives");
        var stringsFolder = Path.Combine(workspace, "original_files", "[strings]"); Directory.CreateDirectory(stringsFolder);
        File.WriteAllText(Path.Combine(stringsFolder, "strings_english.str"), "");
        var popupCurrent = reloaded.Entries.Single(entry => entry.PlacementId == "AWARD");
        var mapBeforeQueue = File.ReadAllBytes(popupCurrent.Source);
        using (var popup = new TruckReplacementDialog(workspace, popupCurrent,
                   () => [new("test_truck", "Test truck", "Scout"), new("test_truck_second", "Second truck", "Scout")], reloaded.EquipmentCatalog))
        {
            popup.Show(); var grid = Field<DataGridView>(popup, "_list"); var timer = System.Diagnostics.Stopwatch.StartNew();
            while (grid.Rows.Count == 0 && timer.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(5); }
            Check(grid.Rows.Count == 2, "Apply popup loads fixture candidates");
            foreach (DataGridViewRow row in grid.Rows)
                if (Convert.ToString(row.Cells["Truck ID"].Value) == "test_truck") grid.CurrentCell = row.Cells[0];
            Check(!Field<Button>(popup, "_apply").Enabled, "Apply is disabled for the existing truck model");
            foreach (DataGridViewRow row in grid.Rows)
                if (Convert.ToString(row.Cells["Truck ID"].Value) == "test_truck_second") grid.CurrentCell = row.Cells[0];
            Check(Field<Button>(popup, "_apply").Enabled, "Apply is enabled for a different enabled truck");
            Field<Button>(popup, "_apply").PerformClick(); timer.Restart();
            while ((popup.QueuedReplacement is null || Field<bool>(popup, "_saving")) && timer.ElapsedMilliseconds < 10000)
            { Application.DoEvents(); Thread.Sleep(5); }
            Check(popup.QueuedReplacement is not null && File.ReadAllBytes(popupCurrent.Source).SequenceEqual(mapBeforeQueue)
                && !Field<Button>(popup, "_apply").Enabled && Field<Label>(popup, "_status").Text.Contains("Start"),
                "Popup Apply persists the queue without touching map files");
            popup.Close();
        }
        Check(PendingTruckReplacements.Load(workspace).Count == 1, "Queued replacements persist independently of popup lifetime");
        var preview = RegionTruckCatalogService.Load(workspace, maps, new Dictionary<string, string>(), new Dictionary<string, string>(), new RegionTruckAvailabilityStore(Path.Combine(run, "preview-availability.json")));
        Check(preview.Entries.Single(entry => entry.PlacementId == "AWARD") is { TruckCode: "test_truck_second", FileEntry.TruckCode: "test_truck" }, "Table shows queued truck and retains the file-backed original");
        var discovery = preview.Entries.Single(entry => entry.PlacementId.Length == 0);
        PendingTruckReplacements.Queue(workspace, discovery, new("test_truck_second", "Second truck", "Scout"));
        PendingTruckReplacements.Stage(workspace);
        Check(WorkspaceChangeAudit.MapFiles(workspace).Count(row => row.State == WorkspaceChange.Pending) == 2, "Start map list includes every pending replacement");
        Check(File.ReadAllBytes(popupCurrent.Source).SequenceEqual(mapBeforeQueue), "Multiple queued changes still leave the map untouched");
        var appliedMaps = MapStagingChecks.Apply(workspace);
        Check(appliedMaps.Count == 1 && PendingTruckReplacements.Load(workspace).Count == 0, "Start applies same-map changes together and clears only committed plans");
        var committed = RegionTruckCatalogService.Load(workspace, maps, new Dictionary<string, string>(), new Dictionary<string, string>(), new RegionTruckAvailabilityStore(Path.Combine(run, "committed-availability.json")));
        Check(committed.Entries.Single(entry => entry.PlacementId == "AWARD").TruckCode == "test_truck_second"
            && committed.Entries.Single(entry => entry.PlacementId.Length == 0).TruckCode == "test_truck_second" && committed.Entries.All(entry => entry.FileEntry is null),
            "Both replacements survive variable-length edits and reload as applied file values");
        Check(WorkspaceChangeAudit.MapFiles(workspace).Any(row => row.State == WorkspaceChange.InFiles && row.After.Contains("test_truck_second")), "Start distinguishes applied map changes from pending plans");
        var xml = Path.Combine(mediaFor(workspace), "test_truck_second.xml");
        var originalXml = WorkspaceOriginals.GetOriginalReadPath(workspace, "[media]/classes/trucks/test_truck_second.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(xml)!); File.WriteAllText(xml, File.ReadAllText(originalXml).Replace("<TruckData ", "<TruckData FuelCapacity=\"99\" "));
        Check(WorkspaceChangeAudit.PakFiles(workspace).Any(row => row.State == WorkspaceChange.InFiles && row.Property.EndsWith("/@FuelCapacity") && row.After == "99"), "Start PAK audit compares saved XML properties with default originals");
        var pendingAgain = committed.Entries.Single(entry => entry.PlacementId == "AWARD");
        PendingTruckReplacements.Queue(workspace, pendingAgain, new("test_truck", "Test truck", "Scout"));
        PendingTruckReplacements.Stage(workspace);
        StartPanelRegression(workspace);
        BackupSourceRegression(workspace, maps, pak, committed.Entries[0].Source);
        string mediaFor(string root) => Path.Combine(WorkspaceOriginals.GetUpdatedRoot(root), "[media]", "classes", "trucks");
        Console.WriteLine("PASS: replacement saving, byte-identical backups, repeat revisions, task/discovery identity, stale rejection, overlay reload and unrelated archive content.");
    }

    private static void BackupSourceRegression(string workspace, string maps, string baseline, string updated)
    {
        foreach (var pending in PendingTruckReplacements.Load(workspace)) PendingTruckReplacements.Discard(workspace, pending.Key);
        var backup = WorkspaceOriginals.GetMetadataPath(workspace, "region-maps", "backups", "level_us_01_01", "original.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(baseline, backup, overwrite: true);
        var installed = Path.Combine(maps, "level_us_01_01.pak");
        File.Copy(updated, installed, overwrite: true); // Simulate installing the changed fixture map.
        RegionTruckCatalog Load(string folder) => RegionTruckCatalogService.Load(workspace, folder, new Dictionary<string, string>(), new Dictionary<string, string>());
        var withUpdate = Load(maps);
        Check(withUpdate.Entries.Single(row => row.PlacementId.Length == 0) is { TruckCode: "test_truck_second" } row && row.Source == updated,
            "Workspace update overrides its original backup");
        var stamp = RegionTruckCatalogService.SourceStamp(workspace, maps);
        File.Delete(updated); // Only this run's generated fixture output is removed.
        Check(stamp != RegionTruckCatalogService.SourceStamp(workspace, maps), "Deleting an update invalidates the Regions-Trucks source cache");
        var restored = Load(maps).Entries.Single(row => row.PlacementId.Length == 0);
        Check(restored.TruckCode == "test_truck" && restored.Source == backup,
            "Deleting an update restores the backed-up truck even if the installed map still has the replacement");
        Check(Load(Path.Combine(maps, "missing-folder")).Entries.Count == 5, "Workspace backups load without installed maps");
        var bin = Path.Combine(Path.GetDirectoryName(backup)!, "original.bin");
        File.WriteAllBytes(bin, RegionTruckCatalogService.ReadMapData(backup)!); File.Delete(backup);
        Check(Load(maps).Entries.All(row => row.Source == bin && row.TruckCode == "test_truck"), "Extracted original.bin is also a workspace baseline");
        Console.WriteLine("PASS: backup precedence over installed replacements, update overrides, deletion/cache invalidation and workspace-only originals.");
    }

    private static void CheckSceneSplice(byte[] before, byte[] after)
    {
        TruckReplacementStore.ValidateScene(after);
        var boundary = BinaryPrimitives.ReadInt32LittleEndian(before.AsSpan(4, 4));
        var nextBoundary = BinaryPrimitives.ReadInt32LittleEndian(after.AsSpan(4, 4));
        Check(nextBoundary - boundary == after.Length - before.Length && before.AsSpan(boundary).SequenceEqual(after.AsSpan(nextBoundary)),
            "Variable-length replacements update both scene boundaries and retain the complete payload after the header");
        Check(before.AsSpan(12, 4).SequenceEqual(after.AsSpan(12, 4)), "Scene payload prefix is preserved, not mistaken for a checksum");
    }

    private static void CheckArchiveLayout(string original, string edited)
    {
        using var a = ZipFile.OpenRead(original); using var b = ZipFile.OpenRead(edited);
        Check(a.Entries.Select(entry => entry.FullName).SequenceEqual(b.Entries.Select(entry => entry.FullName)), "PAK entry order is preserved");
        var before = File.ReadAllBytes(original); var after = File.ReadAllBytes(edited);
        var first = LocalRecords(before); var second = LocalRecords(after);
        Check(first.Count == second.Count, "Local entry count stays unchanged");
        for (var i = 0; i < first.Count; i++)
        {
            var x = first[i]; var y = second[i];
            Check(x.Name == y.Name && x.Method == y.Method, "Local ZIP order and original stored/deflate methods are preserved");
            if (!x.Name.Replace('\\', '/').Equals("[scene]/data_for_ssl.bin", StringComparison.OrdinalIgnoreCase))
                Check(before.AsSpan(x.Offset, x.Length).SequenceEqual(after.AsSpan(y.Offset, y.Length)), "Unrelated local ZIP records remain byte-identical");
        }
        foreach (var entry in b.Entries) { using var stream = entry.Open(); stream.CopyTo(Stream.Null); }
    }

    private static List<(string Name, ushort Method, int Offset, int Length)> LocalRecords(byte[] bytes)
    {
        var rows = new List<(string, ushort, int, int)>(); var offset = 0;
        while (offset + 30 <= bytes.Length && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) == 0x04034b50)
        {
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 26, 2));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 28, 2));
            var length = checked(30 + nameLength + extraLength + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 18, 4)));
            rows.Add((Encoding.UTF8.GetString(bytes, offset + 30, nameLength), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 8, 2)), offset, length));
            offset += length;
        }
        return rows;
    }

    private static void RepairRegression(string run, string source, RegionTruckEntry current, ReplacementTruck replacement)
    {
        var workspace = Path.Combine(run, "repair-workspace");
        var root = Path.Combine(workspace, "region-maps"); var backups = Path.Combine(root, "backups", current.MapCode);
        Directory.CreateDirectory(backups); Directory.CreateDirectory(Path.Combine(root, "updated"));
        var baselinePath = Path.Combine(backups, "original.pak");
        // Use a stored scene in the middle of the archive, as in the game PAKs.
        var baseline = RegionTruckCatalogService.ReadMapData(source)!;
        using (var zip = ZipFile.Open(baselinePath, ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("before.bin", CompressionLevel.Optimal).Open()) s.Write(new byte[200]);
            using (var s = zip.CreateEntry("[scene]/data_for_ssl.bin", CompressionLevel.NoCompression).Open()) s.Write(baseline);
            using (var s = zip.CreateEntry("after.bin", CompressionLevel.NoCompression).Open()) s.Write(Encoding.ASCII.GetBytes("untouched"));
        }
        // Include trailing PAK padding and retain it through both writing and repair.
        using (var padding = File.Open(baselinePath, FileMode.Append)) padding.Write(new byte[484]);
        var edited = Path.Combine(root, "updated", current.MapCode + ".pak");
        File.Copy(baselinePath, edited);
        var valid = TruckReplacementStore.Patch(baseline, current, replacement); var broken = (byte[])valid.Clone();
        baseline.AsSpan(0, 12).CopyTo(broken);
        using (var zip = ZipFile.Open(edited, ZipArchiveMode.Update))
        { zip.GetEntry("[scene]/data_for_ssl.bin")!.Delete(); using var stream = zip.CreateEntry("[scene]/data_for_ssl.bin", CompressionLevel.Optimal).Open(); stream.Write(broken); }
        var oldBytes = File.ReadAllBytes(edited); var originalBytes = File.ReadAllBytes(baselinePath);
        Check(TruckReplacementStore.RepairSavedMaps(workspace) == 1, "Old writer output is repairable in the workspace");
        Check(RegionTruckCatalogService.ReadMapData(edited)!.SequenceEqual(valid), "Repair retains selected model/equipment and fixes both scene boundaries");
        CheckArchiveLayout(baselinePath, edited);
        Check(File.ReadAllBytes(baselinePath).SequenceEqual(originalBytes) && Directory.GetFiles(backups).Where(path => path != baselinePath).Any(path => File.ReadAllBytes(path).SequenceEqual(oldBytes)),
            "Repair preserves the original backup and backs up the preceding broken output");
        Check(TruckReplacementStore.RepairSavedMaps(workspace) == 0, "Repair is idempotent");
        File.WriteAllBytes(edited, oldBytes);
        using (var zip = ZipFile.Open(edited, ZipArchiveMode.Update))
        { zip.GetEntry("after.bin")!.Delete(); using var s = zip.CreateEntry("after.bin").Open(); s.WriteByte(123); }
        var unsafeBytes = File.ReadAllBytes(edited); var rejected = false;
        try { TruckReplacementStore.RepairSavedMaps(workspace); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && File.ReadAllBytes(edited).SequenceEqual(unsafeBytes), "Repair rejects unrelated changes without publishing an output");
        Console.WriteLine("PASS: scene size boundaries, PAK record order/compression/raw bytes, old-output repair, backups and unsafe-repair rejection.");
    }

    private static void WorkspaceLayoutRegression()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "layout-" + Guid.NewGuid().ToString("N"));
        var original = Path.Combine(root, "original_files", "[media]", "classes", "trucks", "test.xml");
        var edited = Path.Combine(root, "updated", "[media]", "classes", "trucks", "test.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(original)!); Directory.CreateDirectory(Path.GetDirectoryName(edited)!);
        File.WriteAllText(original, "default truck"); File.WriteAllText(edited, "edited truck");
        var oldMaps = Path.Combine(root, ".snowpaktool", "region-maps"); Directory.CreateDirectory(Path.Combine(oldMaps, "updated"));
        var map = Path.Combine(oldMaps, "updated", "level_us_01_01.pak"); File.WriteAllBytes(map, [1, 2, 3, 4]);
        var plan = Path.Combine(oldMaps, "pending-replacements.json");
        File.WriteAllText(plan, JsonSerializer.Serialize(new { Source = map, Xml = edited, Values = new[] { "unchanged", original } }));
        File.WriteAllText(Path.Combine(root, ".snowpaktool", "progression-settings.json"), "{\"value\":7}");
        Directory.CreateDirectory(Path.Combine(root, "region-maps")); File.WriteAllText(Path.Combine(root, "region-maps", "pending-replacements.json"), "{\"preserve\":true}");
        var result = WorkspaceLayout.Migrate(Path.Combine(root, "updated"));
        Check(result.Root == root && !Directory.Exists(Path.Combine(root, ".snowpaktool")) && !Directory.Exists(Path.Combine(root, "original_files"))
            && !Directory.Exists(Path.Combine(root, "updated")), "Layout migration removes the old wrapper and root-level initial.pak folders");
        Check(File.ReadAllText(WorkspaceOriginals.GetOriginalPath(root, "[media]/classes/trucks/test.xml")) == "default truck"
            && File.ReadAllText(WorkspaceOriginals.GetCurrentPath(root, "[media]/classes/trucks/test.xml")) == "edited truck", "Grouped initial.pak baseline and edits are preserved");
        Check(File.ReadAllBytes(Path.Combine(root, "region-maps", "updated", "level_us_01_01.pak")).SequenceEqual(new byte[] { 1, 2, 3, 4 })
            && File.Exists(Path.Combine(root, "progression-settings.json")), "Map data and settings are lifted into workspace root");
        var newPlan = File.ReadAllText(Path.Combine(root, "region-maps", "pending-replacements.json"));
        Check(!newPlan.Contains(".snowpaktool") && newPlan.Contains("initial.pak")
            && Directory.EnumerateFiles(Path.Combine(root, "layout-backups"), "pending-replacements.json", SearchOption.AllDirectories).Any(file => File.ReadAllText(file).Contains("preserve")),
            "Pending paths are remapped and conflicting metadata is retained in layout backups");
        Check(WorkspaceOriginals.GetWorkspaceRoot(Path.Combine(root, "initial.pak", "updated")) == root
            && WorkspaceOriginals.GetUpdatedRoot(Path.Combine(root, "updated")) == Path.Combine(root, "initial.pak", "updated"), "Old and new saved workspace selections resolve to the grouped layout");
        Check(WorkspaceLayout.Migrate(root).Moves.Count == 0, "Layout migration is safe to repeat");
        Console.WriteLine("PASS: workspace layout migration, metadata path remapping, conflict preservation, content integrity and repeat migration.");
    }

    private static void StartPanelRegression(string workspace)
    {
        var pendingPak = true;
        var panel = new StartChangesPanel(() => workspace, new Button { Text = "Pack to new PAK" },
            _ => pendingPak ? [new(WorkspaceChange.Pending, "rank_settings.sso", "Rank 2 XP", "100", "150")] : [],
            () => pendingPak = false, () => { PendingTruckReplacements.ApplyAll(workspace); return Task.CompletedTask; }, () => Task.CompletedTask);
        using var host = new Form { Size = new Size(1100, 850) }; host.Controls.Add(panel); host.Show();
        void Refresh()
        {
            var task = panel.RefreshAsync(); var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(5); }
            task.GetAwaiter().GetResult(); Application.DoEvents();
        }
        Refresh(); var pak = Field<DataGridView>(panel, "_changes"); var maps = pak;
        Check(pak.Rows.Cast<DataGridViewRow>().Any(row => row.Cells["State"].Value?.ToString() == WorkspaceChange.Pending)
            && pak.Rows.Cast<DataGridViewRow>().Any(row => row.Cells["State"].Value?.ToString() == WorkspaceChange.InFiles)
            && maps.Rows.Cast<DataGridViewRow>().Any(row => row.Cells["State"].Value?.ToString() == WorkspaceChange.Pending), "Start shows saved file changes together with pending PAK and map edits");
        Check(pak.Rows.Cast<DataGridViewRow>().Where(row => row.Cells["State"].Value?.ToString() == WorkspaceChange.Pending).All(row => row.DefaultCellStyle.BackColor == AppColors.Get("EngineMatch"))
            && pak.Rows.Cast<DataGridViewRow>().Where(row => row.Cells["State"].Value?.ToString() == WorkspaceChange.InFiles).All(row => row.DefaultCellStyle.BackColor == AppColors.Get("DAF8E0")), "Start uses distinct state colors");
        using (var image = new Bitmap(host.Width, host.Height))
        { host.DrawToBitmap(image, new Rectangle(0, 0, host.Width, host.Height)); image.Save(Path.Combine(AppContext.BaseDirectory, "start-sections-preview.png")); }
        Field<Button>(panel, "_applyButton").PerformClick();
        var until = DateTime.UtcNow.AddSeconds(15);
        while (Field<bool>(panel, "_applying") && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); }
        Refresh(); Check(!pendingPak, "Start unified Apply invokes pending editor saves");
        Check(PendingTruckReplacements.Load(workspace).Count == 0, "The same Apply commits queued map replacements");
        host.Close();
        Console.WriteLine("PASS: Start PAK/map sections, applied/pending lists, state colors and apply actions.");
    }

    private static void ReplacementPopupRegression(string workspace, RegionTruckEntry source, TruckEquipmentCatalog equipmentCatalog)
    {
        var enabled = new[] { new CustomTruck(source.TruckCode, source.Truck, source.Class),
            new CustomTruck("chevrolet_ck1500", "Chevrolet CK1500", "Scout"), new CustomTruck("tayga_6436", "Tayga 6436", "Offroad") };
        using var dialog = new TruckReplacementDialog(workspace, source, () => enabled, equipmentCatalog);
        dialog.Show();
        var list = Field<DataGridView>(dialog, "_list");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (list.Rows.Count == 0 && clock.Elapsed < TimeSpan.FromSeconds(45)) { Application.DoEvents(); Thread.Sleep(10); }
        Check(list.Rows.Count == enabled.Length, "Popup uses exactly the enabled truck source");
        Check(ReferenceEquals(Field<TruckEquipmentCatalog>(dialog, "_equipmentCatalog"), equipmentCatalog)
            && !equipmentCatalog.TryGetComparisonProperties("tayga_6436", out _), "Popup reuses the region catalog and leaves unselected comparisons lazy");
        var comparison = Field<DataGridView>(dialog, "_comparison");
        Check(comparison.Rows.Count > 10 && comparison.Columns[1].HeaderText.Contains("map equipment")
            && comparison.Columns[2].HeaderText.Contains("defaults"), "Selection fills side-by-side equipment comparison with explicit origins");
        list.Sort(list.Columns["Truck"]!, System.ComponentModel.ListSortDirection.Descending);
        Check(list.Rows[0].Cells["Truck"].Value?.ToString() == "Tayga 6436", "Candidate list sorts by column headers");
        list.CurrentCell = list.Rows[0].Cells[0];
        // Change selection before the uncached comparison finishes; its result must not replace the new one.
        TableHeaderBehavior.Attach(list).AddFilter("Class").SelectValues(new[] { "Scout" });
        Check(list.Rows.Count == 1 && list.Rows[0].Cells["Truck ID"].Value?.ToString() == "chevrolet_ck1500", "Class filter restricts replacement candidates");
        Check(comparison.Columns[2].HeaderText.Contains("Chevrolet CK1500"), "Comparison follows the filtered selection");
        var comparisonClock = System.Diagnostics.Stopwatch.StartNew();
        while (!equipmentCatalog.TryGetComparisonProperties("tayga_6436", out _) && comparisonClock.ElapsedMilliseconds < 5000)
        { Application.DoEvents(); Thread.Sleep(5); }
        Application.DoEvents();
        Check(comparison.Columns[2].HeaderText.Contains("Chevrolet CK1500"), "A late background result cannot overwrite the current candidate");
        var tires = TableHeaderBehavior.Attach(list).AddFilter("Default tires");
        tires.SelectValues(new[] { Field<IReadOnlyList<ReplacementTruck>>(dialog, "_trucks").Single(item => item.Truck.Code == "chevrolet_ck1500").Equipment.Tires });
        Check(list.Rows.Count == 1, "Tire filter combines with class filter");
        Field<CheckBox>(dialog, "_differences").Checked = true;
        Check(comparison.Rows.Count > 0 && comparison.Rows.Cast<DataGridViewRow>().All(row => !Equals(row.Cells[1].Value, row.Cells[2].Value)), "Differences-only comparison hides equal properties");
        Application.DoEvents();
        var viewport = Field<SmoothComparisonPanel>(dialog, "_comparisonScroll");
        Check(viewport.MaximumOffset > 0 && comparison.ScrollBars == ScrollBars.None, "Comparison scrolls in an outer pixel viewport");
        viewport.AutoScrollPosition = Point.Empty; viewport.ScrollWheel(-20);
        var scrollClock = System.Diagnostics.Stopwatch.StartNew();
        var offsets = new HashSet<int>();
        while (scrollClock.ElapsedMilliseconds < 350) { Application.DoEvents(); offsets.Add(-viewport.AutoScrollPosition.Y); Thread.Sleep(5); }
        Check(offsets.Count > 1 && -viewport.AutoScrollPosition.Y > 0 && -viewport.AutoScrollPosition.Y < comparison.Rows[0].Height,
            "Precision wheel input animates through pixel offsets smaller than a table row");
        comparison.CurrentCell = comparison.Rows[comparison.Rows.Count - 1].Cells[0]; Application.DoEvents();
        Check(-viewport.AutoScrollPosition.Y > 0, "Keyboard cell navigation reveals rows in the outer viewport");
        viewport.AutoScrollPosition = Point.Empty;
        using var search = new CurrentTabSearch(() => dialog);
        Check(search.Search("chevrolet") > 0 && search.Search("ch") == 0, "Popup shares the app search and three-character minimum"); search.Clear();
        var lastProperty = comparison.Rows[comparison.Rows.Count - 1].Cells[0].Value?.ToString() ?? "";
        Check(search.Find(lastProperty) && -viewport.AutoScrollPosition.Y > 0 && comparison.FirstDisplayedScrollingRowIndex == 0,
            "Shared search reveals a comparison match without scrolling the inner table"); search.Clear();
        viewport.AutoScrollPosition = Point.Empty; Application.DoEvents();
        using var preview = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(preview, new Rectangle(0, 0, dialog.Width, dialog.Height));
        preview.Save(Path.Combine(AppContext.BaseDirectory, "replacement-popup-preview.png"));
        dialog.Close();
        using var reopened = new TruckReplacementDialog(workspace, source, () => enabled, equipmentCatalog);
        reopened.Show();
        var reopenedList = Field<DataGridView>(reopened, "_list"); comparisonClock.Restart();
        while (reopenedList.Rows.Count == 0 && comparisonClock.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(reopenedList.Rows.Count == enabled.Length && ReferenceEquals(Field<TruckEquipmentCatalog>(reopened, "_equipmentCatalog"), equipmentCatalog),
            "Reopened popup reuses default equipment and comparison caches"); reopened.Close();
        Console.WriteLine("PASS: replacement popup enabled candidates, sorting, combined filters, live comparison, difference mode and shared search.");
    }

    private static byte[] BuildMap(params string[] ids)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write(20190654u); writer.Write(0u); writer.Write(0u);
        WriteString("test_truck"); writer.Write(new byte[70]); // A reference without a transform must not become a placement.
        foreach (var id in ids)
        {
            WriteString("test_truck"); WriteString(id);
            foreach (var value in new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 100, 10, 200, 1 }) writer.Write(value);
            writer.Write(true);
            for (var i = 0; i < 6; i++) writer.Write(float.MaxValue);
            writer.Write(id == "AWARD" ? 1u : 0u);
            if (id == "AWARD") WriteString("test_upgrade");
            foreach (var field in new[] { id == "AWARD" ? "engine_override" : "", id == "AWARD" ? "gear_override" : "", id == "AWARD" ? "winch_override" : "", id == "AWARD" ? "susp_override" : "", id == "REMOVE" ? "wheels_private" : "", "",
                         id switch { "AWARD" => "chains_1", "REMOVE" => "offroad_1", "UNKNOWN" => "allterrain_1", "UNLINKED" => "mudtires_1", _ => "" } }) WriteString(field);
            writer.Write(new byte[26]);
        }
        var headerEnd = (int)memory.Length;
        writer.Write(Encoding.ASCII.GetBytes("unchanged scene payload after header"));
        var data = memory.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), headerEnd);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), data.Length);
        return data;
        void WriteString(string value) { writer.Write((ushort)(value.Length + 1)); writer.Write(Encoding.ASCII.GetBytes(value)); writer.Write((byte)0); }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
}

namespace SnowPakTool.Desktop
{
    internal static class WorkspaceDataCache
    {
        public static long ContentVersion { get; set; }
        public static string ReadAllText(string path, Encoding? encoding = null) => File.ReadAllText(path, encoding ?? Encoding.UTF8);
        public static bool TryGetCurrentFiles(string workspace, string directory, string pattern, SearchOption option, out IReadOnlyList<string> files) { files = []; return false; }
        public static Task PreloadAsync(string workspace, bool forceReload = false) => Task.CompletedTask;
        public static Task PreloadOwnedAsync(string workspace) => Task.CompletedTask;
        public static IReadOnlyDictionary<string, string> GetLocalizedNames(string workspace) => LocalizedStrings.Load(WorkspaceOriginals.GetCurrentPath(workspace, Path.Combine("[strings]", "strings_english.str")));
        public static IReadOnlyDictionary<string, RegionMetadata> GetRegionMetadata(string workspace) => new Dictionary<string, RegionMetadata>();
    }
    internal sealed record RegionMetadata(string Name);
}
