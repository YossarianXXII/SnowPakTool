"""Summarize original truck/upgrade region relationships from the read-only export."""
from pathlib import Path
import collections
import json

root = Path(__file__).resolve().parent / 'results'
data = json.loads((root / 'original-trucks-upgrades.json').read_text(encoding='utf-8'))
links = data['Links']
models = data['Models']
eligible = [m for m in models if m['Regions']]
by_upgrade = collections.defaultdict(list)
for model in models:
    for upgrade in model['CompatibleRewardIds']:
        by_upgrade[upgrade].append(model['Truck'])
exclusive = [link for link in links if len(by_upgrade[link['UpgradeId']]) == 1]
shared = [link for link in links if len(by_upgrade[link['UpgradeId']]) > 1]

def percent(numerator, denominator):
    return f'{100*numerator/denominator:.1f}%' if denominator else '—'

def counts(group):
    return len(group), sum(link['SameRegion'] for link in group)

def row(label, group):
    total, same = counts(group)
    return f'| {label} | {same} ({percent(same,total)}) | {total-same} ({percent(total-same,total)}) | {total} |'

def region_name(code):
    name = data['RegionNames'].get(code, code).title()
    return {'Br. Columbia':'British Columbia', 'Almaty Region':'Almaty'}.get(name,name)

def regions(codes):
    return ', '.join(region_name(code) for code in codes)

def safe(text):
    return str(text).replace('|', '\\|').replace('\n',' ')

pickup_classes = collections.defaultdict(list)
for reference in data['References']:
    matches = [link for link in links if link['UpgradeId'] == reference['UpgradeId'] and link['UpgradeMap'] == reference['Map'] and link['Zone'] == reference['Zone']]
    if not matches:
        key = 'Unresolved ID' if reference['UpgradeId'] in data['UnknownRewardIds'] else 'No compatible obtainable map truck'
    elif all(link['SameRegion'] for link in matches): key = 'All compatible obtainable models have a local copy'
    elif any(link['SameRegion'] for link in matches): key = 'Some compatible obtainable models have a local copy'
    else: key = 'All compatible obtainable models are elsewhere'
    pickup_classes[key].append(reference)

summary = {
    'AllModelRewardPairs': {'Same':sum(l['SameRegion'] for l in links), 'Other':sum(not l['SameRegion'] for l in links)},
    'GloballySingleModelRewardPairs': {'Same':sum(l['SameRegion'] for l in exclusive), 'Other':sum(not l['SameRegion'] for l in exclusive)},
    'GloballySharedModelRewardPairs': {'Same':sum(l['SameRegion'] for l in shared), 'Other':sum(not l['SameRegion'] for l in shared)},
    'PickupCategories': {key:len(value) for key,value in pickup_classes.items()},
    'TruckModelsWithNoLocalUpgrade': [m['Truck'] for m in eligible if not any(l['SameRegion'] for l in links if l['Truck'] == m['Truck'])]
}
(root / 'summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')

lines = [
    '# Original map trucks and upgrade regions',
    '',
    'Analysis date: 4 November 2026. Sources: preserved original map archives and original initial-PAK definitions/scripts, with no edited overlays, randomized plans, pending changes or user availability selections.',
    '',
    '## Main result',
    '',
    '**Truck-specific upgrade pickups are mostly local: 94 of 109 (86.2%) are in a region containing an original obtainable copy of their truck; 15 (13.8%) are elsewhere.** When shared parts are counted once for each compatible obtainable truck model, the overall relationship is **139 local (35.8%) versus 249 elsewhere (64.2%)**.',
    '',
    '| Counting method | Same region | Other region | Total |',
    '| --- | ---: | ---: | ---: |',
    row('All compatible truck-model / reward-location pairs',links),
    row('Parts compatible with exactly one truck model in the full 120-model catalog',exclusive),
    row('Parts compatible with multiple truck models in the full catalog',shared),
    '',
    'The exclusive row includes only models with original obtainable map placements. Shared engines and gearboxes contribute one relationship per compatible obtainable truck model; they are not duplicate pickup locations.',
    '',
    '## Pickup locations counted once',
    '',
    '| Pickup classification | Count |',
    '| --- | ---: |',
]
for key in ['All compatible obtainable models have a local copy','Some compatible obtainable models have a local copy','All compatible obtainable models are elsewhere','No compatible obtainable map truck','Unresolved ID']:
    lines.append(f'| {key} | {len(pickup_classes[key])} |')
lines += [
    '',
    'Among the 148 pickups linked to at least one obtainable map truck, **122 (82.4%) can serve a truck found in the same region; 26 (17.6%) serve only obtainable trucks found in other regions**. Of those 122, 27 also serve other models whose copies are elsewhere. A shared upgrade can therefore be local for one truck and remote for another.',
    '',
    'The remaining 18 pickups consist of 17 resolved parts for models with no obtainable physical map placement in this scan, plus the unresolved Kirovets tire reward. The 17 are not classified as "other region": their trucks have no qualifying location to compare.',
    '',
    '## By upgrade category',
    '',
    'Counts below are truck-model / reward-location relationships, using any original obtainable region for each model.',
    '',
    '| Upgrade type | Same region | Other region | Total |',
    '| --- | ---: | ---: | ---: |',
]
labels = {'awd':'AWD','diff_lock':'Differential lock','frame_addons':'Metal detector frame addon'}
for kind in ['Suspension','diff_lock','awd','Engine','Gearbox','frame_addons']:
    lines.append(row(labels.get(kind,kind),[link for link in links if link['UpgradeType'] == kind]))
lines += [
    '',
    '## By truck location region',
    '',
    'This table treats each original truck-model / region combination separately. For example, a Scout 800 obtained in Ontario is compared against Ontario pickups here, even though another Scout 800 exists in Michigan. This stricter view totals 483 relationships: **139 local (28.8%) and 344 elsewhere (71.2%)**. It intentionally differs from the primary any-obtainable-copy comparison.',
    '',
    '| Region containing truck | Distinct obtainable models | Same-region upgrades | Other-region upgrades | Local share |',
    '| --- | ---: | ---: | ---: | ---: |',
]
for region in sorted({code for model in eligible for code in model['Regions']},key=region_name):
    group = [link for link in data['PerTruckRegionLinks'] if link['TruckRegion'] == region]
    total,same = counts(group)
    lines.append(f'| {region_name(region)} | {sum(region in m["Regions"] for m in eligible)} | {same} | {total-same} | {percent(same,total)} |')
lines += [
    '',
    '## Per obtainable truck model',
    '',
    'Each model is counted once. An upgrade is local when it is in any of that model\'s obtainable regions. Multiple copies within a region do not increase the counts.',
    '',
    '| Truck | Original obtainable regions | Same-region upgrades | Other-region upgrades | Local share |',
    '| --- | --- | ---: | ---: | ---: |',
]
for model in sorted(eligible,key=lambda m:m['Name'].casefold()):
    group = [link for link in links if link['Truck'] == model['Truck']]
    total,same = counts(group)
    lines.append(f'| {safe(model["Name"])} | {regions(model["Regions"])} | {same} | {total-same} | {percent(same,total)} |')
for same_region, heading in [(True, 'Truck-specific upgrades found in the same region'), (False, 'Truck-specific upgrades found elsewhere')]:
    group = sorted((link for link in exclusive if link['SameRegion'] == same_region),
                   key=lambda link: (link['Name'].casefold(), link['UpgradeName'].casefold(), link['UpgradeId']))
    lines += [
        '',
        f'## {heading}',
        '',
        f'{len(group)} truck-specific upgrade pickups. Each part is compatible with exactly one model in the full 120-model catalog; that model has an original obtainable map placement.',
        '',
        '| Truck | Original obtainable region(s) | Upgrade | Pickup region | Exact upgrade ID |',
        '| --- | --- | --- | --- | --- |',
    ]
    for link in group:
        lines.append(f'| {safe(link["Name"])} | {regions(link["TruckRegion"].split(","))} | {safe(link["UpgradeName"])} | {region_name(link["UpgradeRegion"])} | `{link["UpgradeId"]}` |')
lines += [
    '',
    '## Coverage and method',
    '',
    '- 56 original map scenes, 248 truck placements: 82 obtainable placements representing 66 truck models; 166 mission/task-only placements. No unverified placements remained under the app\'s task-action classification and 17 existing confirmed mission-only overrides.',
    '- Obtainable includes discovery/start vehicles and explicit task awards of existing map vehicles. Models sold only in stores, and rewards that do not correspond to an obtainable physical map placement, are outside the main comparison. A mission-only model elsewhere does not create an obtainable home region.',
    '- Original means the first preserved workspace map inputs and `initial.pak/original_files`. That is the application baseline, not an independently authenticated factory download. The installed scenes retain matching placement IDs/positions, but 168 current installed truck models differ from the baseline; none of those replacements were used.',
    '- All 56 sources used preserved `region-maps/backups/<map>/original.pak`; 55 installed scene payloads differ from those originals. The source comparison found the same 248 placement identities/positions in both sets.',
    '- Upgrade locations come from original `ZonePropertyUpgradesGiver.upgrades` lists. 166 pickups, 166 distinct reward IDs; the prior scene check matched every zone ID to its installed scene. 165 reward IDs resolve through the original vehicle definitions; `wheels_extend_kirovets_k7m_tire_1` stays unresolved.',
    '- Component compatibility follows each original truck\'s engine/gearbox/suspension/winch socket family and its named variants, including file parents. AWD/differential/frame addon relationships require matching installation sockets and the app\'s addon/block/requirement compatibility check. Cosmetic naming and truck class are not used as compatibility evidence.',
    '- This is a definition-based availability comparison, not a rank, DLC-ownership, savegame-unlock or gameplay collection simulation. It does not assume the original truck already has the upgrade equipped.',
    '',
    'As a sensitivity check, including mission-only physical trucks as well gives 173 local / 302 elsewhere (36.4% / 63.6%) across 475 model–pickup relationships. The main result remains focused on trucks the player can obtain.',
    '',
    '## Reproduction and detailed data',
    '',
    '- `.checks/UpgradeRegionAnalysis/Program.cs`: exports the original catalog and exact compatible model–pickup links through the existing application readers.',
    '- `.checks/UpgradeRegionAnalysis/CompareSources.py`: checks baseline/installed placement identities and model changes.',
    '- `.checks/UpgradeRegionAnalysis/Summarize.py`: produces this report and summary JSON.',
    '- `.checks/UpgradeRegionAnalysis/results/original-trucks-upgrades.json`: all placements, model compatibility sets, individual links, source paths and region names.',
    '- `.checks/UpgradeRegionAnalysis/results/source-truck-comparison.json`: original/current source comparison.',
    '',
    'No game data or application code was modified for this analysis.',
]
report = Path(__file__).resolve().parents[2] / 'original-trucks-upgrade-region-analysis.md'
report.write_text('\n'.join(lines)+'\n',encoding='utf-8')
print(report)
print(json.dumps(summary,indent=2))
