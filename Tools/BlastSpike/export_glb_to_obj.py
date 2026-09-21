"""Writes the first mesh of a .glb as an OBJ with one vertex/normal/uv index per corner.

    python3 export_glb_to_obj.py in.glb out.obj [--largest-part]

--largest-part keeps only the biggest connected piece (vertices welded by exact
position), which for the tower is its closed conical roof: the input Blast was
built for, as opposed to the whole open, many-part model.
"""
import collections, json, struct, sys

def largest_part(pos, idx):
    ids = {}
    weld = [ids.setdefault(p, len(ids)) for p in pos]
    parent = list(range(len(ids)))
    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]; a = parent[a]
        return a
    for k in range(0, len(idx), 3):
        for u, v in ((idx[k], idx[k+1]), (idx[k+1], idx[k+2])):
            ru, rv = find(weld[u]), find(weld[v])
            if ru != rv: parent[ru] = rv
    parts = collections.defaultdict(list)
    for k in range(0, len(idx), 3):
        parts[find(weld[idx[k]])].extend(idx[k:k+3])
    return max(parts.values(), key=len)

def main(src, dst, only_largest):
    d = open(src, 'rb').read()
    length = struct.unpack_from('<III', d, 0)[2]
    off, js, b = 12, None, None
    while off < length:
        clen, ctype = struct.unpack_from('<II', d, off); off += 8
        if ctype == 0x4E4F534A: js = json.loads(d[off:off+clen].decode())
        elif ctype == 0x004E4942: b = d[off:off+clen]
        off += clen
    acc, bvs = js['accessors'], js['bufferViews']
    CT = {5121: ('B', 1), 5123: ('H', 2), 5125: ('I', 4), 5126: ('f', 4)}
    NC = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}
    def read(i):
        a = acc[i]; bv = bvs[a['bufferView']]
        base = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
        fmt, sz = CT[a['componentType']]; n = NC[a['type']]
        stride = bv.get('byteStride') or sz * n
        return [struct.unpack_from('<' + fmt * n, b, base + k * stride) for k in range(a['count'])]
    pr = js['meshes'][0]['primitives'][0]
    pos = read(pr['attributes']['POSITION'])
    nrm = read(pr['attributes']['NORMAL'])
    uv = read(pr['attributes']['TEXCOORD_0'])
    idx = [t[0] for t in read(pr['indices'])]
    if only_largest:
        kept = largest_part(pos, idx)
        used = sorted(set(kept)); remap = {old: new for new, old in enumerate(used)}
        pos = [pos[i] for i in used]; nrm = [nrm[i] for i in used]; uv = [uv[i] for i in used]
        idx = [remap[i] for i in kept]
    with open(dst, 'w') as f:
        for p in pos: f.write('v %.7g %.7g %.7g\n' % p)
        for n in nrm: f.write('vn %.7g %.7g %.7g\n' % n)
        # glTF puts v=0 at the top of the image, OBJ and Blast at the bottom.
        for t in uv: f.write('vt %.7g %.7g\n' % (t[0], 1.0 - t[1]))
        for k in range(0, len(idx), 3):
            a, c, e = idx[k] + 1, idx[k+1] + 1, idx[k+2] + 1
            f.write('f %d/%d/%d %d/%d/%d %d/%d/%d\n' % (a, a, a, c, c, c, e, e, e))
    print('%s: %d vertices, %d triangles' % (dst, len(pos), len(idx) // 3))

main(sys.argv[1], sys.argv[2], '--largest-part' in sys.argv[3:])
