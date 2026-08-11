"""
gf_motion.py — reader for Gen-7 Pokemon GFMotion animations (basic_anim/fight_anim
PC containers), ported from SPICA (Formats/GFL2/Motion/*). Decodes the skeletal
section into per-bone per-frame local TRS (translation, quaternion rotation, scale),
starting from the model's bind pose. Feeds the mtn2 conversion.
"""

import math
import struct
import gf_model


# ---- keyframe decode (GFMotKeyFrame.SetList) -------------------------------
def _read_kf_list(r, flags, frames_count):
    """Return list of (frame:int, value:float, slope:float) for one track element."""
    mode = flags & 7
    if mode == 3:                       # constant
        return [(0, r.f32(), 0.0)]
    if mode in (4, 5):                  # keyframe list
        n = r.u32()
        frames = []
        for _ in range(n):
            frames.append(r.u16() if frames_count > 0xff else r.u8())
        while r.tell() & 3:             # align to 4
            r.u8()
        out = []
        if flags & 1:                   # float: value + slope (2 f32 each)
            for i in range(n):
                out.append((frames[i], r.f32(), r.f32()))
        else:                           # quantized uint16 + scale/offset
            vs, vo, ss, so = r.f32(), r.f32(), r.f32(), r.f32()
            for i in range(n):
                v = (r.u16() / 65535.0) * vs + vo
                s = (r.u16() / 65535.0) * ss + so
                out.append((frames[i], v, s))
        return out
    return []                           # no track for this element -> use bind


def _herp(lhs, rhs, ls, rs, diff, w):
    res = lhs + (lhs - rhs) * (2 * w - 3) * w * w
    res += (diff * (w - 1)) * (ls * (w - 1) + rs * w)
    return res


def _eval(kfs, frame, bind):
    """Evaluate a keyframe list at `frame`, defaulting to bind."""
    if not kfs:
        return bind
    if len(kfs) == 1:
        return kfs[0][1]
    # LHS = last kf with frame<=Frame ; RHS = first kf with frame>=Frame
    lhs = kfs[0]
    for kf in kfs:
        if kf[0] <= frame:
            lhs = kf
        else:
            break
    rhs = next((kf for kf in kfs if kf[0] >= frame), kfs[-1])
    if lhs[0] != rhs[0]:
        fd = frame - lhs[0]
        w = fd / (rhs[0] - lhs[0])
        return _herp(lhs[1], rhs[1], lhs[2], rhs[2], fd, w)
    return lhs[1]


# ---- per-bone transform (GFMotBoneTransform) -------------------------------
class BoneMot:
    __slots__ = ('name', 'is_axis_angle', 'tracks')

    def __init__(self, name):
        self.name = name
        self.is_axis_angle = False
        self.tracks = [[] for _ in range(9)]   # SX SY SZ RX RY RZ TX TY TZ


def _read_bone_mot(r, name, frames_count):
    b = BoneMot(name)
    flags = r.u32()
    r.u32()                              # length
    b.is_axis_angle = (flags >> 31) == 0
    for e in range(9):
        b.tracks[e] = _read_kf_list(r, flags, frames_count)
        flags >>= 3
    return b


# ---- quaternion helpers ----------------------------------------------------
def _quat_mul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (aw * bx + ax * bw + ay * bz - az * by,
            aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw,
            aw * bw - ax * bx - ay * by - az * bz)


def _quat_axis_angle(ax, ay, az, angle):
    h = angle * 0.5
    s = math.sin(h)
    return (ax * s, ay * s, az * s, math.cos(h))


def _euler_zyx_quat(rx, ry, rz):
    # SPICA: CreateFromAxisAngle(Z,rz) * (Y,ry) * (X,rx)
    qz = _quat_axis_angle(0, 0, 1, rz)
    qy = _quat_axis_angle(0, 1, 0, ry)
    qx = _quat_axis_angle(1, 0, 0, rx)
    return _quat_mul(_quat_mul(qz, qy), qx)


# ---- GFMotion container ----------------------------------------------------
class Motion:
    def __init__(self):
        self.frames = 0
        self.looping = False
        self.bones = []          # list of BoneMot (skeletal)
        self.has_skeletal = False


def _read_motion(data):
    r = gf_model.Reader(data)
    magic = r.u32()
    if magic != 0x00060000:
        return None
    scount = r.u32()
    sections = []
    for _ in range(scount):
        sections.append((r.u32(), r.u32(), r.u32()))   # (name, length, address)

    m = Motion()
    # subheader (section 0)
    r.seek(sections[0][2])
    m.frames = r.u32()
    m.looping = (r.u16() & 1) != 0
    r.u16()                              # blended
    r.vec3(); r.vec3()                   # region min/max
    r.u32()                              # anim hash

    for name, length, addr in sections[1:]:
        if name == 1:                    # SkeletalAnim
            r.seek(addr)
            bcount = r.i32()
            blen = r.u32()
            pos = r.tell()
            names = [r.byte_len_string() for _ in range(bcount)]
            r.seek(pos + blen)
            for nm in names:
                m.bones.append(_read_bone_mot(r, nm, m.frames))
            m.has_skeletal = True
    return m


def parse_motion_pc(path):
    """Return list of (index, Motion) for each non-empty GFMotion in a *_anim.bin."""
    data = open(path, 'rb').read()
    out = []
    for i, sub in enumerate(gf_model.pc_slices(data)):
        if len(sub) < 8:
            continue
        m = _read_motion(sub)
        if m is not None:
            out.append((i, m))
    return out


# ---- evaluation: per-bone per-frame local TRS ------------------------------
def evaluate(motion, model):
    """Return {bone_name: [(loc(x,y,z), quat(x,y,z,w), scale(x,y,z)) per frame]}.
    Starts from the model bind pose; animated tracks override per frame."""
    bind = {b.name: b for b in model.skeleton}
    frames = max(1, motion.frames)
    out = {}
    for bm in motion.bones:
        bb = bind.get(bm.name)
        if bb is None:
            continue
        bsx, bsy, bsz = bb.scale
        brx, bry, brz = bb.rotation
        btx, bty, btz = bb.translation
        seq = []
        for f in range(frames):
            sx = _eval(bm.tracks[0], f, bsx)
            sy = _eval(bm.tracks[1], f, bsy)
            sz = _eval(bm.tracks[2], f, bsz)
            rx = _eval(bm.tracks[3], f, brx)
            ry = _eval(bm.tracks[4], f, bry)
            rz = _eval(bm.tracks[5], f, brz)
            tx = _eval(bm.tracks[6], f, btx)
            ty = _eval(bm.tracks[7], f, bty)
            tz = _eval(bm.tracks[8], f, btz)
            if bm.is_axis_angle:
                l = math.sqrt(rx * rx + ry * ry + rz * rz)
                ang = l * 2.0
                if ang > 0:
                    q = _quat_axis_angle(rx / l, ry / l, rz / l, ang)
                else:
                    q = (0.0, 0.0, 0.0, 1.0)
            else:
                q = _euler_zyx_quat(rx, ry, rz)
            seq.append(((tx, ty, tz), q, (sx, sy, sz)))
        out[bm.name] = seq
    return out


if __name__ == '__main__':
    import sys
    apath = sys.argv[1] if len(sys.argv) > 1 else r"D:\Pokemon\PIKA\basic_anim_pikachu.bin"
    mpath = sys.argv[2] if len(sys.argv) > 2 else r"D:\Pokemon\PIKA\pikachu_model.bin"
    model = gf_model.load_pokemon_model(mpath)
    motions = parse_motion_pc(apath)
    print("%s: %d motion(s)" % (apath, len(motions)))
    for idx, m in motions:
        anim_bones = sum(1 for b in m.bones if any(b.tracks))
        print("  [%02d] frames=%d looping=%s bones=%d animated=%d axisAngle=%s"
              % (idx, m.frames, m.looping, len(m.bones), anim_bones,
                 m.bones[0].is_axis_angle if m.bones else '?'))
    # sanity: evaluate first motion, show a moving bone
    if motions:
        ev = evaluate(motions[0][1], model)
        for name, seq in ev.items():
            t0, t1 = seq[0][0], seq[-1][0]
            if abs(t0[0] - t1[0]) + abs(t0[1] - t1[1]) + abs(t0[2] - t1[2]) > 0.01:
                print("  moving bone '%s': frame0 loc=%s -> lastFrame loc=%s"
                      % (name, tuple(round(x, 2) for x in t0), tuple(round(x, 2) for x in t1)))
                break
