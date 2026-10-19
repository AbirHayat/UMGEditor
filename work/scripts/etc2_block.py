# Independent ETC2 RGB block check, written from the Khronos Data Format spec (ETC2, section "Format RGB ETC2").
# Decodes the mode of one 8-byte block and, for planar mode, the full 4x4 pixel grid.
# usage: python etc2_block.py raw_mip.bin width blockX blockY [blockX blockY ...]
import sys

def ext(v, bits):  # expand an n-bit value to 8 bits by bit replication
    return (v << (8 - bits)) | (v >> (2 * bits - 8)) if bits >= 4 else v

def clamp(v):
    return max(0, min(255, v))

def decode(block):
    w = int.from_bytes(block, 'big')        # bit 63 = MSB of the first byte
    bit = lambda i: (w >> i) & 1
    bits = lambda hi, lo: (w >> lo) & ((1 << (hi - lo + 1)) - 1)
    diff = bit(33)
    if not diff:
        return 'individual', None
    def sgn3(v): return v - 8 if v & 4 else v
    r = bits(63, 59) + sgn3(bits(58, 56))
    g = bits(55, 51) + sgn3(bits(50, 48))
    b = bits(47, 43) + sgn3(bits(42, 40))
    if not 0 <= r <= 31: return 'T', None
    if not 0 <= g <= 31: return 'H', None
    if not 0 <= b <= 31:
        # planar: RO 62..57, GO 56 + 54..49, BO 48 + 44..43 + 41..39,
        #         RH 38..34 + 32, GH 31..25, BH 24..19, RV 18..13, GV 12..6, BV 5..0
        RO = bits(62, 57)
        GO = (bit(56) << 6) | bits(54, 49)
        BO = (bit(48) << 5) | (bits(44, 43) << 3) | bits(41, 39)
        RH = (bits(38, 34) << 1) | bit(32)
        GH = bits(31, 25); BH = bits(24, 19)
        RV = bits(18, 13); GV = bits(12, 6); BV = bits(5, 0)
        o = (ext(RO, 6), ext(GO, 7), ext(BO, 6))
        h = (ext(RH, 6), ext(GH, 7), ext(BH, 6))
        v = (ext(RV, 6), ext(GV, 7), ext(BV, 6))
        grid = [[tuple(clamp((x * (h[c] - o[c]) + y * (v[c] - o[c]) + 4 * o[c] + 2) >> 2) for c in range(3))
                 for x in range(4)] for y in range(4)]
        info = dict(RO=RO, RH=RH, RH_bits_38_34=bits(38, 34), bit32=bit(32), bit33=bit(33), O=o, H=h, V=v)
        return 'planar', (grid, info)
    return 'differential', None

data = open(sys.argv[1], 'rb').read()
width = int(sys.argv[2])
blocks_per_row = width // 4
coords = list(map(int, sys.argv[3:]))
for bx, by in zip(coords[::2], coords[1::2]):
    off = (by * blocks_per_row + bx) * 8
    mode, result = decode(data[off:off + 8])
    print(f'block ({bx},{by}) bytes {data[off:off+8].hex()} mode {mode}')
    if result:
        grid, info = result
        print('   ', info)
        print('    red row 0 by x:', [grid[0][x][0] for x in range(4)], ' full pixel row 0:', grid[0])
