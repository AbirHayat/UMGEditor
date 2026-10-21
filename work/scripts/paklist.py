# Read-only lister for UE4.26 pak v11 (encrypted index). Usage: paklist.py <pak> <hexkey> [filter]
import sys, struct
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
pak, key = sys.argv[1], bytes.fromhex(sys.argv[2].removeprefix('0x'))
flt = sys.argv[3].lower() if len(sys.argv) > 3 else ''
dec = lambda b: Cipher(algorithms.AES(key), modes.ECB()).decryptor().update(b)
f = open(pak, 'rb'); f.seek(0, 2); n = f.tell(); f.seek(n - 221); info = f.read(221)
i = info.find(struct.pack('<I', 0x5A6F12E1)); enc = info[i-1]
ver, ioff, isz = struct.unpack_from('<iqq', info, i + 4)
comp = [info[i+44+32*k:i+44+32*k+32].split(b'\0')[0].decode() for k in range(5)]
def rd(off, sz):
    f.seek(off); b = f.read(sz); return dec(b) if enc else b
class R:
    def __init__(s, b): s.b, s.p = b, 0
    def u(s, fmt):
        v = struct.unpack_from(fmt, s.b, s.p); s.p += struct.calcsize(fmt); return v[0] if len(v) == 1 else v
    def fs(s):
        l = s.u('<i')
        if l == 0: return ''
        if l < 0: v = s.b[s.p:s.p-2*l].decode('utf-16-le')[:-1]; s.p += -2*l; return v
        v = s.b[s.p:s.p+l-1].decode('latin1'); s.p += l; return v
r = R(rd(ioff, isz))
mount = r.fs(); num = r.u('<i'); seed = r.u('<Q')
if r.u('<i'): r.u('<qq'); r.p += 20
hasfdi = r.u('<i')
if hasfdi: fdo, fds = r.u('<qq'); r.p += 20
esz = r.u('<i'); encoded = r.b[r.p:r.p+esz]
print(f'{pak}: size {n} ver {ver} index_encrypted {enc} mount {mount!r} entries {num} compression {comp}')
if not hasfdi: print('no full directory index'); sys.exit()
d = R(rd(fdo, (fds + 15) // 16 * 16))
for _ in range(d.u('<i')):
    dn = d.fs()
    for _ in range(d.u('<i')):
        fn = d.fs(); eo = d.u('<i')
        if flt and flt not in (dn + fn).lower(): continue
        e = R(encoded); e.p = eo; bits = e.u('<I')
        off = e.u('<I') if bits & (1 << 31) else e.u('<Q')
        usz = e.u('<I') if bits & (1 << 30) else e.u('<Q')
        cm = (bits >> 23) & 0x3f
        csz = (e.u('<I') if bits & (1 << 29) else e.u('<Q')) if cm else usz
        print(f'  {dn}{fn}  off {off} size {usz} stored {csz} comp {comp[cm-1] if cm else "none"} encrypted {(bits>>22)&1} blocks {(bits>>6)&0xffff}')
