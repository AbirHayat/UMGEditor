# Compare preview widget rects (exported JSON) with edges measured in an in-game screenshot.
#
# Measurement: for each edge, start a few px inside the element on three scanlines (25/50/75 %) and walk
# outward; the edge is the first colour step >= STEP. Walking from inside means busy backgrounds beyond the
# edge can't win. The position most scanlines agree on is kept. Pass if |diff| <= 3 px; every diff is also
# given in design units (diff px / px-per-unit of that widget).
#
# Text: a scanline that crosses a text widget's box (from the export) near the edge is only used if the element's
# inside next to the edge has no glyph ink on it (runtime text such as player names can run over an edge, and its glyph
# steps would be taken for the edge). Ink = a colour step >= STEP in that band that no visible image widget's edge
# explains (within EXPLAINED px). When a default scanline is at risk, the scanlines are re-picked from
# every row (column) of the element that is clear of text; a note names the text widget and the lines used. An edge
# with no text-free line is reported n/a.
#
# Elements are chosen by a path suffix ("Normal_Display/Img_Line") so repeated names resolve to one widget,
# optionally followed by ":lrtb" to measure only edges that are visible in game (not covered by others).
#
# The layout is in game-viewport pixels; when the screenshot shows the viewport scaled (an emulator window),
# pass --display sx sy (screenshot px per viewport px). Errors are reported in screenshot px (what is measured)
# and in design units per axis (screenshot px / (px-per-unit x display scale of that axis)).
#
# usage: python validate_layout.py layout.json screenshot.png cropX cropY [--display sx sy] element[:edges]...
import json, sys
from PIL import Image

PASS_PX = 3
STEP = 40          # min RGB step (sum of channel diffs) that counts as an edge
REACH = 8          # how far outside the predicted edge the walk may go
INK_BAND = 12      # px of the element's inside, next to the walk start, that must be uniform on a line crossing text
EXPLAINED = 2      # a step this close to an image widget's predicted edge is that edge, not ink
TEXT_CLASSES = ('uiText', 'TextBlock', 'RichTextBlock')
IMAGE_CLASSES = ('uiImage', 'Image')

args = sys.argv[1:]
sx = sy = 1.0
if '--display' in args:
    i = args.index('--display'); sx, sy = float(args[i + 1]), float(args[i + 2]); del args[i:i + 3]
data = json.load(open(args[0], encoding='utf-8'))
shot = Image.open(args[1]).convert('RGB')
ox, oy = int(args[2]), int(args[3])
px = shot.load()

def step(a, b):
    return sum(abs(x - y) for x, y in zip(a, b))

def pixel(horizontal, along, line):
    return px[ox + along, oy + line] if horizontal else px[ox + line, oy + along]

def walk(pred, line, horizontal, outward, extent):
    """Walk from inside the element toward outside; return (edge position, step strength) or None."""
    inset = inset_of(extent)
    pos = int(pred) - outward * inset if outward > 0 else int(pred) + inset
    for _ in range(inset + REACH + 1):
        nxt = pos + outward
        s = step(pixel(horizontal, pos, line), pixel(horizontal, nxt, line))
        if s >= STEP:
            return (nxt if outward > 0 else pos), s   # boundary between pos and nxt, as a pixel-edge coordinate
        pos = nxt
    return None

def inset_of(extent):
    return max(1, min(4, int(extent / 2) - 1))

def text_boxes(element):
    return [(w['Name'], w['Px']) for w in data['Widgets']
            if w is not element and w['Visible'] and w['Class'].startswith(TEXT_CLASSES)]

def crossing(texts, pred, line, horizontal, outward, extent):
    """Text widgets whose box covers this line within the walk window (plus the ink band inside it)."""
    inset = inset_of(extent)
    lo, hi = sorted((pred - outward * (inset + INK_BAND), pred + outward * REACH))
    hits = []
    for name, v in texts:
        x0, y0, x1, y1 = v['X'] * sx, v['Y'] * sy, (v['X'] + v['W']) * sx, (v['Y'] + v['H']) * sy
        a0, a1, p0, p1 = (x0, x1, y0, y1) if horizontal else (y0, y1, x0, x1)
        if p0 <= line < p1 and a0 < hi and a1 > lo:
            hits.append(name)
    return hits

def image_edges(element, line, horizontal):
    """Predicted edges (along the walk axis) of the visible image widgets that cover this line."""
    edges = []
    for w in data['Widgets']:
        if w is element or not w['Visible'] or not w['Class'].startswith(IMAGE_CLASSES): continue
        v = w['Px']
        x0, y0, x1, y1 = v['X'] * sx, v['Y'] * sy, (v['X'] + v['W']) * sx, (v['Y'] + v['H']) * sy
        a0, a1, p0, p1 = (x0, x1, y0, y1) if horizontal else (y0, y1, x0, x1)
        if p0 <= line < p1: edges += [a0, a1]
    return edges

def ink_free(element, pred, line, horizontal, outward, extent):
    """No unexplained colour step in the element's inside next to the walk start on this line."""
    start = int(pred) - outward * inset_of(extent) if outward > 0 else int(pred) + inset_of(extent)
    band = [start - outward * i for i in range(INK_BAND + 1)]
    known = image_edges(element, line, horizontal)
    for a, b in zip(band, band[1:]):
        boundary = max(a, b)   # pixel-edge coordinate between the two pixels
        if step(pixel(horizontal, a, line), pixel(horizontal, b, line)) >= STEP and not any(abs(boundary - e) <= EXPLAINED for e in known):
            return False
    return True

def scanlines(element, texts, pred, lines, horizontal, outward, extent, span):
    """The default lines, or (when one crosses text) up to three text-free lines spread over the element."""
    if not any(crossing(texts, pred, l, horizontal, outward, extent) for l in lines):
        return lines, None
    names = sorted({n for l in lines for n in crossing(texts, pred, l, horizontal, outward, extent)})
    first, last = int(span[0]) + 2, int(span[1]) - 2
    clean = [l for l in range(first, last + 1)
             if not crossing(texts, pred, l, horizontal, outward, extent) or ink_free(element, pred, l, horizontal, outward, extent)]
    picked = sorted({clean[round((len(clean) - 1) * f)] for f in (0.25, 0.5, 0.75)}) if clean else []
    axis = 'rows' if horizontal else 'columns'
    note = (f'default {axis} cross text ({", ".join(names)}); measured on text-free {axis} {", ".join(map(str, picked))} '
            f'({len(clean)} of {last - first + 1} clear)' if picked else f'every {axis[:-1]} crosses text ({", ".join(names)})')
    return picked, note

def pick(suffix):
    hits = [w for w in data['Widgets'] if w['Path'] == suffix or w['Path'].endswith('/' + suffix)]
    if len(hits) != 1:
        sys.exit(f'{suffix}: {len(hits)} matches: ' + ' | '.join(w['Path'] for w in hits))
    return hits[0]

rows = []
notes = []
for arg in args[4:]:
    suffix, _, edges = arg.partition(':')
    edges = edges or 'lrtb'
    w = pick(suffix)
    v = w['Px']; ppu = w['PixelsPerUnit']
    r = {'X': v['X'] * sx, 'Y': v['Y'] * sy, 'W': v['W'] * sx, 'H': v['H'] * sy}   # in screenshot px, relative to the crop
    xs = [round(r['X'] + r['W'] * f) for f in (0.25, 0.5, 0.75)]
    ys = [round(r['Y'] + r['H'] * f) for f in (0.25, 0.5, 0.75)]
    rowspan, colspan = (r['Y'], r['Y'] + r['H']), (r['X'], r['X'] + r['W'])
    spec = {'l': ('left', r['X'], True, ys, -1, r['W'], rowspan), 'r': ('right', r['X'] + r['W'], True, ys, +1, r['W'], rowspan),
            't': ('top', r['Y'], False, xs, -1, r['H'], colspan), 'b': ('bottom', r['Y'] + r['H'], False, xs, +1, r['H'], colspan)}
    texts = text_boxes(w)
    for key in edges:
        name, pred, horiz, lines, outward, extent, span = spec[key]
        lines, note = scanlines(w, texts, pred, lines, horiz, outward, extent, span)
        if note: notes.append(f'{suffix[-43:]} {name}: {note}')
        if not lines:
            rows.append((suffix, name, pred, None, 0, None, None)); continue
        found = [f for f in (walk(pred, line, horiz, outward, extent) for line in lines) if f]
        if not found:
            rows.append((suffix, name, pred, None, 0, None, None)); continue
        positions = [f[0] for f in found]
        best = max(found, key=lambda f: (positions.count(f[0]), f[1]))
        diff = best[0] - pred
        rows.append((suffix, name, pred, best[0], best[1], diff, diff / (ppu * (sx if horiz else sy))))

print(f'{"element":<44}{"edge":<8}{"predicted":>10}{"measured":>10}{"step":>6}{"diff px":>9}{"diff units":>12}  result')
fails = 0
for name, edge, pred, pos, strength, diff, units in rows:
    if pos is None:
        print(f'{name[-43:]:<44}{edge:<8}{pred:>10.1f}{"-":>10}{"-":>6}{"-":>9}{"-":>12}  no edge found (n/a)')
        continue
    ok = abs(diff) <= PASS_PX
    if not ok: fails += 1
    print(f'{name[-43:]:<44}{edge:<8}{pred:>10.1f}{pos:>10}{strength:>6}{diff:>9.1f}{units:>12.2f}  {"PASS" if ok else "FAIL"}')
for n in notes:
    print('note: ' + n)
print(f'{len(rows)} edges, {fails} outside +-{PASS_PX} px (display scale x {sx:.5f}, y {sy:.5f})')
