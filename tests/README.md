# Test projects

Run `pwsh ./tools/Build.ps1` from the repository root to build the solution.
Run `pwsh ./tools/Test.ps1` for the isolated default smoke suite, or select a named
regression executable with `pwsh ./tools/Test.ps1 -Check FrameFuelRegression`.
Use `-NoBuild` only after building the same configuration.

## Default smoke suite

The defaults are SharedPreset (portable export/stage/apply and synthetic map
fixtures), VariousRegression (tire edits and generated fixtures),
UpgradePlannerRegression (synthetic planner cases), and
WorkspaceLayoutMigration (synthetic workspace migration). They do not require
installed game archives. Generated fixtures and outputs live under `artifacts`.

The planner's optional real-data checks require an explicitly supplied analysis
file, which is not included in Git:

```powershell
dotnet run --project tests/Regression/UpgradePlannerRegression -c Release -- --real-fixture <original-trucks-upgrades.json>
```

The executable regressions are not framework-discovered unit tests. `dotnet test`
alone does not run them. Non-default checks may read the external `../workspace`
or use application preferences; inspect their program/arguments before running
against a working setup. This repository does not automatically run every UI or
workspace-dependent executable.

## Original unit tests

`SnowTruckConfig.Tests` retains the original MSTest suite and target framework.
Run `dotnet test tests/SnowTruckConfig.Tests/SnowTruckConfig.Tests.csproj -c Release`.
A compatible .NET Core 3.1 runtime or an explicitly selected compatible runtime
roll-forward policy is required for these legacy-targeted unit tests. Framework
modernization is a separate change from repository organization.

For example, the 20 original tests were verified on this machine with
`DOTNET_ROLL_FORWARD=Major` set for the test process. This is an explicit opt-in,
not a change to the production runtime policy.

## Maintained regression projects

Most projects reference `src/SnowPakTool.Desktop` through `ProjectReference`.
VariousRegression and UpgradePlannerRegression intentionally compile selected
production files with narrow stubs. Historical source snapshots are separate in
`Legacy/` and `docs/history/checks/`; those projects are not included in the solution.

- `AddonExploration`
- `AddonFreeze`
- `AnkCivilian`
- `ExplorationUnlock`
- `FrameAddonRegression`
- `FrameFuelRegression`
- `HardTiresRegionOrder`
- `LogsHover`
- `MapSceneSmoke`
- `MissionUI`
- `Navigation`
- `OwnedDlcPanes`
- `RankStaging`
- `RankSummary`
- `SharedPreset`
- `SharedTableHeaders`
- `SmallBatch8`
- `StartSmoke`
- `TireProgression`
- `TrailerRegression`
- `truck-properties`
- `TruckHeaderFilters`
- `UnifiedChanges`
- `UnlockRanks`
- `UpgradeApply`
- `UpgradeAppTrial`
- `UpgradePlannerRegression`
- `VariousRegression`
- `WinchSummaryRegression`
- `WorkspaceLayoutMigration`
