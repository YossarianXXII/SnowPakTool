# Store-only truck upgrade verification

Checked 4 November 2026 against preserved original truck definitions, level/task scripts, DLC grants and the previous original-scene placement scan. No game data was changed.

**Confirmed: the 17 previously unlocated truck-specific upgrade pickups split into 16 for 13 store-only models, and one for the contract-awarded Tayga 6455B.**

## Purchase-only models

| Truck | Map upgrade pickups | Store rank |
| --- | ---: | ---: |
| Azov 73210 | 1 | 20 |
| BOAR 45318 | 2 | 1 |
| Caterpillar CT680 | 1 | 12 |
| Derry Longhorn 4520 | 1 | 24 |
| Ford CLT9000 | 2 | 1 |
| Freightliner M916A1 | 2 | 17 |
| International Loadstar 1700 | 1 | 1 |
| International Paystar 5070 | 1 | 1 |
| KHAN Lo4F | 1 | 1 |
| KOLOB 74760 | 1 | 22 |
| KOLOB 74941 | 1 | 1 |
| Voron D-53233 | 1 | 1 |
| Voron Grad | 1 | 18 |

All 13 have an explicit store price, `UnlockByExploration="false"`, no `UnlockByObjective` or unconditional lock, no obtainable physical scene placement, no matching direct item/unlock/contest reward, and no DLC `trucksToAdd` grant. Rank and any required content ownership still apply. The DLC references found for several models describe skins or entitlement lists rather than free truck grants. Lo4F's game automation test reference is outside campaign acquisition.

## Tayga 6455B: direct garage reward

- Original truck definition: `[media]/_dlc/dlc_6/classes/trucks/tayga_6455b.xml`, with `UnlockByObjective="US_06_01_MILL_LOGS"`.
- Original contract: `[ps]/us_06_01_objects.sso`, objective `US_06_01_MILL_LOGS`, region `us_06` (Maine). Its `ObjectiveRewardItem.itemName` is `tayga_6455b`.
- Localized contract title: **Starting The Plank Production**.
- `truck_unlock_info.sso` independently associates the same model with the same objective.
- Its raised suspension pickup is on `level_us_06_02`, also Maine. It is therefore a same-region truck-specific upgrade once direct garage rewards are included.

[Saber's SnowRunner reward documentation](https://expeditions-guides.saber.games/map_modding/creating_a_map/objectives/objectives_in_snowrunner/rewards/) confirms that an `ObjectiveRewardItem` naming a truck grants it to the Garage when the player accepts the reward. `ObjectiveRewardUnlock` is a separate unlock-only reward.

## Corrected full-pool allocation

Including the separately confirmed Kirovets composite tire ID and treating a direct contract reward region as the truck's home:

| Allocation | Pickups |
| --- | ---: |
| Truck-specific, same region | 96 |
| Truck-specific, other region | 15 |
| Truck-specific, purchase-only model with no acquisition region | 16 |
| Generic/shared upgrades | 39 |
| **Total** | **166** |

The earlier 95 / 15 / 17 / 39 split used obtainable physical scene placements only. Tayga's one upgrade moves from the unlocated group to same-region; generic/shared upgrades remain 39. Among the 111 region-locatable truck-specific pickups, the split is 86.5% same-region / 13.5% elsewhere.

Scope is the campaign acquisition routes configured in these original files. Custom New Game+ rules, mods and edited saves are outside this classification.

Reproducible evidence: `.checks/UpgradeRegionAnalysis/ConfirmAcquisition.py` inspected 1774 original SSO files; the detailed per-model fields and reward references are in `.checks/UpgradeRegionAnalysis/results/store-only-verification.json`.
