"""Confirm the acquisition routes of the 17 previously unlocated upgrade pickups.

Reads preserved original data and writes analysis artifacts only.
"""
from pathlib import Path
import collections
import json
import re
import xml.etree.ElementTree as ET

project = Path(__file__).resolve().parents[2]
results = Path(__file__).resolve().parent / 'results'
original = project.parent / 'workspace/initial.pak/original_files'
data = json.loads((results / 'original-trucks-upgrades.json').read_text(encoding='utf-8'))
users = collections.defaultdict(list)
for model in data['Models']:
    for upgrade in model['CompatibleRewardIds']:
        users[upgrade].append(model)
targets = {}
for reference in data['References']:
    compatible = users[reference['UpgradeId']]
    if len(compatible) == 1 and not compatible[0]['Regions']:
        truck = compatible[0]
        targets.setdefault(truck['Truck'], {'Name':truck['Name'], 'Upgrades':[], 'RewardReferences':[], 'DlcTruckGrants':[]})['Upgrades'].append(reference)

tokens_pattern = re.compile(r'"(?:\\.|[^"\\])*"|//[^\r\n]*|[{}\[\]=,]|[^\s{}\[\]=,]+')
def parse_sso(text):
    tokens = [m.group() for m in tokens_pattern.finditer(text) if not m.group().startswith('//')]
    position = 0
    def decode(token):
        if not token.startswith('"'): return token
        return re.sub(r'\\(.)',lambda m:{'n':'\n','r':'\r','t':'\t'}.get(m[1],m[1]),token[1:-1])
    def require(expected):
        nonlocal position
        if position >= len(tokens) or tokens[position] != expected: raise ValueError(f'Expected {expected}')
        position += 1
    def members(end=None):
        nonlocal position
        result = {}
        while position < len(tokens) and tokens[position] != end:
            if tokens[position] == ',': position += 1; continue
            key = decode(tokens[position]); position += 1
            require('='); result[key] = value()
        if end is not None: require(end)
        return result
    def value():
        nonlocal position
        token = tokens[position]; position += 1
        if token == '{': return members('}')
        if token == '[':
            result = []
            while position < len(tokens) and tokens[position] != ']':
                if tokens[position] == ',': position += 1; continue
                result.append(value())
            require(']'); return result
        return decode(token)
    return members()

def nodes(node, path=()):
    yield path,node
    if isinstance(node,dict):
        for key,value in node.items(): yield from nodes(value,path+(key,))
    elif isinstance(node,list):
        for index,value in enumerate(node): yield from nodes(value,path+(str(index),))

pattern = re.compile(r'(?<![A-Za-z0-9_])('+'|'.join(map(re.escape,targets))+r')(?![A-Za-z0-9_])',re.I)
inspected = 0
for source in (original / '[ps]').rglob('*.sso'):
    text = source.read_text(encoding='utf-8-sig')
    inspected += 1
    if not pattern.search(text): continue
    root = parse_sso(text)
    for path,node in nodes(root):
        if not isinstance(node,dict): continue
        if path and path[-1] == 'trucksToAdd':
            for truck in set(node) & set(targets): targets[truck]['DlcTruckGrants'].append({'Source':source.name,'Path':'.'.join(path)})
        if path and path[-1] in ('ObjectiveRewardItem','ObjectiveRewardUnlock'):
            item = node.get('itemName') if path[-1] == 'ObjectiveRewardItem' else node.get('itemToUnlock')
            if item not in targets: continue
            objective = root
            context = None
            for depth,key in enumerate(path):
                objective = objective[int(key)] if isinstance(objective,list) else objective[key]
                if isinstance(objective,dict) and objective.get('__type') in ('TaskSettings','ContractSettings','ContestSettings'):
                    context = {'Objective':key,'Region':objective.get('region'),'Kind':objective['__type']}
            targets[item]['RewardReferences'].append({'Source':source.name,'Path':'.'.join(path),'RewardKind':path[-1],**(context or {})})
        if node.get('__type') == 'ContestSettings':
            for subpath,reward in nodes(node):
                if isinstance(reward,dict):
                    for key,value in reward.items():
                        if isinstance(value,str) and value in targets and 'reward' in '.'.join(subpath).lower():
                            targets[value].setdefault('ContestRewardReferences',[]).append({'Source':source.name,'Path':'.'.join(path+subpath+(key,))})

for code,item in targets.items():
    files = [p for p in (original/'[media]').rglob(code+'.xml') if p.parent.name == 'trucks']
    if len(files) != 1: raise ValueError(f'Ambiguous truck file: {code}')
    source = files[0]
    text = re.sub(r'<(/?)([A-Za-z_][\w.-]*):',r'<\1\2_',source.read_text(encoding='utf-8-sig'))
    root = ET.fromstring('<Root>'+text+'</Root>')
    if root.find('_parent') is not None: raise ValueError(f'Parent metadata needs checking: {code}')
    game = root.find('Truck/GameData')
    item['Definition'] = str(source.relative_to(original)).replace('\\','/')
    item['GameData'] = {key:game.get(key) for key in ('Price','Country','UnlockByRank','UnlockByExploration','UnlockByObjective','LockedUnconditionally')}
    item['MissionOnlyPlacements'] = [{key:p[key] for key in ('Map','PlacementId','Availability','Evidence')} for p in data['Placements'] if p['Truck'] == code]
    assert not any(p['Availability'].startswith('Obtainable') for p in item['MissionOnlyPlacements'])
    if item['RewardReferences']:
        item['Classification'] = 'Direct contract reward'
    elif (game.get('Price') and game.get('UnlockByExploration') == 'false'
          and not game.get('UnlockByObjective') and game.get('LockedUnconditionally') != 'true'
          and not item['DlcTruckGrants'] and not item.get('ContestRewardReferences')):
        item['Classification'] = 'Store-only in original campaign'
    else: item['Classification'] = 'Needs further review'

store = [item for item in targets.values() if item['Classification'] == 'Store-only in original campaign']
awarded = [item for item in targets.values() if item['Classification'] == 'Direct contract reward']
assert len(store) == 13 and sum(len(item['Upgrades']) for item in store) == 16
assert len(awarded) == 1 and awarded[0]['Name'] == 'Tayga 6455B'
assert awarded[0]['RewardReferences'][0]['Objective'] == 'US_06_01_MILL_LOGS'
assert awarded[0]['RewardReferences'][0]['Region'] == 'us_06'
assert awarded[0]['Upgrades'][0]['Region'] == 'us_06'
evidence = {'ScriptsInspected':inspected,'StoreOnlyModels':len(store),'StoreOnlyUpgradePickups':16,
            'DirectRewardModels':len(awarded),'DirectRewardUpgradePickups':1,'Trucks':targets}
(results/'store-only-verification.json').write_text(json.dumps(evidence,indent=2),encoding='utf-8')
lines = [
    '# Store-only truck upgrade verification', '',
    'Checked 4 November 2026 against preserved original truck definitions, level/task scripts, DLC grants and the previous original-scene placement scan. No game data was changed.', '',
    '**Confirmed: the 17 previously unlocated truck-specific upgrade pickups split into 16 for 13 store-only models, and one for the contract-awarded Tayga 6455B.**', '',
    '## Purchase-only models', '',
    '| Truck | Map upgrade pickups | Store rank |', '| --- | ---: | ---: |',
]
for item in sorted(store,key=lambda item:item['Name'].casefold()):
    lines.append(f'| {item["Name"]} | {len(item["Upgrades"])} | {item["GameData"]["UnlockByRank"]} |')
lines += [
    '',
    'All 13 have an explicit store price, `UnlockByExploration="false"`, no `UnlockByObjective` or unconditional lock, no obtainable physical scene placement, no matching direct item/unlock/contest reward, and no DLC `trucksToAdd` grant. Rank and any required content ownership still apply. The DLC references found for several models describe skins or entitlement lists rather than free truck grants. Lo4F\'s game automation test reference is outside campaign acquisition.', '',
    '## Tayga 6455B: direct garage reward', '',
    '- Original truck definition: `[media]/_dlc/dlc_6/classes/trucks/tayga_6455b.xml`, with `UnlockByObjective="US_06_01_MILL_LOGS"`.',
    '- Original contract: `[ps]/us_06_01_objects.sso`, objective `US_06_01_MILL_LOGS`, region `us_06` (Maine). Its `ObjectiveRewardItem.itemName` is `tayga_6455b`.',
    '- Localized contract title: **Starting The Plank Production**.',
    '- `truck_unlock_info.sso` independently associates the same model with the same objective.',
    '- Its raised suspension pickup is on `level_us_06_02`, also Maine. It is therefore a same-region truck-specific upgrade once direct garage rewards are included.', '',
    '[Saber\'s SnowRunner reward documentation](https://expeditions-guides.saber.games/map_modding/creating_a_map/objectives/objectives_in_snowrunner/rewards/) confirms that an `ObjectiveRewardItem` naming a truck grants it to the Garage when the player accepts the reward. `ObjectiveRewardUnlock` is a separate unlock-only reward.', '',
    '## Corrected full-pool allocation', '',
    'Including the separately confirmed Kirovets composite tire ID and treating a direct contract reward region as the truck\'s home:', '',
    '| Allocation | Pickups |', '| --- | ---: |',
    '| Truck-specific, same region | 96 |', '| Truck-specific, other region | 15 |',
    '| Truck-specific, purchase-only model with no acquisition region | 16 |', '| Generic/shared upgrades | 39 |',
    '| **Total** | **166** |', '',
    'The earlier 95 / 15 / 17 / 39 split used obtainable physical scene placements only. Tayga\'s one upgrade moves from the unlocated group to same-region; generic/shared upgrades remain 39. Among the 111 region-locatable truck-specific pickups, the split is 86.5% same-region / 13.5% elsewhere.', '',
    'Scope is the campaign acquisition routes configured in these original files. Custom New Game+ rules, mods and edited saves are outside this classification.', '',
    f'Reproducible evidence: `.checks/UpgradeRegionAnalysis/ConfirmAcquisition.py` inspected {inspected} original SSO files; the detailed per-model fields and reward references are in `.checks/UpgradeRegionAnalysis/results/store-only-verification.json`.',
]
(project/'store-only-truck-upgrades-verification.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
print(json.dumps({key:value for key,value in evidence.items() if key != 'Trucks'},indent=2))
print('Report:',project/'store-only-truck-upgrades-verification.md')
