#!/usr/bin/env python3
"""
ykport — Puni-Puni -> Yo-kai Watch 3 animation porter.

Automates the two tedious manual steps of an animation port:

  1. COMBINE: lay every individual animation clip (each exported to its own
     .mtn2 from Blender via studio_eleven) end-to-end onto a single timeline,
     and save ONE combined .mtn2 per group (p10/p20/p21/p84).

  2. SPLIT (.mtninf): emit one MINF file per clip, carrying the YW3 slot ID and
     the clip's [frame_start..frame_end] range inside the combined timeline,
     plus its playback speed.

You keep exporting each clip to its own .mtn2 in Blender (which you already do);
this tool replaces the by-hand "paste all animations into one" step and the
by-hand mtninf authoring.

Driven by a small JSON config — see config_example.json and the README.

    python ykport.py build myconfig.json
    python ykport.py slots            # print the YW3 slot-ID reference

Requires studio_eleven installed (for the .mtn2 codec). Pass --se <path> or set
the STUDIO_ELEVEN env var if it isn't auto-detected.
"""

import io
import os
import sys
import json
import zlib
import struct
import argparse

import slots as SLOTS
from se_loader import load_studio_eleven


# --------------------------------------------------------------------------
# MINF (.mtninf) writer — raw slot id
# --------------------------------------------------------------------------
def write_mtninf(slot_id_bytes, split_name, animation_name, frame_start,
                 frame_end, speed):
    """
    Build a MINF1 (.mtninf) blob (96 bytes), matching studio_eleven's layout
    but writing the YW3 slot ID *verbatim* instead of crc32(split_name).

    Layout (little-endian):
      0x00 'MINF'
      0x04 u32 0
      0x08 u32 0
      0x0C u32 0x1C   (offset to the data block below)
      0x10 u32 0
      0x14 u32 0x60   (total size = 96)
      0x18 u32 0
      0x1C  4  split_id   <- the YW3 animation slot ID (raw bytes)
      0x20 36  split_name (shift-jis, null-padded, max 36)
      0x44  4  anim_crc32 = crc32(animation_name)  (links to the combined mtn2)
      0x48 u32 0
      0x4C i32 frame_start
      0x50 i32 frame_end
      0x54 f32 speed
      0x58 u32 0
      0x5C u32 0
    """
    if len(slot_id_bytes) != 4:
        raise ValueError("slot id must be exactly 4 bytes")
    name = split_name[:36].encode("shift-jis")

    out = bytearray()
    out += b"MINF"
    out += struct.pack("<IIIII I", 0, 0, 0x1C, 0, 0x60, 0)  # 24 bytes -> 0x1C..
    out += bytes(slot_id_bytes)                              # 0x1C split_id
    out += name.ljust(36, b"\x00")                           # 0x20 name
    out += struct.pack("<I", zlib.crc32(animation_name.encode("shift-jis")) & 0xFFFFFFFF)
    out += struct.pack("<I", 0)
    out += struct.pack("<i", int(frame_start))
    out += struct.pack("<i", int(frame_end))
    out += struct.pack("<f", float(speed))
    out += struct.pack("<II", 0, 0)
    assert len(out) == 0x60, len(out)
    return bytes(out)


# --------------------------------------------------------------------------
# Combine clips onto one timeline
# --------------------------------------------------------------------------
class CombineResult:
    def __init__(self, mtn2_bytes, animation_name, frame_count, splits):
        self.mtn2_bytes = mtn2_bytes
        self.animation_name = animation_name
        self.frame_count = frame_count
        self.splits = splits  # list of dicts: file, start, end, ...


def combine_managers(AM, animation_name, clips, gap=1):
    """
    Core combine. `AM` is studio_eleven's animation_manager module; `clips` is a
    list of dicts each carrying an already-loaded AnimationManager under "manager"
    (plus slot/name/speed metadata). Clips are concatenated in list order; each
    occupies [start .. start + FrameCount], the next starting `gap` frames later
    so ranges never overlap. Returns a CombineResult.

    This is the shared kernel used by both the standalone CLI/GUI (which loads
    clips from .mtn2 files) and the Blender panel (which exports each action to
    an in-memory manager).
    """
    combined = AM.AnimationManager(Format="XMTN", Version="V2",
                                   AnimationName=animation_name,
                                   FrameCount=0, Tracks=[])
    track_by_idx = {}

    def get_track(idx, name):
        t = track_by_idx.get(idx)
        if t is None:
            t = AM.Track(name, idx, [])
            track_by_idx[idx] = t
            combined.Tracks.append(t)
        return t

    offset = 0
    splits = []
    for entry in clips:
        src = entry["manager"]
        start = offset
        end = offset + src.FrameCount
        for t in src.Tracks:
            ct = get_track(t.Index, t.Name)
            for n in t.Nodes:
                node = ct.GetNodeByName(n.Name)
                if node is None:
                    node = AM.Node(n.Name, n.isMainTrack, [])
                    ct.Nodes.append(node)
                for f in n.Frames:
                    node.add_frame(f.Key + offset, f.Value)

        splits.append({
            "file": entry.get("file"),
            "slot": entry["slot"],
            "name": entry.get("name") or entry.get("file", ""),
            "speed": float(entry.get("speed", 1.0)),
            "start": start,
            "end": end,
        })
        offset = end + gap

    combined.FrameCount = max(0, offset - gap)
    combined.Tracks.sort(key=lambda t: t.Index)
    blob = combined.Save()
    return CombineResult(blob, animation_name, combined.FrameCount, splits)


def combine_group(se, animation_name, clip_entries, clips_dir, gap=1):
    """Load clips from .mtn2 files then combine. clip_entries: {file, slot, name, speed}."""
    AM = se.animation_manager
    clips = []
    for entry in clip_entries:
        path = os.path.join(clips_dir, entry["file"])
        if not os.path.isfile(path):
            raise FileNotFoundError(f"clip not found: {path}")
        mgr = AM.AnimationManager(reader=io.BytesIO(open(path, "rb").read()))
        clips.append({**entry, "manager": mgr,
                      "name": entry.get("name", os.path.splitext(entry["file"])[0])})
    return combine_managers(AM, animation_name, clips, gap=gap)


# --------------------------------------------------------------------------
# Package into a _pXX.xc using a vanilla donor archive
# --------------------------------------------------------------------------
# Offsets inside a MINF (.mtninf) record — see write_mtninf.
MINF_SPLIT_ID = 0x1C
MINF_FSTART = 0x4C
MINF_FEND = 0x50
MINF_SPEED = 0x54


def _mtn2_key(files):
    for k in files:
        if k.lower().endswith(".mtn2"):
            return k
    raise KeyError("donor .xc has no .mtn2 entry")


def _l5_store(data):
    """Wrap raw bytes as a Level-5 'method 0' (uncompressed) block that
    compressor.decompress() reads back verbatim. Used to re-emit a patched RES."""
    return struct.pack("<I", (len(data) << 3) | 0) + bytes(data)


def _relabel_res(se, res_bytes, id_remap):
    """Rewrite slot ids inside RES.bin (each id appears once). Decompress, byte-
    replace old->new, re-emit uncompressed. id_remap: {old4 -> new4}."""
    dec = bytearray(se.xpck.compressor.decompress(res_bytes))
    for old, new in id_remap.items():
        idx = dec.find(old)
        if idx >= 0:
            dec[idx:idx + 4] = new
    return _l5_store(bytes(dec))


def package_xc(se, donor_path, combined_mtn2, slot_ranges, fallback_range, out_path,
               id_remap=None):
    """
    Donor-template packaging: open a vanilla _pXX.xc, swap in our combined mtn2,
    and repoint every .mtninf's frame range to OUR timeline. RES.bin and .cmn are
    kept (RES ids relabelled if id_remap given).

    slot_ranges: dict {4-byte slot id -> (start, end, speed)} keyed by the FINAL
      (post-remap) slot id.
    id_remap: optional {old 4-byte id -> new 4-byte id}. When set, each donor
      mtninf's split id is rewritten and the RES ids are relabelled too — this is
      how a p84 is made from a p20 donor ("same archive, different hex ids").
    Returns (n_matched, n_fallback, list_of_unmatched_slot_hex).
    """
    files = dict(se.xpck.open_file(donor_path))
    mkey = _mtn2_key(files)
    files[mkey] = combined_mtn2  # our combined animation, named like the donor's

    matched = fallback = 0
    unmatched = []
    for k in list(files.keys()):
        if not k.lower().endswith(".mtninf"):
            continue
        rec = bytearray(files[k])
        sid = bytes(rec[MINF_SPLIT_ID:MINF_SPLIT_ID + 4])
        new_sid = id_remap.get(sid, sid) if id_remap else sid
        if new_sid != sid:
            rec[MINF_SPLIT_ID:MINF_SPLIT_ID + 4] = new_sid
        rng = slot_ranges.get(new_sid) or slot_ranges.get(sid)
        if rng is None:
            rng = fallback_range
            fallback += 1
            unmatched.append(" ".join("%02X" % x for x in new_sid))
        else:
            matched += 1
        start, end, speed = rng
        struct.pack_into("<i", rec, MINF_FSTART, int(start))
        struct.pack_into("<i", rec, MINF_FEND, int(end))
        struct.pack_into("<f", rec, MINF_SPEED, float(speed))
        files[k] = bytes(rec)

    if id_remap:
        res_key = next((k for k in files if k.lower() == "res.bin"), None)
        if res_key:
            files[res_key] = _relabel_res(se, files[res_key], id_remap)

    se.xpck.pack_archive(files, out_path)
    return matched, fallback, unmatched


# --------------------------------------------------------------------------
# Build from config
# --------------------------------------------------------------------------
def full_split_name(model_id, name):
    if model_id and not name.startswith(model_id):
        return f"{model_id}_{name}"
    return name


def _group_spec(entries):
    """Accept both the list form (loose files only) and the object form
    {donor, fallback, clips} (produces a packaged .xc)."""
    if isinstance(entries, dict):
        return entries.get("clips", []), entries.get("donor"), entries.get("fallback", "idle")
    return entries, None, "idle"


def build_from_cfg(cfg, base_dir, se, log=print):
    """
    Run the whole port from an in-memory config dict. Used by both the CLI and
    the GUI. `base_dir` resolves relative donor paths; `log` is a callable that
    receives progress lines. Returns a list of per-group result dicts.
    """
    model_id = cfg.get("model_id", "")
    clips_dir = cfg["clips_dir"]
    out_dir = cfg.get("output_dir", "out")
    gap = int(cfg.get("gap", 1))
    os.makedirs(out_dir, exist_ok=True)
    results = []

    for group, entries in cfg["groups"].items():
        clips, donor, fallback_role = _group_spec(entries)
        if not clips:
            continue

        # normalise slot strings -> bytes; allow role keys from slots.py tables
        table = SLOTS.GROUPS.get(group, {})
        norm = []
        for e in clips:
            slot = e["slot"]
            if slot in table:            # a role key like "attack"
                slot = table[slot]
            norm.append({**e, "slot": slot})

        # The combined animation must be named like the donor's mtn2 (e.g.
        # "out_00") so each donor mtninf's anim_crc32 keeps matching it.
        if donor:
            dfiles = se.xpck.open_file(_resolve(donor, base_dir))
            dm = se.animation_manager.AnimationManager(
                reader=io.BytesIO(dfiles[_mtn2_key(dfiles)]))
            anim_name = dm.AnimationName
        else:
            anim_name = cfg.get("animation_names", {}).get(
                group, full_split_name(model_id, group))

        res = combine_group(se, anim_name, norm, clips_dir, gap=gap)

        mtn2_path = os.path.join(out_dir, f"{full_split_name(model_id, group)}.mtn2")
        with open(mtn2_path, "wb") as fh:
            fh.write(res.mtn2_bytes)

        minf_dir = os.path.join(out_dir, group)
        os.makedirs(minf_dir, exist_ok=True)
        log(f"[{group}] {anim_name!r}  ({res.frame_count} frames)  -> {mtn2_path}")

        # slot -> range map + loose .mtninf output
        slot_ranges = {}
        fallback_range = None
        for sp in res.splits:
            slot_b = SLOTS.slot_bytes(sp["slot"])
            rng = (sp["start"], sp["end"], sp["speed"])
            slot_ranges[slot_b] = rng
            if sp.get("name", "").endswith(fallback_role) or sp["slot"] == table.get(fallback_role):
                fallback_range = rng
            split_name = full_split_name(model_id, sp["name"])
            blob = write_mtninf(slot_b, split_name, anim_name,
                                sp["start"], sp["end"], sp["speed"])
            with open(os.path.join(minf_dir, f"{split_name}.mtninf"), "wb") as fh:
                fh.write(blob)
            log(f"    {sp['slot']:11}  [{sp['start']:>4}..{sp['end']:>4}]  "
                f"x{sp['speed']:<4}  {split_name}")

        entry = {"group": group, "frame_count": res.frame_count, "mtn2": mtn2_path}
        if donor:
            if fallback_range is None:
                fallback_range = next(iter(slot_ranges.values()))
            xc_path = os.path.join(out_dir, f"{full_split_name(model_id, group)}.xc")
            m, f, un = package_xc(se, _resolve(donor, base_dir), res.mtn2_bytes,
                                  slot_ranges, fallback_range, xc_path)
            log(f"    -> packaged {os.path.basename(xc_path)}  "
                f"({m} slots mapped, {f} fell back to '{fallback_role}')")
            if un:
                log(f"       fallback slots: {', '.join(un)}")
            entry.update(xc=xc_path, mapped=m, fell_back=f)
        results.append(entry)

    log("\nDone.")
    return results


def cmd_build(args):
    with open(args.config, "r", encoding="utf-8") as fh:
        cfg = json.load(fh)
    se = load_studio_eleven(cfg.get("studio_eleven") or args.se)
    build_from_cfg(cfg, os.path.dirname(os.path.abspath(args.config)), se)


def _resolve(path, base_dir):
    """Resolve a donor path relative to base_dir if it isn't absolute/existing."""
    if not path:
        return path
    if os.path.isabs(path) or os.path.exists(path):
        return path
    cand = os.path.join(base_dir, path)
    return cand if os.path.exists(cand) else path


def cmd_slots(args):
    for name, table in SLOTS.GROUPS.items():
        print(f"\n# {name.upper()}")
        for role, hexid in table.items():
            print(f"  {role:16} {hexid}")


def main():
    ap = argparse.ArgumentParser(description="Puni-Puni -> YW3 animation porter")
    ap.add_argument("--se", help="path to studio_eleven (auto-detected if omitted)")
    sub = ap.add_subparsers(dest="cmd", required=True)

    b = sub.add_parser("build", help="combine clips + emit .mtninf from a config")
    b.add_argument("config")
    b.set_defaults(func=cmd_build)

    s = sub.add_parser("slots", help="print the YW3 slot-ID reference tables")
    s.set_defaults(func=cmd_slots)

    args = ap.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
