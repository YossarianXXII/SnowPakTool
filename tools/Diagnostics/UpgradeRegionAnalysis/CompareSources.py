"""Check preserved versus installed scenes without changing either source."""
from pathlib import Path
import collections
import json
import math
import re
import struct
import zipfile

root = Path(__file__).resolve().parent / 'results'
data = json.loads((root / 'original-trucks-upgrades.json').read_text(encoding='utf-8'))
codes = {model['Truck'] for model in data['Models']}
client = Path('S:/SteamLibrary/steamapps/common/SnowRunner/preload/paks/client')

def scene(path):
    with zipfile.ZipFile(path) as archive:
        return archive.read(next(name for name in archive.namelist() if name.replace('\\', '/') == '[scene]/data_for_ssl.bin'))

def string(blob, offset):
    if offset < 0 or offset + 2 > len(blob): return None
    length = struct.unpack_from('<H', blob, offset)[0]
    if not 1 <= length <= 256 or offset + 2 + length > len(blob): return None
    value = blob[offset+2:offset+2+length]
    if value[-1] != 0 or any(b < 32 or b > 126 for b in value[:-1]): return None
    return value[:-1].decode('ascii'), offset+2+length

def placements(blob):
    result = []
    for match in re.finditer(rb'[a-z][a-z0-9_]{1,254}\x00', blob):
        model = string(blob, match.start()-2)
        if model is None or model[0] not in codes: continue
        identity = string(blob, model[1])
        if identity is None or identity[1]+64 > len(blob): continue
        transform = struct.unpack_from('<16f', blob, identity[1])
        if not all(math.isfinite(v) for v in transform): continue
        if any(abs(transform[i]) > .0001 for i in (3,7,11)) or abs(transform[15]-1) > .0001: continue
        if any(abs(transform[i]) > 1.1 for i in range(12)): continue
        result.append((identity[0], tuple(transform[12:15]), model[0]))
    return result

results = []
for check in data['SourceChecks']:
    original = scene(check['Source'])
    installed = scene(client / (check['Map']+'.pak'))
    a = placements(original)
    b = placements(installed)
    pairs = zip(a,b)
    geometry_same = len(a) == len(b) and all(x[:2] == y[:2] for x,y in pairs)
    changes = [{'Placement':x[0], 'Original':x[2], 'Installed':y[2]} for x,y in zip(a,b) if x[2] != y[2]] if geometry_same else []
    results.append({'Map':check['Map'], 'OriginalPlacements':len(a), 'InstalledPlacements':len(b), 'PlacementIdsAndPositionsMatch':geometry_same, 'ModelChanges':changes, 'SceneEqual':original==installed})
(root / 'source-truck-comparison.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
print(f"Checked {len(results)} original/installed scenes. Scene-equal: {sum(r['SceneEqual'] for r in results)}. Placement IDs/positions equal: {sum(r['PlacementIdsAndPositionsMatch'] for r in results)}.")
print(f"Original placements parsed: {sum(r['OriginalPlacements'] for r in results)}; installed: {sum(r['InstalledPlacements'] for r in results)}; changed truck models at matched placements: {sum(len(r['ModelChanges']) for r in results)}.")
print('Sample changes:',[(r['Map'],c) for r in results for c in r['ModelChanges']][:5])
