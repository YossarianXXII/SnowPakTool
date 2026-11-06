# Diagnostics and investigations

AddonResearch, MapCounts, UpgradeRegionAnalysis, and LoadingBenchmark are optional
workspace-dependent programs, not automatic regression tests. Each project now
references production projects rather than a prebuilt DLL. Build a selected project
with `dotnet build tools/Diagnostics/<name>/<name>.csproj -c Release`.

Inspect program arguments and workspace paths before running. Local game workspaces
and research output are not committed. Historical Python investigation scripts are
retained beside their related project; no downloaded or extracted game data is included.