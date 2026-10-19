# Compare our decoded PNG with FModel's export of the same texture.
# Reports size, per-channel mean/max abs difference, alpha difference, and whether a flipped or
# red/blue-swapped version of ours would match better (it must not, or orientation/channel order is wrong).
# usage: python compare_tex.py ours.png fmodel.png [more pairs...]
import sys
from PIL import Image, ImageChops

def stats(a, b):
    diff = ImageChops.difference(a, b)
    per = []
    for ch in diff.split():
        hist = ch.histogram()
        total = sum(hist)
        mean = sum(i * n for i, n in enumerate(hist)) / total
        mx = max(i for i, n in enumerate(hist) if n)
        per.append((mean, mx))
    return per

def score(a, b):
    return sum(m for m, _ in stats(a, b))

pairs = sys.argv[1:]
ok = True
for ours_path, ref_path in zip(pairs[::2], pairs[1::2]):
    ours = Image.open(ours_path).convert('RGBA')
    ref = Image.open(ref_path).convert('RGBA')
    print(f'== {ours_path.split(chr(92))[-1]}  ours {ours.size}  fmodel {ref.size}')
    if ours.size != ref.size:
        print('   FAIL size differs'); ok = False; continue
    names = 'RGBA'
    per = stats(ours, ref)
    print('   ' + '  '.join(f'{n}: mean {m:.2f} max {x}' for n, (m, x) in zip(names, per)))
    alpha_ours = ours.getchannel('A').getextrema(); alpha_ref = ref.getchannel('A').getextrema()
    print(f'   alpha range ours {alpha_ours} fmodel {alpha_ref}')
    base = score(ours, ref)
    r, g, b, a = ours.split()
    variants = {
        'flipped vertically': ours.transpose(Image.Transpose.FLIP_TOP_BOTTOM),
        'flipped horizontally': ours.transpose(Image.Transpose.FLIP_LEFT_RIGHT),
        'red/blue swapped': Image.merge('RGBA', (b, g, r, a)),
        'alpha ignored (opaque)': Image.merge('RGBA', (r, g, b, Image.new('L', ours.size, 255))),
    }
    for name, v in variants.items():
        s = score(v, ref)
        if s > base: verdict = 'worse (good)'
        elif s == base: verdict = 'no difference (not informative for this texture)'
        else: verdict = 'BETTER -> ours is ' + name; ok = False
        print(f'   {name:24} total mean diff {s:7.2f} vs {base:.2f}  {verdict}')
    exact = all(x == 0 for _, x in per)
    close = all(x <= 2 for _, x in per)
    print('   RESULT', 'IDENTICAL' if exact else 'MATCH (max diff <= 2)' if close else 'DIFFERENT')
    ok = ok and close
print('ALL OK' if ok else 'PROBLEMS FOUND')
