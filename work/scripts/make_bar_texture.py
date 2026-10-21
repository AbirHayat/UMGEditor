"""C15: the scoreboard bar texture for mobile, from the user's design (custom Design/MatchTimePlate_2.png, 3184x96 RGBA).

The design has the bar art at x 0..698, a fully transparent gap, and the "UEFA.tv" watermark at x 3003..3183. On
mobile the bar (Img_Line) maps 698 px to the board's 372 units; at that scale the untrimmed watermark would run past
the 1920-unit edge. So only transparent gap columns are cut, to the target width: art and watermark pixels stay
byte-identical. Writes <out>/<name>_<W>x<H>.bgra (B, G, R, A per pixel, rows top to bottom, as the texture stores
them) and a preview PNG.

usage: python -I make_bar_texture.py <design.png> <outDir> [targetWidth=2964] [name=MatchTimeBarUCL0]
"""
import sys
import os
from PIL import Image

src_path, out_dir = sys.argv[1], sys.argv[2]
target = int(sys.argv[3]) if len(sys.argv) > 3 else 2964
name = sys.argv[4] if len(sys.argv) > 4 else 'MatchTimeBarUCL0'

im = Image.open(src_path)
assert im.mode == 'RGBA', f'{src_path} is {im.mode}, expected RGBA'
w, h = im.size
px = im.load()
col_alpha = [max(px[x, y][3] for y in range(h)) for x in range(w)]
art_end = next(x for x in range(w) if col_alpha[x] == 0)            # first transparent column after the art
mark_start = next(x for x in range(art_end, w) if col_alpha[x] > 0)  # the watermark
cut = w - target
assert cut >= 0, f'target {target} is wider than the design ({w})'
assert mark_start - art_end > cut, f'the gap ({mark_start - art_end} columns) is narrower than the cut ({cut})'
keep_until = mark_start - cut                                         # cut columns [keep_until, mark_start)
assert all(col_alpha[x] == 0 for x in range(keep_until, mark_start)), 'a cut column has non-zero alpha'

out = Image.new('RGBA', (target, h))
out.paste(im.crop((0, 0, keep_until, h)), (0, 0))
out.paste(im.crop((mark_start, 0, w, h)), (keep_until, 0))
o = out.load()
# byte-exact checks: the art and the watermark
for y in range(h):
    for x in range(0, art_end):
        assert o[x, y] == px[x, y], f'art pixel {x},{y} changed'
    for x in range(mark_start, w):
        assert o[x - cut, y] == px[x, y], f'watermark pixel {x},{y} changed'

os.makedirs(out_dir, exist_ok=True)
raw = bytearray()
for y in range(h):
    for x in range(target):
        r, g, b, a = o[x, y]
        raw += bytes((b, g, r, a))
raw_path = os.path.join(out_dir, f'{name}_{target}x{h}.bgra')
with open(raw_path, 'wb') as f:
    f.write(raw)
out.save(os.path.join(out_dir, f'{name}_{target}x{h}.png'))
print(f'design {w}x{h}: art x 0..{art_end - 1}, watermark x {mark_start}..{w - 1}; cut {cut} transparent columns at x {keep_until}..{mark_start - 1}')
print(f'-> {raw_path} ({len(raw)} bytes, {target}x{h} BGRA); watermark now at x {mark_start - cut}..{target - 1}')
