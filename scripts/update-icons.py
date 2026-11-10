"""Vendor the small Lucide outline icon set used by the native desktop UI (MIT)."""
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
import urllib.request
import xml.etree.ElementTree as ET
import re

root = Path(__file__).resolve().parents[1]
names = ['mic', 'sliders-horizontal', 'ellipsis', 'captions', 'history', 'play', 'square', 'x', 'minus', 'arrow-right', 'headphones', 'refresh-cw', 'download', 'arrow-up-right', 'check', 'chevron-down', 'lock-keyhole']

def fetch(name):
    cache = root / '.tools/icon-cache' / (name + '.svg')
    cache.parent.mkdir(parents=True, exist_ok=True)
    if not cache.exists():
        for attempt in range(3):
            try:
                with urllib.request.urlopen(f'https://raw.githubusercontent.com/lucide-icons/lucide/0.468.0/icons/{name}.svg', timeout=25) as response:
                    cache.write_bytes(response.read())
                break
            except Exception as error:
                if attempt == 2: raise RuntimeError(f'Unable to fetch icon {name}') from error
    svg = ET.fromstring(cache.read_bytes())
    paths = []
    for child in svg:
        tag = child.tag.split('}')[-1]
        a = child.attrib
        if tag == 'path':
            d = a['d'].strip()
            # SVG path elements each start at (0,0); preserve that when merging their geometry.
            if d.startswith('m'):
                number = r'[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?'
                match = re.match(r'm\s*(' + number + r')[ ,]*(' + number + r')(.*)', d)
                if not match: raise ValueError('Unsupported SVG move')
                tail = match[3].strip()
                if tail and (tail[0].isdigit() or tail[0] in '+-.'):
                    tail = 'l' + tail
                d = f'M{match[1]},{match[2]} {tail}'
            paths.append(d)
        elif tag == 'line': paths.append(f"M{a['x1']},{a['y1']} L{a['x2']},{a['y2']}")
        elif tag in ('polyline', 'polygon'): paths.append('M' + a['points'] + (' Z' if tag == 'polygon' else ''))
        elif tag == 'circle':
            x, y, r = float(a['cx']), float(a['cy']), float(a['r'])
            paths.append(f'M{x-r},{y} A{r},{r} 0 1 0 {x+r},{y} A{r},{r} 0 1 0 {x-r},{y}')
        elif tag == 'rect':
            x,y,w,h = (float(a.get(k,0)) for k in ['x','y','width','height'])
            r = float(a.get('rx',0))
            paths.append(f'M{x+r},{y} H{x+w-r} Q{x+w},{y} {x+w},{y+r} V{y+h-r} Q{x+w},{y+h} {x+w-r},{y+h} H{x+r} Q{x},{y+h} {x},{y+h-r} V{y+r} Q{x},{y} {x+r},{y} Z')
    key = ''.join(part.title() for part in name.split('-')) + 'Icon'
    return f'  <Geometry x:Key="{key}">{" ".join(paths)}</Geometry>'

with ThreadPoolExecutor(max_workers=8) as pool:
    content = list(pool.map(fetch, names))
destination = root / 'src/ClassroomCaption.App/UI/Icons.xaml'
destination.parent.mkdir(exist_ok=True)
destination.write_text('<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n<!-- Lucide, MIT. See LUCIDE-LICENSE.txt. -->\n' + '\n'.join(content) + '\n</ResourceDictionary>\n', encoding='utf-8')
with urllib.request.urlopen('https://raw.githubusercontent.com/lucide-icons/lucide/0.468.0/LICENSE',timeout=30) as response:
    (root / 'LUCIDE-LICENSE.txt').write_bytes(response.read())
print(f'Vendored {len(content)} Lucide icons')
