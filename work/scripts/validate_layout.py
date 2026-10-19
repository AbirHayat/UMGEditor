# Compare preview widget rects (exported JSON) with edges measured in an in-game screenshot.
#
# Measurement: for each edge, start a few px inside the element on three scanlines (25/50/75 %) and walk
# outward; the edge is the first colour step >= STEP. Walking from inside means busy backgrounds beyond the
# edge can't win. The position most scanlines agree on is kept. Pass if |diff| <= 3 px; every diff is also
# given in design units (diff px / px-per-unit of that widget).
#
# Elements are chosen by a path suffix ("Normal_Display/Img_Line") so repeated names resolve to one widget,
# optionally followed by ":lrtb" to measure only edges that are visible in game (not covered by others).
#
# usage: python validate_layout.py preview.json screenshot.png cropX cropY element[:edges]...
import json, sys
from PIL import Image

PASS_PX = 3
STEP = 40          # min RGB step (sum of channel diffs) that counts as an edge
REACH = 8          # how far outside the predicted edge the walk may go

data = json.load(open(sys.argv[1], encoding='utf-8'))
shot = Image.open(sys.argv[2]).convert('RGB')
ox, oy = int(sys.argv[3]), int(sys.argv[4])
px = shot.load()

def step(a, b):
    return sum(abs(x - y) for x, y in zip(a, b))

def pixel(horizontal, along, line):
    return px[ox + along, oy + line] if horizontal else px[ox + line, oy + along]

def walk(pred, line, horizontal, outward, extent):
    """Walk from inside the element toward outside; return (edge position, step strength) or None."""
    inset = max(1, min(4, int(extent / 2) - 1))
    pos = int(pred) - outward * inset if outward > 0 else int(pred) + inset
    for _ in range(inset + REACH + 1):
        nxt = pos + outward
        s = step(pixel(horizontal, pos, line), pixel(horizontal, nxt, line))
        if s >= STEP:
            return (nxt if outward > 0 else pos), s   # boundary between pos and nxt, as a pixel-edge coordinate
        pos = nxt
    return None

def pick(suffix):
    hits = [w for w in data['Widgets'] if w['Path'] == suffix or w['Path'].endswith('/' + suffix)]
    if len(hits) != 1:
        sys.exit(f'{suffix}: {len(hits)} matches: ' + ' | '.join(w['Path'] for w in hits))
    return hits[0]

rows = []
for arg in sys.argv[5:]:
    suffix, _, edges = arg.partition(':')
    edges = edges or 'lrtb'
    w = pick(suffix)
    r = w['Px']; ppu = w['PixelsPerUnit']
    xs = [round(r['X'] + r['W'] * f) for f in (0.25, 0.5, 0.75)]
    ys = [round(r['Y'] + r['H'] * f) for f in (0.25, 0.5, 0.75)]
    spec = {'l': ('left', r['X'], True, ys, -1, r['W']), 'r': ('right', r['X'] + r['W'], True, ys, +1, r['W']),
            't': ('top', r['Y'], False, xs, -1, r['H']), 'b': ('bottom', r['Y'] + r['H'], False, xs, +1, r['H'])}
    for key in edges:
        name, pred, horiz, lines, outward, extent = spec[key]
        found = [f for f in (walk(pred, line, horiz, outward, extent) for line in lines) if f]
        if not found:
            rows.append((suffix, name, pred, None, 0, None, None)); continue
        positions = [f[0] for f in found]
        best = max(found, key=lambda f: (positions.count(f[0]), f[1]))
        diff = best[0] - pred
        rows.append((suffix, name, pred, best[0], best[1], diff, diff / ppu))

print(f'{"element":<44}{"edge":<8}{"predicted":>10}{"measured":>10}{"step":>6}{"diff px":>9}{"diff units":>12}  result')
fails = 0
for name, edge, pred, pos, strength, diff, units in rows:
    if pos is None:
        print(f'{name[-43:]:<44}{edge:<8}{pred:>10.1f}{"-":>10}{"-":>6}{"-":>9}{"-":>12}  no edge found (n/a)')
        continue
    ok = abs(diff) <= PASS_PX
    if not ok: fails += 1
    print(f'{name[-43:]:<44}{edge:<8}{pred:>10.1f}{pos:>10}{strength:>6}{diff:>9.1f}{units:>12.2f}  {"PASS" if ok else "FAIL"}')
print(f'{len(rows)} edges, {fails} outside +-{PASS_PX} px')
