"""
gf_model.py — reader for Gen-7 (Sun/Moon) Pokemon GFModel, ported faithfully
from gdkchan's SPICA (Formats/GFL2/Model/*). No Blender, no dependencies.

Pipeline entry: a per-Pokemon "PC" container (magic 'PC') whose first entry is a
GFModel (magic 0x15122117, name 'gfmodel'). This module parses:
  - PC container split
  - GFModel header (sections, 4 hash-name tables, bbox, transform)
  - GFBone skeleton (name/parent/flags + scale/rot/translation)
  - GFMesh -> GFSubMesh: PICA200 command lists -> attributes/stride/index-fmt,
    then the raw interleaved vertex buffer decoded to per-vertex
    position/normal/uv/color/bone-index/bone-weight.

Materials and LUTs are skipped by their GFSection length (we only need geometry
+ skeleton + skinning), keeping the port small.

Reference: SPICA @ gdkchan (MIT).  This is a clean re-implementation of the
binary layout, not a copy of the C# source.
"""

import struct
from io import BytesIO


# ----------------------------------------------------------------------------
# PICA enums (values match SPICA)
# ----------------------------------------------------------------------------
class AttrName:
    Position = 0; Normal = 1; Tangent = 2; Color = 3
    TexCoord0 = 4; TexCoord1 = 5; TexCoord2 = 6
    BoneIndex = 7; BoneWeight = 8


# format: 0 Byte, 1 Ubyte, 2 Short, 3 Float
FMT_SCALES = [1.0 / 127, 1.0 / 255, 1.0 / 32767, 1.0]

PRIM_TRIANGLES = 0
PRIM_TRISTRIP = 1
PRIM_TRIFAN = 2

# PICA registers used by GFMesh
R_ATTRIBBUFFERS_FORMAT_LOW = 0x0201
R_ATTRIBBUFFERS_FORMAT_HIGH = 0x0202
R_ATTRIBBUFFER0_CONFIG1 = 0x0204
R_ATTRIBBUFFER0_CONFIG2 = 0x0205
R_INDEXBUFFER_CONFIG = 0x0227
R_NUMVERTICES = 0x0228
R_FIXEDATTRIB_INDEX = 0x0232
R_FIXEDATTRIB_DATA0 = 0x0233
R_FIXEDATTRIB_DATA1 = 0x0234
R_FIXEDATTRIB_DATA2 = 0x0235
R_VSH_NUM_ATTR = 0x0242
R_PRIMITIVE_CONFIG = 0x025E
R_VSH_ATTRIBUTES_PERMUTATION_LOW = 0x02BB
R_VSH_ATTRIBUTES_PERMUTATION_HIGH = 0x02BC


# ----------------------------------------------------------------------------
# Little-endian reader mirroring SPICA's BinaryReader helpers
# ----------------------------------------------------------------------------
class Reader:
    def __init__(self, data, pos=0):
        self.d = data
        self.p = pos

    def tell(self): return self.p
    def seek(self, p): self.p = p
    def skip(self, n): self.p += n

    def u8(self):
        v = self.d[self.p]; self.p += 1; return v

    def i8(self):
        v = struct.unpack_from('<b', self.d, self.p)[0]; self.p += 1; return v

    def u16(self):
        v = struct.unpack_from('<H', self.d, self.p)[0]; self.p += 2; return v

    def u32(self):
        v = struct.unpack_from('<I', self.d, self.p)[0]; self.p += 4; return v

    def i32(self):
        v = struct.unpack_from('<i', self.d, self.p)[0]; self.p += 4; return v

    def u64(self):
        v = struct.unpack_from('<Q', self.d, self.p)[0]; self.p += 8; return v

    def f32(self):
        v = struct.unpack_from('<f', self.d, self.p)[0]; self.p += 4; return v

    def vec3(self):
        return (self.f32(), self.f32(), self.f32())

    def vec4(self):
        return (self.f32(), self.f32(), self.f32(), self.f32())

    def mat4(self):
        return [self.f32() for _ in range(16)]

    def padded_string(self, length):
        if length <= 0:
            return None
        raw = self.d[self.p:self.p + length]
        self.p += length
        z = raw.find(0)
        if z >= 0:
            raw = raw[:z]
        return raw.decode('latin-1')

    def byte_len_string(self):
        return self.padded_string(self.u8())

    def int_len_string(self):
        return self.padded_string(self.i32())

    def align16(self):
        if self.p & 0xf:
            self.p += 0x10 - (self.p & 0xf)


# ----------------------------------------------------------------------------
# PC container
# ----------------------------------------------------------------------------
def pc_entries(data):
    """Return list of (offset, length) byte slices of a 'PC' container."""
    assert data[:2] == b'PC', "not a PC container: %r" % data[:2]
    count = struct.unpack_from('<H', data, 2)[0]
    offs = [struct.unpack_from('<I', data, 4 + 4 * i)[0] for i in range(count + 1)]
    return [(offs[i], offs[i + 1]) for i in range(count)]


def pc_slices(data):
    return [data[a:b] for (a, b) in pc_entries(data)]


# ----------------------------------------------------------------------------
# Minimal PICA command reader: decode uint[] -> [(register, [params...])]
# ----------------------------------------------------------------------------
def read_pica_commands(cmds):
    out = []
    i = 0
    n = len(cmds)
    while i < n:
        param = cmds[i]; i += 1
        command = cmds[i]; i += 1
        cid = command & 0xffff
        mask = (command >> 16) & 0xf
        extra = (command >> 20) & 0x7ff
        consecutive = (command >> 31) != 0
        if consecutive:
            for k in range(extra + 1):
                out.append((cid, [param]))
                cid = (cid + 1) & 0xffff
                if k < extra:
                    param = cmds[i]; i += 1
        else:
            params = [param]
            for _ in range(extra):
                params.append(cmds[i]); i += 1
            out.append((cid, params))
        if i & 1:  # 8-byte padded blocks
            i += 1
    return out


class Attribute:
    __slots__ = ('name', 'fmt', 'elements', 'scale')

    def __init__(self, name, fmt, elements, scale):
        self.name = name; self.fmt = fmt; self.elements = elements; self.scale = scale


class FixedAttribute:
    __slots__ = ('name', 'value')

    def __init__(self, name, value):
        self.name = name; self.value = value  # value = (x,y,z,w)


class SubMesh:
    def __init__(self):
        self.name = None
        self.bone_indices_count = 0
        self.bone_indices = [0] * 0x1f
        self.vertex_stride = 0
        self.attributes = []
        self.fixed_attributes = []
        self.primitive_mode = PRIM_TRIANGLES
        self.raw_buffer = b''
        self.indices = []
        self.vertices = []   # filled by decode_vertices


class Mesh:
    def __init__(self):
        self.name = None
        self.bone_indices_per_vertex = 0
        self.submeshes = []


# ----------------------------------------------------------------------------
# Float24 (fixed attribute values are stored as 3x float24 words)
# ----------------------------------------------------------------------------
def float24(word):
    # PICAVectorFloat24 packs three 24-bit floats into 3 words; SPICA's *
    # operator scales a whole vector. For our needs fixed attrs are rare on
    # Pokemon meshes, so we decode a single 24-bit float from the low 24 bits.
    if word == 0:
        return 0.0
    exp = (word >> 16) & 0x7f
    mant = word & 0xffff
    sign = (word >> 23) & 1
    if exp == 0:
        val = 0.0
    else:
        val = (1.0 + mant / 65536.0) * (2.0 ** (exp - 63))
    return -val if sign else val


# ----------------------------------------------------------------------------
# GFMesh (port of SPICA GFMesh constructor)
# ----------------------------------------------------------------------------
def read_gfsection(r):
    magic = r.padded_string(8)
    length = r.u32()
    r.u32()  # padding 0xffffffff
    return magic, length


def read_mesh(r):
    magic, sec_len = read_gfsection(r)
    position = r.tell()

    r.u32()                      # name hash
    name = r.padded_string(0x40)
    r.u32()
    r.vec4()                     # bbox min
    r.vec4()                     # bbox max
    submeshes_count = r.u32()
    bone_indices_per_vertex = r.i32()
    r.skip(0x10)                 # padding

    # command blocks
    cmd_list = []
    while True:
        cmds_len = r.u32()
        cmd_index = r.u32()
        cmds_count = r.u32()
        r.u32()                  # padding
        block = [r.u32() for _ in range(cmds_len >> 2)]
        cmd_list.append(block)
        if not (cmd_index < cmds_count - 1):
            break

    mesh = Mesh()
    mesh.name = name
    mesh.bone_indices_per_vertex = bone_indices_per_vertex

    sizes = []
    for _ in range(submeshes_count):
        sm = SubMesh()
        r.u32()                              # submesh name hash
        sm.name = r.int_len_string()
        sm.bone_indices_count = r.u8()
        for b in range(0x1f):
            sm.bone_indices[b] = r.u8()
        vcount = r.i32(); icount = r.i32(); vlen = r.i32(); ilen = r.i32()
        sizes.append((vcount, icount, vlen, ilen))
        mesh.submeshes.append(sm)

    for mi in range(submeshes_count):
        sm = mesh.submeshes[mi]
        enable_cmds = cmd_list[mi * 3 + 0]
        index_cmds = cmd_list[mi * 3 + 2]

        buffer_formats = 0
        buffer_attributes = 0
        buffer_permutation = 0
        attributes_total = 0
        fixed = [(0, 0, 0)] * 12
        fixed_index = 0

        for reg, params in read_pica_commands(enable_cmds):
            p = params[0]
            if reg == R_ATTRIBBUFFERS_FORMAT_LOW:
                buffer_formats |= p << 0
            elif reg == R_ATTRIBBUFFERS_FORMAT_HIGH:
                buffer_formats |= p << 32
            elif reg == R_ATTRIBBUFFER0_CONFIG1:
                buffer_attributes |= p
            elif reg == R_ATTRIBBUFFER0_CONFIG2:
                buffer_attributes |= (p & 0xffff) << 32
                sm.vertex_stride = (p >> 16) & 0xff
                # AttributesCount = p >> 28 (unused directly)
            elif reg == R_FIXEDATTRIB_INDEX:
                fixed_index = p
            elif reg == R_FIXEDATTRIB_DATA0:
                fixed[fixed_index] = (p, fixed[fixed_index][1], fixed[fixed_index][2])
            elif reg == R_FIXEDATTRIB_DATA1:
                fixed[fixed_index] = (fixed[fixed_index][0], p, fixed[fixed_index][2])
            elif reg == R_FIXEDATTRIB_DATA2:
                fixed[fixed_index] = (fixed[fixed_index][0], fixed[fixed_index][1], p)
            elif reg == R_VSH_NUM_ATTR:
                attributes_total = p + 1
            elif reg == R_VSH_ATTRIBUTES_PERMUTATION_LOW:
                buffer_permutation |= p << 0
            elif reg == R_VSH_ATTRIBUTES_PERMUTATION_HIGH:
                buffer_permutation |= p << 32

        for idx in range(attributes_total):
            if ((buffer_formats >> (48 + idx)) & 1) != 0:
                name_v = (buffer_permutation >> (idx * 4)) & 0xf
                scale = FMT_SCALES[1] if name_v in (AttrName.Color, AttrName.BoneWeight) else 1.0
                w = fixed[idx]
                val = (float24(w[0]) * scale, float24(w[1]) * scale, float24(w[2]) * scale, 0.0)
                sm.fixed_attributes.append(FixedAttribute(name_v, val))
            else:
                perm_idx = (buffer_attributes >> (idx * 4)) & 0xf
                attr_name = (buffer_permutation >> (perm_idx * 4)) & 0xf
                attr_fmt = (buffer_formats >> (perm_idx * 4)) & 0xf
                fmt = attr_fmt & 3
                elements = (attr_fmt >> 2) + 1
                scale = FMT_SCALES[fmt]
                if attr_name == AttrName.BoneIndex:
                    scale = 1.0
                sm.attributes.append(Attribute(attr_name, fmt, elements, scale))

        buffer_address = 0
        buffer_count = 0
        for reg, params in read_pica_commands(index_cmds):
            p = params[0]
            if reg == R_INDEXBUFFER_CONFIG:
                buffer_address = p
            elif reg == R_NUMVERTICES:
                buffer_count = p
            elif reg == R_PRIMITIVE_CONFIG:
                sm.primitive_mode = p >> 8

        vcount, icount, vlen, ilen = sizes[mi]
        sm.raw_buffer = r.d[r.tell():r.tell() + vlen]
        r.skip(vlen)

        idx_addr = r.tell()
        idx16 = (buffer_address >> 31) != 0
        indices = []
        for _ in range(buffer_count):
            indices.append(r.u16() if idx16 else r.u8())
        sm.indices = indices
        r.seek(idx_addr + ilen)

    r.seek(position + sec_len)
    return mesh


# ----------------------------------------------------------------------------
# Vertex decode (port of SPICA VerticesConverter.GetVertices)
# ----------------------------------------------------------------------------
class Vertex:
    __slots__ = ('position', 'normal', 'color', 'uv0', 'indices', 'weights')

    def __init__(self):
        self.position = (0.0, 0.0, 0.0, 0.0)
        self.normal = (0.0, 0.0, 0.0, 0.0)
        self.color = (1.0, 1.0, 1.0, 1.0)
        self.uv0 = (0.0, 0.0, 0.0, 0.0)
        self.indices = [0, 0, 0, 0]
        self.weights = [0.0, 0.0, 0.0, 0.0]


def decode_vertices(sm):
    if not sm.raw_buffer or sm.vertex_stride == 0:
        return []
    out = []
    stride = sm.vertex_stride
    count = len(sm.raw_buffer) // stride
    data = sm.raw_buffer
    for vi in range(count):
        pos = vi * stride
        v = Vertex()
        bi = 0
        wi = 0
        for a in sm.attributes:
            # align: short/float need 2-byte alignment relative to buffer start
            if a.fmt not in (0, 1) and (pos & 1):
                pos += 1
            elems = []
            for _ in range(a.elements):
                if a.fmt == 0:
                    elems.append(struct.unpack_from('<b', data, pos)[0]); pos += 1
                elif a.fmt == 1:
                    elems.append(data[pos]); pos += 1
                elif a.fmt == 2:
                    elems.append(struct.unpack_from('<h', data, pos)[0]); pos += 2
                else:
                    elems.append(struct.unpack_from('<f', data, pos)[0]); pos += 4
            while len(elems) < 4:
                elems.append(0.0)
            x, y, z, w = (elems[0] * a.scale, elems[1] * a.scale,
                          elems[2] * a.scale, elems[3] * a.scale)
            if a.name == AttrName.Position:
                v.position = (x, y, z, w)
            elif a.name == AttrName.Normal:
                v.normal = (x, y, z, w)
            elif a.name == AttrName.Color:
                v.color = (x, y, z, w)
            elif a.name == AttrName.TexCoord0:
                v.uv0 = (x, y, z, w)
            elif a.name == AttrName.BoneIndex:
                for val in (x, y, z, w)[:a.elements]:
                    if bi < 4:
                        v.indices[bi] = int(round(val)); bi += 1
            elif a.name == AttrName.BoneWeight:
                for val in (x, y, z, w)[:a.elements]:
                    if wi < 4:
                        v.weights[wi] = val; wi += 1
        # fixed attributes (rare): bone index/weight
        for fa in sm.fixed_attributes:
            if fa.name == AttrName.BoneIndex:
                v.indices[0] = int(fa.value[0]); v.indices[1] = int(fa.value[1]); v.indices[2] = int(fa.value[2])
            elif fa.name == AttrName.BoneWeight:
                v.weights[0] = fa.value[0]; v.weights[1] = fa.value[1]; v.weights[2] = fa.value[2]
        out.append(v)
    return out


# ----------------------------------------------------------------------------
# GFModel (header + skeleton + skip materials/LUTs + meshes)
# ----------------------------------------------------------------------------
class Bone:
    __slots__ = ('name', 'parent', 'flags', 'scale', 'rotation', 'translation')


class GFModel:
    def __init__(self):
        self.name = None
        self.skeleton = []
        self.meshes = []
        self.transform = None


def read_hash_table(r):
    count = r.u32()
    out = []
    for _ in range(count):
        h = r.u32()
        name = r.padded_string(0x40)
        out.append((h, name))
    return out


def read_model(data, model_name='gfmodel'):
    r = Reader(data)
    magic = r.u32()
    assert magic == 0x15122117, "bad GFModel magic 0x%08x" % magic
    sections_count = r.u32()
    r.align16()
    read_gfsection(r)                       # model section

    shader_names = read_hash_table(r)
    texture_names = read_hash_table(r)
    material_names = read_hash_table(r)
    mesh_names = read_hash_table(r)

    r.vec4()                                # bbox min
    r.vec4()                                # bbox max
    transform = r.mat4()

    unk_len = r.u32()
    unk_off = r.u32()
    r.u64()                                 # padding
    r.skip(unk_off + unk_len)

    bones_count = r.i32()
    r.skip(0xc)

    m = GFModel()
    m.name = model_name
    m.transform = transform
    for _ in range(bones_count):
        b = Bone()
        b.name = r.byte_len_string()
        b.parent = r.byte_len_string()
        b.flags = r.u8()
        b.scale = r.vec3()
        b.rotation = r.vec3()
        b.translation = r.vec3()
        m.skeleton.append(b)

    r.align16()
    luts_count = r.i32()
    lut_length = r.i32()
    r.align16()
    for _ in range(luts_count):
        # GFLUT: u32 hash + skip 0xc + lut_length bytes of commands
        r.u32(); r.skip(0xc); r.skip(lut_length)

    # materials: parse just enough to recover material_name -> diffuse texture.
    # GFHashName = hash(4) + byte-length string (variable). Layout (SPICA
    # GFMaterial): 4 GFHashName + 168 fixed bytes + UnitsCount(4) + TextureCoords,
    # each TextureCoord starting with a GFHashName whose Name is the texture.
    def _hashname():
        r.u32()
        return r.byte_len_string()

    m.materials = {}    # material_name -> diffuse texture name (or None)
    m.mat_wrap = {}     # material_name -> (WrapU, WrapV)  (0 clampEdge,1 clampBorder,2 repeat,3 mirror)
    m.mat_uvxf = {}     # material_name -> (ScaleX, ScaleY, RotZ, TransX, TransY) of TextureCoords[0]
    for _ in range(len(material_names)):
        _mg, mlen = read_gfsection(r)
        pos = r.tell()
        mat_name = _hashname()          # MaterialName
        _hashname()                     # ShaderName
        _hashname()                     # VtxShaderName
        _hashname()                     # FragShaderName
        r.skip(168)                     # fixed material fields up to UnitsCount
        units = r.u32()
        diffuse = None
        wrap = (2, 2)
        uvxf = (1.0, 1.0, 0.0, 0.0, 0.0)
        if units > 0:
            diffuse = _hashname()       # TextureCoords[0].Name
            r.skip(2)                   # UnitIndex(1) + MappingType(1)
            sx, sy = r.f32(), r.f32()   # Scale
            rot = r.f32()               # Rotation
            tx, ty = r.f32(), r.f32()   # Translation
            uvxf = (sx, sy, rot, tx, ty)
            wrap = (r.u32(), r.u32())   # WrapU, WrapV
        m.materials[mat_name] = diffuse
        m.mat_wrap[mat_name] = wrap
        m.mat_uvxf[mat_name] = uvxf
        r.seek(pos + mlen)              # jump to section end regardless

    for _ in range(len(mesh_names)):
        m.meshes.append(read_mesh(r))

    # decode geometry
    for mesh in m.meshes:
        for sm in mesh.submeshes:
            sm.vertices = decode_vertices(sm)

    return m


def load_pokemon_model(model_bin_path):
    """Open a *_model.bin PC container and parse its GFModel (entry 0)."""
    data = open(model_bin_path, 'rb').read()
    ents = pc_slices(data)
    return read_model(ents[0])


# ----------------------------------------------------------------------------
# Validation dump: OBJ + stats
# ----------------------------------------------------------------------------
def _tris(sm):
    """Yield triangles (as index triplets into sm.vertices) per primitive mode."""
    idx = sm.indices
    if sm.primitive_mode == PRIM_TRISTRIP:
        for i in range(len(idx) - 2):
            a, b, c = idx[i], idx[i + 1], idx[i + 2]
            if a == b or b == c or a == c:
                continue
            if i & 1:
                yield (a, c, b)
            else:
                yield (a, b, c)
    elif sm.primitive_mode == PRIM_TRIFAN:
        for i in range(1, len(idx) - 1):
            yield (idx[0], idx[i], idx[i + 1])
    else:
        for i in range(0, len(idx) - 2, 3):
            yield (idx[i], idx[i + 1], idx[i + 2])


def dump_obj(model, obj_path):
    base = 1
    faces_total = 0
    with open(obj_path, 'w') as f:
        for mi, mesh in enumerate(model.meshes):
            for si, sm in enumerate(mesh.submeshes):
                if not sm.vertices:
                    continue
                f.write("o %s.%s\n" % (mesh.name, sm.name or si))
                for v in sm.vertices:
                    f.write("v %.6f %.6f %.6f\n" % (v.position[0], v.position[1], v.position[2]))
                for v in sm.vertices:
                    f.write("vt %.6f %.6f\n" % (v.uv0[0], v.uv0[1]))
                for (a, b, c) in _tris(sm):
                    f.write("f %d/%d %d/%d %d/%d\n" %
                            (base + a, base + a, base + b, base + b, base + c, base + c))
                    faces_total += 1
                base += len(sm.vertices)
    return faces_total


def stats(model):
    print("GFModel '%s' — %d bone(s), %d mesh(es)" %
          (model.name, len(model.skeleton), len(model.meshes)))
    roots = [b for b in model.skeleton if not b.parent]
    print("  roots: %s" % ", ".join(b.name for b in roots))
    for mesh in model.meshes:
        for sm in mesh.submeshes:
            if not sm.vertices:
                continue
            xs = [v.position[0] for v in sm.vertices]
            ys = [v.position[1] for v in sm.vertices]
            zs = [v.position[2] for v in sm.vertices]
            maxinf = max((sum(1 for w in v.weights if w > 0) for v in sm.vertices), default=0)
            attrs = ",".join(_attr_name(a.name) + ":" + _fmt_name(a.fmt) + "x" + str(a.elements)
                             for a in sm.attributes)
            print("  [%s/%s] verts=%d stride=%d prim=%d idx=%d maxInfl=%d bbox=x[%.2f,%.2f] y[%.2f,%.2f] z[%.2f,%.2f]"
                  % (mesh.name, sm.name, len(sm.vertices), sm.vertex_stride, sm.primitive_mode,
                     len(sm.indices), maxinf, min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)))
            print("        attrs: %s  localBones=%d" % (attrs, sm.bone_indices_count))


def _attr_name(n):
    return {0: "Pos", 1: "Nrm", 2: "Tan", 3: "Col", 4: "UV0", 5: "UV1", 6: "UV2",
            7: "BIdx", 8: "BWgt"}.get(n, "?%d" % n)


def _fmt_name(f):
    return ["s8", "u8", "s16", "f32"][f]


if __name__ == '__main__':
    import sys
    path = sys.argv[1] if len(sys.argv) > 1 else r"D:\Pokemon\PIKA\pikachu_model.bin"
    model = load_pokemon_model(path)
    stats(model)
    out = (sys.argv[2] if len(sys.argv) > 2
           else path.rsplit('.', 1)[0] + ".obj")
    faces = dump_obj(model, out)
    print("wrote %s (%d faces)" % (out, faces))
