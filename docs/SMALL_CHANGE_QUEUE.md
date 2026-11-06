# Small change queue

Queued requests (0 of 10):

None.

Completed requests:

- On Start, wrap the introductory “Find your game installation…” label at approximately half the available view width, adapting when the window is resized.

- In Start → PAK, summarize winch changes per winch, using the same brief summary style as per-truck changes.

- Move Truck properties and Cargo to Editors, and move Map trucks to Randomizer. Remove the empty Game data tab.
- Make Truck properties the first sub-tab under Game data, before Trucks-Cargo and Regions-Trucks.
- In Truck properties, use the softer tier list pane background colors for tier cells instead of the saturated tier colors.
- In Truck properties, round updated fuel capacities to the nearest multiple of 5 litres.
- In Truck randomization, place the pane containing the randomization controls directly below the rule panel.
- Use a very light gray background, lighter than the default pane and not white, for text-heavy areas including logs and randomization rules.
- In Truck properties, replace the "No verified obtainable placement in scanned maps" text with a short horizontal line, roughly five characters wide.
- Add header filters to the Truck winch compatibility table, using the existing whole-tab search rather than a separate search box.
- Fix table-header sort-arrow spacing/background so it does not obscure more header text than the glyph needs; retain separation from the filter funnel.

- Rank XP staging: opening/leaving Experience / Ranks, loading saved settings, or changing the preview must not create a pending change on Start. Only pressing Save rank XP stages the selected values in internal/persisted memory, without editing game/workspace rank files. Start â†’ Apply performs the actual file changes from those explicitly staged values.
- In Region progression, replace the dark tile backgrounds with the corresponding Region Difficulty tier palette color. Use dark/blackish text on light tier colors and light text where needed for contrast; keep Unsorted visually distinct.
- Make the numbers in Region progression position badges slightly larger and thicker/heavier while keeping them centered and readable, including double digits.


- Move Region progression to the first sub-tab under Randomizer, before Truck-Randomization.

- Make the currently targeted search result stand out more clearly from other matches, including when navigating with Tab / Shift+Tab.
- Put Aramatsu Forester in a separate app-internal `Logging` class, leaving its game TruckType unchanged.

- Shorten the Engines tab description to no more than one row, ideally two sentences and at most three, explaining the tab's general purpose.
- In Game data â†’ Regions-Trucks, show the underlying tire class in parentheses for Custom tires in the Tires column, e.g. `Custom (Offroad)` or `Custom (Mudtires)`.
- In the shared app-wide search, press Tab to focus the next match and Shift+Tab to focus the previous match.
- Add an app-internal Farming Tractor class for Ankatra 1160, Futom 7290RA, Kirovets K-700, and Kirovets K-7M.
- Add a loading/progress bar at the bottom of the Unpack / Pack tab.
- In the replacement popup comparison table, hide hood ornaments, `miscellenious`, `EngineMarkerOffset`, `ShadowHideDistance`, `HideDistance`, `ExhaustStartTime`, `EngineStartDelay`, `EngineIconScale`, `EngineIconMesh`, `SteerSpeed`, and `TruckImage`. For wheels, show only **Compatible families and scale**, without listing individual tire types in each family.
- Close the search overlay when clicking anywhere (implemented in the shared search overlay).
