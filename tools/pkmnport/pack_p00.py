"""
pack_p00.py — build a YW3 model archive (_p00.xc) from a parsed Gen-7 GFModel,
by clone-patching a known-good foreign port (Meloetta y152000_p00.xc).

Strategy (first milestone = geometry + skeleton in-game):
  - keep the donor's materials/textures (.mtr/.atr/.xi + RES material tables) as-is,
  - replace the .mbn set with our bones (pure-Python, matching mbn.matrix_to_bytes),
  - replace the .prm set with our submeshes (studio_eleven xmpr.write, bpy-free),
  - rebuild RES BONE + MESH_NAME tables,
  - each submesh points at one of the donor's existing materials (temporary skin).

No Blender, no mathutils (pure-Python matrices).  se_loader gives us the fork's
bpy-free xpck / res, and we side-load xmpr with a tiny utils stub (only stripify).
"""

import os
import sys
import math
import types
import struct
import zlib
import importlib.util

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "punipuni"))  # se_loader lives there
from se_loader import load_studio_eleven

import gf_model
import gf_texture


# ----------------------------------------------------------------------------
# Side-load studio_eleven's xmpr with a stub utils package (stripify only)
# ----------------------------------------------------------------------------
def load_fork(se_root, pkg="_se_vendor"):
    """Side-load the fork's bpy-free formats.xmpr + formats.imgc by building a
    synthetic utils package that exposes only the bpy-free utils submodules
    (avoiding utils/__init__ which imports bpy via mesh_faces_utils)."""
    fmt = os.path.join(se_root, "formats")
    utils = os.path.join(se_root, "utils")

    def leaf(fullname, path):
        spec = importlib.util.spec_from_file_location(fullname, path)
        m = importlib.util.module_from_spec(spec)
        sys.modules[fullname] = m
        spec.loader.exec_module(m)
        return m

    up = sys.modules.get(pkg + ".utils")
    if up is None:
        up = types.ModuleType(pkg + ".utils")
        up.__path__ = [utils]
        sys.modules[pkg + ".utils"] = up
    for mod in ("trianglemesh", "trianglestripifier", "tristrip",
                "img_format", "img_swizzle", "img_tool"):
        m = leaf(pkg + ".utils." + mod, os.path.join(utils, mod + ".py"))
        for name in dir(m):
            if not name.startswith("_"):
                setattr(up, name, getattr(m, name))

    xmpr = leaf(pkg + ".formats.xmpr", os.path.join(fmt, "xmpr.py"))
    imgc = leaf(pkg + ".formats.imgc", os.path.join(fmt, "imgc.py"))
    xcsl = leaf(pkg + ".formats.xcsl", os.path.join(fmt, "xcsl.py"))
    img_format = sys.modules[pkg + ".utils.img_format"]
    return xmpr, imgc, xcsl, img_format


def load_template(se_root, name="YKW"):
    """Load a studio_eleven export template (templates.json) — the addon's own
    outline material/attribute data. Using template[0].atr/.mtr/outline_mesh_data
    /cmb1/cmb2 is exactly what studio_eleven's 'add outline' export does. The
    Meloetta donor .atr is a 48-byte variant; the YKW template's is the 40-byte
    one real YW3 models (and the game's outline pass) use."""
    import json
    d = json.load(open(os.path.join(se_root, "templates", "templates.json")))
    tl = d["templates"] if isinstance(d, dict) else d
    t = next((x for x in tl if x.get("name") == name), tl[0])
    return {
        "atr": bytes.fromhex(t["atr"]),
        "mtr": bytes.fromhex(t["mtr"]),
        "outline_mesh_data": list(t["outline_mesh_data"]),
        "cmb1": list(t["cmb1"]),
        "cmb2": list(t["cmb2"]),
        "modes": t.get("modes", {}),
    }


# ----------------------------------------------------------------------------
# Pure-Python 3x3 / transform helpers (column-vector, mathutils-compatible)
# ----------------------------------------------------------------------------
def m3_ident():
    return [[1, 0, 0], [0, 1, 0], [0, 0, 1]]


def m3_mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]


def m3_vec(m, v):
    return [sum(m[i][k] * v[k] for k in range(3)) for i in range(3)]


def rot_x(a):
    c, s = math.cos(a), math.sin(a)
    return [[1, 0, 0], [0, c, -s], [0, s, c]]


def rot_y(a):
    c, s = math.cos(a), math.sin(a)
    return [[c, 0, s], [0, 1, 0], [-s, 0, c]]


def rot_z(a):
    c, s = math.cos(a), math.sin(a)
    return [[c, -s, 0], [s, c, 0], [0, 0, 1]]


def euler_xyz(rx, ry, rz):
    # SPICA H3DBone: Scale*RotX*RotY*RotZ*Translation (row vectors) -> column
    # rotation = Rz @ Ry @ Rx  (Blender Euler 'XYZ')
    return m3_mul(rot_z(rz), m3_mul(rot_y(ry), rot_x(rx)))


class Xform:
    """Column-vector rigid+scale transform:  world = parent @ (T @ R @ S)."""
    __slots__ = ('r', 't', 's')  # r=3x3 rotation, t=translation vec3, s=scale vec3

    def __init__(self, r, t, s):
        self.r = r; self.t = t; self.s = s

    def matrix_cols(self):
        # 3x3 columns already include scale (r[:,k]*s[k]); return (rot*scale, t)
        rs = [[self.r[i][j] * self.s[j] for j in range(3)] for i in range(3)]
        return rs, self.t


def compose(parent_rs, parent_t, local_r, local_t, local_s):
    """world = parent @ local, both as (3x3 with scale, translation)."""
    # local matrix columns = local_r * diag(local_s); local translation = local_t
    lrs = [[local_r[i][j] * local_s[j] for j in range(3)] for i in range(3)]
    # world 3x3 = parent_rs @ lrs
    wrs = m3_mul(parent_rs, lrs)
    # world t = parent_rs @ local_t + parent_t
    wt = m3_vec(parent_rs, local_t)
    wt = [wt[i] + parent_t[i] for i in range(3)]
    return wrs, wt


def decompose(rs, t):
    """Split a 3x3-with-scale into (pure-rotation 3x3, scale vec3, translation)."""
    scale = []
    rot = [[0, 0, 0], [0, 0, 0], [0, 0, 0]]
    for j in range(3):
        col = [rs[0][j], rs[1][j], rs[2][j]]
        n = math.sqrt(col[0]**2 + col[1]**2 + col[2]**2) or 1.0
        scale.append(n)
        for i in range(3):
            rot[i][j] = rs[i][j] / n
    return rot, scale, list(t)


def m3_inv_rigid(rs, t):
    """Inverse of a (rotation*scale, translation) as columns; assumes ~orthogonal
    with uniform-ish scale. Returns (3x3, t) inverse for relative-matrix math."""
    rot, scale, tr = decompose(rs, t)
    # inverse rotation = transpose; inverse scale = 1/scale applied
    inv_rs = [[rot[j][i] / scale[i] for j in range(3)] for i in range(3)]  # (R*S)^-1 = S^-1 R^T
    # wait: (R S)^-1 = S^-1 R^-1 = S^-1 R^T ; build as 3x3
    inv_rs = [[rot[j][i] / scale[j] for j in range(3)] for i in range(3)]
    inv_t = m3_vec(inv_rs, [-tr[0], -tr[1], -tr[2]])
    return inv_rs, inv_t


# ----------------------------------------------------------------------------
# .mbn writer (pure Python, replicating studio_eleven formats/mbn.py::write)
# ----------------------------------------------------------------------------
def sci(f):
    return float("{:.4f}".format(f))


def build_mbn(name, parent_name, rel_rs, rel_t, world_rs, world_t, head, tail):
    out = bytes()
    out += zlib.crc32(name.encode("utf-8")).to_bytes(4, 'little')
    out += (zlib.crc32(parent_name.encode("utf-8")) if parent_name else 0).to_bytes(4, 'little')
    out += (4).to_bytes(4, 'little')

    rel_rot, rel_scale, rel_loc = decompose(rel_rs, rel_t)
    wrot, _wscale, _wt = decompose(world_rs, world_t)

    # Location
    for i in range(3):
        out += struct.pack("f", sci(rel_loc[i]))
    # Rotation: matrix_rotation[j][i] (transpose of relative rotation)
    for i in range(3):
        for j in range(3):
            out += struct.pack("f", float(rel_rot[j][i]))
    # Scale
    for i in range(3):
        out += struct.pack("f", float(rel_scale[i]))
    # Local rotation: local_matrix_rotation[i][j] (world rotation, non-transposed)
    local = wrot
    ordered = [[0, 0, 0], [0, 0, 0], [0, 0, 0]]
    for i in range(3):
        for j in range(3):
            out += struct.pack("f", sci(local[i][j]))
            ordered[i][j] = local[j][i]
    # rotated_head = -(ordered @ head)
    rh = m3_vec(ordered, head)
    for i in range(3):
        out += struct.pack("f", float(-rh[i]))
    # first column of local rotation
    for j in range(3):
        out += struct.pack("f", float(local[j][0]))
    # tail - head
    for i in range(3):
        out += struct.pack("f", float(tail[i] - head[i]))
    # last column of local rotation
    for j in range(3):
        out += struct.pack("f", float(local[j][2]))
    # head
    for i in range(3):
        out += struct.pack("f", float(head[i]))
    return out


def build_bones(model):
    """Return list of (index, mbn_bytes) and a crc->name map, computing world
    transforms from the GF TRS hierarchy."""
    bones = model.skeleton
    by_name = {b.name: b for b in bones}
    world = {}  # name -> (world_rs, world_t)

    def world_of(b):
        if b.name in world:
            return world[b.name]
        local_r = euler_xyz(*b.rotation)
        local_s = list(b.scale)
        local_t = list(b.translation)
        if b.parent and b.parent in by_name:
            prs, pt = world_of(by_name[b.parent])
            wrs, wt = compose(prs, pt, local_r, local_t, local_s)
        else:
            wrs = [[local_r[i][j] * local_s[j] for j in range(3)] for i in range(3)]
            wt = local_t
        world[b.name] = (wrs, wt)
        return world[b.name]

    out = []
    crc_names = {}
    for b in bones:
        wrs, wt = world_of(b)
        # relative matrix = parent_world^-1 @ world  (== local for direct parent)
        if b.parent and b.parent in by_name:
            prs, pt = world_of(by_name[b.parent])
            inv_rs, inv_t = m3_inv_rigid(prs, pt)
            rel_rs = m3_mul(inv_rs, wrs)
            rel_t = m3_vec(inv_rs, wt)
            rel_t = [rel_t[i] + inv_t[i] for i in range(3)]
        else:
            rel_rs, rel_t = wrs, wt
        wrot, _, _ = decompose(wrs, wt)
        head = list(wt)
        tail = [head[i] + wrot[i][1] * 0.01 for i in range(3)]  # +Y local, tiny length
        mbn = build_mbn(b.name, b.parent, rel_rs, rel_t, wrs, wt, head, tail)
        out.append(mbn)
        crc_names[zlib.crc32(b.name.encode('utf-8')) & 0xffffffff] = b.name
    return out, crc_names


# ----------------------------------------------------------------------------
# .prm builder via xmpr.write
# ----------------------------------------------------------------------------
def submesh_bone_palette(model, mesh, sm):
    """Names for the submesh's local bone palette (index -> skeleton bone name)."""
    names = []
    for k in range(min(sm.bone_indices_count, len(sm.bone_indices))):
        gi = sm.bone_indices[k]
        names.append(model.skeleton[gi].name if 0 <= gi < len(model.skeleton) else "Origin")
    if not names:  # rigid single-bind fallback -> root (or "Origin" if the model has no skeleton)
        names = [model.skeleton[0].name] if model.skeleton else ["Origin"]
    return names


def mirror_double(w, h, top):
    """Bake a horizontal Mirror wrap into the texture: width*2, right = mirror of
    left. Combined with a u->u/2 UV remap + plain repeat, reproduces GL Mirror wrap
    (which YW3's material template doesn't do) for u in [0,2]."""
    nw = 2 * w
    out = bytearray(nw * h * 4)
    for y in range(h):
        base = y * nw * 4
        row = top[y * w * 4:(y + 1) * w * 4]
        out[base:base + w * 4] = row
        for x in range(w):
            s = x * 4
            d = base + (w + (w - 1 - x)) * 4
            out[d:d + 4] = row[s:s + 4]
    return nw, h, out


def build_prm(xmpr, model, mesh, sm, mesh_name, material_name, texspace, mode,
              mirror_u=False, uvxf=(1.0, 1.0, 0.0, 0.0, 0.0), outline=False):
    """Remap to first-appearance vertex order, then call xmpr.write.

    outline: append a duplicate of every face with reversed winding + flipped
    normals + a red (1,0,0,1) vertex-color flag — the studio_eleven "Auto
    Outline" convention. The game expands these flagged back-faces into the toon
    silhouette (a companion .sil/XCSL declares thickness/visibility)."""
    verts = sm.vertices
    palette = submesh_bone_palette(model, mesh, sm)

    # Resolve a vertex bone index to a palette slot. In-range indices map straight through (unchanged for
    # models that already worked). An OUT-OF-RANGE index (a submesh whose local palette is short/absent, so the
    # vertices carry direct global skeleton indices) is appended to the palette as the matching skeleton bone —
    # this is what caused xmpr.used_bones' "list index out of range".
    _bpos = {}
    def resolve_bone(raw):
        if 0 <= raw < len(palette):
            return raw
        if raw in _bpos:
            return _bpos[raw]
        nm = model.skeleton[raw].name if (0 <= raw < len(model.skeleton)) else (palette[0] if palette else "Origin")
        _bpos[raw] = len(palette)
        palette.append(nm)
        return _bpos[raw]

    remap = {}
    order = []
    tris = []
    for (a, b, c) in gf_model._tris(sm):
        t = []
        for vi in (a, b, c):
            if vi not in remap:
                remap[vi] = len(order); order.append(vi)
            t.append(remap[vi])
        tris.append(tuple(t))

    positions, normals, uvs, colors, weights = [], [], [], [], {}
    have_color = any(sm.vertices and a.name == gf_model.AttrName.Color for a in sm.attributes)
    for new_i, old_i in enumerate(order):
        v = verts[old_i]
        positions.append((v.position[0], v.position[1], v.position[2]))
        normals.append((v.normal[0], v.normal[1], v.normal[2]))
        uvs.append((v.uv0[0], v.uv0[1]))   # raw UV; transformed below
        # Vertex colour is ONLY the outline mask (duplicated faces get red below). A NON-outlined
        # mesh must carry NO colour attribute: the donor's fixed .atr declares none, so a stray Col
        # attr desyncs the vertex layout and the mesh renders INVISIBLE. Pikachu's BodyA/Mouth ship
        # a Col attr (alpha 0.05–0.9) — that's why only Pikachu vanished; Majaspic has none.
        if outline:
            colors.append((0.0, 0.0, 0.0, 1.0))
        w = {}
        for j in range(4):
            if v.weights[j] > 0:
                w[resolve_bone(v.indices[j])] = v.weights[j]
        if not w:
            w = {0: 1.0}
        weights[new_i] = w

    if outline:
        base = len(order)
        for new_i, old_i in enumerate(order):
            v = verts[old_i]
            positions.append((v.position[0], v.position[1], v.position[2]))
            normals.append((-v.normal[0], -v.normal[1], -v.normal[2]))   # flipped
            uvs.append((v.uv0[0], v.uv0[1]))
            colors.append((1.0, 0.0, 0.0, 1.0))                          # outline flag
            weights[base + new_i] = dict(weights[new_i])
        for (a, b, c) in list(tris):
            tris.append((base + a, base + c, base + b))                  # reversed winding

    # Apply the GF material's UV transform (GFTextureCoord, DccMaya, rot=0):
    #   u' = SX*(u - TX),  v' = SY*(v - TY)   -- matches Ohana's DAE UVs.
    # WrapU=Mirror is baked as a width*2 mirror-doubled texture, so u maps as
    # u'/2 (period 2 -> [0,1] of the doubled texture, plain repeat). Then V is
    # pre-flipped (1-v') because xmpr writes (u, 1-v) and the game samples from
    # the texture top.
    sx, sy, _rot, tx, ty = uvxf
    def xf(u, v):
        up = sx * (u - tx)
        vp = sy * (v - ty)
        if mirror_u:
            up *= 0.5
        return (up, 1.0 - vp)
    uvs = [xf(u, v) for (u, v) in uvs]

    # Normalize each island into [0,1) with a constant integer shift per axis
    # (seam-safe; the game plain-repeats).
    if uvs:
        uoff = math.floor(min(u for u, _ in uvs))
        voff = math.floor(min(v for _, v in uvs))
        if uoff or voff:
            uvs = [(u - uoff, v - voff) for (u, v) in uvs]

    return xmpr.write(mesh_name, texspace, tris, positions, uvs, normals,
                      colors, weights, palette, material_name, mode)


# ----------------------------------------------------------------------------
# Donor material/texspace/mode extraction from a real .prm
# ----------------------------------------------------------------------------
# ----------------------------------------------------------------------------
# Textures: decode GFTexture -> RGBA -> IMGC .xi ; map submesh -> texture
# ----------------------------------------------------------------------------
class ShimImage:
    """Duck-types the bits of a Blender image that imgc.write reads."""
    def __init__(self, w, h, rgba_bottomup):
        self.size = (w, h)
        self.pixels = [c / 255.0 for c in rgba_bottomup]


def rgba_to_xi(imgc, img_format, w, h, top):
    """top-down RGBA bytes -> IMGC .xi (RGBA8)."""
    bu = bytearray(len(top))                       # top-down -> bottom-up (Blender)
    for y in range(h):
        bu[y * w * 4:(y + 1) * w * 4] = top[(h - 1 - y) * w * 4:(h - y) * w * 4]
    return imgc.write(ShimImage(w, h, bu), img_format.RGBA8())


def encode_xi(imgc, img_format, tex):
    """GFTexture dict -> IMGC .xi bytes (RGBA8)."""
    w, h = tex['width'], tex['height']
    top = gf_texture.decode_texture(w, h, tex['fmt'], tex['raw'])
    return rgba_to_xi(imgc, img_format, w, h, top)


def load_png_rgba(path):
    """PNG -> (w, h, top-down RGBA bytes) via PIL."""
    from PIL import Image
    im = Image.open(path).convert('RGBA')
    return im.width, im.height, im.tobytes()


def xi_for_base(imgc, img_format, base, tex_by_base, png_dir, mirror=False):
    """Encode .xi for a texture base-name: prefer a user PNG override, else the
    GFTexture decoded from tex.bin. When mirror, bake a horizontal Mirror wrap
    (width*2). Returns .xi bytes (or None if unavailable).

    Note: PNG overrides are expected at ORIGINAL size — the mirror is applied
    automatically, so custom skins don't need to be pre-doubled."""
    wht = None
    if png_dir:
        for cand in ("pm0025_00_%s.png" % base, "%s.png" % base):
            p = os.path.join(png_dir, cand)
            if os.path.exists(p):
                wht = load_png_rgba(p)
                break
    if wht is None and base in tex_by_base:
        t = tex_by_base[base]
        wht = (t['width'], t['height'], gf_texture.decode_texture(t['width'], t['height'], t['fmt'], t['raw']))
    if wht is None:
        return None
    w, h, top = wht
    if mirror:
        w, h, top = mirror_double(w, h, top)
    return rgba_to_xi(imgc, img_format, w, h, top)


def texture_for_mesh(mesh_name):
    """Pick the diffuse texture base-name for a submesh from its mesh name."""
    n = mesh_name
    if n.startswith("LEye") or n.startswith("REye") or n.startswith("Eye"):
        return "Eye1"
    if n.startswith("Mouth"):
        return "Mouth1"
    if n.startswith("BodyB"):
        return "BodyB1"
    return "BodyA1"   # BodyA, BodyAVco, fallback


def build_res(se, model, mesh_entries, tex_order, mat_to_tex, outlines=None):
    """RES with our bones + meshes + real textures (+ optional outline SHADING names).
    mesh_entries = [(mesh_name, material_name)]; tex_order = [tex_name] (== .xi order);
    mat_to_tex = {material_name: [tex_name,...]}; outlines = [outline_name,...]."""
    RT = se.res.RESType
    crc = lambda s: zlib.crc32(s.encode("shift-jis")) & 0xffffffff
    items = {}
    st = bytearray()
    materials_offset = {}

    mat1 = []
    for mesh_name, material_name in mesh_entries:
        mat1.append(crc(material_name).to_bytes(4, 'little') + len(st).to_bytes(4, 'little'))
        if material_name not in materials_offset:
            materials_offset[material_name] = len(st)
        st += material_name.encode("shift-jis") + b'\x00'
    items[RT.MATERIAL_1] = mat1
    items[RT.MATERIAL_2] = mat1

    mesh_recs = []
    for mesh_name, material_name in mesh_entries:
        mesh_recs.append(crc(mesh_name).to_bytes(4, 'little') + len(st).to_bytes(4, 'little'))
        st += mesh_name.encode("shift-jis") + b'\x00'
    items[RT.MESH_NAME] = mesh_recs

    tex_recs = []
    for tex_name in tex_order:
        tex_recs.append(crc(tex_name).to_bytes(4, 'little') + len(st).to_bytes(4, 'little')
                        + bytes.fromhex("030A00000000000000000000"))
        st += tex_name.encode("shift-jis") + b'\x00'
    items[RT.TEXTURE_DATA] = tex_recs

    md = []
    for material_name in dict.fromkeys(m for _, m in mesh_entries):
        c = crc(material_name).to_bytes(4, 'little')
        off = materials_offset.get(material_name, 0)
        rec = c + off.to_bytes(4, 'little') + c + c
        texs = mat_to_tex.get(material_name, [])
        for i in range(4):
            if i < len(texs):
                rec += crc(texs[i]).to_bytes(4, 'little') + bytes.fromhex(
                    "010000000000803F0000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F")
            else:
                rec += bytes.fromhex(
                    "00000000000000000000803F0000803F00000000000000000000803F00000000000000000000803F00000000000000000000803F")
        md.append(rec)
    items[RT.MATERIAL_DATA] = md

    bone_recs = []
    for b in model.skeleton:
        bone_recs.append(crc(b.name).to_bytes(4, 'little') + len(st).to_bytes(4, 'little'))
        st += b.name.encode("shift-jis") + b'\x00'
    items[RT.BONE] = bone_recs

    if outlines:
        sh = []
        for name in outlines:
            sh.append(crc(name).to_bytes(4, 'little') + len(st).to_bytes(4, 'little'))
            st += name.encode("shift-jis") + b'\x00'
        items[RT.SHADING] = sh

    return items, bytes(st)



def donor_material_params(prm_bytes):
    """Pull (texspace, mode) from an existing XMPR .prm so our meshes reuse a
    valid material style. Layout per xmpr.write: after XMPR(64) + XPVB + XPVI
    comes the material block."""
    # XMPR header: magic 'XMPR'(4), 64, matlen_field(4), off_material(4)...
    # off to material = int at 0x0C is (84 + xpvb + xpvi) from file start.
    off_mat = struct.unpack_from('<I', prm_bytes, 0x0C)[0]
    m = off_mat
    # material: crc(mesh)4, crc(mat)4, mode4, x4, 0,0 (8), 6 floats texspace
    mode = prm_bytes[m + 8:m + 12].hex()
    tf = struct.unpack_from('<6f', prm_bytes, m + 24)
    texspace = [[tf[0], tf[1], tf[2]], [tf[3], tf[4], tf[5]]]
    return texspace, [mode]


# ----------------------------------------------------------------------------
# Main clone-patch
# ----------------------------------------------------------------------------
def pack(model, donor_xc_path, out_xc_path, se_root, tex_bin_path=None, png_dir=None,
         outline=True, outline_thickness=0.0025, outline_visibility=0.5,
         include_meshes=None, outline_meshes=None):
    se = load_studio_eleven(se_root)
    xmpr, imgc, xcsl, img_format = load_fork(se_root)

    donor = open(donor_xc_path, 'rb').read()
    files = dict(se.xpck.open_file(donor))

    # RES magic is 8 bytes ("CHRC00\0\0") — the version bytes [4:8] matter to the
    # game (Blender's reader ignores them, so this only bites in-game).
    res_magic = se.res.compressor.decompress(files['RES.bin'])[:8]
    donor_prm = next(files[n] for n in files if n.endswith('.prm'))
    texspace, mode = donor_material_params(donor_prm)
    # studio_eleven's method: use the export template's .atr/.mtr (the 40-byte atr
    # the game's outline pass expects), not the Meloetta donor's 48-byte variant.
    tpl = load_template(se_root, "YKW")
    tpl_mtr, tpl_atr = tpl["mtr"], tpl["atr"]

    # ---- bones ----
    mbns, _ = build_bones(model)

    # ---- textures: decode + encode the diffuse maps we reference ----
    tex_by_base = {}   # "BodyA1" -> GFTexture dict
    if tex_bin_path and os.path.exists(tex_bin_path):
        for t in gf_texture.parse_tex_pc(tex_bin_path):
            base = t['name'].replace('.tga', '').replace('pm0025_00_', '')
            tex_by_base[base] = t

    # ---- submeshes -> prm, choosing material/texture per mesh ----
    prms, mesh_entries = [], []
    used_tex = []                      # texture base-names actually referenced (order)
    mat_to_tex = {}
    tex_mirror = {}                    # base -> needs horizontal Mirror-wrap bake
    outlined_meshes = []               # mesh names that carry outline faces
    for mesh in model.meshes:
        base = mesh.name.replace("_OptMesh", "")
        if include_meshes is not None and base not in include_meshes:
            continue
        for smi, sm in enumerate(mesh.submeshes):
            if not sm.vertices:
                continue
            mesh_name = "%s_%d" % (base, smi)
            # real GF binding: submesh.name == material name -> its diffuse texture
            diffuse = (model.materials or {}).get(sm.name)
            tex_base = None
            if diffuse:
                tex_base = diffuse.replace('.tga', '').replace('pm0025_00_', '')
                # match a decoded texture by suffix if the pm-prefix differs
                if tex_base not in tex_by_base:
                    tex_base = next((b for b in tex_by_base if diffuse.replace('.tga', '').endswith(b)), None)
            if not tex_base:
                tex_base = texture_for_mesh(mesh_name)   # heuristic fallback
            if tex_base not in tex_by_base:
                tex_base = next(iter(tex_by_base), None) if tex_by_base else None
            material_name = "mat_%s" % (tex_base or "none")
            # GF WrapU == Mirror(3): bake the mirror into the texture + UV transform
            mirror_u = (model.mat_wrap or {}).get(sm.name, (2, 2))[0] == 3
            uvxf = (model.mat_uvxf or {}).get(sm.name, (1.0, 1.0, 0.0, 0.0, 0.0))
            if tex_base:
                tex_mirror[tex_base] = tex_mirror.get(tex_base, False) or mirror_u
            if tex_base and tex_base not in used_tex:
                used_tex.append(tex_base)
            if tex_base:
                mat_to_tex.setdefault(material_name, [])
                if tex_base not in [b for b in mat_to_tex[material_name]]:
                    mat_to_tex[material_name].append(tex_base)
            # Outline: user-chosen set if given, else auto (body silhouette, skip *Vco).
            if outline_meshes is not None:
                do_outline = base in outline_meshes
            else:
                do_outline = outline and mesh_name.startswith("Body") and "Vco" not in mesh_name
            prm = build_prm(xmpr, model, mesh, sm, mesh_name, material_name,
                            texspace, mode, mirror_u=mirror_u, uvxf=uvxf, outline=do_outline)
            prms.append(prm)
            mesh_entries.append((mesh_name, material_name))
            if do_outline:
                outlined_meshes.append(mesh_name)

    # encode .xi for each used texture (order == TEXTURE_DATA order).
    # PNG override in png_dir wins over the tex.bin decode (lets the user edit
    # textures — e.g. the mirror-"doubled" body maps).
    xis = []
    tex_names_ordered = []             # full RES texture names (== base here)
    for base in used_tex:
        xi = xi_for_base(imgc, img_format, base, tex_by_base, png_dir,
                         mirror=tex_mirror.get(base, False))
        if xi is None:
            continue
        xis.append(xi)
        tex_names_ordered.append(base)
    # map material -> full texture names
    mat_to_tex_full = {m: [b for b in bases] for m, bases in mat_to_tex.items()}

    # ---- assemble archive: our mbn + prm + xi, template mtr/atr per mesh ----
    out = {}
    for i, b in enumerate(mbns):
        out["%03d.mbn" % i] = b
    for i, b in enumerate(prms):
        out["%03d.prm" % i] = b
        out["%03d.mtr" % i] = tpl_mtr
        out["%03d.atr" % i] = tpl_atr
    for i, b in enumerate(xis):
        out["%03d.xi" % i] = b

    # ---- outline: one .sil (XCSL) declaring the outlined meshes + a SHADING name ----
    outline_names = []
    if outlined_meshes:
        outline_name = "outline_0"   # match studio_eleven export
        sil = xcsl.write(outline_name, outlined_meshes, outline_thickness, outline_visibility,
                         list(tpl["outline_mesh_data"]), list(tpl["cmb1"]), list(tpl["cmb2"]))
        out["000.sil"] = sil
        outline_names = [outline_name]

    items, string_table = build_res(se, model, mesh_entries, tex_names_ordered,
                                    mat_to_tex_full, outlines=outline_names)
    out['RES.bin'] = se.res.write_res(res_magic, items, string_table)

    os.makedirs(os.path.dirname(out_xc_path), exist_ok=True)
    se.xpck.pack_archive(out, out_xc_path)
    return len(mbns), len(prms), len(xis), len(outlined_meshes), out_xc_path


if __name__ == '__main__':
    se_root = r"E:\Yo-kai watch Mods\studio_eleven"
    model_bin = sys.argv[1] if len(sys.argv) > 1 else r"D:\Pokemon\PIKA\pikachu_model.bin"
    donor = sys.argv[2] if len(sys.argv) > 2 else r"E:\Yo-kai watch Mods\Meloetta port\y152000_p00.xc"
    out = sys.argv[3] if len(sys.argv) > 3 else os.path.join(HERE, "out", "y956000_p00.xc")
    tex_bin = sys.argv[4] if len(sys.argv) > 4 else model_bin.replace("_model.bin", "_tex.bin")
    # Optional custom-skin PNG overrides (ORIGINAL size — mirror wrap is applied
    # automatically). Off by default: textures come from tex.bin.
    png_dir = None
    if "--png" in sys.argv:
        png_dir = sys.argv[sys.argv.index("--png") + 1] or None
    model = gf_model.load_pokemon_model(model_bin)
    nb, npm, nx, no, path = pack(model, donor, out, se_root, tex_bin, png_dir,
                                 outline="--no-outline" not in sys.argv)
    print("packed %d bones, %d meshes, %d textures, %d outlined -> %s  (png_dir=%s)"
          % (nb, npm, nx, no, path, png_dir))
