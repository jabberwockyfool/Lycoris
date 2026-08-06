using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lycoris.Formats;

namespace Lycoris.Yokai
{
    /// <summary>One YW2 boss attack, resolved from its battle_command → skill_config.</summary>
    public sealed class Yw2Attack
    {
        public string Name;
        public int Yw3Type;      // inferred YW3 SkillType (1 Attack, 3 Technique, 4 Soultimate, 5 Inspirit)
        public int Element;      // 0 = none/physical
        public int Power;
        public int Yw2CmdId;     // the YW2 battle_command id (for reference)
    }

    /// <summary>A YW2 boss read from a YW2 dump — everything needed to recreate it in YW3.</summary>
    public sealed class Yw2BossInfo
    {
        public string Yw2Folder;
        public string ModelId { get; set; }   // e.g. "x171000"
        public string Name { get; set; }       // property so WPF DisplayMemberPath can bind it
        public int Param;
        public int Hp, Str, Spr, Def, Spd, Money, Exp;
        public readonly List<Yw2Attack> Attacks = new List<Yw2Attack>();
        public override string ToString() => $"{Name}  ({ModelId})  HP {Hp}, {Attacks.Count} attacks";
    }

    /// <summary>
    /// Reads a boss from a YW2 dump and recreates it in the loaded YW3 mod: yo-kai (stats/model/Boss/Unrank),
    /// the YW2 mtn2 model copied in, each attack as a YW3 skill + battle_command (playing the model's real
    /// animation clip), the BOSS_PARTS command list, and a common_enc table wired to the boss battle event.
    /// See memory yw2-vs-yw3-boss-system for the format details.
    /// </summary>
    public static class BossPort
    {
        // ---------- YW2 reading ----------

        private static string Find(string root, params string[] parts)
        {
            // parts = [subfolder-under-data/res, filename-prefix]; picks the newest match.
            try
            {
                string dir = Path.Combine(root, "data", "res", parts[0]);
                if (!Directory.Exists(dir)) return null;
                return Directory.EnumerateFiles(dir, parts[1] + "*.cfg.bin")
                    .Where(p => !VariantSuffix(Path.GetFileName(p), parts[1]))   // skip _link/_menu/_backup siblings
                    .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>True if the filename is a sibling variant (prefix_link/_menu/_backup/…) rather than the
        /// real "prefix" or "prefix_&lt;version&gt;" file — so a prefix search doesn't pick battle_command_link.</summary>
        private static bool VariantSuffix(string fileName, string prefix)
        {
            string rest = fileName.Length > prefix.Length ? fileName.Substring(prefix.Length) : "";
            if (rest.Length == 0 || rest.StartsWith(".")) return false;         // "prefix.cfg.bin"
            if (rest.StartsWith("_") && rest.Length > 1 && char.IsDigit(rest[1])) return false; // "prefix_0.04d…"
            return true;                                                         // "_link", "_menu", "_en", …
        }

        private static Dictionary<int, string> TextMap(string path)
        {
            var d = new Dictionary<int, string>();
            if (path == null || !File.Exists(path)) return d;
            foreach (var e in T2bReader.ReadFile(path).Entries)
            {
                int? k = e.FirstIntKey();
                string s = null;
                for (int i = 1; i < e.Values.Count; i++)
                    if (e.Values[i].Type == Formats.ValueType.String && !string.IsNullOrEmpty(e.Values[i].Value as string))
                    { s = (string)e.Values[i].Value; break; }
                if (k.HasValue && s != null && !d.ContainsKey(k.Value)) d[k.Value] = s;
            }
            return d;
        }

        /// <summary>List all YW2 bosses (chara_param entries that have a BOSS_PARTS_INFO), by model id + name.</summary>
        public static List<Yw2BossInfo> Scan(string yw2Folder, YokaiSchema s)
        {
            var list = new List<Yw2BossInfo>();
            string paramPath = Find(yw2Folder, "character", "chara_param");
            string basePath = Find(yw2Folder, "character", "chara_base");
            if (paramPath == null || basePath == null) return list;

            var pf = T2bReader.ReadFile(paramPath);
            var bf = T2bReader.ReadFile(basePath);
            var bossParams = new HashSet<int>(pf.Records(s.BossPartsRecord).Select(e => e.GetInt(0) ?? 0));
            var baseByHash = bf.Records(s.BaseYokaiRecord).ToDictionary(e => e.GetInt(0) ?? 0, e => e);
            var names = TextMap(Find(yw2Folder, "text", "chara_text_engb") ?? Find(yw2Folder, "text", "chara_text_en"));

            foreach (var p in pf.Records(s.ParamRecord))
            {
                int param = p.GetInt(0) ?? 0;
                if (!bossParams.Contains(param)) continue;
                int baseHash = p.GetInt(s.Param_BaseHashIndex) ?? 0;
                if (!baseByHash.TryGetValue(baseHash, out var be)) continue;
                string model = IconNaming.GetFileModelText(be.GetInt(1) ?? -1, be.GetInt(2) ?? 0, be.GetInt(3) ?? 0);
                int nameHash = be.GetInt(4) ?? 0;
                string name = names.TryGetValue(nameHash, out var n) ? n : model;
                // de-dup by model (a boss often has several param variants)
                if (list.Any(b => b.ModelId == model)) continue;
                list.Add(new Yw2BossInfo { Yw2Folder = yw2Folder, ModelId = model, Name = name, Param = param });
            }
            return list.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Fully read one YW2 boss (stats + attacks) by its model id.</summary>
        public static Yw2BossInfo Read(string yw2Folder, string modelId, YokaiSchema s)
        {
            string paramPath = Find(yw2Folder, "character", "chara_param");
            var pf = T2bReader.ReadFile(paramPath);
            int wantBase = ModelBaseHash(modelId);
            var p = pf.Records(s.ParamRecord).FirstOrDefault(e => (e.GetInt(s.Param_BaseHashIndex) ?? 0) == wantBase
                                                                  && pf.Records(s.BossPartsRecord).Any(b => (b.GetInt(0) ?? 0) == (e.GetInt(0) ?? 0)));
            if (p == null) throw new InvalidOperationException($"No YW2 boss with model {modelId} (base 0x{(uint)wantBase:X8}) found.");
            int param = p.GetInt(0) ?? 0;

            var boss = new Yw2BossInfo
            {
                Yw2Folder = yw2Folder,
                ModelId = modelId,
                Param = param,
                Hp = p.GetInt(s.Yw2P_HpIndex) ?? 0,
                Str = p.GetInt(s.Yw2P_StrIndex) ?? 0,
                Spr = p.GetInt(s.Yw2P_SprIndex) ?? 0,
                Def = p.GetInt(s.Yw2P_DefIndex) ?? 0,
                Spd = p.GetInt(s.Yw2P_SpdIndex) ?? 0,
                Money = p.GetInt(s.Yw2P_MoneyIndex) ?? 0,
                Exp = p.GetInt(s.Yw2P_ExpIndex) ?? 0,
            };
            var names = TextMap(Find(yw2Folder, "text", "chara_text_engb") ?? Find(yw2Folder, "text", "chara_text_en"));
            int nameHash = 0;
            var bf = T2bReader.ReadFile(Find(yw2Folder, "character", "chara_base"));
            var be = bf.Records(s.BaseYokaiRecord).FirstOrDefault(e => (e.GetInt(0) ?? 0) == wantBase);
            if (be != null) nameHash = be.GetInt(4) ?? 0;
            boss.Name = names.TryGetValue(nameHash, out var nm) ? nm : modelId;

            // BOSS_PARTS[5..12] = battle_command ids; each command → skill_config (power/element) + battle_text name.
            var bp = pf.Records(s.BossPartsRecord).First(e => (e.GetInt(0) ?? 0) == param);
            var bc = T2bReader.ReadFile(Find(yw2Folder, "battle", "battle_command"))
                .Records(s.BattleCommandRecord).ToDictionary(e => e.GetInt(0) ?? 0, e => e);
            var sk = T2bReader.ReadFile(Find(yw2Folder, "skill", "skill_config"))
                .Records(s.SkillConfigRecord).ToDictionary(e => e.GetInt(0) ?? 0, e => e);
            var btext = TextMap(Find(yw2Folder, "text", "battle_text_engb") ?? Find(yw2Folder, "text", "battle_text_en"));
            var stext = TextMap(Find(yw2Folder, "text", "skill_text_engb") ?? Find(yw2Folder, "text", "skill_text_en"));

            var raw = new List<Yw2Attack>();
            for (int i = 0; i < s.BP_CmdCount; i++)
            {
                int cmdId = bp.GetInt(s.BP_Cmd0Index + i) ?? 0;
                if (cmdId == 0 || !bc.TryGetValue(cmdId, out var c)) continue;
                int tid = c.GetInt(s.Yw2Cmd_TextIndex) ?? 0;
                int scid = c.GetInt(s.Yw2Cmd_SkillIndex) ?? 0;
                int power = 0, elem = 0, skillTid = 0;
                if (sk.TryGetValue(scid, out var se))
                {
                    power = se.GetInt(s.Yw2Skill_PowerIndex) ?? 0;
                    elem = se.GetInt(s.Yw2Skill_ElementIndex) ?? 0;
                    skillTid = se.GetInt(s.Yw2Skill_TextIndex) ?? 0;
                }
                // Prefer the move's own name (skill_text), then the command name (battle_text).
                string name = stext.TryGetValue(skillTid, out var sn) ? sn
                            : btext.TryGetValue(tid, out var bn) ? bn : null;
                if (string.IsNullOrWhiteSpace(name) || name.Trim('?', ' ').Length == 0) name = $"{modelId} Attack {i + 1}";
                raw.Add(new Yw2Attack { Name = name, Element = elem, Power = power, Yw2CmdId = cmdId });
            }
            // Infer YW3 skill types: strongest = Soultimate; power 0 = Inspirit; elemental = Technique; else Attack.
            int maxPow = raw.Count > 0 ? raw.Max(a => a.Power) : 0;
            foreach (var a in raw)
            {
                a.Yw3Type = a.Power == maxPow && maxPow >= 120 ? 4
                          : a.Power == 0 ? 5
                          : a.Element != 0 ? 3
                          : 1;
                boss.Attacks.Add(a);
            }
            return boss;
        }

        // ---------- YW3 writing (the port) ----------

        private static int ModelBaseHash(string model) =>
            unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(model)));

        /// <summary>Read the model's animation clips (mtn2/.mtninf), grouped by role from their JP names.</summary>
        private static Dictionary<string, List<int>> AnimClips(string yw2Folder, string modelId)
        {
            var byRole = new Dictionary<string, List<int>>();
            void Add(string role, int slot) { if (!byRole.TryGetValue(role, out var l)) byRole[role] = l = new List<int>(); l.Add(slot); }
            string p20 = Path.Combine(yw2Folder, "data", "character", modelId, modelId + "_p20.xc");
            if (!File.Exists(p20)) return byRole;
            foreach (var f in Xpck.Read(File.ReadAllBytes(p20)).Where(x => x.Name.EndsWith(".mtninf", StringComparison.OrdinalIgnoreCase)))
            {
                var b = f.Data;
                if (b.Length < 0x24) continue;
                uint slot = BitConverter.ToUInt32(b, 0x1C);
                string nm = Encoding.GetEncoding(932).GetString(b, 0x20, Math.Min(36, b.Length - 0x20)).Split('\0')[0];
                if (nm.Contains("こうげき")) Add("attack", (int)slot);
                else if (nm.Contains("ようじゅつ") || nm.Contains("妖術")) Add("technique", (int)slot);
                else if (nm.Contains("ひっさつ") && !nm.Contains("終")) Add("soultimate", (int)slot);
                else if (nm.Contains("ガード")) Add("guard", (int)slot);
                else if (nm.Contains("ため") && !nm.Contains("L")) Add("charge", (int)slot);
            }
            return byRole;
        }

        private static string RoleForType(int yw3Type)
        {
            switch (yw3Type) { case 4: return "soultimate"; case 3: return "technique"; case 5: return "technique"; default: return "attack"; }
        }

        /// <summary>
        /// Port the boss into the loaded YW3 <paramref name="db"/>. Copies the YW2 model, creates the yo-kai,
        /// its skills+battle_commands+BOSS_PARTS, and a common_enc encounter. Returns a human-readable report.
        /// The caller should have loaded a full mod; boss data (battle_chara_param) must be present.
        /// </summary>
        public static string Port(YokaiDatabase db, Yw2BossInfo boss, YokaiSchema s,
                                  int tribe = 12, int rank = 15, int bossTribe = 12)
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
                    try { File.Copy(f, Path.Combine(dstDir, Path.GetFileName(f)), true); copied++; } catch { }
            }
            report.AppendLine($"Model {boss.ModelId}: copied {copied} file(s) (YW2 mtn2) into the mod.");

            // 2) Create the yo-kai (stats, Boss tribe, Unrank), linked to the model.
            var y = db.AddYokai(boss.Name, "", tribe, rank, null, boss.ModelId);
            y.MinHp = y.MaxHp = boss.Hp;
            y.MinStrength = y.MaxStrength = boss.Str;
            y.MinSpirit = y.MaxSpirit = boss.Spr;
            y.MinDefense = y.MaxDefense = boss.Def;
            y.MinSpeed = y.MaxSpeed = boss.Spd;
            report.AppendLine($"Yo-kai created: {boss.Name}  param=0x{(uint)y.ParamHash:X8}  base=0x{(uint)y.BaseHash:X8} (model {boss.ModelId}).");

            // 3) Animation clips of the model, grouped by role (cycled per role).
            var clips = AnimClips(boss.Yw2Folder, boss.ModelId);
            var roleCursor = new Dictionary<string, int>();
            int NextClip(string role)
            {
                if (!clips.TryGetValue(role, out var l) || l.Count == 0)
                    l = clips.Values.FirstOrDefault(x => x.Count > 0) ?? new List<int>();
                if (l.Count == 0) return 0;
                int idx = roleCursor.TryGetValue(role, out var c) ? c : 0;
                roleCursor[role] = idx + 1;
                return l[idx % l.Count];
            }

            // 4) A YW3 battle_command template (for valid structure) and battle_command file handle.
            string bcFile = FindMod(db, "battle", s.BattleCommandFilePrefix);
            if (bcFile == null) { report.AppendLine("battle_command not found — skills created without commands (attacks won't animate)."); }
            T2bFile bcData = bcFile != null ? T2bReader.ReadFile(bcFile) : null;
            var cmdTemplate = bcData?.Records(s.BattleCommandRecord).FirstOrDefault();

            // 5) Each attack → a YW3 skill + a battle_command playing it with the model's real clip.
            var skillIds = new List<int>();
            foreach (var atk in boss.Attacks)
            {
                var skill = db.AddSkill(atk.Name, atk.Yw3Type);
                skill.Power = atk.Power;
                skill.Element = atk.Element;
                skill.Hits = 1;
                skillIds.Add(skill.SkillConfigID);

                if (bcData != null && cmdTemplate != null)
                {
                    var cmd = cmdTemplate.Clone();
                    int cmdId = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes($"bosscmd_{boss.ModelId}_{skill.SkillConfigID:X8}")));
                    SetInt(cmd, s.Cmd_IdIndex, cmdId);
                    SetInt(cmd, s.Cmd_TypeIndex, atk.Yw3Type == 4 ? 5 : 3);   // 5 = soultimate command
                    SetInt(cmd, s.Cmd_AnimIndex, NextClip(RoleForType(atk.Yw3Type)));
                    SetInt(cmd, s.Cmd_SkillIndex, skill.SkillConfigID);
                    InsertBefore(bcData, s.BattleCommandGroupEndMarker, s.BattleCommandGroupBegin, cmd);
                    skill.EffectID = cmdId;
                }
                report.AppendLine($"  attack \"{atk.Name}\"  skill 0x{(uint)skill.SkillConfigID:X8}  type{atk.Yw3Type} pow{atk.Power} elem{atk.Element}");
            }

            // 6) BOSS_PARTS entry: the skill ids + a phase config (borrow an existing boss's).
            var bpTpl = db.BattleData.Records(s.BossPartsRecord).FirstOrDefault();
            if (bpTpl != null)
            {
                var bp = bpTpl.Clone();
                SetInt(bp, s.BP_ParamIndex, y.ParamHash);
                for (int i = 0; i < s.BP_CmdCount; i++) SetInt(bp, s.BP_Cmd0Index + i, i < skillIds.Count ? skillIds[i] : 0);
                InsertIntoGroup(db.BattleData, s.BossPartsGroupBegin, s.BossPartsGroupEnd, bp);
                report.AppendLine($"BOSS_PARTS: {skillIds.Count} attacks attached (phase config kept from template).");
            }
            else report.AppendLine("No BOSS_PARTS template in battle_chara_param — boss command list NOT created.");

            // 7) Encounter: common_enc table CRC32("edy_<model>_01") → a new ENCOUNT_CHARA (the param).
            string encFile = FindMod(db, "battle", s.EncountFilePrefix);
            string encInfo = "common_enc not found — set up the encounter manually.";
            if (encFile != null)
            {
                var enc = T2bReader.ReadFile(encFile);
                int encId = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes($"edy_{boss.ModelId}_01")));
                // append a chara, then a table pointing at it
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
                    for (int k = 2; k <= 6; k++) SetIntForce(table, k, -1);
                    InsertIntoGroup(enc, s.EncountTableGroupBegin, s.EncountTableGroupEnd, table);

                    if (!IsUnderMod(db, encFile)) encFile = db.MirrorToMod(encFile) ?? encFile;
                    T2bWriter.WriteFile(enc, encFile);
                    encInfo = $"Encounter table 0x{(uint)encId:X8} (= edy_{boss.ModelId}_01) → this boss. Trigger a battle with load_battle_ev(\"edy_{boss.ModelId}_01\", \"<stage>\").";
                }
            }
            report.AppendLine(encInfo);

            // 8) Write battle_command + save the yo-kai/skill/battle files.
            if (bcData != null)
            {
                if (!IsUnderMod(db, bcFile)) bcFile = db.MirrorToMod(bcFile) ?? bcFile;
                T2bWriter.WriteFile(bcData, bcFile);
            }
            db.SaveSkills();
            db.SaveAll();
            report.AppendLine($"\nDONE. Boss param = 0x{(uint)y.ParamHash:X8}. Rebuild the RomFS and test. Refine skill types/animations in the Skill Editor if needed.");
            return report.ToString();
        }

        // ---------- helpers (self-contained; the yo-kai-editor boss code was reverted) ----------

        private static string ModCharacterDir(YokaiDatabase db)
        {
            // data/character next to the mod's include root.
            string inc = db.ModIncludeBase ?? db.ModFolder;
            return Path.Combine(inc, "data", "character");
        }

        private static string FindMod(YokaiDatabase db, string sub, string prefix)
        {
            try
            {
                foreach (var root in new[] { db.ModFolder, db.ReferenceFolder })
                {
                    if (root == null || !Directory.Exists(root)) continue;
                    var hit = Directory.EnumerateFiles(root, prefix + "*.cfg.bin", SearchOption.AllDirectories)
                        .Where(p => p.Replace('\\', '/').Contains("/" + sub + "/"))
                        .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        private static bool IsUnderMod(YokaiDatabase db, string path) =>
            db.ModFolder != null && path != null &&
            path.Replace('\\', '/').StartsWith(db.ModFolder.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

        private static int SetInt(T2bEntry e, int i, int v)
        {
            if (i < 0 || i >= e.Values.Count) return 0;
            e.Values[i].Type = Formats.ValueType.Integer; e.Values[i].Value = v; return 1;
        }
        private static void SetIntForce(T2bEntry e, int i, int v) => SetInt(e, i, v);

        private static void InsertIntoGroup(T2bFile file, string beginName, string endName, T2bEntry entry)
        {
            int endIdx = file.Entries.FindIndex(e => e.Name == endName);
            if (endIdx < 0) throw new InvalidDataException($"Group end '{endName}' not found.");
            file.Entries.Insert(endIdx, entry);
            var begin = file.Entries.FirstOrDefault(e => e.Name == beginName);
            if (begin != null && begin.Values.Count > 0 && begin.Values[0].Value is int count) begin.Values[0].Value = count + 1;
        }

        // battle_command has a BEGIN (count at [0]) but no matching END — insert before the next section.
        private static void InsertBefore(T2bFile file, string nextSectionName, string beginName, T2bEntry entry)
        {
            int idx = file.Entries.FindIndex(e => e.Name == nextSectionName);
            if (idx < 0) idx = file.Entries.Count;
            file.Entries.Insert(idx, entry);
            var begin = file.Entries.FirstOrDefault(e => e.Name == beginName);
            if (begin != null && begin.Values.Count > 0 && begin.Values[0].Value is int count) begin.Values[0].Value = count + 1;
        }
    }
}
