# Upgrade storage and replacement investigation

Investigated 4 November 2026 against the current workspace and installed SnowRunner archives. Installed archives and workspace game data were read only. Findings describe this data snapshot, not a guarantee about other game versions or mods.

## Findings

Upgrade pickups link two separate resources by **zone ID**:

| Resource | Role | Observed example |
| --- | --- | --- |
| `level_us_01_01.pak` → `[scene]/data_for_ssl.bin` | Compiled map scene, including the pickup zone's identity and map presentation; physical location stays with this scene | `US_01_01_UPGRADE_CK_SUSPENSION` |
| `initial.pak` → extracted `[ps]/level_us_01_01.sso` | Zone settings: `zoneDesc.<zone>.props.ZonePropertyUpgradesGiver.upgrades[]` lists awarded component IDs | `chevrolet_ck1500_suspension_high` |
| `initial.pak` → `[media]/classes/...` and `[media]/_dlc/...` | Part definitions, localized names, price, exploration/rank unlock flags, installation sockets/component families | `classes/suspensions/s_chevrolet_ck1500.xml`, variant `Name="chevrolet_ck1500_suspension_high"` |

The map archive also has a `level_us_01_01_brands.desc_block` resource description referencing `<ps>:level_us_01_01.sso`. The actual extracted zone reward settings are in the workspace's initial-PAK script tree. The visible upgrade wreck/props and scene zone identity are not the reward definition.

For Black River, the level settings contain seven upgrade givers. Examples:

| Zone ID | Reward ID | Definition kind |
| --- | --- | --- |
| `US_01_01_UPGRADE_CK_SUSPENSION` | `chevrolet_ck1500_suspension_high` | Named suspension variant |
| `US_01_01_UPGRADE_SCOUT_OLD_ENGINE` | `us_scout_old_engine_ck1500` | Named engine variant |
| `US_01_01_UPGRADE_G_SCOUT_OFFROAD` | `g_scout_offroad` | Named gearbox variant |
| `US_01_01_UPGRADE_FLEESTAR_ALLWHEELS` | `international_fleetstar_f2070a_transferbox_allwheels` | TruckAddon file ID; AWD category |

File IDs and variant IDs must be distinguished. An engine container such as `e_us_scout_old_ck1500.xml` is not the ID that the pickup awards. Its contained Engine's `Name` is the reward ID. TruckAddon pickups such as AWD use the addon filename without `.xml`. A suspension-looking zone name does not impose the reward's component type: the explicit upgrades list controls the award. Zone names should remain stable during redistribution.

## Inventory and scene checks

The catalog built from the active workspace overlay contains 6,091 parsed addon/component definitions, plus one unresolved reward row. Breakdown: 5,182 TruckAddons, 139 engines, 88 gearboxes, 220 suspension variants, 15 winches and 447 tires. Tire `Name` values are local to a wheel family; the catalog labels that scope and does not equate them to global pickup reward IDs.

The current level scripts contain **166 reward entries in 166 zones across 56 maps**, with 166 distinct reward IDs. Every zone ID matched an exact null-terminated, uint16-length-prefixed string in that installed map's `data_for_ssl.bin`. The archive lookup used the exact map name, with `_crop` as a filename fallback. This proves the identity link, not complete decoding of zone geometry. Coordinates, rotation, discovery visibility and trigger shapes have not been decoded or changed by this work.

165 reward IDs resolve directly to catalog definitions. `wheels_extend_kirovets_k7m_tire_1`, referenced in `level_ru_08_03.sso`, remains unresolved: the corresponding wheel-family file declares a TruckTire with `Name="tire"`, rather than that exact global reward ID. Do not invent an alias or include this item in an automatic replacement pool until the game's tire-unlock mapping is understood.

The catalog reports six additional source-data warnings: two XML files with undeclared `region` prefixes, a trailer XML with a duplicate attribute, and three cosmetic addons whose named parent definitions are unavailable/ambiguous in the workspace. These are surfaced instead of silently inventing metadata. Local/shared XML templates are shown as source properties without expansion. Explicit file-parent metadata is followed, including inherited AWD install sockets and `_noinherit` boundaries. Full vehicle installation compatibility is not calculated.

## Is replacement possible?

**Yes: replacing the reward at an existing pickup is supported by the data structure and the official zone specification.** Keep the zone ID and scene unchanged and replace the appropriate string in its `upgrades` list. Swapping these lists across maps reorganizes the rewards while preserving pickup locations. A zone can list multiple rewards; future persistence must identify a reward by source file + zone ID + array position, not just by current reward ID.

An isolated copy of Black River's extracted settings was changed from `chevrolet_ck1500_suspension_high` to `us_scout_old_engine_2` at the same zone. The production reader reparsed the changed file and checked that all seven reward entries remain, the selected zone awards the new ID, and the workspace source stays unchanged. This demonstrates a cross-category reference change at the settings level. It has **not** been packed, loaded or collected in the game, so runtime behavior remains to be verified on a disposable game setup/save.

For these reward swaps, the write target is the **initial PAK's level SSO settings**, through the existing cache-block/PAK pipeline. The truck scene binary writer is not the appropriate writer. Physically relocating, adding or removing pickup zones is a separate operation requiring more scene-format work.

A future writer should:

1. Preserve source hashes, original reward IDs, zone IDs, array order and unrelated script text. Use an exact parsed/scoped edit rather than replacing an ID everywhere in a file.
2. Retain drafts/staged edits and conflict checks independently from truck replacements, then apply the changed level settings through Start's PAK workflow.
3. Preserve a complete reward pool when redistributing so exploration-locked parts remain discoverable. Check ownership, content availability and component definitions; avoid unresolved IDs.
4. Review `UnlockByExploration`, rank locks and any content-specific lock/unlock settings. Replacing the pickup reference alone does not rewrite those independent restrictions or vehicle compatibility.
5. Verify behavior in a new/disposable save and separately assess existing-save collected/unlocked state. This investigation did not inspect or alter saves and does not establish how already-collected zones behave after a change.

## UI delivered

Editors → Addons now uses Cargo's table, description and source-properties layout. It loads in the background and provides Reload addons, shared sortable/filterable headers, localized names, definition type/category, scoped IDs, socket/family, content, explicit/inherited price/rank/exploration metadata and referenced maps. Selecting a row lists the precise map, zone, array index and SSO source alongside XML properties and their source files. This catalog is read only; no upgrade randomization or staging has been enabled.

## Reproducible evidence

- `.checks/AddonResearch/Program.cs`: real-workspace inventory, inherited AWD metadata, map-reference joins, isolated replacement check and UI rendering.
- `.checks/AddonResearch/bin/Release/net10.0-windows/inventory.json`: detailed inventory and warnings.
- `.checks/AddonResearch/InspectScenes.py` and `scene-links.json`: read-only scene/zone matching, with offsets and archive paths.
- `.checks/AddonResearch/bin/Release/net10.0-windows/replacement-proof/[ps]/level_us_01_01.sso`: isolated replacement sample only.

Official references:

- [UpgradesGiver zones](https://expeditions-guides.saber.games/map_modding/creating_a_map/zones/snowrunner_zones/upgrades_giver_zones/): the upgrades list contains IDs of the awards; multiple awards are supported.
- [How to identify IDs of truck parts](https://expeditions-guides.saber.games/map_modding/creating_a_map/trucks/how_to_identify_ids_of_truck_parts/): part definitions and IDs live in initial.pak, including DLC definitions.
- [Locking and unlocking trucks and upgrades](https://expeditions-guides.saber.games/map_modding/creating_a_map/other_map_settings/content_settings/locking_and_unlocking_trucks_and_upgrades/): content locks and level-based availability are separate settings to consider.
