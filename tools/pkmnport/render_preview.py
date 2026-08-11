"""
render_preview.py — tiny software renderer to preview how a parsed GFModel looks
textured, using the SAME UV/texture convention as the YW3 export. Lets us iterate
the UV orientation offline instead of round-tripping in-game.

Front view: project (x,y); sample each submesh's diffuse texture. The sampling
convention (u/v flips) is a single knob so we can find the one that matches
in-game, then apply the inverse fix in the packer.
"""

import sys
import math
import gf_model
import gf_texture


def load_diffuse(model, tex_bin, png_dir=None):
    """base-name -> (W,H, upright-top-down RGBA bytes). PNG override wins."""
    import os
    out = {}
    for t in gf_texture.parse_tex_pc(tex_bin):
        if t['fmt'] not in gf_texture.BPP or t['width'] == 0:
            continue
        base = t['name'].replace('.tga', '').replace('pm0025_00_', '')
        rgba = gf_texture.decode_texture(t['width'], t['height'], t['fmt'], t['raw'])
        out[base] = (t['width'], t['height'], rgba)
    if png_dir and os.path.isdir(png_dir):
        from PIL import Image
        for base in list(out.keys()):
            for cand in ("pm0025_00_%s.png" % base, "%s.png" % base):
                p = os.path.join(png_dir, cand)
                if os.path.exists(p):
                    im = Image.open(p).convert('RGBA')
                    out[base] = (im.width, im.height, im.tobytes())
                    break
    return out


def _mirror(t):
    t = t - 2 * math.floor(t / 2)   # into [0,2)
    return t if t < 1 else 2 - t


def sample(tex, u, v, flip_u, flip_v, mirror_u=False):
    W, H, rgba = tex
    if flip_u:
        u = 1.0 - u
    if flip_v:
        v = 1.0 - v
    u = _mirror(u) if mirror_u else u - math.floor(u)   # mirror or repeat
    v -= math.floor(v)
    x = min(W - 1, int(u * W))
    y = min(H - 1, int(v * H))   # rgba is top-down; y=0 top
    o = (y * W + x) * 4
    return rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]


def render(model, diffuse, size=384, flip_u=False, flip_v=True):
    # world bbox (front view uses x,y)
    xs, ys = [], []
    for mesh in model.meshes:
        for sm in mesh.submeshes:
            for v in sm.vertices:
                xs.append(v.position[0]); ys.append(v.position[1])
    minx, maxx, miny, maxy = min(xs), max(xs), min(ys), max(ys)
    cx, cy = (minx + maxx) / 2, (miny + maxy) / 2
    span = max(maxx - minx, maxy - miny) * 1.1
    scale = size / span

    def proj(p):
        sx = (p[0] - cx) * scale + size / 2
        sy = size / 2 - (p[1] - cy) * scale   # y up -> screen down
        return sx, sy

    canvas = bytearray([40] * (size * size * 4))
    for i in range(3, len(canvas), 4):
        canvas[i] = 255
    zbuf = [-1e9] * (size * size)   # keep nearest = largest z (front faces +Z)

    for mesh in model.meshes:
        for sm in mesh.submeshes:
            if not sm.vertices:
                continue
            base = None
            mat = (model.materials or {}).get(sm.name)
            if mat:
                base = mat.replace('.tga', '').replace('pm0025_00_', '')
            if base not in diffuse:
                base = next((b for b in diffuse if sm.name.startswith(b[:4])), None)
            if base not in diffuse:
                continue
            tex = diffuse[base]
            sx, sy, _rot, tx, ty = (model.mat_uvxf or {}).get(sm.name, (1.0, 1.0, 0.0, 0.0, 0.0))
            mir = (model.mat_wrap or {}).get(sm.name, (2, 2))[0] == 3

            def X(uv):   # GF UV transform (DccMaya, rot=0)
                return (sx * (uv[0] - tx), sy * (uv[1] - ty))

            for (a, b, c) in gf_model._tris(sm):
                va, vb, vc = sm.vertices[a], sm.vertices[b], sm.vertices[c]
                pa, pb, pc = proj(va.position), proj(vb.position), proj(vc.position)
                raster(canvas, zbuf, size, pa, pb, pc,
                       va.position[2], vb.position[2], vc.position[2],
                       X(va.uv0), X(vb.uv0), X(vc.uv0), tex, flip_u, flip_v, mir)
    return canvas, size


def raster(canvas, zbuf, size, pa, pb, pc, za, zb, zc, uva, uvb, uvc, tex, fu, fv, mir=False):
    minx = max(0, int(min(pa[0], pb[0], pc[0])))
    maxx = min(size - 1, int(max(pa[0], pb[0], pc[0])))
    miny = max(0, int(min(pa[1], pb[1], pc[1])))
    maxy = min(size - 1, int(max(pa[1], pb[1], pc[1])))
    d = (pb[1] - pc[1]) * (pa[0] - pc[0]) + (pc[0] - pb[0]) * (pa[1] - pc[1])
    if abs(d) < 1e-9:
        return
    for py in range(miny, maxy + 1):
        for px in range(minx, maxx + 1):
            w0 = ((pb[1] - pc[1]) * (px - pc[0]) + (pc[0] - pb[0]) * (py - pc[1])) / d
            w1 = ((pc[1] - pa[1]) * (px - pc[0]) + (pa[0] - pc[0]) * (py - pc[1])) / d
            w2 = 1 - w0 - w1
            if w0 < 0 or w1 < 0 or w2 < 0:
                continue
            z = w0 * za + w1 * zb + w2 * zc
            idx = py * size + px
            if z <= zbuf[idx]:
                continue
            u = w0 * uva[0] + w1 * uvb[0] + w2 * uvc[0]
            v = w0 * uva[1] + w1 * uvb[1] + w2 * uvc[1]
            r, g, b, a = sample(tex, u, v, fu, fv, mirror_u=mir)
            if a < 8:
                continue
            zbuf[idx] = z
            o = idx * 4
            canvas[o] = r; canvas[o + 1] = g; canvas[o + 2] = b; canvas[o + 3] = 255


if __name__ == '__main__':
    model_bin = sys.argv[1] if len(sys.argv) > 1 else r"D:\Pokemon\PIKA\pikachu_model.bin"
    tex_bin = sys.argv[2] if len(sys.argv) > 2 else model_bin.replace("_model.bin", "_tex.bin")
    fu = '--flipu' in sys.argv
    fv = '--noflipv' not in sys.argv   # default: flip v (matches xmpr 1-v)
    png_dir = sys.argv[sys.argv.index("--png") + 1] if "--png" in sys.argv else None
    model = gf_model.load_pokemon_model(model_bin)
    diffuse = load_diffuse(model, tex_bin, png_dir)
    canvas, size = render(model, diffuse, flip_u=fu, flip_v=fv)
    out = r"D:\Pokemon\PIKA\tex_png\_preview_png_fu%d_fv%d.png" % (fu, fv)
    gf_texture.write_png(out, size, size, canvas)
    print("wrote", out, "flip_u=%d flip_v=%d" % (fu, fv))
