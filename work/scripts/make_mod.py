# Package one patched Zen chunk as a game-ready eFootball Mobile mod container:
#   <name>.utoc/.ucas (retoc pack-raw) + a hand-built eFootball container header + <name>.pak stub (copy of a stock stub).
# Nothing under the stock Paks folder is written; --stub and --source-header are only read.
import argparse, hashlib, json, shutil, struct, subprocess, sys, tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import hdr

ap = argparse.ArgumentParser()
ap.add_argument('--chunk', required=True, help='patched ExportBundleData chunk; file name = 24-hex chunk id')
ap.add_argument('--path', required=True, help='directory-index path, e.g. ../../../PesMobile/Content/.../MatchTime.uasset')
ap.add_argument('--source-header', required=True, help='ContainerHeader chunk of the stock container that holds the package (from unpack-raw)')
ap.add_argument('--stub', required=True, help='a stock 365-byte .pak mount stub to copy')
ap.add_argument('--name', default='pc9999_mobile_and_P', help='container name (all three files share it)')
ap.add_argument('--out', required=True, help='output folder (gets raw/ and mod/)')
ap.add_argument('--retoc', default=r'E:\n3tron\patch\Tools\retoc-x86_64-pc-windows-msvc\retoc.exe')
a = ap.parse_args()

def run(*args):
    r = subprocess.run([a.retoc, *args], capture_output=True, text=True)
    if r.returncode != 0: sys.exit(f'retoc {args[0]} failed: {r.stderr or r.stdout}')
    return r.stdout

chunk = Path(a.chunk)
chunk_id = chunk.name.lower()
assert len(chunk_id) == 24 and chunk_id.endswith('0002'), 'expected an ExportBundleData chunk file named by its chunk id'
package_id, = struct.unpack('<Q', bytes.fromhex(chunk_id[:16]))   # ExportBundleData chunk id = package id (LE) + index 0 + type 2

out = Path(a.out); raw = out / 'raw'; mod = out / 'mod'
if out.exists(): shutil.rmtree(out)
(raw / 'chunks').mkdir(parents=True); mod.mkdir()
shutil.copyfile(chunk, raw / 'chunks' / chunk_id)
(raw / 'manifest.json').write_text(json.dumps({'chunk_paths': {chunk_id: a.path}, 'version': 'DirectoryIndex', 'mount_point': '../../../'}, indent=2))

# the container id comes from the name; pack once without a header to learn it
with tempfile.TemporaryDirectory() as tmp:
    probe = Path(tmp) / f'{a.name}.utoc'
    run('pack-raw', str(raw), str(probe))
    cid_bytes = probe.read_bytes()[0x38:0x40]
cid, = struct.unpack('<Q', cid_bytes)

src = hdr.parse(Path(a.source_header).read_bytes(), False)
if package_id not in src['ids']: sys.exit(f'package id {package_id:016x} is not in the source container header')
ec, ebc, imps = src['entries'][src['ids'].index(package_id)]
se = struct.pack('<iiII', ec, ebc, len(imps), 8) + struct.pack(f'<{len(imps)}Q', *imps)  # CArrayView offset is relative to the view (entry+8)
b = struct.pack('<QI', cid, 1) + struct.pack('<i', 0) + struct.pack('<i', len(src['hashes'])) + src['hashes']
b += struct.pack('<iQ', 1, package_id) + struct.pack('<i', len(se)) + se + struct.pack('<ii', 0, 0)
b += b'\0' * ((-len(b)) % 16)
check = hdr.parse(b, False)
assert check['cid'] == cid and check['ids'] == [package_id] and check['entries'][0] == (ec, ebc, imps)
(raw / 'chunks' / (cid_bytes.hex() + '0000000a')).write_bytes(b)

run('pack-raw', str(raw), str(mod / f'{a.name}.utoc'))
shutil.copyfile(a.stub, mod / f'{a.name}.pak')
print(run('verify', str(mod / f'{a.name}.utoc')).strip())
for f in sorted(mod.iterdir()):
    print(f'{hashlib.sha256(f.read_bytes()).hexdigest()}  {f.name}')
