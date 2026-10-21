"""C16b: the user's goal_banner.jpg (696x192, two rows: the name row on top, the GOAL! row below) with "flame GOAL! flame"
centred in the bar, as UCL has it, and the ">" chevron removed (the centred block would run into it). The top half is
untouched. In the bottom half:
- the clean background (a horizontal gradient) is rebuilt under the block and the chevron, per column, from that
  column's own background pixels;
- the block is moved as its difference from that background, so its anti-aliased edges and glow come along.
Writes <out>/<name>.png (preview), <out>/<name>_696x192.bgra (B, G, R, A, as image_to_bgra.py) and a side-by-side
comparison.

usage: python -I center_goal_banner.py <goal_banner.jpg> <outDir> [name=DemoTitleUCL01] [centreX=348]
"""
import sys
import os
from statistics import median
from PIL import Image

src, out_dir = sys.argv[1], sys.argv[2]
name = sys.argv[3] if len(sys.argv) > 3 else 'DemoTitleUCL01'
centre = int(sys.argv[4]) if len(sys.argv) > 4 else 348

im = Image.open(src).convert('RGB')
W, H = im.size
assert (W, H) == (696, 192), f'{src} is {W}x{H}, expected 696x192'
px = im.load()
TOP, BODY_END = 96, 186            # the GOAL! row; rows >= BODY_END hold the pink line (kept as is)
CHEVRON = (192, 244)               # x range of the ">" chevron
BLOCK = (314, 660)                 # x range searched for the flames + GOAL!

def bg_column(x):
    """The background colour of column x: the median of its pixels that look like the gradient (no grey/white ink)."""
    vals = [px[x, y] for y in range(TOP + 4, BODY_END - 2) if px[x, y][0] < 30 and px[x, y][1] < 60]
    assert len(vals) >= 10, f'column {x}: too few background pixels'
    return tuple(median(v[c] for v in vals) for c in range(3))

def is_ink(x, y, bg):
    p = px[x, y]
    return max(abs(p[c] - bg[c]) for c in range(3)) > 14

# 1. a smooth background model of the GOAL! row: per-column colour (median of gradient-looking pixels, smoothed over
#    9 columns) + each row's offset (measured on ink-free columns); the chevron and block ranges are rebuilt from it
cols = {x: bg_column(x) for x in range(W)}
# columns with no ink at all (around and between the letters) are the anchors; the rest is interpolated between them
anchor = [x for x in range(W) if not any(is_ink(x, y, cols[x]) for y in range(TOP, BODY_END))]
interp = {}
for x in range(W):
    lo = max((k for k in anchor if k <= x), default=None)
    hi = min((k for k in anchor if k >= x), default=None)
    if lo is None or hi is None or lo == hi:
        interp[x] = cols[x if lo is None and hi is None else (lo if lo is not None else hi)]
    else:
        t = (x - lo) / (hi - lo)
        interp[x] = tuple(cols[lo][c] * (1 - t) + cols[hi][c] * t for c in range(3))
colbg = {x: tuple(sum(interp[k][c] for k in range(max(0, x - 4), min(W, x + 5))) / (min(W, x + 5) - max(0, x - 4)) for c in range(3)) for x in range(W)}
free = [x for x in range(W) if not (CHEVRON[0] - 4 <= x < CHEVRON[1] + 4 or BLOCK[0] - 4 <= x < BLOCK[1] + 4)]
rowoff = {y: tuple(median(px[x, y][c] - colbg[x][c] for x in free) for c in range(3)) for y in range(TOP, BODY_END)}
def model(x, y):
    return tuple(colbg[x][c] + rowoff[y][c] for c in range(3))
clean = im.copy()
cp = clean.load()
for lo, hi in (CHEVRON, BLOCK):
    for x in range(lo, hi):
        for y in range(TOP, BODY_END):
            cp[x, y] = tuple(max(0, min(255, int(round(v)))) for v in model(x, y))
ink_cols = [x for x in range(*BLOCK) if any(is_ink(x, y, cols[x]) for y in range(TOP, BODY_END))]
x0, x1 = ink_cols[0], ink_cols[-1]
block_centre = (x0 + x1) / 2
dx = int(round(centre - block_centre))

# 2. the block by alpha: per pixel the coverage a (the largest channel step above the old background, as a share of
#    what's left to 255) and the ink colour F, so that a*F + (1-a)*old = the original; then a*F + (1-a)*new
out = clean.copy()
op = out.load()
for x in range(x0 - 3, x1 + 4):
    for y in range(TOP, BODY_END):
        o, old = px[x, y], model(x, y)
        a = max(0.0, max((o[c] - old[c]) / max(1.0, 255 - old[c]) for c in range(3)))
        if a < 0.06:
            continue
        a = min(1.0, a)
        f = [(o[c] - (1 - a) * old[c]) / a for c in range(3)]
        nx = x + dx
        new = cp[nx, y]
        op[nx, y] = tuple(max(0, min(255, int(round(a * f[c] + (1 - a) * new[c])))) for c in range(3))

# checks: the top half is unchanged; the moved block's ink equals the original's (shifted), within rounding
for y in range(TOP):
    for x in range(W):
        assert op[x, y] == px[x, y], f'top-half pixel {x},{y} changed'
os.makedirs(out_dir, exist_ok=True)
out.save(os.path.join(out_dir, f'{name}.png'))
raw = bytearray()
for y in range(H):
    for x in range(W):
        r, g, b = op[x, y]
        raw += bytes((b, g, r, 255))
open(os.path.join(out_dir, f'{name}_{W}x{H}.bgra'), 'wb').write(raw)
cmp_ = Image.new('RGB', (W, 2 * (H - TOP) + 8), 'white')
cmp_.paste(im.crop((0, TOP, W, H)), (0, 0))
cmp_.paste(out.crop((0, TOP, W, H)), (0, H - TOP + 8))
cmp_.resize((2 * W, 2 * cmp_.height)).save(os.path.join(out_dir, f'{name}_before_after.png'))
print(f'{len([k for k in anchor if BLOCK[0] <= k < BLOCK[1]])} ink-free anchor columns inside the block range')
print(f'block x {x0}-{x1} (centre {block_centre:.1f}) moved by {dx} to centre {centre}: x {x0 + dx}-{x1 + dx}; chevron x {CHEVRON[0]}-{CHEVRON[1]} removed')
print(f'-> {out_dir}/{name}_{W}x{H}.bgra ({len(raw)} bytes)')
