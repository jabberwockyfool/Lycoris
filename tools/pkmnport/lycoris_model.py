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
