using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lycoris.Formats;
using VT = Lycoris.Formats.ValueType;

namespace Lycoris.Yokai
{
    /// <summary>
    /// Character-switch ("equip-transform", à la Enma → Enma Blade): equipping the switch item turns yo-kai
    /// FROM (the original) into TO (a custom/alternate form). Two parts, reverse-engineered from retail
    /// chara_ability_5.00.40 + chara_param:
    ///  1) chara_ability — the switch ability's EFF_DATA list gets one CHARA_ABILITY_CONFIG_INFO_EFF_DATA per
    ///     mapping: [0]=EffectID, [6]=2, [7]=ToParamID (custom), [8]=FromParamID (original); and the matching
    ///     CHARA_ABILITY_EFFECT_INFO (keyed by EffectID) has [1] = how many EFF_DATA load into it (incremented).
    ///  2) chara_param — a CHARA_SAME_KIND_INFO declares the two forms are the SAME character:
    ///     INFO[0]=ToParamID, nested CHARA_SAME_KIND_INFO_DATA[0]=FromParamID.
    /// The switch item + ability already exist in-game; this just registers a new FROM→TO pair on them.
    /// </summary>
    public static class CharaSwitch
    {
        /// <summary>The vanilla character-switch EffectID (the one Jibanyan/Enma switches use).</summary>
        public const int DefaultEffect = unchecked((int)0xC58E24C1);

        private const string EffData = "CHARA_ABILITY_CONFIG_INFO_EFF_DATA";
        private const string EffBeg = "CHARA_ABILITY_CONFIG_INFO_EFF_DATA_LIST_BEG";
        private const string EffEnd = "CHARA_ABILITY_CONFIG_INFO_EFF_DATA_LIST_END";
        private const string EffectInfo = "CHARA_ABILITY_EFFECT_INFO";
        private const int Eff_Id = 0, Eff_Unk6 = 6, Eff_To = 7, Eff_From = 8;
        private const int Fx_Id = 0, Fx_Count = 1;

        private const string Same = "CHARA_SAME_KIND_INFO";
        private const string SameBeg = "CHARA_SAME_KIND_INFO_LIST_BEG";
        private const string SameEnd = "CHARA_SAME_KIND_INFO_LIST_END";
        private const string SameData = "CHARA_SAME_KIND_INFO_DATA";
        private const string SameDataBeg = "CHARA_SAME_KIND_INFO_DATA_LIST_BEG";
        private const string SameDataEnd = "CHARA_SAME_KIND_INFO_DATA_LIST_END";

        /// <summary>Find chara_ability (the switch ability config) — the mod's copy first, else the reference.
        /// Skips hackslash_chara_ability.</summary>
        public static string FindAbilityFile(YokaiDatabase db)
        {
            foreach (var root in new[] { db?.ModFolder, db?.ReferenceFolder })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                string hit = SafeEnum(root, "chara_ability_*.cfg.bin")
                    .Where(p => Path.GetFileName(p).IndexOf("hackslash", StringComparison.OrdinalIgnoreCase) < 0)
                    .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (hit != null) return hit;
            }
            return null;
        }

        private static IEnumerable<string> SafeEnum(string root, string pat)
        {
            try { return Directory.EnumerateFiles(root, pat, SearchOption.AllDirectories); }
            catch { return Enumerable.Empty<string>(); }
        }

        /// <summary>Detect the switch EffectIDs already used (those with To+From EFF_DATA), with how many mappings
        /// each has — for the picker (the vanilla one is 0xC58E24C1).</summary>
        public static List<KeyValuePair<int, int>> DetectEffects(T2bFile ability)
        {
            var d = new Dictionary<int, int>();
            foreach (var e in ability.Entries)
                if (e.Name == EffData && GI(e, Eff_To) != 0 && GI(e, Eff_From) != 0)
                {
                    int id = GI(e, Eff_Id);
                    d[id] = d.TryGetValue(id, out int c) ? c + 1 : 1;
                }
            return d.OrderByDescending(kv => kv.Value).ToList();
        }

        /// <summary>The existing FROM→TO mappings registered on a switch effect (from=[8], to=[7]).</summary>
        public static List<int[]> Mappings(T2bFile ability, int eff)
        {
            var list = new List<int[]>();
            foreach (var e in ability.Entries)
                if (e.Name == EffData && GI(e, Eff_Id) == eff && (GI(e, Eff_To) != 0 || GI(e, Eff_From) != 0))
                    list.Add(new[] { GI(e, Eff_From), GI(e, Eff_To) });
            return list;
        }

        public static bool SwitchExists(T2bFile ability, int eff, int from, int to) =>
            ability.Entries.Any(e => e.Name == EffData && GI(e, Eff_Id) == eff && GI(e, Eff_From) == from && GI(e, Eff_To) == to);

        /// <summary>Add a FROM→TO mapping to the switch ability's EFF_DATA list and bump its EFFECT_INFO count.</summary>
        public static void AddToAbility(T2bFile ability, int eff, int from, int to)
        {
            var idxs = new List<int>();
            for (int i = 0; i < ability.Entries.Count; i++)
                if (ability.Entries[i].Name == EffData && GI(ability.Entries[i], Eff_Id) == eff) idxs.Add(i);
            if (idxs.Count == 0)
                throw new InvalidOperationException($"Switch effect 0x{unchecked((uint)eff):X8} not found in chara_ability (no existing To/From entry to extend).");

            int first = idxs[0], last = idxs[idxs.Count - 1];
            int endIdx = -1; for (int i = last + 1; i < ability.Entries.Count; i++) if (ability.Entries[i].Name == EffEnd) { endIdx = i; break; }
            int begIdx = -1; for (int i = first - 1; i >= 0; i--) if (ability.Entries[i].Name == EffBeg) { begIdx = i; break; }
            if (endIdx < 0 || begIdx < 0)
                throw new InvalidOperationException("Could not locate the switch effect's EFF_DATA list markers.");

            var e2 = ability.Entries[first].Clone();
            for (int k = 0; k < e2.Values.Count; k++) { e2.Values[k].Type = VT.Integer; e2.Values[k].Value = 0; }
            SetI(e2, Eff_Id, eff); SetI(e2, Eff_Unk6, 2); SetI(e2, Eff_To, to); SetI(e2, Eff_From, from);
            ability.Entries.Insert(endIdx, e2);   // before the list END
            Bump(ability, begIdx);                // the enclosing list's count

            var fx = ability.Records(EffectInfo).FirstOrDefault(x => GI(x, Fx_Id) == eff);
            if (fx != null && Fx_Count < fx.Values.Count && fx.Values[Fx_Count].Value is int c)
            { fx.Values[Fx_Count].Type = VT.Integer; fx.Values[Fx_Count].Value = c + 1; }
        }

        /// <summary>Declare the two forms as the SAME character in chara_param: INFO[0]=<paramref name="infoId"/>
        /// (the ORIGINAL / equip-on form), nested DATA[0]=<paramref name="dataId"/> (the CUSTOM / transformed form).</summary>
        public static void AddSameKind(T2bFile param, int infoId, int dataId)
        {
            var info = Clone(param, Same); SetI(info, 0, infoId);
            var dbeg = Clone(param, SameDataBeg); SetI(dbeg, 0, 1);
            var data = Clone(param, SameData); SetI(data, 0, dataId);
            var dend = Clone(param, SameDataEnd);

            int endIdx = param.Entries.FindIndex(x => x.Name == SameEnd);
            if (endIdx < 0) throw new InvalidOperationException("chara_param has no CHARA_SAME_KIND_INFO_LIST_END.");
            param.Entries.InsertRange(endIdx, new[] { info, dbeg, data, dend });

            int begIdx = param.Entries.FindIndex(x => x.Name == SameBeg);
            if (begIdx >= 0) Bump(param, begIdx);
        }

        public static bool SameKindExists(T2bFile param, int infoId)
        {
            return param.Records(Same).Any(i => GI(i, 0) == infoId);   // a group already headed by this form
        }

        // ---- removal (undo a switch) ----

        /// <summary>Remove a FROM→TO mapping from the switch ability's EFF_DATA and decrement its EFFECT_INFO count.</summary>
        public static bool RemoveFromAbility(T2bFile ability, int eff, int from, int to)
        {
            var hit = ability.Entries.FirstOrDefault(e => e.Name == EffData && GI(e, Eff_Id) == eff && GI(e, Eff_From) == from && GI(e, Eff_To) == to);
            if (hit == null) return false;
            int idx = ability.Entries.IndexOf(hit);
            // decrement the enclosing EFF_DATA list count.
            for (int i = idx - 1; i >= 0; i--) if (ability.Entries[i].Name == EffBeg) { BumpEntry(ability.Entries[i], 0, -1); break; }
            ability.Entries.Remove(hit);
            var fx = ability.Records(EffectInfo).FirstOrDefault(x => GI(x, Fx_Id) == eff);
            if (fx != null && Fx_Count < fx.Values.Count && fx.Values[Fx_Count].Value is int c && c > 0)
            { fx.Values[Fx_Count].Type = VT.Integer; fx.Values[Fx_Count].Value = c - 1; }
            return true;
        }

        /// <summary>Remove the CHARA_SAME_KIND_INFO group headed by <paramref name="infoId"/> (its INFO + nested DATA list).</summary>
        public static bool RemoveSameKind(T2bFile param, int infoId)
        {
            var info = param.Records(Same).FirstOrDefault(i => GI(i, 0) == infoId);
            if (info == null) return false;
            int idx = param.Entries.IndexOf(info);
            int endIdx = -1;
            for (int i = idx + 1; i < param.Entries.Count; i++)
            {
                string n = param.Entries[i].Name;
                if (n == SameDataEnd) { endIdx = i; break; }
                if (n == Same || n == SameEnd) { endIdx = i - 1; break; }   // no nested data list
            }
            if (endIdx < 0) endIdx = idx;
            for (int i = endIdx; i >= idx; i--) param.Entries.RemoveAt(i);
            var beg = param.Entries.FirstOrDefault(x => x.Name == SameBeg);
            BumpEntry(beg, 0, -1);
            return true;
        }

        private static void BumpEntry(T2bEntry b, int idx, int d)
        {
            if (b != null && idx < b.Values.Count && b.Values[idx].Value is int c) { b.Values[idx].Type = VT.Integer; b.Values[idx].Value = c + d; }
        }

        // ---- item equip condition (who can equip the switch item) ----

        private const string ItemEquip = "ITEM_EQUIPMENT";
        private const string Cond = "ITEM_EQUIP_COND";
        private const string RefCondChara = "ITEM_EQUIP_COND_REF_COND_CHARA";
        private const string CondChara = "ITEM_EQUIP_COND_CHARA";
        private const string CondCharaBeg = "ITEM_EQUIP_COND_CHARA_LIST_BEG";
        private const string CondCharaEnd = "ITEM_EQUIP_COND_CHARA_LIST_END";
        private const int Item_CondIndex = 17;   // ITEM_EQUIPMENT[17] = ITEM_EQUIP_COND id
        private const int Ref_Start = 0, Ref_Count = 1;

        /// <summary>Find item_config (mod first, else reference).</summary>
        public static string FindItemConfig(YokaiDatabase db)
        {
            foreach (var root in new[] { db?.ModFolder, db?.ReferenceFolder })
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                string hit = SafeEnum(root, "item_config*.cfg.bin").OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (hit != null) return hit;
            }
            return null;
        }

        /// <summary>Allow a yo-kai (by BASE hash) to equip an item: append it to the item's ITEM_EQUIP_COND chara
        /// range and shift every later range's start. Returns a message on failure, null on success/no-op.</summary>
        public static string AllowEquip(T2bFile item, int itemId, int baseHash)
        {
            if (!FindItemCond(item, itemId, out var refEntry, out int start, out int count, out string err)) return err;
            var charas = item.Records(CondChara).ToList();
            for (int i = start; i < start + count && i >= 0 && i < charas.Count; i++)
                if (GI(charas[i], 0) == baseHash) return null;   // already allowed

            int insertPos = start + count;
            var tpl = charas.FirstOrDefault();
            if (tpl == null) return "item_config has no ITEM_EQUIP_COND_CHARA template.";
            var ne = tpl.Clone(); SetI(ne, 0, baseHash);
            int entIdx = insertPos < charas.Count ? item.Entries.IndexOf(charas[insertPos]) : item.Entries.FindIndex(e => e.Name == CondCharaEnd);
            if (entIdx < 0) return "ITEM_EQUIP_COND_CHARA list end not found.";
            item.Entries.Insert(entIdx, ne);
            SetI(refEntry, Ref_Count, count + 1);
            foreach (var r in item.Records(RefCondChara)) if (!ReferenceEquals(r, refEntry) && GI(r, Ref_Start) >= insertPos) SetI(r, Ref_Start, GI(r, Ref_Start) + 1);
            BumpByName(item, CondCharaBeg, 0, +1);
            return null;
        }

        /// <summary>Remove a yo-kai (by BASE hash) from an item's equip-cond chara range.</summary>
        public static string DisallowEquip(T2bFile item, int itemId, int baseHash)
        {
            if (!FindItemCond(item, itemId, out var refEntry, out int start, out int count, out string err)) return err;
            var charas = item.Records(CondChara).ToList();
            int at = -1;
            for (int i = start; i < start + count && i >= 0 && i < charas.Count; i++)
                if (GI(charas[i], 0) == baseHash) { at = i; break; }
            if (at < 0) return null;   // not present — nothing to remove
            item.Entries.Remove(charas[at]);
            SetI(refEntry, Ref_Count, count - 1);
            foreach (var r in item.Records(RefCondChara)) if (!ReferenceEquals(r, refEntry) && GI(r, Ref_Start) > at) SetI(r, Ref_Start, GI(r, Ref_Start) - 1);
            BumpByName(item, CondCharaBeg, 0, -1);
            return null;
        }

        // ---- item equip SKILL (make the item GRANT the switch skill) ----
        // The switch item needs an ITEM_EQUIPMENT_REF_EQUIPMENT_SKILL right AFTER its ITEM_EQUIPMENT record
        // (positional link; the ref is an uncounted sub-record of ITEM_EQUIPMENT_LIST). [0]=StartPos (index into
        // ITEM_EQUIPMENT_SKILL, e.g. 5 = the character-switch skill 0x9A5F207E), [1]=Length (1).
        private const string RefEquipSkill = "ITEM_EQUIPMENT_REF_EQUIPMENT_SKILL";
        public const int DefaultEquipSkillStart = 5;   // ITEM_EQUIPMENT_SKILL[5] = the vanilla switch skill

        /// <summary>Give an item the switch skill: insert a REF_EQUIPMENT_SKILL[start,len] right after its
        /// ITEM_EQUIPMENT. No-op (updates in place) if one already follows. Returns a message on failure, null on ok.</summary>
        public static string AddEquipSkill(T2bFile item, int itemId, int start, int len)
        {
            var eq = item.Records(ItemEquip).FirstOrDefault(e => GI(e, 0) == itemId);
            if (eq == null) return $"Item 0x{unchecked((uint)itemId):X8} is not an ITEM_EQUIPMENT.";
            int ei = item.Entries.IndexOf(eq);
            if (ei + 1 < item.Entries.Count && item.Entries[ei + 1].Name == RefEquipSkill)
            { SetI(item.Entries[ei + 1], 0, start); SetI(item.Entries[ei + 1], 1, len); return null; }

            var tpl = item.Records(RefEquipSkill).FirstOrDefault();
            T2bEntry rec;
            if (tpl != null) { rec = tpl.Clone(); SetI(rec, 0, start); SetI(rec, 1, len); }
            else
            {
                var nm = item.Names.FirstOrDefault(n => n.Name == RefEquipSkill);
                if (nm.Name != RefEquipSkill) return "item_config has no ITEM_EQUIPMENT_REF_EQUIPMENT_SKILL to use as a template.";
                rec = new T2bEntry { Name = RefEquipSkill, Crc = nm.Crc };
                rec.Values.Add(new T2bValue(VT.Integer, start));
                rec.Values.Add(new T2bValue(VT.Integer, len));
            }
            item.Entries.Insert(ei + 1, rec);   // uncounted sub-record → do NOT bump ITEM_EQUIPMENT_LIST_BEG
            return null;
        }

        /// <summary>Remove an item's granted skill (the REF_EQUIPMENT_SKILL right after its ITEM_EQUIPMENT).</summary>
        public static string DisallowEquipSkill(T2bFile item, int itemId)
        {
            var eq = item.Records(ItemEquip).FirstOrDefault(e => GI(e, 0) == itemId);
            if (eq == null) return null;
            int ei = item.Entries.IndexOf(eq);
            if (ei + 1 < item.Entries.Count && item.Entries[ei + 1].Name == RefEquipSkill)
                item.Entries.RemoveAt(ei + 1);
            return null;
        }

        private static bool FindItemCond(T2bFile item, int itemId, out T2bEntry refEntry, out int start, out int count, out string err)
        {
            refEntry = null; start = count = 0; err = null;
            var eq = item.Records(ItemEquip).FirstOrDefault(e => GI(e, 0) == itemId);
            if (eq == null) { err = $"Item 0x{unchecked((uint)itemId):X8} is not an ITEM_EQUIPMENT (only equipment items have equip conditions)."; return false; }
            if (eq.Values.Count <= Item_CondIndex) { err = "This item has no equip-cond field."; return false; }
            int condId = GI(eq, Item_CondIndex);
            var condEntry = item.Records(Cond).FirstOrDefault(e => GI(e, 0) == condId);
            if (condEntry == null) { err = $"The item's equip cond ({condId}) was not found."; return false; }
            int ci = item.Entries.IndexOf(condEntry);
            for (int i = ci + 1; i < item.Entries.Count; i++)
            {
                if (item.Entries[i].Name == RefCondChara) { refEntry = item.Entries[i]; break; }
                if (item.Entries[i].Name == Cond) break;
            }
            if (refEntry == null) { err = "This item's equip cond has no yo-kai list (it may already allow everyone) — no change needed."; return false; }
            start = GI(refEntry, Ref_Start); count = GI(refEntry, Ref_Count);
            return true;
        }

        private static void BumpByName(T2bFile f, string name, int idx, int d)
        {
            var b = f.Entries.FirstOrDefault(e => e.Name == name);
            BumpEntry(b, idx, d);
        }

        // ---- helpers ----
        private static T2bEntry Clone(T2bFile f, string name)
        {
            var t = f.Entries.FirstOrDefault(e => e.Name == name);
            if (t == null) throw new InvalidOperationException($"This chara_param has no {name} record to clone as a template (needs a vanilla file that already contains same-kind groups).");
            return t.Clone();
        }
        private static void Bump(T2bFile f, int begIdx)
        {
            var b = f.Entries[begIdx];
            if (b.Values.Count > 0 && b.Values[0].Value is int c) { b.Values[0].Type = VT.Integer; b.Values[0].Value = c + 1; }
        }
        private static int GI(T2bEntry e, int i) => i < e.Values.Count && e.Values[i].Value is int v ? v : 0;
        private static void SetI(T2bEntry e, int i, int v) { if (i < e.Values.Count) { e.Values[i].Type = VT.Integer; e.Values[i].Value = v; } }
    }
}
