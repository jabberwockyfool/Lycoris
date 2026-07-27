"""
ykport_blender — combine a whole .blend of animations into YW3 _pXX.xc files.

Run this INSIDE Blender (Scripting tab -> Open -> Run Script, once per session).
It adds a panel: 3D View -> Sidebar (press N) -> "YW3 Port" tab.

What it does, in one click:
  * groups every Action on the selected armature by its pXX (from the action
    name, e.g. y432000_p20_21000s_sti -> group p20, slot battle_start),
  * exports each action to an in-memory .mtn2 (via studio_eleven's exporter),
  * combines them onto ONE timeline per group,
  * generates the .mtninf splits, and
  * packages a ready _pXX.xc using a vanilla donor .xc as template.

Requirements: studio_eleven add-on installed AND enabled (this script borrows its
XMTN exporter + xpck codec from memory). Set the donor .xc for each group you use.

Action naming: the group and slot are read from the action name. Names ending in
a known canonical suffix are recognised (see slots.PUNIPUNI_NAMES / role keys).
"""

import os
import sys
import io

import bpy

# --- locate the ykport helpers (slots.py / ykport.py live in tools/punipuni) ---
# In Blender __file__ points at wherever you saved THIS script, which may not be
# the tools/punipuni folder. So we probe several candidates and pick the one that
# actually contains slots.py. If you moved the repo, edit the hard-coded path.
_CANDIDATES = []
try:
    _CANDIDATES.append(os.path.dirname(os.path.abspath(__file__)))
except NameError:
    pass
_CANDIDATES += [
    r"D:\cc\Lycoris\Lycoris\tools\punipuni",
    os.getcwd(),
]
YKPORT_DIR = next((d for d in _CANDIDATES
                   if os.path.isfile(os.path.join(d, "slots.py"))), None)
if YKPORT_DIR is None:
    raise RuntimeError(
        "Can't find slots.py / ykport.py. Put ykport_blender.py in the SAME "
        "folder as slots.py (…\\Lycoris\\tools\\punipuni), or fix the hard-coded "
        "path in _CANDIDATES near the top of this script.")
if YKPORT_DIR not in sys.path:
    sys.path.insert(0, YKPORT_DIR)

import importlib               # noqa: E402
import slots as SLOTS          # noqa: E402
import ykport                  # noqa: E402
# Re-running the script in the same Blender session must pick up edits to these.
importlib.reload(SLOTS)
importlib.reload(ykport)


# --------------------------------------------------------------------------
# Borrow studio_eleven's already-imported modules (robust to the add-on's name)
# --------------------------------------------------------------------------
def find_se():
    am = asupp = minf = res = xpck = fio = None
    for name, mod in list(sys.modules.items()):
        if mod is None:
            continue
        try:
            if name.endswith("formats.animation_manager") and hasattr(mod, "AnimationManager"):
                am = mod
            elif name.endswith("formats.animation_support") and hasattr(mod, "Header"):
                asupp = mod
            elif name.endswith("formats.minf") and hasattr(mod, "write_minf1"):
                minf = mod
            elif name.endswith("formats.res") and hasattr(mod, "make_library"):
                res = mod
            elif name.endswith("formats.xpck") and hasattr(mod, "pack_archive"):
                xpck = mod
            elif name.endswith("fileio_animation_manager") and hasattr(mod, "fileio_write_xmtn"):
                fio = mod
        except Exception:
            pass
    if not (am and xpck and fio):
        raise RuntimeError(
            "studio_eleven not found. Install AND enable the studio_eleven add-on "
            "(Edit > Preferences > Add-ons) before running this.")

    class SE:
        pass
    se = SE()
    se.animation_manager = am
    se.animation_support = asupp
    se.minf = minf
    se.res = res
    se.xpck = xpck
    se.fileio = fio
    return se


# --------------------------------------------------------------------------
# Export helpers
# --------------------------------------------------------------------------
def normalize(mgr):
    """Shift a clip's frame keys so it starts at 0, and set FrameCount = length."""
    keys = [f.Key for t in mgr.Tracks for n in t.Nodes for f in n.Frames]
    if not keys:
        mgr.FrameCount = 0
        return
    lo = min(keys)
    if lo:
        for t in mgr.Tracks:
            for n in t.Nodes:
                for f in n.Frames:
                    f.Key -= lo
    mgr.FrameCount = max(keys) - lo


def export_action_to_manager(se, armature, action):
    """Assign an action, export it to .mtn2 bytes via studio_eleven, parse back."""
    if armature.animation_data is None:
        armature.animation_data_create()
    armature.animation_data.action = action

    scene = bpy.context.scene
    fr = action.frame_range
    scene.frame_start = int(fr[0])
    scene.frame_end = int(fr[1])

    bones = [b.name for b in armature.pose.bones]
    blob = se.fileio.fileio_write_xmtn(
        bpy.context, armature, action.name,
        transformations=["location", "rotation", "scale"],
        bones=bones)
    mgr = se.animation_manager.AnimationManager(reader=io.BytesIO(blob))
    normalize(mgr)
    return mgr


def model_id_from_actions(actions):
    """Longest common prefix up to the first pXX token, e.g. y432000."""
    for a in actions:
        g = SLOTS.parse_group(a.name)
        if g and ("_" + g) in a.name:
            return a.name.split("_" + g)[0]
    return ""


# --------------------------------------------------------------------------
# The build
# --------------------------------------------------------------------------
def donor_anim_name(se, donor_path):
    dfiles = se.xpck.open_file(donor_path)
    mk = ykport._mtn2_key(dfiles)
    return se.animation_manager.AnimationManager(reader=io.BytesIO(dfiles[mk])).AnimationName


def rename_mtn2(se, mtn2_bytes, new_name):
    """Re-save a combined mtn2 under a different AnimationName (for reuse across
    groups whose donor mtn2 has a different name)."""
    mgr = se.animation_manager.AnimationManager(reader=io.BytesIO(mtn2_bytes))
    if mgr.AnimationName == new_name:
        return mtn2_bytes
    mgr.AnimationName = new_name
    return mgr.Save()


def build_group_from_actions(se, group, group_actions, donor, arm, model_id, gap, speed, out_dir, log):
    table = SLOTS.GROUPS.get(group, {})
    # order actions by canonical slot order, then any recognised leftovers
    ordered, used = [], set()
    for role, slot_hex in table.items():
        for a in group_actions:
            if a not in used and SLOTS.resolve_slot(group, a.name) == slot_hex:
                ordered.append((a, slot_hex)); used.add(a); break
    for a in group_actions:
        if a not in used:
            s = SLOTS.resolve_slot(group, a.name)
            if s:
                ordered.append((a, s)); used.add(a)
            else:
                log(f"    ! '{a.name}' — slot not recognised, skipped")
    if not ordered:
        log(f"[{group}] no recognised actions — skipped")
        return None

    anim_name = donor_anim_name(se, donor)
    clips = [{"manager": export_action_to_manager(se, arm, a), "slot": slot_hex,
              "name": a.name, "speed": speed} for a, slot_hex in ordered]
    res = ykport.combine_managers(se.animation_manager, anim_name, clips, gap=gap)
    log(f"[{group}] {anim_name!r} — {len(clips)} clips, {res.frame_count} frames")

    slot_ranges, fallback = {}, None
    role_ranges = {}
    for sp in res.splits:
        rng = (sp["start"], sp["end"], sp["speed"])
        slot_ranges[SLOTS.slot_bytes(sp["slot"])] = rng
        role = SLOTS.role_of_slot(sp["slot"])
        if role:
            role_ranges[role] = rng
        if sp["slot"] == table.get("idle"):
            fallback = rng
        log(f"    {sp['slot']:11} [{sp['start']:>4}..{sp['end']:>4}]  {sp['name']}")
    if fallback is None:
        fallback = next(iter(slot_ranges.values()))

    xc = os.path.join(out_dir, f"{ykport.full_split_name(model_id, group)}.xc")
    m, f, _ = ykport.package_xc(se, donor, res.mtn2_bytes, slot_ranges, fallback, xc)
    log(f"    -> {os.path.basename(xc)}  ({m} mapped, {f} -> idle fallback)")
    return {"mtn2": res.mtn2_bytes, "anim_name": anim_name, "role_ranges": role_ranges}


def build_group_by_reuse(se, group, donor, source, model_id, out_dir, log):
    """Fill p10/p84 (no source anims) from a real donor of that group, mapping
    each donor slot to a p20 animation by reading the donor's split NAMES
    (立ち/こうげき/ダメージ/死/ひっさつ/勝利…). Donor names are authoritative, so
    this doesn't depend on hard-coded id tables."""
    src_roles = source["role_ranges"]
    if not src_roles:
        log(f"[{group}] reuse skipped — source group has no ranges")
        return False

    mtn2 = rename_mtn2(se, source["mtn2"], donor_anim_name(se, donor))
    fallback = src_roles.get("idle") or next(iter(src_roles.values()))

    slot_ranges = {}
    for k, data in se.xpck.open_file(donor).items():
        if not k.lower().endswith(".mtninf"):
            continue
        sid = bytes(data[0x1C:0x20])
        try:
            name = data[0x20:0x44].split(b"\x00")[0].decode("shift-jis")
        except Exception:
            name = ""
        srole = SLOTS.donor_source_role(name)
        rng = src_roles.get(srole) if srole else None
        if rng:
            slot_ranges[sid] = rng

    xc = os.path.join(out_dir, f"{ykport.full_split_name(model_id, group)}.xc")
    m, f, _ = ykport.package_xc(se, donor, mtn2, slot_ranges, fallback, xc)
    log(f"[{group}] reused p20 anims (by donor names) — {os.path.basename(xc)}  "
        f"({m} mapped, {f} -> idle fallback)")
    return True


def build_group_by_relabel(se, group, p20_donor, source, model_id, out_dir, log):
    """No dedicated donor for `group` (e.g. p84): build it FROM the p20 donor by
    relabelling the slot ids — 'a p84 is a p20 with the hex ids changed'. Same
    mtn2 / cmn / structure; only the overlapping-role ids (and their ranges) are
    swapped to this group's ids. Slots unique to this group (walk/run/… for p84)
    aren't present — they'd need their own donor."""
    reuse = SLOTS.REUSE_FROM_P20.get(group)
    if not reuse:
        return False
    src_roles = source["role_ranges"]
    if not src_roles:
        return False
    table = SLOTS.GROUPS.get(group, {})
    combined = {**SLOTS.P21, **SLOTS.P20}   # ids present in the p20 donor, by role
    fallback = src_roles.get("idle") or next(iter(src_roles.values()))

    id_remap = {}                            # p20/p21 id -> this group's id (same role)
    for role in table:
        if role in combined:
            id_remap[SLOTS.slot_bytes(combined[role])] = SLOTS.slot_bytes(table[role])
    slot_ranges = {}                         # keyed by this group's (new) id
    for target_role, src_role in reuse.items():
        hx = table.get(target_role)
        if hx:
            slot_ranges[SLOTS.slot_bytes(hx)] = src_roles.get(src_role) or fallback

    xc = os.path.join(out_dir, f"{ykport.full_split_name(model_id, group)}.xc")
    m, f, _ = ykport.package_xc(se, p20_donor, source["mtn2"], slot_ranges,
                                fallback, xc, id_remap=id_remap)
    log(f"[{group}] built from p20 donor (ids relabelled) — {os.path.basename(xc)}  "
        f"({m} mapped, {f} -> idle fallback)")
    return True


def run_build(props, log):
    se = find_se()

    arm = bpy.context.active_object
    if arm is None or arm.type != "ARMATURE":
        arm = next((o for o in bpy.context.scene.objects if o.type == "ARMATURE"), None)
    if arm is None:
        raise RuntimeError("No armature found. Select your rig first.")

    actions = list(bpy.data.actions)
    if not actions:
        raise RuntimeError("No actions in this .blend.")

    model_id = props.model_id.strip() or model_id_from_actions(actions)
    out_dir = bpy.path.abspath(props.output_dir) or os.path.join(YKPORT_DIR, "out")
    os.makedirs(out_dir, exist_ok=True)
    gap = int(props.gap)
    speed = float(props.speed)
    donors = {"p10": props.donor_p10, "p20": props.donor_p20,
              "p21": props.donor_p21, "p84": props.donor_p84}

    def donor_path(g):
        d = donors.get(g)
        return bpy.path.abspath(d) if d else ""

    log(f"Armature: {arm.name} | model_id: {model_id or '(none)'} | {len(actions)} actions")

    # group actions by pXX
    by_group = {}
    for a in actions:
        g = SLOTS.parse_group(a.name)
        if g:
            by_group.setdefault(g, []).append(a)

    # -- pass 1: groups that HAVE actions --
    built = {}
    made = 0
    for group, group_actions in by_group.items():
        donor = donor_path(group)
        if not donor or not os.path.isfile(donor):
            log(f"[{group}] {len(group_actions)} actions — SKIPPED (no donor .xc set)")
            continue
        result = build_group_from_actions(se, group, group_actions, donor, arm,
                                          model_id, gap, speed, out_dir, log)
        if result:
            built[group] = result
            made += 1

    # -- pass 2: fill p10/p84 (no source anims) from the p20 build --
    if props.reuse_missing:
        p20_donor = donor_path("p20")
        for group in ("p10", "p84"):
            if group in built:
                continue
            if "p20" not in built:
                if donor_path(group) or (group == "p84" and p20_donor):
                    log(f"[{group}] skipped — build p20 first (needs p20 actions + donor)")
                continue
            donor = donor_path(group)
            if donor and os.path.isfile(donor):
                # dedicated donor for this group -> reuse p20 ranges with it
                if build_group_by_reuse(se, group, donor, built["p20"], model_id, out_dir, log):
                    made += 1
            elif p20_donor and os.path.isfile(p20_donor):
                # no donor -> derive from the p20 donor by relabelling ids
                if build_group_by_relabel(se, group, p20_donor, built["p20"], model_id, out_dir, log):
                    made += 1

    log(f"\nDone. {made} .xc file(s) written to {out_dir}")


# --------------------------------------------------------------------------
# Blender UI
# --------------------------------------------------------------------------
class YKPORT_Props(bpy.types.PropertyGroup):
    output_dir: bpy.props.StringProperty(name="Output dir", subtype="DIR_PATH")
    model_id: bpy.props.StringProperty(name="Model ID", description="Auto from action names if empty")
    gap: bpy.props.IntProperty(name="Gap", default=1, min=0, max=30)
    speed: bpy.props.FloatProperty(name="Speed", default=0.5, min=0.01, max=10.0)
    reuse_missing: bpy.props.BoolProperty(
        name="Fill p10/p84 from p20",
        description="If p10/p84 have no actions, reuse the p20 animations for their slots",
        default=True)
    donor_p10: bpy.props.StringProperty(name="Donor p10", subtype="FILE_PATH")
    donor_p20: bpy.props.StringProperty(name="Donor p20", subtype="FILE_PATH")
    donor_p21: bpy.props.StringProperty(name="Donor p21", subtype="FILE_PATH")
    donor_p84: bpy.props.StringProperty(name="Donor p84", subtype="FILE_PATH")


class YKPORT_OT_export(bpy.types.Operator):
    bl_idname = "ykport.export"
    bl_label = "Export YW3 animation pack"
    bl_description = "Combine all actions into _pXX.xc files"

    def execute(self, context):
        lines = []

        def log(m):
            print(m)
            lines.append(m)
        try:
            run_build(context.scene.ykport, log)
            self.report({"INFO"}, "ykport: done — see System Console for details")
        except Exception as ex:
            import traceback
            traceback.print_exc()
            self.report({"ERROR"}, f"ykport: {ex}")
            return {"CANCELLED"}
        return {"FINISHED"}


class YKPORT_PT_panel(bpy.types.Panel):
    bl_label = "YW3 Animation Pack"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "YW3 Port"

    def draw(self, context):
        s = context.scene.ykport
        col = self.layout.column()
        col.prop(s, "output_dir")
        col.prop(s, "model_id")
        row = col.row()
        row.prop(s, "gap")
        row.prop(s, "speed")
        col.prop(s, "reuse_missing")
        col.separator()
        col.label(text="Donor .xc (per group):")
        col.prop(s, "donor_p10")
        col.prop(s, "donor_p20")
        col.prop(s, "donor_p21")
        col.prop(s, "donor_p84")
        col.separator()
        col.operator("ykport.export", icon="ARMATURE_DATA")


_classes = (YKPORT_Props, YKPORT_OT_export, YKPORT_PT_panel)


def register():
    for c in _classes:
        bpy.utils.register_class(c)
    bpy.types.Scene.ykport = bpy.props.PointerProperty(type=YKPORT_Props)


def unregister():
    del bpy.types.Scene.ykport
    for c in reversed(_classes):
        bpy.utils.unregister_class(c)


if __name__ == "__main__":
    try:
        unregister()
    except Exception:
        pass
    register()
    print("ykport: panel ready — View3D > Sidebar (N) > 'YW3 Port' tab")
