# Slot-based upgrade selection draft

`UpgradeDistributionPlanner.Generate(input, seed, options)` selects replacement rewards from a supplied eligible catalog. Each pickup keeps its original upgrade type. There is no locality-percentage roll, whole-plan score or swap search. This supersedes the earlier shuffle planner.

```csharp
var input = new UpgradeDistributionInput(
    originalPickupSlots,
    fullVerifiedUpgradeCatalog,
    effectiveObtainableTruckHomes,
    finalizedRegionOrder);

var draft = UpgradeDistributionPlanner.Generate(
    input, seed: 20261105,
    options: new UpgradeDistributionOptions(
        FirstRegionForeignSharedChance: .70));
```

## Selection rules

The original reward's compatibility determines whether a slot is truck-specific (one compatible model) or shared (multiple models). Compatibility must cover the full truck catalog, independently of which models currently appear on maps.

Metal detectors and definitions with `KeepInPlace=true` stay at their exact original source/map/zone/reward-index identity. Other slots are processed with truck-specific slots first, prioritizing scarce local candidate pools; slots without local options come afterward. Shared slots in the first progression region are considered before other shared slots. Seeded random tie-breaks avoid relying on input ordering.

For an original **truck-specific slot**, try:

1. An unused same-type upgrade compatible with a currently obtainable truck in that region. The replacement may itself be shared if it is compatible.
2. An unused shared upgrade of the same type.
3. Any other unused eligible upgrade of the same type.
4. Reuse from the local, shared, then general same-type pools, in that order. Within a reused pool, favor the least-used IDs.
5. Retain the original reward, with a report note, when no eligible replacement exists.

For an original **shared slot**, try unused shared upgrades of the same type, then any unused same-type upgrade. Only afterward consider shared reuse and general same-type reuse. There is no purchase-only restriction: shared choices may serve regionally obtainable, contract-awarded or store-only trucks.

For shared slots in the first region, a 70% roll by default attempts to prefer candidates with no compatible obtainable truck in that region. If that unused foreign pool is empty, retain unused-first selection from the available shared pool. The other 30% uses the ordinary shared pool, which can also contain foreign parts; 70% is the chance of applying an extra preference, not an exact resulting foreign share. No such preference is applied to truck-specific slots or their fallbacks.

## What stays constant

- Every original pickup is filled, including individual entries in multi-reward zones.
- Each slot retains its type, so map, regional and global type counts remain exact.
- Fixed rewards stay at their original locations.
- Same snapshot, options and seed reproduce the same result, even when the caller reorders slots, definitions or home entries.

Reward IDs and their original occurrence counts are **not** conserved. The full catalog can supply upgrades never originally found on maps. Reuse is permitted after unused pools are exhausted, and specific/shared proportions may change through fallbacks.

## Input and integration

- `Slots`: original source SSO, map, region, zone, reward array index and original reward ID. All regions must occur in `RegionOrder`.
- `Definitions`: one merged definition per exact reward ID, including kind and full compatible-model list. It must include the original rewards as metadata, plus the full replacement catalog. Set `Eligible=false` on stock/default, unavailable or otherwise unwanted replacement candidates. Such an original can still supply slot metadata or remain unchanged if no eligible replacement exists. `KeepInPlace` also excludes a part from replacement pools.
- `TruckHomes`: effective obtainable randomized/draft truck models and their regions, plus direct garage/contract awards. Exclude mission-only and unverified trucks. Repeated copies do not change compatibility; any home region counts as local.
- `RegionOrder`: finalized progression; its first region alone receives the shared-slot foreign preference.

Purchase-only models have no regional home unless randomization places them on a map. Their parts can enter the ordinary same-type fallback pools. Tayga 6455B still needs its direct Maine contract-award home supplied. The confirmed Kirovets tire must use its composite reward ID `wheels_extend_kirovets_k7m_tire_1`.

The planner remains a pure in-memory function. The app now connects it through `UpgradeTrialService` and the compatibility reader in `TruckUpgradeCatalog.cs`. **Randomizer → Upgrades** reads current obtainable truck previews, enabled content and original pickup settings, then persists the resulting preview as app metadata under `workspace/upgrade-randomization/draft.json`. Direct garage rewards are included and the Kirovets composite tire ID is resolved. The reader excludes declared unowned tire packs, stock/default-only parts and objective-locked candidates conservatively. **Map upgrades** reviews, reloads and resets that preview. Input changes are stamped so a stale saved plan is identified. Stage/Apply remains disabled: no upgrade game-file writer has been connected. A later writer must maintain exploration locks and perform source conflict checks before applying SSO changes. “Unused” is measured within the supplied plan scope.

The returned report contains original slot roles, local choices for specific slots, fallback/reuse counts, fixed/retained outcomes, first-region shared outcomes and original/planned type totals. Per-slot results retain the original pickup identity, selected ID, compatibility relation and selection/fallback path.

## Verification

`.checks/UpgradePlannerRegression` tests new IDs outside the old pickup pool, per-slot/regional/global type preservation, fixed rewards, generic eligibility for obtainable trucks, first-region shared-only bias, fallback order, reuse, ineligible/default candidates, deterministic seeds and collection order, validation and empty scopes.

It also checks a real 166-slot baseline and a synthetic randomized truck-home snapshot. The real-sized fixture uses the previously verified map-reward catalog, not the yet-to-be-connected full optional-part catalog. That fixture verifies compatibility and type preservation; the separate new-ID fixture proves that the function can select newly introduced catalog rewards.

Run:

```powershell
dotnet run --project .checks/UpgradePlannerRegression/UpgradePlannerRegression.csproj -c Release
```

Examples are written only under `.checks/UpgradePlannerRegression/results/slot-*.json`.

The separate `.checks/UpgradeAppTrial` integration check uses the real workspace and isolates its saved/reset draft under an analysis workspace. It verifies full-catalog candidates beyond the old reward pool, all 166 original slots, ownership-limited generation, composite tires, direct rewards, effective model overrides, preview persistence, review-grid binding and unchanged original SSO hashes. With the ownership snapshot tested on 5 November 2026 it resolved 2,585 component IDs, exposed 448 eligible replacements (362 absent from the original reward pool), and generated 60 changed rewards across 68 owned-region slots; five selected reward IDs were new to maps.
