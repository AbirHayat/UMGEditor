# Parse an eFootball (UE4.26 pre-version) IoStore container header chunk.
import struct, sys
def parse(b, verbose=True):
    o=0
    cid,=struct.unpack_from('<Q',b,o); o+=8
    pcount,=struct.unpack_from('<I',b,o); o+=4
    names_n,=struct.unpack_from('<i',b,o); o+=4; names=b[o:o+names_n]; o+=names_n
    hashes_n,=struct.unpack_from('<i',b,o); o+=4; hashes=b[o:o+hashes_n]; o+=hashes_n
    ids_n,=struct.unpack_from('<i',b,o); o+=4
    ids=list(struct.unpack_from('<%dQ'%ids_n,b,o)); o+=8*ids_n
    se_n,=struct.unpack_from('<i',b,o); o+=4; se_start=o; se=b[o:o+se_n]; o+=se_n
    entries=[]
    for i in range(ids_n):
        p=16*i
        ec,ebc,imp_n,imp_off=struct.unpack_from('<iiII',se,p)
        imps=list(struct.unpack_from('<%dQ'%imp_n,se,p+8+imp_off)) if imp_n else []
        entries.append((ec,ebc,imps))
    tail=b[o:]
    nz=len(tail.rstrip(b'\0'))
    if verbose:
        print(f'container_id {cid:016x} packageCount {pcount} names {names_n}B hashes {hashes_n}B ids {ids_n} storeEntries {se_n}B ({se_n/ max(ids_n,1):.1f}B/pkg) tail {len(tail)}B, nonzero tail {nz}B: {tail[:max(nz,8)].hex()}')
    return dict(cid=cid,pcount=pcount,names=names,hashes=hashes,ids=ids,entries=entries,tail=tail[:nz] if nz else b'',end=o)
if __name__=='__main__':
    parse(open(sys.argv[1],'rb').read())
