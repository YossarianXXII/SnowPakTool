# Code review findings — 6 November 2026

## Scope and evidence

This document consolidates the 14 findings from the first review and the eight additional findings from the follow-up review. It records analysis and proposed work; it does not authorize or implement application changes.

The follow-up source inventory contained 136 C# files and approximately 23,872 lines under `src`, excluding generated build/publish folders. Inspection concentrated on archive operations, workspace caching, catalogs, staged editors, randomizer UI, shared presets, and regression-project wiring. This is not a claim that every method received an exhaustive audit.

Evidence consists of source inspection and small in-memory checks of path normalization and numeric parsing. No application builds, regression executables, SonarQube/SonarLint scans, benchmarks, or in-game checks were run during these reviews. Suggested tests below are future verification, not completed checks. Line numbers may move; links identify files and the text identifies relevant methods.

**Correction to the first review:** substantial regression harnesses exist in the hidden `.checks` directory, including preset, staging, cargo, frame-addon, and various-editor checks. The earlier statement that tests only covered string token parsing was incomplete. The concern is discoverability, dependency drift, and reliable execution of maintained checks.

Priority means recommended implementation order, not measured incident frequency:

- **High:** potential unintended file creation, corrupted binary output, or broken asynchronous lifecycle.
- **Medium:** correctness, recovery, responsiveness, or substantial maintenance concerns.
- **Lower:** cleanup or optimization with limited demonstrated immediate impact.

Confidence distinguishes a source-confirmed defect/behavior from a conditional risk or a refactoring opportunity. Performance benefits are not quantified without measurements. Static-analysis categories are descriptive; no particular Sonar rule result is asserted.

## Finding index

| ID | Priority | Finding | Evidence classification |
| --- | --- | --- | --- |
| F01 | High | Cache-block extraction lacks destination containment | Source-confirmed; path example checked in memory |
| F02 | High | Workspace cache load generations and readiness are unsafe | Source-confirmed interleavings; runtime reproduction pending |
| F03 | High | Binary helpers share a mutable process-wide buffer | Source-confirmed design; concurrent corruption is conditional |
| F04 | Medium | Binary readers assume a complete single read | Source-confirmed stream-contract issue |
| F05 | Medium | Legacy Country migration contradicts validation order | Source-confirmed control flow |
| F06 | Medium | Cargo loading runs on the UI thread | Source-confirmed; timings unmeasured |
| F07 | Medium | Randomizer UI repeats rule/layout/logging code | Refactoring opportunity |
| F08 | Medium | JSON persistence has inconsistent infrastructure | Refactoring and conditional reliability risk |
| F09 | Medium | XML readers/patchers disagree on supported syntax | Source-confirmed parsing inconsistency |
| F10 | Medium | Catalog matching and property traversal repeat work | Source-confirmed complexity; timings unmeasured |
| F11 | Medium | Cache types and data preparation depend on UI classes | Architectural/refactoring opportunity |
| F12 | Medium | Silent catches obscure failed loads and staging | Source-confirmed diagnostic limitation |
| F13 | Lower | Map CRC duplicates a slower algorithm | Source-confirmed duplication; benefit unmeasured |
| F14 | Lower | Unused private archive writer remains | No source callers found |
| F15 | Medium | Cargo numeric validation and display disagree | Parser mismatch checked in memory |
| F16 | Medium | Shared-preset export blocks the UI and copies full trees | Source-confirmed; timings unmeasured |
| F17 | Medium | Preset settings are validated as JSON, not store schemas | Source-confirmed validation gap |
| F18 | Medium | A rollback failure stops remaining recovery attempts | Source-confirmed failure path; injection test pending |
| F19 | Medium | Cargo and tire XML source scanners duplicate machinery | Refactoring opportunity |
| F20 | Medium | Staged-edit apply pipelines duplicate infrastructure | Refactoring opportunity |
| F21 | Lower | Preset browsing permissions depend on control captions | Source-confirmed coupling; behavioral risk on UI changes |
| F22 | Medium | Some regression projects have stale source dependencies | Source-confirmed missing dependencies; build not run |

## F01 — Cache-block extraction lacks destination containment

**Evidence.** [CacheBlockReader.cs](src/SnowPakTool/CacheBlocks/CacheBlockReader.cs), `Unpack`, combines `targetDirectory` with `item.ExternalName` and creates directories/files. [CacheBlockFileFileEntry.cs](src/SnowPakTool/CacheBlocks/CacheBlockFileFileEntry.cs), `InternalNameToExternalName`, checks invalid characters but does not reject parent-directory segments.

**Trigger and impact.** An internal name such as `<ps>\..\..\outside.txt` becomes `[ps]\..\..\outside.txt`. The in-memory normalization example resolved an extraction root of `S:\example\extract` to `S:\example\outside.txt`. A crafted input can therefore create files outside the requested extraction directory. `FileMode.CreateNew` prevents overwriting an existing file in this path, but does not prevent unintended creation or directory creation. No crafted archive was executed.

**Proposed improvement.** Validate the canonical destination immediately before extraction. Reject rooted names and traversal segments, and require the full destination to start with the normalized root plus a directory separator. Decide explicitly whether extraction through existing filesystem links/junctions is supported; string containment alone does not constrain those links. Reuse the path-safety policy where appropriate, without coupling the archive library to Desktop.

**Verification.** Cover ordinary names, nested folders, `..`, mixed separators, root-prefix siblings, rooted names, and existing junctions if those are in scope. Assert no filesystem mutation occurs for rejected entries and normal cache-block extraction remains compatible.

## F02 — Workspace cache loads can publish stale results or strand waiters

**Evidence.** [WorkspaceDataCache.cs](src/SnowPakTool.Desktop/Workspace/WorkspaceDataCache.cs), `PreloadAsync`, `PreloadOwnedAsync`, and `LoadSnapshot`, share `_workspace`, `_loadTask`, `_snapshot`, and `_ownedReady`. Publication checks compare workspace paths, not the identity of the load. The first workspace-mismatch return in `LoadSnapshot` does not complete its captured `ownedReady` signal.

**Failure scenarios.** First, load A begins for a workspace, a forced reload B begins for the same path, B finishes first, and A later publishes its older snapshot because the path still matches. Second, a caller awaits A's readiness, the active workspace changes before A's first publication check, and A returns without completing that signal. A further race exists between requesting preload and retrieving the global readiness task in `PreloadOwnedAsync`.

**Proposed improvement.** Represent a load as one object containing workspace, generation, completion/readiness tasks, cancellation, and results. Return/capture that object atomically. Only the active generation may publish; every exit must complete, fault, or cancel its own signals. Prefer returning the requested snapshot to callers rather than rereading unrelated global state after an await.

**Verification.** Use controlled barriers rather than timing sleeps: finish same-path reloads in reverse order; switch workspaces at each publication boundary; fault a load; force repeated reloads. Assert the newest generation wins, no caller receives another workspace's data, and all awaited tasks terminate.

## F03 — Binary helpers share a mutable buffer

**Evidence.** [IOHelpers.cs](src/SnowPakTool/Utilities/IOHelpers.cs) uses static `__Buffer` through `GetBuffer` for primitive reads/writes, string encoding, chunk copying, and CRC processing. The array can also be replaced when a larger buffer is requested. There is no ownership or synchronization around the full operations.

**Impact.** Two overlapping operations can fill or modify the same array before the other operation consumes it, producing incorrect values or output bytes. This is a conditional library-level risk: inspection establishes that concurrent use is unsafe, not that every current UI path performs overlapping archive operations. Callback/reentrant use deserves attention too. Adding parallel packing later would expose this design immediately.

**Proposed improvement.** Give each operation exclusive ownership of its buffer. Use small local/stack buffers for primitives where the target framework permits, and local or rented arrays for larger copies. Account for the core project's `netcoreapp3.1` target when selecting APIs. Avoid a single global lock that serializes otherwise independent archives.

**Verification.** Run independent readers/writers concurrently with distinct byte patterns; verify round trips and output hashes. Include requests that grow the buffer and a chunk callback that invokes another helper. Ensure rented arrays are returned on exceptions and input archives remain untouched.

## F04 — Binary readers assume one read fills the requested range

**Evidence.** [IOHelpers.cs](src/SnowPakTool/Utilities/IOHelpers.cs), `ReadByteArray`, `ReadString`, and both `ReadValue` overloads, perform one `Stream.Read` and treat a short result as end-of-stream. [CacheBlockReader.cs](src/SnowPakTool/CacheBlocks/CacheBlockReader.cs), `ReadHeader`, makes the same assumption for the signature.

**Impact.** `Stream` permits a positive short read even when more data will become available. The helpers can reject valid input supplied by a throttled, wrapped, or decompression stream. Ordinary file-based paths may mask the defect. Archive-controlled lengths/counts also deserve explicit validation before allocation and offset arithmetic.

**Proposed improvement.** Centralize an exact-read loop that continues until the range is filled and throws only when progress ends. Use a framework exact-read API only where the project's target supports it. Validate negative lengths, implausible counts, and checked offset arithmetic with errors identifying the relevant structure.

**Verification.** Wrap a valid stream so each read returns only one to three bytes; parsing should still succeed. Truly truncated data must fail deterministically. Check zero-length requests, malformed lengths, and both primitive-reader overloads. Coordinate the implementation with F03 so exact reads do not retain shared-buffer races.

## F05 — Legacy Country entries are rejected before migration

**Evidence.** [TruckPropertiesStore.cs](src/SnowPakTool.Desktop/Editors/Trucks/TruckPropertiesStore.cs), `ReadPresetSnapshot`, calls `ReadEdits` on all values. `ReadEdits` rejects attributes absent from `Specs`; Country is absent. The following comment and README describe ignoring the short-lived legacy Country payload.

**Trigger and impact.** A version-2 snapshot containing an old Country entry is rejected before supported entries can load. This contradicts the documented compatibility behavior. This is a rejection/migration defect, not evidence that current code applies unintended country changes. The strongly typed numeric edit model also means migration needs to consider the actual historical JSON shape before deserializing it too early.

**Proposed improvement.** Inspect versioned legacy input, remove only the explicitly retired Country payload, then deserialize and validate the supported schema. Continue rejecting arbitrary unknown attributes rather than weakening all validation. Keep migration separate from current model validation and use captured historical fixtures to define compatibility.

**Verification.** Cover legacy array snapshots, versions 1/2, mixed Country plus valid numeric edits, Country-only snapshots, malformed retained entries, unknown attributes, and attribute-removal edits. Confirm no migration path writes country values or silently discards supported edits.

## F06 — Cargo loading blocks the UI

**Evidence.** [TrucksCargoTab.cs](src/SnowPakTool.Desktop/Editors/Cargo/TrucksCargoTab.cs), `EnsureLoaded` and `LoadCargo`, call `CargoCatalogService.Load` and `CargoMassStore.ReadRows` synchronously. The reload event awaits cache preload, then calls `LoadCargo` after returning to the UI context. [CargoCatalogService.cs](src/SnowPakTool.Desktop/Catalogs/CargoCatalogService.cs) now also resolves loose-unit templates and builds editable body targets.

**Impact.** Opening/reloading Cargo can freeze navigation, painting, and input while parsing files and constructing the catalog. A warm text cache does not eliminate XML parsing, property collection, or mass-target processing. No timing measurements were collected.

**Proposed improvement.** Load an immutable catalog/row result in a worker task, then bind it on the UI thread. Capture the workspace and a request generation before starting. Reject stale results, handle disposal, and preserve the current distinction between reloading and intentionally retaining unstaged previews. Reuse a loading pattern across catalog tabs rather than implementing another independent lifecycle.

**Verification.** Use a large synthetic catalog and delayed reads; verify the UI remains responsive. Switch workspace or dispose the tab mid-load, request overlapping reloads, and confirm that sorting/filtering, staged restoration, and the existing binding-callback guards still work.

## F07 — Randomizer panes duplicate UI and workflow code

**Evidence.** [TruckRandomizationTab.cs](src/SnowPakTool.Desktop/Randomization/Trucks/TruckRandomizationTab.cs) and [UpgradeRandomizationTab.cs](src/SnowPakTool.Desktop/Randomization/Upgrades/UpgradeRandomizationTab.cs) repeat rule-note rows, add/edit/remove actions, the editing dialog, glyph-button construction, layout fitting, detailed-log filtering, and staging/run state. The first review measured an 87-line contiguous match after trimming indentation; that is a local comparison, not a project duplication percentage.

**Impact.** A UI fix must be repeated across implementations. They already differ in details such as disposed-control checks and run finalization. Differences can be intentional, but copies make that harder to establish and maintain.

**Proposed improvement.** Extract composed controls such as a rule-notes editor and a log pane, then a small run/stage coordinator if still useful. Supply persistence and domain actions as callbacks. Keep truck, mission, and upgrade eligibility/algorithms separate; do not use a large inheritance hierarchy or title-string switches to configure behavior.

**Verification.** Check rule persistence failure and rollback of edited notes, intentionally empty lists, resizing, detailed-log toggling, disposal during a run, and button enablement after failure. Preserve existing colors, dimensions, labels, and behavior.

## F08 — JSON persistence has inconsistent guarantees and ownership

**Evidence.** [TierListStore.cs](src/SnowPakTool.Desktop/Planning/TierListStore.cs) and [RegionTierListStore.cs](src/SnowPakTool.Desktop/Planning/RegionTierListStore.cs) use fixed `path + ".tmp"` files. [TruckPropertiesStore.cs](src/SnowPakTool.Desktop/Editors/Trucks/TruckPropertiesStore.cs), `WriteAtomic`, uses unique temporary names and cleanup. Unrelated cargo, addon, and tire stores depend on that truck-specific utility. Multiple stores construct identical serializer options for each save.

**Impact.** Concurrent writers or app instances can collide on fixed temporary names. Cleanup and serialization conventions vary. Unique temp files prevent collisions but do not by themselves resolve concurrent read-modify-write lost updates or guarantee durability across power loss. Per-save options construction is secondary to these reliability and ownership concerns.

**Proposed improvement.** Move atomic replacement and shared JSON options into neutral infrastructure. Define synchronization, cleanup, encoding, and error reporting explicitly; retain per-store schema validation and migration. Do not force every file to adopt identical recovery behavior where its semantics differ.

**Verification.** Inject write/move failures, test concurrent saves and existing stale temp files, and verify the last valid file survives failure. Test store-specific malformed data and round-trip compatibility. State whether cross-process coordination and durable flushes are required rather than assuming atomic rename supplies both.

## F09 — XML readers and patchers accept different syntax

**Evidence.** [TruckPropertiesStore.cs](src/SnowPakTool.Desktop/Editors/Trucks/TruckPropertiesStore.cs), `Root`, `Tag`, and `Attribute`, use regex matching; `Attribute` supports double quotes only. [FrameAddonFuelStore.cs](src/SnowPakTool.Desktop/Editors/Addons/FrameAddonFuelStore.cs) handles both quote styles, while catalog readers use XML fragments. [UpgradeChanges.cs](src/SnowPakTool.Desktop/Randomization/Upgrades/UpgradeChanges.cs) has another selector/source-position approach.

**Trigger and impact.** `FuelCapacity='80'` is valid XML but is not read equivalently by the truck-property helper. Regex matching also does not inherently distinguish comments or the intended structural context. This is a compatibility inconsistency; the review did not demonstrate every possible wrong-target patch scenario.

**Proposed improvement.** Share fragment parsing and exact element/attribute source locations. Parse for identity and validation, then patch only the intended source span. Retain support for sibling template/definition roots, explicit inheritance rules, attribute absence, and the game's template syntax. Avoid serializing an entire document merely to update one attribute, because that can rewrite unrelated text.

**Verification.** Cover both quotes, comments containing tag-like text, templates, repeated elements, whitespace, empty elements, explicit removals, and unrelated text preservation. Test any game-specific name normalization required by existing readers.

## F10 — Catalog matching and property traversal repeat avoidable work

**Evidence.** [CargoCatalogService.cs](src/SnowPakTool.Desktop/Catalogs/CargoCatalogService.cs) filters the complete addons list for each cargo type and the complete loose-unit list for each variant. Its `CollectProperties` and [AddonCatalogService.cs](src/SnowPakTool.Desktop/Catalogs/AddonCatalogService.cs), `Collect`, repeatedly materialize same-name siblings and search for each element's index.

**Impact.** Matching grows approximately with types multiplied by addon candidates, plus variants multiplied by loose-unit candidates. For a parent with many same-name children, repeated sibling materialization/search can be quadratic in that sibling count. Repeated properties/template resolution also allocate extra intermediate arrays. Actual impact depends on workspace size and has not been profiled.

**Proposed improvement.** Build case-insensitive lookups by cargo type and loose-unit load type/slot count once. Walk each parent's children once, maintaining per-name counters and totals to preserve existing path suffix rules. Share the property walker; consider per-load template memoization where repeated includes are significant.

**Verification.** Compare catalog contents, ordering, warnings, unresolved values, and property paths before/after. Benchmark a representative workspace and a synthetic high-sibling-count XML file. Preserve ambiguous loose-weight handling rather than selecting the first candidate.

## F11 — Workspace data preparation is coupled to UI types

**Evidence.** [WorkspaceDataCache.cs](src/SnowPakTool.Desktop/Workspace/WorkspaceDataCache.cs) calls static preparation methods on editor/tab classes, exposes `object` results, and stores object-typed prepared data. Consumers cast to private UI-owned records. `EnsureLoaded` and `EnsureOwnedLoaded` synchronously wait on async work. Large editor classes combine parsing, persistence, UI construction, and workflow orchestration.

**Impact.** The compiler cannot enforce the cache/caller type contract. Data processing is difficult to test without UI dependencies, and synchronous cache access can block the UI when preparation is incomplete. A deadlock was not established: synchronous waiting is a blocking concern here, not sufficient evidence of a deadlock by itself. Moving files into feature folders improves navigation but does not remove these dependencies.

**Proposed improvement.** Move typed prepared models and loaders into services used by both cache and UI. Return typed async results, with explicit readiness boundaries and immutable snapshots. Split domain responsibilities from large controls incrementally, starting with preparation and persistence rather than cosmetic class-size reduction.

**Verification.** Compile-time typed contracts, service-level tests without forms, invalid-source diagnostics, partial readiness, and workspace switching. Coordinate with F02 so architectural extraction does not preserve unsafe global load state under new class names.

## F12 — Silent catches hide actionable failures

**Evidence.** Tier-list loads catch I/O, access, and JSON errors and return defaults. [UpgradeRandomizationTab.cs](src/SnowPakTool.Desktop/Randomization/Upgrades/UpgradeRandomizationTab.cs) and the truck equivalent catch every exception while checking staging readiness and simply disable the action. Cache preparation also suppresses several source errors and later exposes generic missing-data failures.

**Impact.** Missing files, malformed data, and permission failures can look identical. A corrupt saved tier file can appear as an empty/default board; a later save may replace it. A disabled Stage button may provide no explanation. Broad catches at UI boundaries can be legitimate; loss of diagnostic context is the concern, not catching exceptions in itself.

**Proposed improvement.** Distinguish not-found from invalid/unreadable data, preserve the last known valid state where appropriate, and surface a concise status with detailed logs. Return explicit load results/warnings from services. Preserve user custom notes and intentionally empty configurations rather than treating them as corruption.

**Verification.** Exercise absent, corrupt, locked, and access-denied files. Ensure fallbacks are visible, diagnostics identify the source, and failed loads cannot silently overwrite recoverable user data. Avoid flooding logs from repeatedly evaluated readiness checks.

## F13 — CRC logic is duplicated and map CRC uses bit-by-bit processing

**Evidence.** [MapArchiveWriter.cs](src/SnowPakTool.Desktop/Maps/MapArchiveWriter.cs), `Crc`, processes every byte with an eight-iteration bit loop. [Crc32Managed.cs](src/SnowPakTool/Utilities/Crc32Managed.cs) already provides a table-based implementation in the referenced core project.

**Impact.** Large scene buffers require unnecessary per-byte work, and two CRC implementations must remain consistent. This does not imply the map writer's CRC is incorrect. Compression or archive copying may dominate overall write time; no speedup estimate is justified yet.

**Proposed improvement.** Expose/reuse one validated CRC implementation, with an explicit numeric return contract and correct ZIP representation. The existing hash class exposes bytes in a particular order, so replacing the call requires deliberate conversion. Keep the map writer's preservation of unrelated ZIP records, entry order, and compression methods.

**Verification.** Compare empty input, the standard `123456789` CRC-32 vector, random buffers, chunked hashing, and representative scene bytes. Validate resulting ZIP headers and archive readability. Benchmark CRC separately and complete map writes before deciding optimization priority.

## F14 — Unused private archive writer increases maintenance surface

**Evidence.** [ZipPakHelper.cs](src/SnowPakTool/Archives/ZipPakHelper.cs), `StoreFiles`, implements manual ZIP/ZIP64 writing. The active `CreatePak` path uses `ZipArchive`, and source inspection found no calls to the private `StoreFiles` method. Several helper methods support that inactive path.

**Impact.** Readers must distinguish two archive-writing implementations, and format fixes can be mistakenly applied to inactive code. Private unused code adds review and maintenance cost even when it does not affect runtime output. The ZIP record types and header-listing code may still have active callers and must not be removed indiscriminately.

**Proposed improvement.** Confirm no reflection or external tooling depends on the private method, then remove it and only helpers/types shown to be exclusive to it. Retain historical implementation in version history or an explicitly noncompiled reference if necessary. Preserve deterministic timestamps/order and new-output-only behavior of the active writer.

**Verification.** Recheck symbol references, compile the affected projects, and compare active PAK outputs and listing behavior before/after. Check standard/large archive cases relevant to retained ZIP64 support.

## F15 — Cargo numeric validation accepts values its UI cannot display

**Evidence.** [CargoMassStore.cs](src/SnowPakTool.Desktop/Editors/Cargo/CargoMassStore.cs), `Valid` and `Equal`, parse invariant values using `NumberStyles.Float`. [CargoMassEditing.cs](src/SnowPakTool.Desktop/Editors/Cargo/CargoMassEditing.cs), `PreviewTotal`, and [CargoMassDialog.cs](src/SnowPakTool.Desktop/Editors/Cargo/CargoMassDialog.cs), `Input`, use the default `decimal.Parse` styles. Catalog mass reading also accepts float-style values.

**Concrete example.** In-memory checks confirmed that `1e3` parses as 1000 with the store's styles but throws `FormatException` with the display parser. A source or imported staged mass can pass domain validation yet fail when totals are formatted or a dialog opens. The dialog's own input path normalizes ordinary numeric values, so this is not a claim that typing scientific notation into that dialog is currently accepted.

**Proposed improvement.** Use one invariant parser for persisted/source values, and a separately explicit user-culture parser for UI input. Preserve raw attribute text for conflict checks or exact restoration, but calculate/display through the shared numeric result. Do not normalize whole files just to fix display.

**Verification.** Test exponent notation, ordinary decimals, zero, limits, whitespace, missing attributes, malformed values, and Slovak/English UI cultures. Formatting callbacks must not throw for any accepted source or pending value. Keep this focused correction separate from larger cargo refactors.

## F16 — Shared-preset export blocks the UI and duplicates full trees

**Evidence.** [MainForm.cs](src/SnowPakTool.Desktop/Application/MainForm.cs), `SaveSharedPresetAsync`, disables the form and calls `SharedPreset.Export` synchronously. [SharedPreset.cs](src/SnowPakTool.Desktop/Presets/SharedPreset.cs) fingerprints originals, copies full original/updated trees to a temporary directory, renders edits, constructs deltas, fingerprints originals again, and writes/validates the compressed result.

**Impact.** An async method name and wait cursor do not move the export off the UI thread. Work scales with the full workspace, even when a preset contains only a few changes. Peak disk space includes the temporary copy; processing also holds source/output/delta buffers. The small exported file size therefore does not imply a cheap export. No memory/disk/time benchmark was run.

**Proposed improvement.** Capture immutable preview DTOs on the UI thread, then run export in a worker service with progress and cancellation. Do not simply wrap the existing render callback in `Task.Run`, because it reads live controls. Later consider an overlay-backed rendering abstraction using immutable original sources and isolated changed outputs, preserving sender immutability.

**Verification.** Test large originals with a tiny edit, cancellation, disk exhaustion, export failures, and concurrent input changes. Assert sender files/staged previews remain unchanged, output determinism is retained, and cleanup does not hide the primary error. Measure peak temporary disk use as well as elapsed time.

## F17 — Shared presets do not validate each settings schema

**Evidence.** [SharedPreset.cs](src/SnowPakTool.Desktop/Presets/SharedPreset.cs), `Read`, validates allowed planning/metadata keys and parses non-null payloads with `JsonDocument.Parse`. It additionally checks that map-state `Pending` is an empty array. It does not validate each other payload through the destination store's typed schema and rules. `Apply` writes those JSON strings to the planning/metadata destinations.

**Trigger and impact.** A payload such as `42` is valid JSON but is not a valid tier-list document. A malformed or manually altered preset can pass this syntax layer and replace a working configuration; failure or silent defaults occur later when its consumer loads it. Existing path checks, checksums, and binary patch validation are valuable protections but do not establish settings validity.

**Proposed improvement.** Define a preset settings contract mapping every allowed key to versioned deserialization and domain validation. Reuse pure validators from stores rather than invoking loaders that write defaults/migrate files as side effects. Preserve intentional null/missing semantics, custom prose, and empty lists. Validate before staging and again before replacement if required by the lifecycle.

**Verification.** Mutate each payload to wrong root types, null members, duplicates, out-of-range enums/numbers, and inconsistent references. Rejected input must leave planning and workspace files untouched. Retain existing valid-preset round trips and compatibility tests.

## F18 — Rollback stops on its first restoration error

**Evidence.** [SharedPreset.cs](src/SnowPakTool.Desktop/Presets/SharedPreset.cs), `Apply`, records committed outputs and restores them in reverse order inside one catch block. Each `File.Delete` or `File.Copy` can throw. There is no per-target recovery catch, so one restoration failure ends the loop and replaces the original exception.

**Failure scenario.** Several replacements succeed, a later replacement fails, and an earlier target becomes locked or inaccessible before restoration. Recovery stops at that target; other recoverable outputs remain replaced. This requires an additional failure during rollback and was not reproduced. Existing locked-file commit-failure tests do not automatically cover this two-failure scenario.

**Proposed improvement.** Attempt every recovery action, retain the original apply exception, and collect target-specific recovery errors. Keep backups and a clear transaction status/manifest for incomplete recovery. Clean transient sibling `.preset-tmp` files where safe. Specify crash recovery separately: catching exceptions does not provide recovery after process termination or power loss.

**Verification.** Inject one commit failure plus a distinct rollback failure. Assert recovery continues for remaining targets, reports both errors, retains backups/staging, and permits deliberate retry or recovery. Revalidate caches after partial recovery. Do not claim all-or-nothing recovery when restoration itself failed.

## F19 — Cargo and tire source-position scanners duplicate low-level XML logic

**Evidence.** [CargoMassStore.cs](src/SnowPakTool.Desktop/Editors/Cargo/CargoMassStore.cs), `Bodies`, and [TireIceStore.cs](src/SnowPakTool.Desktop/Editors/Various/TireIceStore.cs), `Targets`, both build line-start arrays, maintain depth stacks and sibling counters, derive indexed paths, convert XML reader line/column data to text offsets, and scan opening tags while tracking quote state.

**Impact.** Boundary fixes and format handling must be repeated. The scanners also combine format mechanics with feature-specific decisions, making independent tests harder. Their identity and target-selection rules differ legitimately; extraction should preserve those differences rather than forcing cargo structures into tire semantics.

**Proposed improvement.** Introduce a small immutable fragment-scanning result containing names, attributes, ancestry, indexed path, opening-tag span, and empty-element state. Let each feature derive its own identity and edits. Reuse quote-aware attribute-span handling with F09 where practical, but avoid building a general XML editor framework beyond demonstrated needs.

**Verification.** Cover self-closing siblings, nested repeated names, comments, CRLF/LF, quotes containing `>`, declarations if supported, and fragment roots. Compare existing paths/identities and byte-preserving patches against current regression fixtures. Keep ambiguous-target failures conservative.

## F20 — Staged-edit apply pipelines repeat the same infrastructure

**Evidence.** [CargoMassStore.cs](src/SnowPakTool.Desktop/Editors/Cargo/CargoMassStore.cs), [AddonExplorationStore.cs](src/SnowPakTool.Desktop/Editors/Addons/AddonExplorationStore.cs), [TireIceStore.cs](src/SnowPakTool.Desktop/Editors/Various/TireIceStore.cs), and existing property/fuel stores independently group edits by source, read text, prepare/validate patches, materialize all outputs, copy originals into the overlay, atomically write, invalidate cache, and clear pending data.

**Impact.** Encoding, cleanup, retry, and partial-failure behavior can diverge. Validating all outputs before writes prevents preflight conflicts from causing partial output, but does not make the multi-file commit transactional: an I/O failure after the first write can still leave completed outputs applied. The README explicitly permits completed changes to remain applied for normal Start workflows; that behavior should not be silently changed by refactoring.

**Proposed improvement.** Share prepared-output application, atomic writing, cache invalidation, and completion reporting. Keep patch generation and validation in domain stores. Define idempotent retry and staged-clear semantics explicitly. Keep the stronger shared-preset rollback policy separate from normal staged stores unless a behavioral change is deliberately requested.

**Verification.** Fail validation on the final file, then separately fail a write after one successful output. Verify no writes in the first case, documented partial completion in the second, retained remaining staging, idempotent retry, original preservation, and unrelated XML retention.

## F21 — Preset browsing permissions derive from displayed captions

**Evidence.** [PresetBrowseMode.cs](src/SnowPakTool.Desktop/Presets/PresetBrowseMode.cs), `ViewAction`, allows controls based on captions starting with `Show`, `Reload`, `Refresh`, `Browse`, and similar text, plus special control/column names. Caption changes are observed to reapply the policy.

**Impact.** A wording or localization change can unexpectedly disable navigation or enable a setup-changing action. New controls must conform to implicit naming rules. This is a coupling/maintenance risk, not proof that a specific currently permitted button mutates protected data. Existing editor-level guards and preset checks should remain defense in depth.

**Proposed improvement.** Explicitly register action capabilities such as viewing/filtering versus modifying setup. Keep runtime enabled state separate from preset restrictions, so unlocking restores current editor-owned state rather than stale defaults. Register dynamic controls through the same API. Do not repurpose an existing `Tag` field if other code already uses it.

**Verification.** Rename/localize captions without changing capabilities; add controls while locked; change editor-owned enablement during background loads; bind new grid columns; enter/leave locked mode repeatedly. Preserve selection, sorting, filters, logs, scrolling, and tooltips while mutation actions remain blocked.

## F22 — Regression harness dependencies have drifted

**Evidence.** [.checks/CargoRegression/CargoRegression.csproj](.checks/CargoRegression/CargoRegression.csproj) directly links the current `TrucksCargoTab.cs` but omits the new `CargoMassEditing.cs` partial and its dependencies. Its `Program.cs` cache stub lacks `ContentVersion`, now used by that tab. These are concrete missing source dependencies; compiler diagnostics were not collected. Some other harnesses, such as SharedPreset, reference already-built Release DLLs by `HintPath` rather than a project reference.

**Impact.** An old harness can stop compiling as production evolves. A DLL-based harness can exercise stale binaries unless build order is enforced. Numerous directories named Baseline appear to be historical fixtures/snapshots; they should not all be assumed to be maintained checks or counted as current regression coverage.

**Proposed improvement.** Publish a maintained-check inventory and a single command that builds the application and executes those checks in a known order. Repair linked dependencies or extract testable services to reduce stubs. Prefer project references when practical; otherwise verify referenced binaries were produced by the current build. Keep historical snapshots clearly labeled and separate.

**Verification.** From a clean build state, run the maintained-check command without relying on preexisting DLLs. Ensure it discovers hidden projects, returns a failure code on any failed check, and keeps generated fixtures/output out of source and game-data directories. Report build failures separately from test assertion failures.

## Suggested implementation sequence

1. Address F01–F04 first with focused containment, load-lifecycle, and stream/buffer regressions. Avoid combining these safety/correctness changes into a broad UI cleanup.
2. Fix the narrow compatibility defects F05 and F15; add historical preset and scientific-notation fixtures.
3. Establish the maintained regression entry point in F22 before larger extractions. Reuse current `.checks` coverage rather than replacing it wholesale.
4. Strengthen preset validation/recovery (F17/F18), then separate preview capture from export work (F16). Preserve deterministic output and immutable originals.
5. Introduce neutral persistence/prepared-output infrastructure (F08/F20) and shared XML mechanics (F09/F19). Migrate one store at a time and compare output behavior.
6. Make catalog loading asynchronous and typed (F06/F11), then remove avoidable traversal work (F10), using measurements to guide additional caching.
7. Consolidate rule/log UI and explicit browsing capabilities (F07/F21), improve diagnostics (F12), and complete measured CRC/dead-code cleanup (F13/F14).

These are recommendations, not an approved implementation plan. Related findings overlap intentionally: F08 concerns file persistence, F20 concerns staged multi-file workflows; F09 concerns XML interpretation, F19 concerns shared source-position scanning; F06 concerns Cargo loading, F16 concerns full preset export. They should share solutions where appropriate without losing their distinct acceptance criteria.

## Review boundaries and change record

- Application source, game assets, extracted workspaces, and installed archives were not modified for this report.
- Only this Markdown report was added. No build or publish was needed for the documentation-only change; generated output was deliberately not created.
- The document is the reviewable diff: a single new file. No README, project file, or existing review artifact was overwritten.
- Findings describe the inspected source snapshot. Recheck relevant methods and maintained tests before implementing changes, particularly if other app work continues in parallel.
