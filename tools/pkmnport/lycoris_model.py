"""
lycoris_model.py — thin CLI driver so Lycoris (C#) can drive the Gen-7 model porter.

  --scan  <model.bin>
        Print one line per mesh: "<base_name>\t<auto_outline 0|1>". Lycoris uses this
        to populate its per-mesh Port / Outline checkboxes.

  --build --model <model.bin> --donor <p00.xc> --out <out.xc> [--tex <tex.bin>]
          [--se-root <studio_eleven>] [--include a,b,c] [--outline a,b] [--no-outline]
        Run pack_p00.pack and print "OK <bones> <meshes> <tex> <outlined> <out.xc>".

On any error, prints "ERR: <message>" to stderr and exits 1.
"""

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import gf_model
import pack_p00

DEFAULT_SE_ROOT = r"E:\Yo-kai watch Mods\studio_eleven"


def _mesh_bases(model):
    bases, seen = [], set()
    for m in model.meshes:
        base = m.name.replace("_OptMesh", "")
        if base in seen or not any(sm.vertices for sm in m.submeshes):
            continue
        seen.add(base)
        bases.append(base)
    return bases


def _arg(name, default=None):
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else default


def main():
    if "--scan" in sys.argv:
        model = gf_model.load_pokemon_model(_arg("--scan"))
        for base in _mesh_bases(model):
            # Outline OFF by default: pack_p00's outline adds a Col attr the donor's fixed .atr
            # doesn't declare, which hides the mesh. Do outlines manually until that's solved.
            print("%s\t0" % base)
        return 0

    if "--anim" in sys.argv:
        import struct
        from se_loader import load_studio_eleven
        import gf_motion, gf_mtn2_v11, ykport
        import slots as SLOTS
        import gf_anim_pack as GAP

        model_path = _arg("--model")
        basic = _arg("--basic")
        fight = _arg("--fight")
        out_dir = _arg("--out-dir")
        mid = _arg("--mid")
        se_root = _arg("--se-root", DEFAULT_SE_ROOT)
        bundled = os.path.join(se_root, "ykport", "templates")
        tpl_dir = _arg("--tpl-dir") or (bundled if os.path.isdir(bundled) else r"D:\cc\template")
        donor10 = _arg("--donor-p10") or os.path.join(tpl_dir, "template_p10.xc")
        donor20 = _arg("--donor-p20") or os.path.join(tpl_dir, "template_p20.xc")
        txt = _arg("--txt")
        se = load_studio_eleven(se_root)
        model = gf_model.load_pokemon_model(model_path)

        def donor_name(path):
            f = se.xpck.open_file(path)
            b = f[ykport._mtn2_key(f)]
            noff = struct.unpack_from("<I", b, 12)[0]
            return b[noff + 4:noff + 4 + 40].split(b'\x00')[0].decode('latin-1', 'ignore') or "out_00"

        def parse_txt(path):
            out = {}
            for raw in open(path, encoding="utf-8", errors="ignore"):
                p = [x.strip() for x in raw.strip().split(" - ")]
                if len(p) < 4 or len(p[1].split()) != 4:
                    continue
                try:
                    out[SLOTS.slot_bytes(p[1])] = (int(p[-2]), int(p[-1]))
                except Exception:
                    pass
            return out

        override = parse_txt(txt) if (txt and os.path.isfile(txt)) else None

        def build(anim, slot_table, role_map, out_path, donor, ov=None):
            motions = [m for _, m in gf_motion.parse_motion_pc(anim) if m.has_skeletal]
            if not motions:
                return 0
            mtn2, ranges = gf_mtn2_v11.build_combined(
                se.xpck.lz10.compress, motions, model, gap=1, name=donor_name(donor))
            idle_i = role_map.get("idle", 0)
            idle = (ranges[idle_i][0], ranges[idle_i][1], 0.5)
            sr = {}
            if ov:
                for sid, (s, e) in ov.items():
                    sr[sid] = (s, e, 0.5)
            for role, hx in slot_table.items():
                sid = SLOTS.slot_bytes(hx)
                if sid in sr:
                    continue
                mi = role_map.get(role)
                sr[sid] = (ranges[mi][0], ranges[mi][1], 0.5) if (mi is not None and mi < len(ranges)) else idle
            ykport.package_xc(se, donor, mtn2, sr, idle, out_path)
            return len(motions)

        made = []
        if basic and os.path.isfile(basic) and os.path.isfile(donor10):
            n = build(basic, SLOTS.P10, GAP.ROLE_TO_MOTION_P10, os.path.join(out_dir, "%s_p10.xc" % mid), donor10)
            made.append("p10(%d)" % n)
        if fight and os.path.isfile(fight) and os.path.isfile(donor20):
            n = build(fight, SLOTS.P20, GAP.ROLE_TO_MOTION, os.path.join(out_dir, "%s_p20.xc" % mid), donor20, override)
            made.append("p20(%d%s)" % (n, ",TXT" if override else ""))
        print("OK anim %s -> %s" % (" ".join(made) if made else "nothing", out_dir))
        return 0

    if "--build" in sys.argv:
        model_path = _arg("--model")
        donor = _arg("--donor")
        out = _arg("--out")
        tex = _arg("--tex")
        se_root = _arg("--se-root", DEFAULT_SE_ROOT)
        inc = _arg("--include")
        outl = _arg("--outline")
        include_meshes = set(inc.split(",")) if inc else None
        outline_meshes = set(outl.split(",")) if outl else None
        model = gf_model.load_pokemon_model(model_path)
        nb, npm, nx, no, path = pack_p00.pack(
            model, donor, out, se_root, tex_bin_path=tex,
            outline="--no-outline" not in sys.argv,
            include_meshes=include_meshes, outline_meshes=outline_meshes)
        print("OK %d %d %d %d %s" % (nb, npm, nx, no, path))
        return 0

    sys.stderr.write("ERR: use --scan or --build\n")
    return 2


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:
        sys.stderr.write("ERR: %s\n" % e)
        sys.exit(1)
