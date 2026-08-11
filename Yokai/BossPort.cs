using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lycoris.Formats;

namespace Lycoris.Yokai
{
    /// <summary>One YW2 boss attack, resolved to the fields a YW3 skill needs.</summary>
    public sealed class Yw2Attack
    {
        public string Name;
        public int Yw3Type;    // 1=Attack, 3=Technique, 4=Soultimate, 5=Inspirit
        public int Element;
        public int Power;
        public int Yw2CmdId;
    }

    /// <summary>A YW2 boss (chara_param entry with a BOSS_PARTS_INFO), with its stats and attacks.</summary>
    public sealed class Yw2BossInfo
    {
        public string ModelId { get; set; }
        public string Name { get; set; }
        public string Yw2Folder;
        public int Param;
        public int Hp, Str, Spr, Def, Spd, Money, Exp;
        public readonly List<Yw2Attack> Attacks = new List<Yw2Attack>();
    }

    /// <summary>
    /// Imports a boss from a YW2 dump into the loaded YW3 mod: copies the YW2 (mtn2) model, recreates the boss
    /// as a yo-kai with its stats, its attacks as YW3 skills + battle_commands, a BOSS_PARTS entry and a
    /// common_enc encounter. Read side is validated on the YW2 dump; the port reuses an existing YW3 base when
    /// the model already exists, and sets BOSS_PARTS[21]=0 so the boss uses its OWN moveset (not another boss's).
    /// </summary>
    public static class BossPort
    {
        // ---------------------------------------------------------------- file lookup

        /// <summary>Valid continuation after a prefix: end, ".", or "_"+digit (skips _link/_menu siblings).</summary>
        private static bool VariantSuffix(string fileName, string prefix)
        {
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = fileName.Substring(prefix.Length);
            return rest.Length == 0 || rest[0] == '.' || (rest[0] == '_' && rest.Length > 1 && char.IsDigit(rest[1]));
        }

        /// <summary>First .cfg.bin under <paramref name="root"/> whose name matches <paramref name="prefix"/>.</summary>
        private static string Find(string root, string prefix)
        {
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateFiles(root, "*.cfg.bin", SearchOption.AllDirectories)
                .Where(p => VariantSuffix(Path.GetFileName(p), prefix))
                .OrderBy(p => Path.GetFileName(p).Length)
                .FirstOrDefault();
        }

        /// <summary>Find a mod file (under the loaded mod folder) by prefix.</summary>
        private static string FindMod(YokaiDatabase db, string prefix) =>
            db.ModFolder != null ? Find(db.ModFolder, prefix) : null;

        private static string ModCharacterDir(YokaiDatabase db) =>
            Path.Combine(db.ModFolder, "data", "character");

        // ---------------------------------------------------------------- small helpers

        private static void SetInt(T2bEntry e, int index, int value)
        {
            while (e.Values.Count <= index) e.Values.Add(new T2bValue(Lycoris.Formats.ValueType.Integer, 0));
            e.Values[index] = new T2bValue(Lycoris.Formats.ValueType.Integer, value);
        }

        /// <summary>Insert a clone before the first record named <paramref name="beforeName"/> (fallback: append).</summary>
        private static void InsertBefore(T2bFile f, string beforeName, string groupBegin, T2bEntry entry)
        {
            int idx = f.Entries.FindIndex(e => e.Name == beforeName);
            if (idx < 0) idx = f.Entries.Count;
            f.Entries.Insert(idx, entry);
            BumpCount(f, groupBegin);
        }

        /// <summary>Insert a clone at the end of a _LIST_BEG/_END group and bump the group's count field.</summary>
        private static void InsertIntoGroup(T2bFile f, string beginName, string endName, T2bEntry entry)
        {
            int end = f.Entries.FindIndex(e => e.Name == endName);
            if (end < 0) { f.Entries.Add(entry); BumpCount(f, beginName); return; }
            f.Entries.Insert(end, entry);
            BumpCount(f, beginName);
        }

        private static void BumpCount(T2bFile f, string beginName)
        {
            var beg = f.Entries.FirstOrDefault(e => e.Name == beginName);
            if (beg == null) return;
            // the count is the first integer value of the BEGIN marker
            for (int i = 0; i < beg.Values.Count; i++)
                if (beg.Values[i].Value is int c) { beg.Values[i] = new T2bValue(Lycoris.Formats.ValueType.Integer, c + 1); return; }
        }

        private static int ModelBaseHash(string model) =>
            unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(model)));

        /// <summary>Does a chara_base entry for this model (BaseID = CRC32(model)) already exist in the loaded YW3 mod?</summary>
        public static bool ModelExistsInYw3(YokaiDatabase db, string modelId, YokaiSchema s)
        {
            if (db.BaseData == null || string.IsNullOrEmpty(modelId)) return false;
            int h = ModelBaseHash(modelId);
            return db.BaseData.Records(s.BaseYokaiRecord).Any(e => (e.GetInt(s.Base_BaseHashIndex) ?? 0) == h);
        }

        // ---------------------------------------------------------------- name tables

        private static Dictionary<uint, string> NameTable(string path)
        {
            var d = new Dictionary<uint, string>();
            if (path == null || !File.Exists(path)) return d;
            try
            {
                var f = T2bReader.ReadFile(path);
                foreach (var e in f.Entries)
                {
                    int? k = e.FirstIntKey(); string t = e.FirstText();
                    if (k.HasValue && !string.IsNullOrEmpty(t) && !d.ContainsKey((uint)k.Value)) d[(uint)k.Value] = t;
                }
            }
            catch { }
            return d;
        }

        // ---------------------------------------------------------------- YW2 reading

        private static string ModelForBase(T2bEntry b, YokaiSchema s) =>
            IconNaming.GetFileModelText(b.GetInt(s.Base_FileNamePrefixIndex) ?? -1,
                                        b.GetInt(s.Base_FileNameNumberIndex) ?? 0,
                                        b.GetInt(s.Base_FileNameVariantIndex) ?? 0);

        /// <summary>List all YW2 bosses (chara_param entries that own a BOSS_PARTS_INFO).</summary>
        public static List<Yw2BossInfo> Scan(string yw2Folder, YokaiSchema s)
        {
            var res = new List<Yw2BossInfo>();
            string paramF = Find(yw2Folder, s.ParamFilePrefix);
            string baseF = Find(yw2Folder, s.BaseFilePrefix);
            if (paramF == null || baseF == null) return res;
            var param = T2bReader.ReadFile(paramF);
            var baseData = T2bReader.ReadFile(baseF);
            var names = NameTable(Find(yw2Folder, "chara_text_engb"));

            // base by baseHash, param by baseHash link
            var baseByHash = baseData.Records(s.BaseYokaiRecord)
                .GroupBy(e => e.GetInt(s.Base_BaseHashIndex) ?? 0).ToDictionary(g => g.Key, g => g.First());
            var paramById = param.Records(s.ParamRecord)
                .GroupBy(e => e.GetInt(s.ParamHashIndex) ?? 0).ToDictionary(g => g.Key, g => g.First());

            foreach (var bp in param.Records(s.BossPartsRecord))
            {
                int pid = bp.GetInt(s.BP_ParamIndex) ?? 0;
                if (pid == 0 || !paramById.TryGetValue(pid, out var pe)) continue;
                int baseHash = pe.GetInt(s.Param_BaseHashIndex) ?? 0;
                if (!baseByHash.TryGetValue(baseHash, out var be)) continue;
                string model = ModelForBase(be, s);
                if (model == null) continue;
                string nm = names.TryGetValue((uint)(be.GetInt(s.Base_NameHashIndex) ?? 0), out var t) ? t : model;
                res.Add(new Yw2BossInfo { ModelId = model, Name = nm, Yw2Folder = yw2Folder, Param = pid });
            }
            return res.GroupBy(b => b.ModelId).Select(g => g.First())   // one entry per model (drop variant duplicates)
                      .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Fully read one YW2 boss: stats + attacks (resolved via BOSS_PARTS -> battle_command -> skill_config).</summary>
        public static Yw2BossInfo Read(string yw2Folder, string modelId, YokaiSchema s)
        {
            var param = T2bReader.ReadFile(Find(yw2Folder, s.ParamFilePrefix));
            var baseData = T2bReader.ReadFile(Find(yw2Folder, s.BaseFilePrefix));
            var cmd = T2bReader.ReadFile(Find(yw2Folder, s.BattleCommandFilePrefix));
            string skillF = Find(yw2Folder, s.SkillConfigFilePrefix);
            var skill = skillF != null ? T2bReader.ReadFile(skillF) : null;
            var skillNames = NameTable(Find(yw2Folder, "skill_text_engb"));
            var battleNames = NameTable(Find(yw2Folder, "battle_text_engb"));

            var be = baseData.Records(s.BaseYokaiRecord).FirstOrDefault(e => ModelForBase(e, s) == modelId);
            if (be == null) throw new InvalidOperationException("Model " + modelId + " not found in YW2 chara_base.");
            int baseHash = be.GetInt(s.Base_BaseHashIndex) ?? 0;
            var pe = param.Records(s.ParamRecord).FirstOrDefault(e => (e.GetInt(s.Param_BaseHashIndex) ?? 0) == baseHash);
            if (pe == null) throw new InvalidOperationException("No YW2 chara_param for base 0x" + ((uint)baseHash).ToString("X8"));
            int pid = pe.GetInt(s.ParamHashIndex) ?? 0;

            var boss = new Yw2BossInfo
            {
                ModelId = modelId,
                Name = (skillNames.Count >= 0 && be.GetInt(s.Base_NameHashIndex).HasValue &&
                        NameTable(Find(yw2Folder, "chara_text_engb")).TryGetValue((uint)(be.GetInt(s.Base_NameHashIndex) ?? 0), out var nm)) ? nm : modelId,
                Yw2Folder = yw2Folder,
                Param = pid,
                Hp = pe.GetInt(s.Yw2P_HpIndex) ?? 0,
                Str = pe.GetInt(s.Yw2P_StrIndex) ?? 0,
                Spr = pe.GetInt(s.Yw2P_SprIndex) ?? 0,
                Def = pe.GetInt(s.Yw2P_DefIndex) ?? 0,
                Spd = pe.GetInt(s.Yw2P_SpdIndex) ?? 0,
                Money = pe.GetInt(s.Yw2P_MoneyIndex) ?? 0,
                Exp = pe.GetInt(s.Yw2P_ExpIndex) ?? 0,
            };

            var bp = param.Records(s.BossPartsRecord).FirstOrDefault(e => (e.GetInt(s.BP_ParamIndex) ?? 0) == pid);
            if (bp != null)
            {
                for (int i = 0; i < s.BP_CmdCount; i++)
                {
                    int cmdId = bp.GetInt(s.BP_Cmd0Index + i) ?? 0;
                    if (cmdId == 0) continue;
                    var ce = cmd.Records(s.BattleCommandRecord).FirstOrDefault(e => (e.GetInt(s.Cmd_IdIndex) ?? 0) == cmdId);
                    int skillId = ce?.GetInt(s.Yw2Cmd_SkillIndex) ?? 0;
                    var se = skill?.Records(s.SkillConfigRecord).FirstOrDefault(e => (e.GetInt(0) ?? 0) == skillId);
                    int pow = se?.GetInt(s.Yw2Skill_PowerIndex) ?? 0;
                    int ele = se?.GetInt(s.Yw2Skill_ElementIndex) ?? 0;
                    string aname =
                        (se != null && skillNames.TryGetValue((uint)(se.GetInt(s.Yw2Skill_TextIndex) ?? 0), out var sn) && !string.IsNullOrEmpty(sn)) ? sn :
                        (ce != null && battleNames.TryGetValue((uint)(ce.GetInt(s.Yw2Cmd_TextIndex) ?? 0), out var bn) && !string.IsNullOrEmpty(bn)) ? bn :
                        "Attack " + (i + 1);
                    // YW2 English text sometimes stores a move name as "??????" (missing glyphs) — replace with a
                    // readable placeholder so the in-game move isn't unnamed.
                    if (string.IsNullOrWhiteSpace(aname) || aname.Trim('?', ' ', '？').Length == 0) aname = "Attack " + (i + 1);
                    boss.Attacks.Add(new Yw2Attack { Name = aname, Power = pow, Element = ele, Yw2CmdId = cmdId });
                }
                int maxPow = boss.Attacks.Count > 0 ? boss.Attacks.Max(a => a.Power) : 0;
                foreach (var a in boss.Attacks)
                    a.Yw3Type = (a.Power == maxPow && maxPow >= 120) ? 4 : a.Power == 0 ? 5 : a.Element != 0 ? 3 : 1;
            }
            return boss;
        }

        // ---------------------------------------------------------------- animation clips (from the YW2 model)

        // Shift-JIS byte sequences of the clip-name keywords (encoding-proof — no source-file Japanese literals).
        private static readonly byte[] SjisAttack    = { 0x82,0xB1,0x82,0xA4,0x82,0xB0,0x82,0xAB }; // こうげき
        private static readonly byte[] SjisTechnique = { 0x82,0xE6,0x82,0xA4,0x82,0xB6,0x82,0xE3,0x82,0xC2 }; // ようじゅつ
        private static readonly byte[] SjisSoul      = { 0x82,0xD0,0x82,0xC1,0x82,0xB3,0x82,0xC2 }; // ひっさつ
        private static readonly byte[] SjisGuard     = { 0x83,0x4B,0x81,0x5B,0x83,0x68 }; // ガード

        private static bool Has(byte[] hay, int len, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= len; i++)
            {
                int j = 0; while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return true;
            }
            return false;
        }

        /// <summary>
        /// Animation clips of the YW2 model, grouped by role. The clip id (used in YW3 battle_command[3]) is the
        /// mtninf field at 0x1C = CRC32(Shift-JIS clip name). Roles are matched on the raw name BYTES so a
        /// mangled source encoding can never empty the pools. "any" always holds every clip (guaranteed fallback).
        /// </summary>
        private static Dictionary<string, List<int>> AnimClips(string yw2Folder, string modelId)
        {
            var byRole = new Dictionary<string, List<int>>();
            void Add(string role, int id) { if (!byRole.TryGetValue(role, out var l)) byRole[role] = l = new List<int>(); if (!l.Contains(id)) l.Add(id); }
            string p20 = Path.Combine(yw2Folder, "data", "character", modelId, modelId + "_p20.xc");
            if (!File.Exists(p20)) return byRole;
            try
            {
                foreach (var f in Xpck.Read(File.ReadAllBytes(p20)).Where(x => x.Name.EndsWith(".mtninf", StringComparison.OrdinalIgnoreCase)))
                {
                    var b = f.Data;
                    if (b.Length < 0x24) continue;
                    int id = (int)BitConverter.ToUInt32(b, 0x1C);   // = CRC32(name) — the clip hash YW3 expects
                    int end = 0x20; while (end < b.Length && b[end] != 0) end++;
                    Add("any", id);
                    if (Has(b, end, SjisAttack)) Add("attack", id);
                    else if (Has(b, end, SjisTechnique)) Add("technique", id);
                    else if (Has(b, end, SjisSoul)) Add("soultimate", id);
                    else if (Has(b, end, SjisGuard)) Add("guard", id);
                }
            }
            catch { }
            return byRole;
        }

        private static string RoleForType(int yw3Type)
        {
            switch (yw3Type) { case 4: return "soultimate"; case 3: return "technique"; case 5: return "technique"; default: return "attack"; }
        }

        // ---------------------------------------------------------------- boss AI (battle_ai + battle_boss_config)

        private static T2bEntry CloneRec(T2bFile f, string name)
        {
            var e = f.Entries.FirstOrDefault(x => x.Name == name);
            return e?.Clone();
        }

        private static void InsertBlockBefore(T2bFile f, string endName, string beginName, List<T2bEntry> block)
        {
            int end = f.Entries.FindIndex(e => e.Name == endName);
            if (end < 0) end = f.Entries.Count;
            f.Entries.InsertRange(end, block);
            BumpCount(f, beginName);
        }

        /// <summary>
        /// Give the ported boss real behaviour: a BTL_CMD_AI_PRESET_DATA gambit (listing our command ids) in
        /// battle_ai, and a single-phase BOSS_PHASE_INFO (keyed by the ParamID, pointing at that gambit) in
        /// battle_boss_config. BOSS_PARTS[21] already points at the ParamID. Without this the boss just guards.
        /// </summary>
        private static void CreateBossAi(YokaiDatabase db, YokaiSchema s, int paramHash, List<int> cmdIds, StringBuilder report)
        {
            if (cmdIds == null || cmdIds.Count == 0)
            { report.AppendLine("AI: no commands — boss has no gambit (will guard)."); return; }
            string aiFile = FindMod(db, "battle_ai");
            string cfgFile = FindMod(db, s.BossConfigFilePrefix);
            if (aiFile == null || cfgFile == null)
            {
                report.AppendLine($"AI: {(aiFile == null ? "battle_ai " : "")}{(cfgFile == null ? "battle_boss_config " : "")}not in the mod — the boss will GUARD. Add from the base game and re-port.");
                return;
            }

            // 108/111 real YW3 boss phases point PAHSE[1] at a REAL battle_ai preset (BTL_CMD_AI_PRESET_DATA) that
            // lists the boss's command ids; ZERO point at the ParamID. So we MUST create a preset and point the
            // phase at it. aiHash is derived from the first command id -> unique per port (re-ports make a fresh
            // preset; the phase is repointed).
            int aiHash = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes($"bossai_{(uint)cmdIds[0]:X8}")));

            // --- battle_ai: one preset, one sub ([0]=1,[1]=3 constants), N acts (each = a command id, weight [2]=1) ---
            var ai = T2bReader.ReadFile(aiFile);
            var actTpl = CloneRec(ai, "BTL_CMD_AI_PRESET_DATA_SUB_ACT");
            if (CloneRec(ai, "BTL_CMD_AI_PRESET_DATA") == null || actTpl == null)
            { report.AppendLine("AI: battle_ai has no BTL_CMD_AI_PRESET_DATA template — boss will guard."); return; }
            var aiBlock = new List<T2bEntry>();
            var head = CloneRec(ai, "BTL_CMD_AI_PRESET_DATA"); SetInt(head, 0, aiHash); aiBlock.Add(head);
            var subBeg = CloneRec(ai, "BTL_CMD_AI_PRESET_DATA_SUB_LIST_BEG"); SetInt(subBeg, 0, 1); aiBlock.Add(subBeg);
            var sub = CloneRec(ai, "BTL_CMD_AI_PRESET_DATA_SUB"); SetInt(sub, 0, 1); SetInt(sub, 1, 3); aiBlock.Add(sub);
            var actBeg = CloneRec(ai, "BTL_CMD_AI_PRESET_DATA_SUB_ACT_LIST_BEG"); SetInt(actBeg, 0, cmdIds.Count); aiBlock.Add(actBeg);
            foreach (var c in cmdIds)
            {
                var a = actTpl.Clone();
                SetInt(a, 0, c); SetInt(a, 1, 0); SetInt(a, 2, 1); SetInt(a, 3, 0); SetInt(a, 4, 0); SetInt(a, 5, 0);
                aiBlock.Add(a);
            }
            aiBlock.Add(CloneRec(ai, "BTL_CMD_AI_PRESET_DATA_SUB_ACT_LIST_END"));
            aiBlock.Add(CloneRec(ai, "BTL_CMD_AI_PRESET_DATA_SUB_LIST_END"));
            InsertBlockBefore(ai, "BTL_CMD_AI_PRESET_DATA_LIST_END", "BTL_CMD_AI_PRESET_DATA_LIST_BEG", aiBlock);
            T2bWriter.WriteFile(ai, aiFile);

            // --- battle_boss_config: point the phase's PAHSE[1] at our preset (update existing, or create new) ---
            var cfg = T2bReader.ReadFile(cfgFile);
            if (CloneRec(cfg, s.BossPhaseRecord) == null)
            { report.AppendLine("AI: battle_boss_config has no BOSS_PHASE_INFO template — boss will guard."); return; }
            int phaseIdx = cfg.Entries.FindIndex(e => e.Name == s.BossPhaseRecord && (e.GetInt(0) ?? 0) == paramHash);
            if (phaseIdx >= 0)
            {
                var pah = cfg.Entries.Skip(phaseIdx + 1)
                             .TakeWhile(e => e.Name != s.BossPhaseRecord)
                             .FirstOrDefault(e => e.Name == s.BossPhaseChildRecord);
                if (pah != null) SetInt(pah, 1, aiHash);
                report.AppendLine($"AI: preset 0x{(uint)aiHash:X8} ({cmdIds.Count} attacks); existing phase repointed to it.");
            }
            else
            {
                var pBlock = new List<T2bEntry>();
                var ph = CloneRec(cfg, s.BossPhaseRecord); SetInt(ph, 0, paramHash); pBlock.Add(ph);
                var pBeg = CloneRec(cfg, "BOSS_PHASE_INFO_PAHSE_LIST_BEG"); SetInt(pBeg, 0, 1); pBlock.Add(pBeg);
                var pph = CloneRec(cfg, s.BossPhaseChildRecord); SetInt(pph, 0, 1); SetInt(pph, 1, aiHash); pBlock.Add(pph);
                pBlock.Add(CloneRec(cfg, "BOSS_PHASE_INFO_PAHSE_LIST_END"));
                InsertBlockBefore(cfg, "BOSS_PHASE_INFO_LIST_END", "BOSS_PHASE_INFO_LIST_BEG", pBlock);
                report.AppendLine($"AI: preset 0x{(uint)aiHash:X8} ({cmdIds.Count} attacks) + new single-phase config -> PAHSE[1]=preset.");
            }
            T2bWriter.WriteFile(cfg, cfgFile);
        }

        // ---------------------------------------------------------------- port

        /// <summary>The existing boss yo-kai for this model (a param linked to the model's base that already owns
        /// a BOSS_PARTS) — e.g. McKraken 0x6DA47D92 for x171000. Overwriting it in place means the game's existing
        /// fight/encounter (btl_x171_100 -> 0x6DA47D92) uses the new boss with no encounter re-wiring.</summary>
        public static YokaiInfo FindExistingBoss(YokaiDatabase db, YokaiSchema s, string modelId)
        {
            if (db.BattleData == null || db.Yokai == null) return null;
            int baseHash = ModelBaseHash(modelId);
            var bossPids = new HashSet<int>(db.BattleData.Records(s.BossPartsRecord).Select(e => e.GetInt(s.BP_ParamIndex) ?? 0));
            return db.Yokai.FirstOrDefault(y => y.BaseHash == baseHash && bossPids.Contains(y.ParamHash));
        }

        public static string Port(YokaiDatabase db, Yw2BossInfo boss, YokaiSchema s,
                                  int tribe = 12, int rank = 15, bool overwriteExisting = true)
        {
            if (db.ModFolder == null) throw new InvalidOperationException("Load a mod first.");
            if (db.BattleData == null) throw new InvalidOperationException("battle_chara_param not loaded (needed for BOSS_PARTS).");
            var report = new StringBuilder();

            // 1) Copy the YW2 model (mtn2 — loads in YW3) into the mod.
            string srcDir = Path.Combine(boss.Yw2Folder, "data", "character", boss.ModelId);
            string dstDir = Path.Combine(ModCharacterDir(db), boss.ModelId);
            int copied = 0;
            if (Directory.Exists(srcDir))
            {
                Directory.CreateDirectory(dstDir);
                foreach (var f in Directory.EnumerateFiles(srcDir))
                {
                    try { File.Copy(f, Path.Combine(dstDir, Path.GetFileName(f)), true); copied++; } catch { }
                }
            }
            report.AppendLine($"Model {boss.ModelId}: {copied} file(s) copied from YW2.");

            // 2) Get the yo-kai: OVERWRITE the existing boss param in place when one exists (so the game's
            //    existing fight keeps working), otherwise create a fresh one (reusing the base if present).
            var existing = overwriteExisting ? FindExistingBoss(db, s, boss.ModelId) : null;
            bool overwrite = existing != null;
            YokaiInfo y;
            if (overwrite)
            {
                y = existing;
                report.AppendLine($"OVERWRITE: replacing the existing boss param 0x{(uint)y.ParamHash:X8} (model {boss.ModelId}) in place — the game's existing fight/encounter will use the new boss (no new param, no encounter re-wiring).");
            }
            else
            {
                bool baseExisted = ModelExistsInYw3(db, boss.ModelId, s);
                y = db.AddYokai(boss.Name, "", tribe, rank, null, boss.ModelId, reuseExistingBase: true);
                report.AppendLine(baseExisted
                    ? $"Base 0x{(uint)y.BaseHash:X8} for model {boss.ModelId} already existed in YW3 -> reused it (no duplicate)."
                    : $"New base 0x{(uint)y.BaseHash:X8} created for model {boss.ModelId}.");
            }
            y.MinHp = y.MaxHp = boss.Hp;
            y.MinStrength = y.MaxStrength = boss.Str;
            y.MinSpirit = y.MaxSpirit = boss.Spr;
            y.MinDefense = y.MaxDefense = boss.Def;
            y.MinSpeed = y.MaxSpeed = boss.Spd;
            // chara_param[40] = 5 is the BOSS flag — ALL 311 YW3 bosses have it; a cloned normal-yokai template
            // has [40]=3, and the game then won't run boss AI on it (the boss just stands idle). MUST be set.
            if (y.SourceEntry != null) SetInt(y.SourceEntry, 40, 5);
            report.AppendLine($"Yo-kai: {boss.Name}  param=0x{(uint)y.ParamHash:X8}  base=0x{(uint)y.BaseHash:X8}  ([40]=5 boss flag).");

            // (No icon copy — YW3 already has the model's face/medal icons; importing the YW2 ones corrupts them.)

            // 3) Animation clips of the model, cycled per role.
            var clips = AnimClips(boss.Yw2Folder, boss.ModelId);
            var roleCursor = new Dictionary<string, int>();
            int NextClip(string role)
            {
                if (!clips.TryGetValue(role, out var l) || l.Count == 0)
                    if (!clips.TryGetValue("any", out l) || l.Count == 0)   // guaranteed fallback: any clip, never 0
                        return 0;
                int idx = roleCursor.TryGetValue(role, out var c) ? c : 0;
                roleCursor[role] = idx + 1;
                return l[idx % l.Count];
            }

            // 4) battle_command file + template. Pick a COMPLETE command (real execution/timing data at [4] and
            //    [8]) — cloning the first record can grab a stub with [4]=[8]=0, whose attacks never execute
            //    (boss selects a move then passes → looks passive). Fall back to the first only if none qualify.
            string bcFile = FindMod(db, s.BattleCommandFilePrefix);
            T2bFile bcData = bcFile != null ? T2bReader.ReadFile(bcFile) : null;
            var cmds = bcData?.Records(s.BattleCommandRecord);
            var cmdTemplate = cmds?.FirstOrDefault(e => (e.GetInt(1) ?? 0) == 3 && (e.GetInt(4) ?? 0) == 0x600 && (e.GetInt(8) ?? 0) != 0)
                           ?? cmds?.FirstOrDefault(e => (e.GetInt(1) ?? 0) == 3 && (e.GetInt(4) ?? 0) == 0x400 && (e.GetInt(8) ?? 0) != 0)
                           ?? cmds?.FirstOrDefault(e => (e.GetInt(4) ?? 0) != 0 && (e.GetInt(8) ?? 0) != 0)
                           ?? cmds?.FirstOrDefault();
            if (bcData == null) report.AppendLine("battle_command not found in mod — skills created without commands (attacks won't animate).");

            // 5) Each attack -> a YW3 skill + a battle_command playing it with the model's real clip.
            var skillIds = new List<int>();
            var effectIds = new List<int>();   // the command ids (skill EffectIDs) — the AI gambit references THESE
            foreach (var atk in boss.Attacks)
            {
                var skill = db.AddSkill(atk.Name, atk.Yw3Type);
                skill.Power = atk.Power;
                skill.Element = atk.Element;
                skill.Hits = 1;
                skill.SkillGrowth = 2;   // skill_config[5] — every working boss attack has 2 (AddSkill's generic
                                         // template leaves 1); mismatch here makes the boss select but not act.
                skillIds.Add(skill.SkillConfigID);

                if (bcData != null && cmdTemplate != null)
                {
                    var cmd = cmdTemplate.Clone();
                    int cmdId = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes($"bosscmd_{boss.ModelId}_{skill.SkillConfigID:X8}")));
                    SetInt(cmd, s.Cmd_IdIndex, cmdId);
                    SetInt(cmd, s.Cmd_TypeIndex, atk.Yw3Type == 4 ? 5 : 3);   // 5 = soultimate command
                    // 0xA26C7169 = YW3's universal attack clip (the engine's approach+hit), used by every official
                    // boss (Duwheel/Maddiman/…). The model's own YW2 mtn2 attack clips are stationary in YW3's
                    // battle layout, so the swing never reaches the target. Standard clip = the attack connects.
                    SetInt(cmd, s.Cmd_AnimIndex, unchecked((int)0xA26C7169));
                    SetInt(cmd, s.Cmd_SkillIndex, skill.SkillConfigID);
                    InsertBefore(bcData, s.BattleCommandGroupEndMarker, s.BattleCommandGroupBegin, cmd);
                    skill.EffectID = cmdId;
                    effectIds.Add(cmdId);
                }
                report.AppendLine($"  attack \"{atk.Name}\"  skill 0x{(uint)skill.SkillConfigID:X8}  type{atk.Yw3Type} pow{atk.Power} elem{atk.Element}");
            }

            // 5b) Point the yo-kai's STANDARD move slots at its own skills (else it keeps the template's).
            int SkillOfType(params int[] types)
            {
                for (int i = 0; i < boss.Attacks.Count && i < skillIds.Count; i++)
                    if (Array.IndexOf(types, boss.Attacks[i].Yw3Type) >= 0) return skillIds[i];
                return 0;
            }
            int primary = SkillOfType(1); if (primary == 0) primary = skillIds.FirstOrDefault();
            int tec = SkillOfType(3); if (tec == 0) tec = primary;
            int insp = SkillOfType(5); if (insp == 0) insp = primary;
            int soul = SkillOfType(4); if (soul == 0) soul = tec;
            if (primary != 0) y.AttackHash = primary;
            if (tec != 0) y.TechniqueHash = tec;
            if (insp != 0) y.InspiritHash = insp;
            if (soul != 0) y.SoultimateHash = soul;

            // 6) BOSS_PARTS: set skills + weights. In overwrite mode a boss can have SEVERAL BOSS_PARTS entries
            //    (parts/phases) for the same ParamID — update them ALL so none keeps the old moveset. The primary
            //    (the one carrying the phase link [21]) also gets the flags [2]=2,[3]=1,[4]=1,[22]=[23]=1.
            void FillBossParts(T2bEntry e, bool isPrimary)
            {
                for (int i = 0; i < s.BP_CmdCount; i++) SetInt(e, s.BP_Cmd0Index + i, i < skillIds.Count ? skillIds[i] : 0);
                for (int i = 0; i < s.BP_CmdCount; i++) SetInt(e, 13 + i, i < skillIds.Count ? 1 : 0);
                if (isPrimary)
                {
                    SetInt(e, s.BP_ParamIndex, y.ParamHash);
                    SetInt(e, s.BP_PhaseIndex, y.ParamHash);
                    SetInt(e, 1, 0); SetInt(e, 2, 2); SetInt(e, 3, 1); SetInt(e, 4, 1);
                    SetInt(e, 22, 1); SetInt(e, 23, 1);
                }
            }
            if (overwrite)
            {
                var parts = db.BattleData.Records(s.BossPartsRecord).Where(e => (e.GetInt(s.BP_ParamIndex) ?? 0) == y.ParamHash).ToList();
                // primary = the one whose [21] already points at the param (has the phase link), else the first.
                var primaryPart = parts.FirstOrDefault(e => (e.GetInt(s.BP_PhaseIndex) ?? 0) == y.ParamHash) ?? parts.FirstOrDefault();
                foreach (var e in parts) FillBossParts(e, e == primaryPart);
                report.AppendLine($"BOSS_PARTS: {skillIds.Count} attacks written to {parts.Count} part(s) of the existing boss, [21]=ParamID.");
            }
            else
            {
                var tpl = db.BattleData.Records(s.BossPartsRecord).FirstOrDefault();
                if (tpl != null)
                {
                    var bp = tpl.Clone();
                    FillBossParts(bp, true);
                    InsertIntoGroup(db.BattleData, s.BossPartsGroupBegin, s.BossPartsGroupEnd, bp);
                    report.AppendLine($"BOSS_PARTS: {skillIds.Count} attacks attached, weights [13..20]=1, [21]=ParamID.");
                }
                else report.AppendLine("No BOSS_PARTS template in battle_chara_param — boss command list NOT created.");
            }

            // 6b) The AI: a battle_ai gambit listing our commands + a single-phase battle_boss_config keyed by our
            //     ParamID. WITHOUT this the boss has no behaviour and just guards.
            CreateBossAi(db, s, y.ParamHash, effectIds, report);

            // 7) Encounter: only for a NEW param. When overwriting, the game's existing fight already spawns this
            //    ParamID (e.g. btl_x171_100 -> 0x6DA47D92), so adding another encounter would be pointless.
            string encFile = overwrite ? null : FindMod(db, s.EncountFilePrefix);
            if (overwrite)
                report.AppendLine("Encounter: reusing the existing fight for this ParamID (no new common_enc entry).");
            else if (encFile != null)
            {
                var enc = T2bReader.ReadFile(encFile);
                int encId = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes($"edy_{boss.ModelId}_01")));
                var charaTpl = enc.Records(s.EncountCharaRecord).FirstOrDefault();
                var tableTpl = enc.Records(s.EncountTableRecord).FirstOrDefault();
                if (charaTpl != null && tableTpl != null)
                {
                    int charaIdx = enc.Records(s.EncountCharaRecord).Count();
                    var chara = charaTpl.Clone();
                    SetInt(chara, s.Enc_ParamIndex, y.ParamHash);
                    SetInt(chara, s.Enc_LevelIndex, 0);
                    InsertIntoGroup(enc, s.EncountCharaGroupBegin, s.EncountCharaGroupEnd, chara);

                    var table = tableTpl.Clone();
                    SetInt(table, s.EncTable_IdIndex, encId);
                    SetInt(table, s.EncTable_Off1Index, charaIdx);
                    InsertIntoGroup(enc, s.EncountTableGroupBegin, s.EncountTableGroupEnd, table);

                    T2bWriter.WriteFile(enc, encFile);
                    report.AppendLine($"Encounter: common_enc table 0x{(uint)encId:X8} (edy_{boss.ModelId}_01) -> param.");
                }
            }
            else report.AppendLine("common_enc not found in mod — set up the encounter manually.");

            // 8) Save everything.
            if (bcData != null && bcFile != null) T2bWriter.WriteFile(bcData, bcFile);
            db.SaveSkills();
            db.SaveAll();
            report.AppendLine("Saved. Rebuild the RomFS and test in-game.");
            return report.ToString();
        }
    }
}
