# Source layout

Keep related features together. Use shared folders for code used by several features, and avoid adding a folder for every class.

## Desktop application (`src/SnowPakTool.Desktop`)

| Folder | Contents |
| --- | --- |
| `Application` | Entry point, main window, settings and user preferences |
| `Catalogs` | Truck, equipment, addon and cargo catalogs; localization and game-data readers |
| `Editors` | Addons, cargo, engines, experience, truck properties and winches, grouped by editor |
| `Maps` | Map catalogs, placements, replacement dialogs, staging and archive writing |
| `Planning` | Ownership, truck groups, tiers and region progression |
| `Presets` | Shared presets, overwrite review and browsing restrictions |
| `Randomization` | Shared logging plus truck and upgrade randomizers and their rules |
| `UI` | Shared controls, search, table headers, styling, thumbnails and conflict dialogs |
| `Workspace` | Workspace paths, preserved originals, caching, change auditing and Start changes panel |
| `Assets` | Existing embedded truck and region images and metadata |

The winch editor's existing `TruckEditTab.cs` name is retained. The folder identifies its purpose without renaming the class.

## Archive tool (`src/SnowPakTool`)

`Program.cs` remains the command-line entry point. `Archives` handles PAK operations, `CacheBlocks` handles cache-block formats, `Containers` abstracts file sources, `Lists` handles load/sound lists, `Utilities` contains shared helpers, and the existing `Zip` folder contains ZIP records.

## Repository layout and maintenance

The repository root contains `SnowRunnerTools.sln`, `global.json`, the shared
`Directory.Build.props`, `.editorconfig`, README, license, and agent guidance.
Production projects remain under `src/`; their namespaces and embedded-resource
identities are unchanged.

| Folder | Purpose |
| --- | --- |
| `tests/Regression` | Maintained executable regression checks, included in the solution |
| `tests/Legacy` | Historical linked/copied-source checks, excluded from the solution |
| `tools` | Build, test and publish entry points |
| `tools/Diagnostics` | Workspace-dependent research and benchmark executables |
| `docs` | Feature notes, planning, investigations and source layout |
| `docs/reviews` | Dated code-review reports |
| `docs/history/patches` | Preserved historical change diffs |
| `docs/history/checks` | Explicitly historical baseline harnesses |
| `docs/history/legacy` | Archived legacy project definition, kept as text |
| `artifacts` | Ignored build, restore, test-fixture and publish output |

Run commands from the repository root:

```powershell
pwsh ./tools/Build.ps1
pwsh ./tools/Test.ps1
pwsh ./tools/Publish.ps1
```

`UseArtifactsOutput` places SDK outputs beneath `artifacts`. The test executables
reference production projects instead of prebuilt DLL paths, so rebuilding the
solution supplies the current assemblies. Two maintained source-isolation checks
still link selected production files intentionally. See `tests/README.md`.

Existing local `.checks`, `bin`, `obj`, and `publish-*` snapshots are left on disk
and ignored. They are not the current build/test entry points. The external game
workspace at `../workspace` is not moved or changed by this reorganization.

The root legacy `SnowTool.csproj` was not part of the active solution; its contents
are retained as `docs/history/legacy/SnowTool.csproj.txt`. Production target
frameworks, assembly identities, game-data paths and archive behavior are unchanged.