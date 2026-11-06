# Loading performance

Measured locally on 2026-11-01 with the active workspace, installed map archives and Debug build. These are single-process measurements with the OS file cache already warm, not cold-disk guarantees.

| Work | Before | After |
| --- | ---: | ---: |
| Regions-Trucks catalog (56 maps) | 6.29 s | 1.27 s |
| Placement scanning alone | 6.18 s | 0.77 s |
| Comparison options for all 120 trucks | 11.05 s | 0.17 s |

The map-catalog measurement excludes workspace preload (about 0.99 seconds in the optimized run) and UI painting. The comparison-options measurement is a backend benchmark; the popup now prepares full comparisons only for the original and initially selected trucks rather than all candidates.

The optimized popup was also measured with all 120 trucks enabled, from showing the window until the initial comparison was populated:

- First opening, reusing the Regions-Trucks equipment catalog: **0.33 seconds**.
- Reopening with the same catalog: **0.16 seconds**.

The enabled-truck provider in this UI benchmark supplies an already prepared list; first-time loading of the Owned Content tab is not included.

Validation: all **248** complete placement records match the pre-optimization JSON snapshot, with **56 maps** and **zero warnings** before and after. Regression checks cover classification, equipment/default resolution, persistent overrides, filters, sorting, shared search, scrolling, cache invalidation, popup reuse and stale selection results.

Benchmark runner: `.checks/LoadingBenchmark/LoadingBenchmark.csproj`, which uses the built desktop assembly and actual workspace cache. Snapshots and build output remain under `.checks/`.
