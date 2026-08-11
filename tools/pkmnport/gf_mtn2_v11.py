"""
gf_mtn2_v11.py — write a Level-5 mtn2 in the OLD studio_eleven v1.1 format
(the TrackType-136 layout YW3 actually plays in-game). The current fork's
AnimationManager.Save writes a "V2" variant that Blender reads but the game
freezes on. Ported byte-for-byte from studio_eleven1.1/formats/xmtn.write_mtn2.

Location + Rotation tracks only (no scale = Meloetta-safe). Rotation is fed as a
ready quaternion (qx,qy,qz,qw); location as (x,y,z). Bones referenced by index
into `nodes` (crc32 utf-8 of each name), matching the model .mbn/RES.
"""

import struct
import zlib


def _table_offset(frame_offset, nframes):
    out = b''
    out += struct.pack('<I', frame_offset)
    out += struct.pack('<I', frame_offset + 4)
    out += struct.pack('<I', frame_offset + 4 + nframes * 2)
    out += struct.pack('<I', 0)
    return out


def _node_modified(node_index, nframes):
    low = nframes & 0xFF
    high = (32 + (nframes >> 8)) & 0xFF
    return struct.pack('<HBB', node_index, low, high)


def write_mtn2_v11(compress, nodes, frame_location, frame_rotation, frame_end, name="out_00",
                   frame_scale=None, decomp_mode="v11"):
    """
    nodes: list of bone names.
    frame_location: {node_index: {frame_key: (x,y,z)}}
    frame_rotation: {node_index: {frame_key: (qx,qy,qz,qw)}}
    frame_scale: {node_index: {frame_key: (sx,sy,sz)}} or None. YW3 COMBAT (_p20) requires a
      scale track present (a real y768000_p20 has scale count == bone count); overworld (_p10)
      plays fine without one. Pass the evaluated scale here for battle animations.
    frame_end: total frame count.
    decomp_mode: header DecompSize formula. "v11" = inner+8316 (works for _p10 overworld);
      "v2" = inner*2, which is what a real combat _p20 uses (see y768000_p20).
    compress: a callable(bytes)->bytes producing a Level-5 method-1 (LZ10) block.
    """
    nscale = len(frame_scale) if frame_scale else 0
    out = b''

    table_node = b''
    for n in nodes:
        table_node += struct.pack('<I', zlib.crc32(n.encode('utf-8')) & 0xffffffff)

    out += struct.pack('<I', 12)
    out += struct.pack('<I', 12 + len(table_node))
    out += struct.pack('<I', 52 + len(table_node))
    out += table_node

    type_offset = len(out)
    for i in range(1, 5):
        out += struct.pack('<H', type_offset + 8 * i)

    for i in range(3):
        spec = (0x03000201, 0x04000102, 0x03000203)[i]
        out += struct.pack('<I', spec)
        out += struct.pack('<H', 0)
        out += struct.pack('<H', frame_end)

    out += b'\x00' * 8   # empty block

    frame_offset = len(out) + (len(frame_location) + len(frame_rotation) + nscale) * 16

    data_location = b''
    for node, frames in frame_location.items():
        out += _table_offset(frame_offset, len(frames))
        frame_offset += len(frames) * 14 + 4
        data_location += _node_modified(node, len(frames))
        dt = b''
        for fk, (x, y, z) in frames.items():
            data_location += struct.pack('<H', fk)
            dt += struct.pack('<fff', x, y, z)
        data_location += dt

    data_rotation = b''
    for node, frames in frame_rotation.items():
        out += _table_offset(frame_offset, len(frames))
        frame_offset += len(frames) * 10 + 4
        data_rotation += _node_modified(node, len(frames))
        dt = b''
        for fk, q in frames.items():
            data_rotation += struct.pack('<H', fk)
            for c in q:
                v = max(-32767, min(32767, int(c * 32767)))
                dt += struct.pack('<h', v)
        data_rotation += dt

    data_scale = b''
    if frame_scale:
        for node, frames in frame_scale.items():
            out += _table_offset(frame_offset, len(frames))
            frame_offset += len(frames) * 14 + 4
            data_scale += _node_modified(node, len(frames))
            dt = b''
            for fk, (sx, sy, sz) in frames.items():
                data_scale += struct.pack('<H', fk)
                dt += struct.pack('<fff', sx, sy, sz)
            data_scale += dt

    out += data_location + data_rotation + data_scale
    data_uncompress = len(out)
    data_compress = compress(out)

    # header
    decomp_size = data_uncompress * 2 if decomp_mode == "v2" else data_uncompress + 8316
    hdr = struct.pack('<I', 0x4e544d58)          # "XMTN"
    hdr += struct.pack('<I', 0)
    hdr += struct.pack('<I', decomp_size)
    hdr += struct.pack('<I', 40)                 # NameOffset
    name_bytes = zlib.crc32(name.encode('utf-8')).to_bytes(4, 'little') + name.encode('utf-8')
    hdr += struct.pack('<I', 88)                 # CompDataOffset (recomputed below? v1.1 hardcodes 88)
    hdr += struct.pack('<I', len(frame_location))
    hdr += struct.pack('<I', len(frame_rotation))
    hdr += struct.pack('<I', nscale)             # scale count (combat _p20 needs this == bone count)
    hdr += struct.pack('<I', 0)
    hdr += struct.pack('<I', len(frame_location))
    hdr += name_bytes
    hdr += b'\x00' * (40 - len(name))
    hdr += struct.pack('<I', frame_end)
    hdr += data_compress
    return hdr


def build_from_motion(compress, motion, model, name="out_00"):
    """Evaluate a GFMotion and emit a v1.1 mtn2 (location + rotation, no scale)."""
    import gf_motion
    ev = gf_motion.evaluate(motion, model)
    nodes = list(ev.keys())
    frame_location = {}
    frame_rotation = {}
    for i, nm in enumerate(nodes):
        seq = ev[nm]
        loc = {}
        rot = {}
        for f, (t, q, s) in enumerate(seq):
            loc[f] = (float(t[0]), float(t[1]), float(t[2]))
            rot[f] = (float(q[0]), float(q[1]), float(q[2]), float(q[3]))
        frame_location[i] = loc
        frame_rotation[i] = rot
    return write_mtn2_v11(compress, nodes, frame_location, frame_rotation,
                          max(1, motion.frames), name)


def build_combined(compress, motions, model, gap=1, name="out_00",
                   with_scale=False, decomp_mode="v11"):
    """Concatenate several GFMotions into one v1.1 mtn2 timeline. Returns
    (mtn2_bytes, ranges) where ranges[i] = (start, end) frames of motion i.

    Every bone gets a keyframe at the start of EVERY motion (its bind pose when
    it isn't animated in that motion). Without this, a bone animated only in a
    later motion has no keyframe at/before an early frame -> the game finds no
    LHS keyframe and freezes.

    with_scale=True adds a dense scale track (required by combat _p20 — a real
    y768000_p20 carries scale count == bone count); pair with decomp_mode="v2"
    (DecompSize = inner*2) to match the real combat mtn2 header."""
    import gf_motion
    all_ev = [gf_motion.evaluate(m, model) for m in motions]
    nodes, seen = [], set()
    for ev in all_ev:
        for nm in ev:
            if nm not in seen:
                seen.add(nm); nodes.append(nm)
    idx = {nm: i for i, nm in enumerate(nodes)}

    bind = {b.name: b for b in model.skeleton}
    def bind_tqs(nm):
        b = bind[nm]
        rx, ry, rz = b.rotation
        return (tuple(float(x) for x in b.translation),
                gf_motion._euler_zyx_quat(rx, ry, rz),
                tuple(float(x) for x in b.scale))

    frame_location = {i: {} for i in range(len(nodes))}
    frame_rotation = {i: {} for i in range(len(nodes))}
    frame_scale = {i: {} for i in range(len(nodes))} if with_scale else None
    ranges = []
    off = 0
    for m, ev in zip(motions, all_ev):
        nf = max(1, m.frames)
        for nm in nodes:
            i = idx[nm]
            seq = ev.get(nm)
            if seq:
                for f, (t, q, s) in enumerate(seq):
                    frame_location[i][off + f] = (float(t[0]), float(t[1]), float(t[2]))
                    frame_rotation[i][off + f] = (float(q[0]), float(q[1]), float(q[2]), float(q[3]))
                    if with_scale:
                        frame_scale[i][off + f] = (float(s[0]), float(s[1]), float(s[2]))
            else:                       # not animated here: bind on EVERY frame of
                bt, bq, bs = bind_tqs(nm)   # this motion (dense, like Meloetta — sparse
                for f in range(nf):         # single-key filling breaks the combined anim)
                    frame_location[i][off + f] = bt
                    frame_rotation[i][off + f] = bq
                    if with_scale:
                        frame_scale[i][off + f] = bs
        ranges.append((off, off + nf))
        off = off + nf + gap
    frame_end = max(1, off - gap)
    mtn2 = write_mtn2_v11(compress, nodes, frame_location, frame_rotation, frame_end, name,
                          frame_scale=frame_scale, decomp_mode=decomp_mode)
    return mtn2, ranges


if __name__ == '__main__':
    import os, sys
    sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "punipuni"))
    from se_loader import load_studio_eleven
    import gf_model, gf_motion
    se = load_studio_eleven(r"E:\Yo-kai watch Mods\studio_eleven")
    # a method-1 (LZ10) Level-5 compressor
    comp = se.xpck.lz10.compress
    model = gf_model.load_pokemon_model(r"D:\Pokemon\PIKA\pikachu_model.bin")
    m = [x for _, x in gf_motion.parse_motion_pc(r"D:\Pokemon\PIKA\fight_anim_pikachu.bin") if x.has_skeletal][0]
    mtn2 = build_from_motion(comp, m, model)
    h = struct.unpack_from('<8s IIIIIII', mtn2, 0)
    coff = h[3]
    inner = struct.unpack_from('<I', mtn2, coff)[0]
    print("v1.1 mtn2 len=%d CompDataOff=%d method=%d loc=%d rot=%d scale=%d"
          % (len(mtn2), coff, inner & 7, h[4], h[5], h[6]))
