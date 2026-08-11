"""
gf_texture.py — decode Gen-7 Pokemon GFTexture (PC container of 'texture' blocks)
to straight RGBA, and dump PNGs for validation. RGB8/RGBA8/RGB565/RGBA5551/RGBA4
+ 3DS 8x8-tile swizzle. No PIL (minimal zlib PNG writer).

GFTexture layout (SPICA): magic 0x15041213, count, GFSection('texture'),
TextureLength@0x18, Name@0x28(0x40), W/H/Fmt/Mip@0x68, RawBuffer@0x80.
"""

import struct
import zlib

# GFTextureFormat
RGB565 = 0x2; RGB8 = 0x3; RGBA8 = 0x4; RGBA4 = 0x16; RGBA5551 = 0x17
L8 = 0x25; A8 = 0x26; LA8 = 0x23
FMT_NAME = {RGB565: 'RGB565', RGB8: 'RGB8', RGBA8: 'RGBA8', RGBA4: 'RGBA4',
            RGBA5551: 'RGBA5551', L8: 'L8', A8: 'A8', LA8: 'LA8'}

# bytes per pixel for the raw (pre-deswizzle) buffer
BPP = {RGB565: 2, RGB8: 3, RGBA8: 4, RGBA4: 2, RGBA5551: 2, L8: 1, A8: 1, LA8: 2}


def _morton(x, y):
    """3DS in-tile Z-order for an 8x8 tile: interleave 3 low bits of x and y."""
    d = 0
    for i in range(3):
        d |= ((x >> i) & 1) << (2 * i)
        d |= ((y >> i) & 1) << (2 * i + 1)
    return d


def _decode_pixel(fmt, buf, o):
    if fmt == RGBA8:
        a, b, g, r = buf[o], buf[o + 1], buf[o + 2], buf[o + 3]
        return (r, g, b, a)
    if fmt == RGB8:
        b, g, r = buf[o], buf[o + 1], buf[o + 2]
        return (r, g, b, 255)
    if fmt == RGB565:
        v = buf[o] | (buf[o + 1] << 8)
        r = ((v >> 11) & 0x1f) * 255 // 31
        g = ((v >> 5) & 0x3f) * 255 // 63
        b = (v & 0x1f) * 255 // 31
        return (r, g, b, 255)
    if fmt == RGBA5551:
        v = buf[o] | (buf[o + 1] << 8)
        r = ((v >> 11) & 0x1f) * 255 // 31
        g = ((v >> 6) & 0x1f) * 255 // 31
        b = ((v >> 1) & 0x1f) * 255 // 31
        a = 255 if (v & 1) else 0
        return (r, g, b, a)
    if fmt == RGBA4:
        v = buf[o] | (buf[o + 1] << 8)
        r = ((v >> 12) & 0xf) * 17
        g = ((v >> 8) & 0xf) * 17
        b = ((v >> 4) & 0xf) * 17
        a = (v & 0xf) * 17
        return (r, g, b, a)
    if fmt == L8:
        l = buf[o]; return (l, l, l, 255)
    if fmt == A8:
        return (255, 255, 255, buf[o])
    if fmt == LA8:
        l, a = buf[o + 1], buf[o]
        return (l, l, l, a)
    raise ValueError("unsupported fmt 0x%x" % fmt)


def decode_texture(width, height, fmt, raw):
    """Deswizzle + decode to a flat RGBA bytearray (row-major, top-left origin)."""
    bpp = BPP[fmt]
    out = bytearray(width * height * 4)
    tiles_x = (width + 7) // 8
    for y in range(height):
        ty, iy = y >> 3, y & 7
        for x in range(width):
            tx, ix = x >> 3, x & 7
            tile = ty * tiles_x + tx
            idx = tile * 64 + _morton(ix, iy)
            r, g, b, a = _decode_pixel(fmt, raw, idx * bpp)
            # 3DS textures are stored bottom-up; flip Y
            d = ((height - 1 - y) * width + x) * 4
            out[d] = r; out[d + 1] = g; out[d + 2] = b; out[d + 3] = a
    return out


def parse_tex_pc(path):
    """Yield dicts {name,width,height,fmt,raw} for each texture in a *_tex.bin."""
    d = open(path, 'rb').read()
    assert d[:2] == b'PC'
    cnt = struct.unpack_from('<H', d, 2)[0]
    offs = [struct.unpack_from('<I', d, 4 + 4 * i)[0] for i in range(cnt + 1)]
    out = []
    for i in range(cnt):
        sub = d[offs[i]:offs[i + 1]]
        texlen = struct.unpack_from('<I', sub, 0x18)[0]
        name = sub[0x28:0x68].split(b'\0')[0].decode('latin-1')
        w, h, fmt, mip = struct.unpack_from('<HHHH', sub, 0x68)
        raw = sub[0x80:0x80 + texlen]
        out.append({'name': name, 'width': w, 'height': h, 'fmt': fmt, 'mip': mip, 'raw': raw})
    return out


def write_png(path, width, height, rgba):
    """Minimal RGBA PNG writer."""
    def chunk(tag, data):
        c = struct.pack('>I', len(data)) + tag + data
        return c + struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)
    raw = bytearray()
    for y in range(height):
        raw.append(0)  # filter type 0
        raw += rgba[y * width * 4:(y + 1) * width * 4]
    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(bytes(raw), 9))
    png += chunk(b'IEND', b'')
    open(path, 'wb').write(png)


if __name__ == '__main__':
    import sys, os
    path = sys.argv[1] if len(sys.argv) > 1 else r"D:\Pokemon\PIKA\pikachu_tex.bin"
    outdir = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(path), "tex_png")
    os.makedirs(outdir, exist_ok=True)
    for t in parse_tex_pc(path):
        if t['fmt'] not in BPP or t['width'] == 0:
            print("skip", t['name'], hex(t['fmt'])); continue
        rgba = decode_texture(t['width'], t['height'], t['fmt'], t['raw'])
        outp = os.path.join(outdir, t['name'].replace('.tga', '') + ".png")
        write_png(outp, t['width'], t['height'], rgba)
        print("%-34s %dx%d %-8s -> %s" % (t['name'], t['width'], t['height'],
                                          FMT_NAME.get(t['fmt'], hex(t['fmt'])), os.path.basename(outp)))
