#!/usr/bin/env python3
"""Independent check of a "pointed" texture re-point (step 3 fix, canary C18): stock chunk vs modded chunk of one
UE 4.26 Zen package, without the editor's C# code.

usage: check_point.py <stock.bin> <mod.bin> [--expect-export NAME ...]

What it proves (exit 0 only if all hold):
  1. the name map is the stock one plus new names at the end (old names and their hashes unchanged);
  2. every stored hash is CityHash64 of the lower-cased name (this file's own CityHash64, which must first reproduce
     every stock hash), including the new ones;
  3. the summary changes only where the name map's growth moves things: NameMapNamesSize, NameMapHashesOffset/Size,
     ImportMap/ExportMap/ExportBundles/GraphData offsets (+shift); everything else in it is identical;
  4. everything from the import map to the end is stock moved by the shift, except whole 8-byte FNames that now name a
     new name (nothing else differs: export map, CookedSerialOffset/Size, bundles, graph, export data);
  5. each changed FName is the AssetPathName of a top-level `texture` StructProperty SoftObjectPath tag (12 bytes,
     empty SubPathString), inside the export it belongs to, and named the stock path before.
"""
import struct
import sys

M64 = (1 << 64) - 1
K0, K1, K2 = 0xc3a5c85c97cb3127, 0xb492b66fbe98f273, 0x9ae16a3b2f90404f


def f64(s, i): return struct.unpack_from('<Q', s, i)[0]
def f32(s, i): return struct.unpack_from('<I', s, i)[0]
def rot(v, n): return v if n == 0 else ((v >> n) | (v << (64 - n))) & M64
def smix(v): return (v ^ (v >> 47)) & M64
def bswap(v): return int.from_bytes(v.to_bytes(8, 'little'), 'big')


def h16(u, v, mul=0x9ddfea08eb382d69):
    a = ((u ^ v) * mul) & M64
    a ^= a >> 47
    b = ((v ^ a) * mul) & M64
    b ^= b >> 47
    return (b * mul) & M64


def len0to16(s):
    n = len(s)
    if n >= 8:
        mul = (K2 + n * 2) & M64
        a = (f64(s, 0) + K2) & M64
        b = f64(s, n - 8)
        c = (rot(b, 37) * mul + a) & M64
        d = ((rot(a, 25) + b) * mul) & M64
        return h16(c, d, mul)
    if n >= 4:
        mul = (K2 + n * 2) & M64
        return h16((n + (f32(s, 0) << 3)) & M64, f32(s, n - 4), mul)
    if n > 0:
        y = s[0] + (s[n >> 1] << 8)
        z = n + (s[n - 1] << 2)
        return (smix(((y * K2) ^ (z * K0)) & M64) * K2) & M64
    return K2


def len17to32(s):
    n = len(s)
    mul = (K2 + n * 2) & M64
    a = (f64(s, 0) * K1) & M64
    b = f64(s, 8)
    c = (f64(s, n - 8) * mul) & M64
    d = (f64(s, n - 16) * K2) & M64
    return h16((rot((a + b) & M64, 43) + rot(c, 30) + d) & M64, (a + rot((b + K2) & M64, 18) + c) & M64, mul)


def len33to64(s):
    n = len(s)
    mul = (K2 + n * 2) & M64
    a = (f64(s, 0) * K2) & M64
    b = f64(s, 8)
    c = f64(s, n - 24)
    d = f64(s, n - 32)
    e = (f64(s, 16) * K2) & M64
    f = (f64(s, 24) * 9) & M64
    g = f64(s, n - 8)
    h = (f64(s, n - 16) * mul) & M64
    u = (rot((a + g) & M64, 43) + (rot(b, 30) + c) * 9) & M64
    v = (((a + g) ^ d) + f + 1) & M64
    w = (bswap(((u + v) * mul) & M64) + h) & M64
    x = (rot((e + f) & M64, 42) + c) & M64
    y = ((bswap(((v + w) * mul) & M64) + g) * mul) & M64
    z = (e + f + c) & M64
    a = (bswap(((x + z) * mul + y) & M64) + b) & M64
    b = (smix(((z + a) * mul + d + h) & M64) * mul) & M64
    return (b + x) & M64


def weak(s, i, a, b):
    w, x, y, z = f64(s, i), f64(s, i + 8), f64(s, i + 16), f64(s, i + 24)
    a = (a + w) & M64
    b = rot((b + a + z) & M64, 21)
    c = a
    a = (a + x + y) & M64
    b = (b + rot(a, 44)) & M64
    return (a + z) & M64, (b + c) & M64


def cityhash64(s):
    n = len(s)
    if n <= 16: return len0to16(s)
    if n <= 32: return len17to32(s)
    if n <= 64: return len33to64(s)
    x = f64(s, n - 40)
    y = (f64(s, n - 16) + f64(s, n - 56)) & M64
    z = h16((f64(s, n - 48) + n) & M64, f64(s, n - 24))
    v = weak(s, n - 64, n, z)
    w = weak(s, n - 32, (y + K1) & M64, x)
    x = (x * K1 + f64(s, 0)) & M64
    left = (n - 1) & ~63
    i = 0
    while True:
        x = (rot((x + y + v[0] + f64(s, i + 8)) & M64, 37) * K1) & M64
        y = (rot((y + v[1] + f64(s, i + 48)) & M64, 42) * K1) & M64
        x ^= w[1]
        y = (y + v[0] + f64(s, i + 40)) & M64
        z = (rot((z + w[0]) & M64, 33) * K1) & M64
        v = weak(s, i, (v[1] * K1) & M64, (x + w[0]) & M64)
        w = weak(s, i + 32, (z + w[1]) & M64, (y + f64(s, i + 16)) & M64)
        z, x = x, z
        i += 64
        left -= 64
        if left == 0: break
    return h16((h16(v[0], w[0]) + smix(y) * K1 + z) & M64, (h16(v[1], w[1]) + x) & M64)


def name_hash(text, wide):
    lower = ''.join(chr(ord(c) + 32) if 'A' <= c <= 'Z' else c for c in text)
    return cityhash64(lower.encode('utf-16-le' if wide else 'latin-1'))


class Package:
    def __init__(self, path):
        self.b = open(path, 'rb').read()
        b = self.b
        (self.names_at, self.names_size, self.hashes_at, self.hashes_size, self.import_at, self.export_map_at,
         self.bundles_at, self.graph_at, self.graph_size) = struct.unpack_from('<9i', b, 24)
        if f64(b, self.hashes_at) != 0xC1640000: raise SystemExit(f'{path}: unknown name hash algorithm')
        self.names, p = [], self.names_at
        for _ in range(self.hashes_size // 8 - 1):
            wide, n = b[p] & 0x80, ((b[p] & 0x7F) << 8) | b[p + 1]
            p += 2
            size = 2 * n if wide else n
            self.names.append((b[p:p + size].decode('utf-16-le' if wide else 'latin-1'), bool(wide)))
            p += size
        if p != self.names_at + self.names_size: raise SystemExit(f'{path}: names block size mismatch')
        self.hashes = [f64(b, self.hashes_at + 8 + 8 * i) for i in range(len(self.names))]
        self.export_count = (self.bundles_at - self.export_map_at) // 72

    def name_at(self, at):
        i, n = struct.unpack_from('<Ii', self.b, at)
        if i >= len(self.names): return f'<name {i}>'
        return self.names[i][0] if n == 0 else f'{self.names[i][0]}_{n - 1}'

    def export_name(self, e):
        i, n = struct.unpack_from('<II', self.b, self.export_map_at + 72 * e + 16)
        i &= 0x3FFFFFFF
        return self.names[i][0] if n == 0 else f'{self.names[i][0]}_{n - 1}'

    def spans(self):
        """(export, start, size) in the chunk, in bundle order (Serialize commands), as the loader reads them."""
        b, pos = self.b, self.bundles_at
        remaining, found, headers = (self.graph_at - self.bundles_at) // 8, 0, []
        while found < remaining:
            remaining -= 1
            first, count = struct.unpack_from('<II', b, pos)
            headers.append((first, count))
            found += count
            pos += 8
        if found != remaining: raise SystemExit('export bundle count mismatch')
        cursor, out = self.graph_at + self.graph_size, []
        for first, count in headers:
            for k in range(count):
                export, command = struct.unpack_from('<II', b, pos + 8 * (first + k))
                if command != 1: continue
                size = f64(b, self.export_map_at + 72 * export + 8)
                out.append((export, cursor, size))
                cursor += size
        if cursor != len(b): raise SystemExit(f'export data ends at {cursor}, the chunk at {len(b)}')
        return out


def main():
    args = sys.argv[1:]
    expect = [args[i + 1] for i, a in enumerate(args) if a == '--expect-export']
    files = [a for i, a in enumerate(args) if not a.startswith('--') and (i == 0 or args[i - 1] != '--expect-export')]
    stock, mod = Package(files[0]), Package(files[1])
    fails = []

    def check(ok, what):
        print(('  ok: ' if ok else '  FAIL: ') + what)
        if not ok: fails.append(what)

    print(f'stock {len(stock.b)} bytes, {len(stock.names)} names; mod {len(mod.b)} bytes, {len(mod.names)} names')
    k = len(mod.names) - len(stock.names)
    check(k > 0 and mod.names[:len(stock.names)] == stock.names and mod.hashes[:len(stock.names)] == stock.hashes,
          f'name map = stock + {k} new name(s) at the end, old names and hashes unchanged')
    new = mod.names[len(stock.names):]
    for t, w in new: print(f'    new name: "{t}" ({len(t)} characters)')
    bad = [t for (t, w), h in zip(stock.names, stock.hashes) if name_hash(t, w) != h]
    check(not bad, f'this CityHash64 reproduces all {len(stock.names)} stock hashes' + (f' (not: {bad[:3]})' if bad else ''))
    check(all(name_hash(t, w) == h for (t, w), h in zip(new, mod.hashes[len(stock.names):])), 'the new hashes are CityHash64 of the lower-cased new names')

    added = sum(2 + len(t) for t, _ in new)
    shift = mod.import_at - stock.import_at
    align8 = lambda v: (v + 7) & ~7
    check(mod.b[:24] == stock.b[:24], 'summary @0..23 (Name, SourceName, PackageFlags, CookedHeaderSize) identical')
    check(mod.names_at == stock.names_at and mod.names_size == stock.names_size + added,
          f'NameMapNamesSize {stock.names_size} -> {mod.names_size} (+{added} = per name 2-byte header + characters)')
    check(mod.hashes_at == align8(mod.names_at + mod.names_size) and mod.hashes_size == stock.hashes_size + 8 * k,
          f'NameMapHashesOffset {stock.hashes_at} -> {mod.hashes_at} (names end padded to 8), NameMapHashesSize +{8 * k}')
    check(mod.b[stock.names_at + stock.names_size:mod.hashes_at][added:] == bytes(mod.hashes_at - (stock.names_at + stock.names_size) - added), 'padding before the hashes is zero')
    check(all(getattr(mod, a) - getattr(stock, a) == shift for a in ('import_at', 'export_map_at', 'bundles_at', 'graph_at'))
          and mod.graph_size == stock.graph_size and mod.b[60:64] == stock.b[60:64],
          f'ImportMap/ExportMap/ExportBundles/GraphData offsets +{shift}, GraphDataSize and pad identical')
    check(len(mod.b) == len(stock.b) + shift, f'chunk {len(stock.b)} -> {len(mod.b)} bytes (+{shift}: the whole difference is the name map growth)')

    tail_s, tail_m = stock.b[stock.import_at:], mod.b[mod.import_at:]
    check(len(tail_s) == len(tail_m), 'import map .. end: same length (moved as one piece)')
    diff = [i for i in range(min(len(tail_s), len(tail_m))) if tail_s[i] != tail_m[i]]
    groups = []
    for i in diff:
        if groups and i < groups[-1][0] + 8: groups[-1][1] = i
        else: groups.append([i, i])
    new_index = {t: len(stock.names) + j for j, (t, _) in enumerate(new)}
    spans = mod.spans()
    check(mod.export_count == stock.export_count and mod.b[mod.export_map_at:mod.bundles_at] == stock.b[stock.export_map_at:stock.bundles_at],
          f'export map identical ({mod.export_count} exports: every CookedSerialOffset/Size unchanged)')
    print(f'  {len(diff)} byte(s) differ after the move, in {len(groups)} place(s)')
    pointed = []
    for lo, hi in groups:
        at = None
        for start in range(max(0, hi - 7), lo + 1):  # the 8-byte FName holding every differing byte
            m_at = mod.import_at + start
            i_new = f32(mod.b, m_at)
            if i_new in new_index.values() and f32(stock.b, stock.import_at + start) < len(stock.names):
                at = start
                break
        if at is None:
            check(False, f'difference at +{lo}..+{hi} after the import map is not an FName naming a new name')
            continue
        m_at, s_at = mod.import_at + at, stock.import_at + at
        export = [(e, st, sz) for e, st, sz in spans if st <= m_at < st + sz]
        owner = mod.export_name(export[0][0]) if export else '?'
        tag = m_at - 49
        ok_tag = (mod.name_at(tag) == 'texture' and mod.name_at(tag + 8) == 'StructProperty' and struct.unpack_from('<ii', mod.b, tag + 16) == (12, 0)
                  and mod.name_at(tag + 24) == 'SoftObjectPath' and mod.b[tag + 32:tag + 49] == bytes(17) and f32(mod.b, m_at + 8) == 0)
        check(ok_tag, f'{owner}: 0x{m_at:X} is the AssetPathName of a top-level texture SoftObjectPath tag (tag @0x{tag:X}, 12 bytes, no SubPathString)')
        check(export and export[0][1] <= tag, f'{owner}: the tag lies inside export {export[0][0] if export else "?"} ({owner})')
        check(stock.b[s_at:s_at + 8] != mod.b[m_at:m_at + 8], f'{owner}: 8 bytes {stock.b[s_at:s_at + 8].hex()} -> {mod.b[m_at:m_at + 8].hex()}')
        print(f'    {owner}.texture: {stock.name_at(s_at)}\n      -> {mod.name_at(m_at)}')
        pointed.append(owner)
    if expect: check(sorted(pointed) == sorted(expect), f'pointed exports = {sorted(expect)} (got {sorted(pointed)})')
    print('PASS' if not fails else f'FAILED ({len(fails)})')
    return 0 if not fails else 1


if __name__ == '__main__':
    sys.exit(main())
