"""
se_loader — import studio_eleven's format codecs OUTSIDE Blender.

studio_eleven (github.com/Tiniifan/studio_eleven) is a Blender add-on: its
top-level package and a couple of sub-package __init__.py files `import bpy`,
so a plain `import studio_eleven...` explodes when run with a normal Python.

But the code we actually need — the XMTN (.mtn2) reader/writer, the MINF
(.mtninf) helpers, the RES table and the XPCK (.pck) codec — is pure-Python
and bpy-free. This loader pulls in exactly those leaf modules from an existing
studio_eleven install, without ever executing the bpy-dependent __init__ files.

Nothing is copied or modified on disk: we import the user's own files.

Usage:
    from se_loader import load_studio_eleven
    se = load_studio_eleven(r"C:\\path\\to\\studio_eleven")
    am = se.animation_manager.AnimationManager(reader=...)
"""

import os
import sys
import glob
import types
import importlib.util


class SEModules:
    """Thin namespace holding the loaded studio_eleven leaf modules."""
    def __init__(self, animation_manager, animation_support, minf, res=None, xpck=None):
        self.animation_manager = animation_manager
        self.animation_support = animation_support
        self.minf = minf
        self.res = res
        self.xpck = xpck


# Candidate roots to search when the caller doesn't pass an explicit path.
def _candidate_roots():
    roots = []
    env = os.environ.get("STUDIO_ELEVEN")
    if env:
        roots.append(env)

    appdata = os.environ.get("APPDATA")
    if appdata:
        # Blender add-ons: .../Blender/<ver>/scripts/addons/<addon>
        pattern = os.path.join(appdata, "Blender Foundation", "Blender", "*",
                               "scripts", "addons", "*")
        roots.extend(glob.glob(pattern))

    # A local vendored copy sitting next to this file (optional fallback).
    here = os.path.dirname(os.path.abspath(__file__))
    roots.append(os.path.join(here, "studio_eleven"))
    return roots


def _looks_like_se(root):
    return os.path.isfile(os.path.join(root, "formats", "animation_manager.py"))


def find_studio_eleven(explicit=None):
    """Return the studio_eleven root dir, or raise with a helpful message."""
    if explicit:
        if _looks_like_se(explicit):
            return explicit
        raise FileNotFoundError(
            f"'{explicit}' does not contain formats/animation_manager.py — "
            "point --se at the studio_eleven folder.")

    for root in _candidate_roots():
        if _looks_like_se(root):
            return root

    raise FileNotFoundError(
        "Could not locate studio_eleven. Pass --se <path>, or set the "
        "STUDIO_ELEVEN environment variable to the add-on folder "
        "(the one containing formats/animation_manager.py).")


def _stub_package(name, path, execute_init):
    """
    Register a package in sys.modules with __path__ set so its *sub-modules*
    are importable, but only run its __init__.py when execute_init is True.

    - animation/ and compression/ __init__.py are bpy-free and DEFINE the names
      that animation_manager relies on (`compress`, `compressor`, BoneLocation…),
      so we execute them.
    - formats/ and utils/ __init__.py pull in bpy (imgc, mesh_faces_utils…), so
      we leave them empty; the leaf modules we need are imported by file instead.
    """
    mod = types.ModuleType(name)
    mod.__path__ = [path]
    mod.__package__ = name
    sys.modules[name] = mod
    if execute_init:
        init = os.path.join(path, "__init__.py")
        with open(init, "r", encoding="utf-8") as fh:
            code = fh.read()
        exec(compile(code, init, "exec"), mod.__dict__)
    return mod


def _load_leaf(fullname, filepath):
    spec = importlib.util.spec_from_file_location(fullname, filepath)
    mod = importlib.util.module_from_spec(spec)
    sys.modules[fullname] = mod
    spec.loader.exec_module(mod)
    return mod


def load_studio_eleven(explicit=None, pkg="_se_vendor"):
    """
    Load studio_eleven's bpy-free format modules and return an SEModules object.

    `pkg` is the synthetic package name we register under; change it only if it
    collides with something already in sys.modules.
    """
    root = find_studio_eleven(explicit)

    # Register the package tree. compression/ and animation/ get their __init__
    # executed (bpy-free, they export the symbols we need); formats/ and utils/
    # are left empty so their bpy-heavy __init__ never runs.
    _stub_package(pkg, root, execute_init=False)
    _stub_package(pkg + ".utils", os.path.join(root, "utils"), execute_init=False)
    _stub_package(pkg + ".compression", os.path.join(root, "compression"), execute_init=True)
    _stub_package(pkg + ".animation", os.path.join(root, "animation"), execute_init=True)
    _stub_package(pkg + ".formats", os.path.join(root, "formats"), execute_init=False)

    fmt = os.path.join(root, "formats")
    # animation_support has no intra-package imports; load it first so
    # animation_manager's `from . import animation_support` resolves.
    animation_support = _load_leaf(pkg + ".formats.animation_support",
                                   os.path.join(fmt, "animation_support.py"))
    animation_manager = _load_leaf(pkg + ".formats.animation_manager",
                                   os.path.join(fmt, "animation_manager.py"))
    minf = _load_leaf(pkg + ".formats.minf", os.path.join(fmt, "minf.py"))

    res = xpck = None
    res_path = os.path.join(fmt, "res.py")
    if os.path.isfile(res_path):
        res = _load_leaf(pkg + ".formats.res", res_path)
    xpck_path = os.path.join(fmt, "xpck.py")
    if os.path.isfile(xpck_path):
        try:
            xpck = _load_leaf(pkg + ".formats.xpck", xpck_path)
        except Exception:
            xpck = None  # xpck may pull extra deps; packaging is optional here.

    return SEModules(animation_manager, animation_support, minf, res, xpck)
