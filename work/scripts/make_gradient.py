"""C16: the navy UCL gradient for the plates (MatchTimeBase → MatchTimeNavyG_0: the banners' name rows and the replay
plate), generated from the user's bar art (work/out/art/C16/MatchTimeBarUC_0_699x96.png): per column the median colour
of the art, smoothed with a moving average (removes the chevrons and the divider), resampled to the target width and
repeated on every row. Opaque. Writes <out>/<name>_<W>x<H>.bgra (B, G, R, A per pixel) and a preview PNG.

usage: python -I make_gradient.py <bar.png> <outDir> [width=512] [height=64] [name=MatchTimeNavyG_0] [window=61]
"""
import sys
import os
from statistics import median
from PIL import Image

src, out_dir = sys.argv[1], sys.argv[2]
W = int(sys.argv[3]) if len(sys.argv) > 3 else 512
H = int(sys.argv[4]) if len(sys.argv) > 4 else 64
name = sys.argv[5] if len(sys.argv) > 5 else 'MatchTimeNavyG_0'
window = int(sys.argv[6]) if len(sys.argv) > 6 else 61

im = Image.open(src).convert('RGBA')
w, h = im.size
px = im.load()
cols = []
for x in range(w):
    opaque = [px[x, y] for y in range(h) if px[x, y][3] == 255]
    assert opaque, f'column {x} has no opaque pixel'
    cols.append(tuple(median(p[c] for p in opaque) for c in range(3)))
half = window // 2
smooth = []
for x in range(w):
    part = cols[max(0, x - half):min(w, x + half + 1)]
    smooth.append(tuple(sum(p[c] for p in part) / len(part) for c in range(3)))
row = []
for X in range(W):
    t = X * (w - 1) / (W - 1)
    i = min(int(t), w - 2)
    f = t - i
    row.append(tuple(round(smooth[i][c] * (1 - f) + smooth[i + 1][c] * f) for c in range(3)))

os.makedirs(out_dir, exist_ok=True)
out = Image.new('RGBA', (W, H))
raw = bytearray()
for y in range(H):
    for X, (r, g, b) in enumerate(row):
        out.putpixel((X, y), (r, g, b, 255))
        raw += bytes((b, g, r, 255))
raw_path = os.path.join(out_dir, f'{name}_{W}x{H}.bgra')
open(raw_path, 'wb').write(raw)
out.save(os.path.join(out_dir, f'{name}_{W}x{H}.png'))
print(f'{src} {w}x{h} -> {raw_path} ({len(raw)} bytes, {W}x{H} BGRA); ends {row[0]} / {row[-1]}, centre {row[W // 2]}')
