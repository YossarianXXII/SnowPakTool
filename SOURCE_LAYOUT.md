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

## Truck configuration tool (`src/SnowTruckConfig`)

`Program.cs` remains the entry point. `Trucks` contains truck transformations, `Localization` contains language/string handling, and `Utilities` contains XML and pair helpers.

## Maintenance

Namespaces, project paths, embedded-resource paths and runtime workspace paths are unchanged. SDK projects discover C# files recursively; moving a source file normally needs no project-file edit. Active regression projects under `.checks` that explicitly link source files must have their `Compile Include` paths updated when those files move. Historical preview and baseline snapshots are left in place.

Build the desktop application from this directory:

```powershell
dotnet build src/SnowPakTool.Desktop/SnowPakTool.Desktop.csproj -c Release
```

The source move and regression-path changes are recorded in `source-folder-reorganization.diff`.
