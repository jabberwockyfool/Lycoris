"""
YW3 animation slot IDs, transcribed from the community animation map.

Each ID is stored as the 4 bytes exactly as they appear in the file (and as
listed by the user), e.g. "4A 09 C3 43". The MINF `split_crc32` field is written
verbatim from these bytes, so the game matches the animation to the right slot
regardless of the (cosmetic) split name.

`slot_id("4A 09 C3 43")` -> b'\\x4a\\x09\\xc3\\x43'.
"""


def slot_bytes(hexstr):
    """'4A 09 C3 43' -> bytes([0x4A,0x09,0xC3,0x43])."""
    parts = hexstr.replace(",", " ").split()
    return bytes(int(p, 16) for p in parts)


# --- P10: Overworld / NPC -------------------------------------------------
P10 = {
    "tpose":      "A8 5A 6A 85",
    "idle":       "4A 09 C3 43",
    "long_idle":  "44 6A 78 62",
    "talk":       "A5 CF E5 80",
    "walk":       "B4 27 F7 FF",
    "run":        "54 43 28 60",
    "unknown6":   "20 33 E6 11",
}

# --- P20: Normal battle ---------------------------------------------------
P20 = {
    "battle_start":     "58 DF 5B 84",
    "idle":             "2F 60 5C C8",
    "long_idle":        "17 94 B0 2D",
    "tired":            "23 8F 2C 41",
    "loaf":             "F8 E2 EE 80",
    "recovering":       "7E 15 40 AB",
    "attack":           "98 12 A9 9B",
    "magic":            "4D 03 C3 B7",
    "guard":            "B1 01 4E 48",
    "miss":             "CD 2A A7 DA",
    "damage":           "D6 D6 8B 14",
    "death":            "B1 85 81 B5",
    "ascension":        "A9 E0 BB 11",   # last frame of death
    "charge":           "04 B7 C0 F9",
    "soultimate_start": "54 53 5B 79",
    "soultimate":       "0A 8E 77 DE",
}

# --- P21: Victory dances --------------------------------------------------
P21 = {
    "victory1_start": "AB 00 7C 21",
    "victory1":       "56 E4 4D EC",
    "victory2_start": "11 51 75 B8",
    "victory2":       "95 B7 60 C7",
    "victory3_start": "87 61 72 CF",
    "victory3":       "D4 86 7B DE",
    "victory4_start": "24 F4 16 51",
    "victory4":       "13 10 3A 91",
}

# --- P84: Blasters T ------------------------------------------------------
# NOTE: this table came from a community text list and is NOT reliable — a real
# y152020_p84.xc showed several ids mean something else (e.g. C2 B2 3B F0 is a
# soultimate-end, not "walk"). The porter does NOT depend on it for p84: it maps
# donor slots by their split NAMES (see donor_source_role). Kept for reference.
P84 = {
    "walk":            "C2 B2 3B F0",
    "run":             "22 D6 E4 6F",
    "idle":            "3C 9C 0F 4C",
    "long_idle":       "B8 32 75 BC",
    "tired":           "30 73 7F C5",
    "recovering":      "6D D8 FD 46",
    "guard":           "47 7A AA 9D",
    "victory1":        "DE E7 1A 37",
    "damage":          "84 65 80 40",
    "death":           "76 16 E5 FB",
    "ascension":       "29 D3 F5 14",
    "victory2":        "B8 CD C1 CC",
    "victory3":        "45 18 1E 68",
    "charge":          "17 4B 93 7D",
    "attack":          "CA A1 A2 CF",
    "power_attack":    "09 A4 F3 67",
    "triple_hit2":     "70 F0 AB 56",
    "triple_hit3":     "E6 C0 AC 21",
    "dash_attack":     "85 CC 3B 21",
    "fallback_attack": "38 BC 48 02",
    "magic":           "A6 8A 26 6D",
    "soultimate":      "06 E0 50 2D",
    "miss":            "BF F1 3A D5",
}

GROUPS = {"p10": P10, "p20": P20, "p21": P21, "p84": P84}


# Puni-Puni yo-kai animation -> the exact YW3 slot it targets.
# Puni-Puni yo-kai have no walk/run. The victory start/loop map to the victory1
# slots, which live inside the p20 archive (confirmed in y152000_p20.xc).
PUNIPUNI_P20 = {
    "p20_21000s_sti": P20["battle_start"],
    "p20_21001d_stl": P20["idle"],
    "p20_24001d_trl": P20["tired"],
    "p20_24201s_lfl": P20["loaf"],
    "p20_25000s_ati": P20["attack"],
    "p20_26500d_dmi": P20["damage"],
    "p20_27000d_dei": P20["death"],
    "p20_27500d_wii": P21["victory1_start"],
    "p20_27501d_wil": P21["victory1"],
    "p20_29000s_spi": P20["soultimate"],
}

# group -> {canonical split-name suffix -> slot id}. Extend as more are mapped.
PUNIPUNI = {"p20": PUNIPUNI_P20}


# Puni-Puni yo-kai have NO overworld (p10) / Blasters-T (p84) animations. When
# those groups are requested, fill each of their slots by reusing a p20 battle
# animation. Maps: target group -> {target role -> source p20 role to borrow}.
# Source roles must be ones a Puni-Puni yo-kai actually has (idle/attack/damage/
# death/tired/soultimate/victory1…); anything missing falls back to idle.
REUSE_FROM_P20 = {
    "p10": {
        "tpose": "idle", "idle": "idle", "long_idle": "idle", "talk": "idle",
        "walk": "idle", "run": "idle", "unknown6": "idle",
    },
    "p84": {
        "walk": "idle", "run": "idle", "idle": "idle", "long_idle": "tired",
        "tired": "tired", "recovering": "idle", "guard": "idle",
        "victory1": "victory1", "victory2": "victory1", "victory3": "victory1",
        "damage": "damage", "death": "death", "ascension": "death",
        "charge": "idle", "attack": "attack", "power_attack": "attack",
        "triple_hit2": "attack", "triple_hit3": "attack", "dash_attack": "attack",
        "fallback_attack": "attack", "magic": "attack", "soultimate": "soultimate",
        "miss": "idle",
    },
}

# Reverse lookup: slot-id bytes -> role name (across battle + victory tables),
# so a built p20 result can be indexed back to role names for reuse.
def role_of_slot(slot_id_hex):
    for role, hx in {**P21, **P20}.items():
        if hx == slot_id_hex:
            return role
    return None


# Map a donor split NAME (JP or EN) to the p20 SOURCE role whose animation range
# to reuse. Donor .xc store readable names (e.g. 戦1立ち1L, こうげき, ダメージ),
# so this is more reliable than id tables (which vary between docs/versions).
# Order matters: most specific keywords first. Anything unmatched -> None -> idle.
_NAME_TO_P20 = [
    (["アイドリング", "long_idle", "longidle"], "idle"),
    (["会話", "talk"],                          "idle"),
    (["歩き", "walk"],                          "idle"),
    (["走り", "run", "dash"],                   "idle"),
    (["疲れ", "tired", "trl", "sleep"],         "tired"),
    (["さぼり", "loaf", "lfl"],                 "loaf"),
    (["喜び", "recover", "joy"],                "idle"),
    (["ようじゅつ", "妖術", "magic", "debuff", "buff"], "attack"),
    (["こうげき", "attack", "ati", "hit"],       "attack"),
    (["ガード", "guard"],                       "idle"),
    (["回避", "miss", "dodge", "evade"],        "idle"),
    (["ダメージ", "damage", "dmi"],             "damage"),
    (["ひっさつ", "必殺", "soul", "spi", "special"], "soultimate"),
    (["勝利", "victory", "win", "wii", "wil"],   "victory1"),
    (["ため", "charge"],                        "idle"),
    (["死", "death", "dei", "ascen"],           "death"),
    (["立ち", "idle", "stl", "stand", "sti"],    "idle"),
]


def donor_source_role(name):
    """Given a donor split name, return which p20 source role's range to reuse."""
    n = (name or "").lower()
    for keys, role in _NAME_TO_P20:
        for k in keys:
            if k.lower() in n:
                return role
    return None


def parse_group(name):
    """Extract the pXX group from an action/file name like 'y432000_p20_21000s_sti'."""
    import re
    m = re.search(r"(p10|p20|p21|p84)", name)
    return m.group(1) if m else None


def _is_raw_hex(s):
    parts = s.split()
    return len(parts) == 4 and all(len(p) == 2 and all(c in "0123456789abcdefABCDEF" for c in p) for p in parts)


# Extra spellings for role keys, so descriptive action names still map. The role
# key itself is always tried too; matching is longest-alias-first (below).
_ALIASES = {
    "tpose":            ["tpose", "t_pose", "t-pose", "tp"],
    "long_idle":        ["long_idle", "longidle", "lidle"],
    "soultimate_start": ["soultimate_start", "soul_start", "spi_start"],
    "soultimate":       ["soultimate", "soul", "spi"],
    "victory1_start":   ["victory1_start", "victory_start", "win_start", "wii"],
    "victory1":         ["victory1", "victory", "win", "wil"],
    "power_attack":     ["power_attack", "powerattack", "charge_attack", "tank"],
    "dash_attack":      ["dash_attack", "dashattack", "dash"],
    "fallback_attack":  ["fallback_attack", "fallback"],
    "triple_hit2":      ["triple_hit2", "hit2", "second_hit"],
    "triple_hit3":      ["triple_hit3", "hit3", "third_hit"],
    "battle_start":     ["battle_start", "start", "sti"],
    "ascension":        ["ascension", "ascend", "lastframe"],
    "recovering":       ["recovering", "recover", "heal"],
}


def resolve_slot(group, name):
    """
    Map an action/file name to a YW3 slot id (hex string), or None if unknown.
    Accepts: raw bytes '58 DF 5B 84', a role key ('attack'), a canonical coded
    name ('y432000_p20_21000s_sti'), or a descriptive name containing a role
    keyword ('y432000_p10_walk', 'Idle', 'p84_guard'...).
    """
    name = (name or "").strip()
    table = GROUPS.get(group, {})
    if _is_raw_hex(name):
        return name
    if name in table:                       # exact role key
        return table[name]
    for suffix, slot in PUNIPUNI.get(group, {}).items():   # coded canonical name
        if name.endswith(suffix) or suffix in name:
            return slot

    # keyword fallback: match a role (or one of its aliases) as a substring,
    # longest alias first so 'long_idle' wins over 'idle', 'power_attack' over
    # 'attack', 'victory1_start' over 'victory1', etc.
    norm = name.lower().replace("-", "_").replace(" ", "_")
    candidates = []
    for role in table:
        for alias in _ALIASES.get(role, [role]):
            candidates.append((alias.lower(), role))
    for alias, role in sorted(candidates, key=lambda x: len(x[0]), reverse=True):
        if alias in norm:
            return table[role]
    return None
