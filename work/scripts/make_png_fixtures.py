"""Test fixtures for the C# PngReader (step 3, textures): PNGs in every supported colour type, each with the BGRA PIL
decodes it to (B, G, R, A per pixel, rows top to bottom), plus one RGBA file written here by hand so that every row
uses a different filter (0-4, in turn) and two negative cases (16-bit, interlaced). PIL is the independent decoder.

usage: python -I make_png_fixtures.py <outDir>
"""
import os
import random
import struct
import sys
import zlib
from PIL import Image

out = sys.argv[1]
os.makedirs(out, exist_ok=True)
rnd = random.Random(20261022)
W, H = 37, 23  # odd sizes: partial bytes at low bit depths


def bgra_of(path):
    im = Image.open(path).convert('RGBA')
    return bytes(b for r, g, b_, a in im.getdata() for b in (b_, g, r, a))


def save(name, im, **kw):
    path = os.path.join(out, name + '.png')
    im.save(path, **kw)
    open(os.path.join(out, name + '.bgra'), 'wb').write(bgra_of(path))
    print(name, im.mode, im.size)


def noise(mode, n):
    return Image.frombytes(mode, (W, H), bytes(rnd.randrange(256) for _ in range(W * H * n)))


save('rgba8', noise('RGBA', 4))
save('rgb8', noise('RGB', 3))
save('grey8', noise('L', 1))
save('greyalpha8', noise('LA', 2))
save('grey1', noise('L', 1).convert('1'))
pal = noise('RGB', 3).quantize(colors=200)
save('palette8_trns', pal, transparency=bytes(rnd.randrange(256) for _ in range(200)))
pal16 = noise('RGB', 3).quantize(colors=16)
save('palette4', pal16, bits=4)
rgb_key = noise('RGB', 3)
save('rgb8_trns', rgb_key, transparency=rgb_key.getpixel((0, 0)))


def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xFFFFFFFF)


def paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    return a if pa <= pb and pa <= pc else b if pb <= pc else c


def write_filtered(name, pixels, w, h):
    """RGBA 8-bit, row y filtered with filter y % 5; the expected BGRA is the input itself."""
    stride, bpp = w * 4, 4
    raw = bytearray()
    for y in range(h):
        f = y % 5
        cur = pixels[y * stride:(y + 1) * stride]
        prev = pixels[(y - 1) * stride:y * stride] if y else bytes(stride)
        raw.append(f)
        for i in range(stride):
            a = cur[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            pred = (0, a, b, (a + b) >> 1, paeth(a, b, c))[f]
            raw.append((cur[i] - pred) & 0xFF)
    ihdr = struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0)
    png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) + chunk(b'tEXt', b'Comment\x00skipped') \
        + chunk(b'IDAT', zlib.compress(bytes(raw))) + chunk(b'IEND', b'')
    open(os.path.join(out, name + '.png'), 'wb').write(png)
    rgba = Image.frombytes('RGBA', (w, h), bytes(pixels))
    open(os.path.join(out, name + '.bgra'), 'wb').write(bgra_of_image(rgba))
    assert bgra_of(os.path.join(out, name + '.png')) == bgra_of_image(rgba), 'PIL disagrees with the hand-made file'
    print(name, 'filters 0-4')


def bgra_of_image(im):
    return bytes(b for r, g, b_, a in im.getdata() for b in (b_, g, r, a))


write_filtered('filters_rgba8', bytes(rnd.randrange(256) for _ in range(W * H * 4)), W, H)

# negative cases (no .bgra): refused by the reader
Image.frombytes('I;16', (W, H), bytes(rnd.randrange(256) for _ in range(W * H * 2))).save(os.path.join(out, 'bad_grey16.png'))
# PIL doesn't write interlaced PNGs: an IHDR with the interlace flag set (valid CRC) is enough for the refusal
interlaced_ihdr = struct.pack('>IIBBBBB', W, H, 8, 2, 0, 0, 1)
open(os.path.join(out, 'bad_interlaced.png'), 'wb').write(
    b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', interlaced_ihdr) + chunk(b'IDAT', zlib.compress(b'')) + chunk(b'IEND', b''))
print('done')
