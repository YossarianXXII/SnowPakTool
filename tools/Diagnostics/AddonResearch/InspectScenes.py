"""Read installed scene archives; write only research results under .checks."""
from pathlib import Path
import collections
import json
import struct
import zipfile

root = Path(__file__).resolve().parent
inventory = json.loads((root / 'bin/Release/net10.0-windows/inventory.json').read_text(encoding='utf-8'))
client = Path('S:/SteamLibrary/steamapps/common/SnowRunner/preload/paks/client')
groups = collections.defaultdict(list)
for reference in inventory['References']:
    groups[reference['Map']].append(reference)
results = []
for name, references in sorted(groups.items()):
    source = client / (name + '.pak')
    if not source.exists():
        source = client / (name + '_crop.pak')
    if not source.exists():
        results.append({'Map': name, 'Status': 'No installed archive', 'Zones': [r['Zone'] for r in references]})
        continue
    with zipfile.ZipFile(source) as archive:
        entry = next((e for e in archive.namelist() if e.replace('\\', '/') == '[scene]/data_for_ssl.bin'), None)
        if entry is None:
            results.append({'Map': name, 'Status': 'No scene entry'})
            continue
        data = archive.read(entry)
    zones = []
    for reference in references:
        encoded = reference['Zone'].encode('ascii') + b'\0'
        needle = struct.pack('<H', len(encoded)) + encoded
        zones.append({'Zone': reference['Zone'], 'UpgradeId': reference['UpgradeId'], 'SceneStringOffset': data.find(needle), 'Occurrences': data.count(needle)})
    results.append({'Map': name, 'Archive': str(source), 'Status': 'Read', 'Zones': zones})
output = root / 'scene-links.json'
output.write_text(json.dumps(results, indent=2), encoding='utf-8')
read = [r for r in results if r['Status'] == 'Read']
checked = [z for r in read for z in r['Zones']]
print(f"Read {len(read)}/{len(results)} referenced map scenes. Matched {sum(z['SceneStringOffset'] >= 0 for z in checked)}/{len(checked)} exact length-prefixed zone IDs.")
for result in results:
    if result['Status'] != 'Read': print(result)
    else:
        for zone in result['Zones']:
            if zone['SceneStringOffset'] < 0: print('Unmatched', result['Map'], zone['Zone'])
