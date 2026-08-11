"""
gf_anim_pack.py — package Gen-7 GFMotion animations into a YW3 _p20.xc (battle),
reusing the punipuni ykport engine: build one AnimationManager per motion, combine
them into a single mtn2, map YW3 p20 slot ids to each motion's frame range, then
swap into a donor _p20.xc (repointing every .mtninf).

First pass: motion 0 (looping idle) drives the 'idle' slot; every other slot
falls back to idle so the yo-kai animates instead of T-posing. Refine ROLE_TO_MOTION
once we know which GF motion index is which action.
"""

import os
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
PUNI = os.path.join(HERE, "..", "punipuni")
sys.path.insert(0, PUNI)
from se_loader import load_studio_eleven
import ykport
import slots as SLOTS

import gf_model
import gf_motion
import gf_mtn2_v11


# fight_anim skeletal-motion list-index -> YW3 p20 role (user-confirmed):
#   0 idle, 1-3 skip, 4 battle_start, 5 attack, 6 dash/attack2, 7 magic/technique,
#   8 magic2/technique2, 9 hit/damage, 10 death.
ROLE_TO_MOTION = {
    "idle":             0,
    "battle_start":     4,
    "attack":           5,
    "charge":           6,   # dash / attack2
    "magic":            7,   # technique
    "soultimate":       8,   # technique2 / magic2
    "soultimate_start": 8,
    "damage":           9,   # hit
    "death":            10,
    "ascension":        10,  # death last frame
}

# basic_anim skeletal-motion list-index -> YW3 p10 role: 0 idle, 1 walk, 2 run.
ROLE_TO_MOTION_P10 = {
    "idle":      0,
    "long_idle": 0,
    "walk":      1,
    "run":       2,
}


def _fix_decompsize(mtn2):
    """studio_eleven's V2 Save writes header DecompSize = uncompressed_len*2; the
    game (and the working v1.1 Meloetta anim) uses inner_decompSize + 8316. A 2x
    value makes the game read past the decompressed buffer -> no animation in-game
    (Blender uses the inner block's own size, so it plays fine there). Rewrite it."""
    import struct as _s
    coff = _s.unpack_from('<I', mtn2, 16)[0]         # CompDataOffset
    inner = _s.unpack_from('<I', mtn2, coff)[0] >> 3  # inner block decompressed size
    b = bytearray(mtn2)
    _s.pack_into('<I', b, 8, inner + 8316)           # header.DecompSize
    return bytes(b)


def pack_anim(model, anim_bin, donor_xc, out_path, se, slot_table, role_map, label):
    motions = [m for _, m in gf_motion.parse_motion_pc(anim_bin) if m.has_skeletal]
    if not motions:
        raise RuntimeError("no skeletal motions in %s" % anim_bin)

    # combine all motions into one game-valid v1.1 mtn2 timeline — the proven no-scale / v11
    # (DecompSize=inner+8316) form that never froze. The real fix under test here is the mtninf
    # trim (keep only P20 action splits), applied below in package_xc.
    mtn2, ranges = gf_mtn2_v11.build_combined(
        se.xpck.lz10.compress, motions, model, gap=1,
        with_scale=False, decomp_mode="v11")

    idle_i = role_map.get("idle", 0)
    idle_range = (ranges[idle_i][0], ranges[idle_i][1], 0.5)

    slot_ranges = {}
    print("  %s role -> motion:" % label)
    for role, sid_hex in slot_table.items():
        mi = role_map.get(role)
        if mi is not None and mi < len(ranges):
            slot_ranges[SLOTS.slot_bytes(sid_hex)] = (ranges[mi][0], ranges[mi][1], 0.5)
            print("    %-16s -> motion %-2d [%d..%d]" % (role, mi, ranges[mi][0], ranges[mi][1]))
        else:
            slot_ranges[SLOTS.slot_bytes(sid_hex)] = idle_range

    # No RES trimming (that corrupted the archive). Build the _p20 like the working _p10: same
    # donor archive + mtn2 swap + repoint the mtninf frame ranges to the P20 slots. RES untouched.
    matched, fallback, _ = ykport.package_xc(se, donor_xc, mtn2, slot_ranges, idle_range, out_path)
    return len(motions), matched, fallback, out_path


def pack_p20(model, anim_bin, donor, out_path, se_root):
    se = load_studio_eleven(se_root)
    return pack_anim(model, anim_bin, donor, out_path, se, SLOTS.P20, ROLE_TO_MOTION, "p20")


if __name__ == '__main__':
    se_root = r"E:\Yo-kai watch Mods\studio_eleven"
    mid = sys.argv[1] if len(sys.argv) > 1 else "y152000"    # output model id / slot
    donor = sys.argv[2] if len(sys.argv) > 2 else r"E:\Yo-kai watch Mods\Meloetta port\y152000_p20.xc"
    outdir = os.path.join(HERE, "out")
    os.makedirs(outdir, exist_ok=True)
    se = load_studio_eleven(se_root)
    model = gf_model.load_pokemon_model(r"D:\Pokemon\PIKA\pikachu_model.bin")

    # Build the p10 (overworld) first, then build the p20 (battle) FROM that working p10 archive —
    # "edit the p10, put the p20 hex IDs": same proven archive, swap mtn2 to fight + repoint mtninf
    # ranges to the P20 slots. RES stays intact (no trimming).
    p10_path = os.path.join(outdir, "%s_p10.xc" % mid)
    n10 = pack_anim(model, r"D:\Pokemon\PIKA\basic_anim_pikachu.bin",
                    donor, p10_path, se,
                    SLOTS.P10, ROLE_TO_MOTION_P10, "p10")
    n20 = pack_anim(model, r"D:\Pokemon\PIKA\fight_anim_pikachu.bin",
                    p10_path, os.path.join(outdir, "%s_p20.xc" % mid), se,
                    SLOTS.P20, ROLE_TO_MOTION, "p20")
    print("p20: %d motions, p10: %d motions -> %s_p20.xc / %s_p10.xc" % (n20[0], n10[0], mid, mid))
