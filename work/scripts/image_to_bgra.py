"""C16: an image as the raw BGRA a B8G8R8A8 texture stores (B, G, R, A per pixel, rows top to bottom), pixels exact (no
resize, no colour change; images without alpha get A = 255). Writes <out>/<name>_<W>x<H>.bgra and a preview PNG, and
checks the round trip.

usage: python -I image_to_bgra.py <image> <outDir> <name> [expectedWidth expectedHeight]
"""
import sys
import os
from PIL import Image

src, out_dir, name = sys.argv[1], sys.argv[2], sys.argv[3]
im = Image.open(src)
mode = im.mode
im = im.convert('RGBA')
w, h = im.size
if len(sys.argv) > 5:
    assert (w, h) == (int(sys.argv[4]), int(sys.argv[5])), f'{src} is {w}x{h}, expected {sys.argv[4]}x{sys.argv[5]}'
px = im.load()
raw = bytearray()
for y in range(h):
    for x in range(w):
        r, g, b, a = px[x, y]
        raw += bytes((b, g, r, a))
back = Image.frombytes('RGBA', (w, h), bytes(raw), 'raw', 'BGRA')
assert back.tobytes() == im.tobytes(), 'round trip differs'
os.makedirs(out_dir, exist_ok=True)
raw_path = os.path.join(out_dir, f'{name}_{w}x{h}.bgra')
open(raw_path, 'wb').write(raw)
im.save(os.path.join(out_dir, f'{name}_{w}x{h}.png'))
print(f'{src} ({mode} {w}x{h}) -> {raw_path} ({len(raw)} bytes)')
