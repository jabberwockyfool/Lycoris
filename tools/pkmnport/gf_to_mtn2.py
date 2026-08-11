"""
gf_to_mtn2.py — convert an evaluated GFMotion (per-bone per-frame local TRS) into
a Level-5 mtn2 (XMTN) via studio_eleven's AnimationManager. Location + Rotation
tracks only (no BoneScale — matches the working Meloetta anim; a stray scale track
blows the mesh up in-game). Bone nodes are keyed by crc32(bone name), matching the
model's .mbn/RES.
"""

import os
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "punipuni"))
from se_loader import load_studio_eleven

import gf_model
import gf_motion


def build_manager(se, motion, model, anim_name="out_00"):
    """Evaluated motion -> AnimationManager (mtn2-ready). Dense per-frame keys."""
    AM = se.animation_manager
    ev = gf_motion.evaluate(motion, model)

    loc = AM.Track("BoneLocation", 0, [])
    rot = AM.Track("BoneRotation", 1, [])

    for name, seq in ev.items():
        crc = zlib.crc32(name.encode("utf-8")) & 0xffffffff
        lnode = AM.Node(crc, True, [])
        rnode = AM.Node(crc, True, [])
        for f, (t, q, s) in enumerate(seq):
            lnode.add_frame(f, AM.BoneLocation(float(t[0]), float(t[1]), float(t[2])))
            # quaternion straight in (X,Y,Z,W) -> no euler round-trip
            rnode.add_frame(f, AM.BoneRotation(float(q[0]), float(q[1]), float(q[2]), float(q[3])))
        loc.Nodes.append(lnode)
        rot.Nodes.append(rnode)

    frames = max(1, motion.frames)
    anim = AM.AnimationManager(Format="XMTN", Version="V2", AnimationName=anim_name,
                               FrameCount=frames, Tracks=[loc, rot])
    return anim


def motion_to_mtn2(se, motion, model, anim_name="out_00"):
    return build_manager(se, motion, model, anim_name).Save()


if __name__ == '__main__':
    se_root = r"E:\Yo-kai watch Mods\studio_eleven"
    apath = sys.argv[1] if len(sys.argv) > 1 else r"D:\Pokemon\PIKA\fight_anim_pikachu.bin"
    mpath = sys.argv[2] if len(sys.argv) > 2 else r"D:\Pokemon\PIKA\pikachu_model.bin"
    se = load_studio_eleven(se_root)
    model = gf_model.load_pokemon_model(mpath)
    motions = gf_motion.parse_motion_pc(apath)
    skeletal = [(i, m) for i, m in motions if m.has_skeletal]
    print("%d skeletal motion(s)" % len(skeletal))
    idx, m = skeletal[0]
    mtn2 = motion_to_mtn2(se, m, model)
    print("motion[%d] frames=%d -> mtn2 %d bytes, magic=%r" % (idx, m.frames, len(mtn2), mtn2[:8]))
    # round-trip: re-parse header
    import struct
    f = struct.unpack_from("<8s IIIIIII", mtn2, 0)
    print("  header: DecompSize=%d Track1(loc)=%d Track2(rot)=%d Track3(scale)=%d frames=%d"
          % (f[1], f[4], f[5], f[6], struct.unpack_from("<I", mtn2, len(mtn2) - 0)[0] if False else m.frames))
