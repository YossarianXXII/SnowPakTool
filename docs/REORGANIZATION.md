# Repository reorganization — 6 November 2026

The project directory remains `SnowRunnerTools-master` locally. Its Git remote is
`https://github.com/YossarianXXII/SnowPakTool.git`. The repository has an import
commit followed by a separate organization commit so the moves can be reviewed.

## Changes

- Moved `src/SnowRunnerTools.sln` and `src/.editorconfig` to the repository root.
- Kept the three production projects and feature folders under `src` unchanged.
- Moved the original MSTest project to `tests/SnowTruckConfig.Tests` and rebased
  its production project reference.
- Migrated 30 regression projects to `tests/Regression` and included them in the
  solution. DLL HintPath references became ProjectReference items; the two
  intentional source-isolation checks retain their linked-source design.
- Retained eight linked/copied-source harnesses under `tests/Legacy`, four
  optional research/benchmark programs under `tools/Diagnostics`, and two
  explicitly historical baseline projects under `docs/history/checks`.
- Moved feature notes to `docs`, reviews to `docs/reviews`, and historical diffs
  to `docs/history/patches`. Updated current documentation links.
- Archived the inactive .NET Framework 4.0 project definition as
  `docs/history/legacy/SnowTool.csproj.txt`.
- Added SDK selection, shared artifact-output settings, and Build/Test/Publish
  PowerShell entry points. Generated output goes under ignored `artifacts`.
- Updated relative repository-root/output paths in relocated checks. The planner
  regression's synthetic cases run without a game-analysis export; its real-data
  portion now requires `--real-fixture <file>` explicitly.
- Added repository agent guidance and exclusions for local snapshots, release
  folders, shortcuts, and raw game/workspace data. Existing embedded UI resources
  and attribution notices remain source inputs.

Existing `.checks`, `bin`, `obj`, and `publish-*` output is preserved locally but
is not imported into Git. The external `../workspace` directory is untouched.

## Verification

- Release solution build: all 34 projects built, zero errors. The first complete
  build reported 17 warnings: two legacy `netcoreapp3.1` target warnings and
  nullable warnings in existing regression programs. These were not suppressed.
- Original MSTest suite: 20 tests passed using process-scoped
  `DOTNET_ROLL_FORWARD=Major`; production runtime settings were not changed.
- Isolated default smoke suite: SharedPreset, VariousRegression, the synthetic
  UpgradePlannerRegression cases, and WorkspaceLayoutMigration passed.
- Framework-dependent desktop publish succeeded at
  `artifacts/publish/SnowPakTool.Desktop/SnowPakTool.Desktop.exe`. Existing local
  release folders were left intact.
- A subsequent sandboxed restore could not contact NuGet's vulnerability-data
  endpoint and cached NU1900 audit warnings. The final build/publish still
  succeeded; those warnings are not a completed dependency vulnerability audit.
- The migration smoke command generates a disposable legacy-shaped workspace
  under `artifacts/checks`, then verifies preserved file contents after migration.
- Real-data planner checks and other workspace-dependent/interactive checks are
  not included in the default smoke suite. Legacy harnesses are not claimed to
  be current passing regressions.

Build/publish logs and generated fixtures are local artifacts, not committed game
data. See `SOURCE_LAYOUT.md` and `../tests/README.md` for current entry points.

The application algorithms, public/internal type identities, embedded-resource
names, target frameworks, and archive safety behavior were not changed by this
organization work. The other issues in the dated code review remain separate work.
