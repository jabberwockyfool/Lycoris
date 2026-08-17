using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Lycoris.Formats;
using Lycoris.Yokai;
using VT = Lycoris.Formats.ValueType;

namespace Lycoris.Npc
{
    /// <summary>One existing NPC placed in a map (an NPC_APPEAR + its matching NPC_BASE, npcId = CRC32(name)).</summary>
    public sealed class ExistingNpc : INotifyPropertyChanged
    {
        public int NpcId;
        public string ModelName;                         // NPC_APPEAR name (the npcbin base) — read-only
        public readonly bool[] Chapters = new bool[12];  // 1..11 talkable
        public int FuncId = -1;                          // XQ trigger function id, -1 if none (vanilla talk)
        public bool HasXqTalk => FuncId >= 0;
        public bool HasBase => BaseEntry != null;        // an NPC_BASE (baseId/type) links to this placement

        internal T2bEntry BaseEntry;
        internal T2bEntry AppearEntry;

        private int _baseId, _npcType;
        private string _appearCond, _onTalk;
        public int BaseId { get => _baseId; set { if (_baseId != value) { _baseId = value; IsDirty = true; Raise(nameof(BaseIdHex)); } } }
        public int NpcType { get => _npcType; set { if (_npcType != value) { _npcType = value; IsDirty = true; Raise(nameof(NpcType)); } } }
        public string AppearCond { get => _appearCond; set { if (_appearCond != value) { _appearCond = value; IsDirty = true; Raise(nameof(AppearCond)); } } }
        public string OnTalk { get => _onTalk; set { if (_onTalk != value) { _onTalk = value; OnTalkDirty = true; Raise(nameof(OnTalk)); } } }

        public bool IsDirty;
        public bool OnTalkDirty;
        public bool ChaptersDirty;

        public string BaseIdHex { get => $"0x{unchecked((uint)_baseId):X8}"; set => BaseId = ParseHex(value); }
        public string NpcIdHex => $"0x{unchecked((uint)NpcId):X8}";
        public string DisplayName => $"{ModelName}  ({NpcIdHex})";

        private static int ParseHex(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim(); if (s.StartsWith("0x") || s.StartsWith("0X")) s = s.Substring(2);
            return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint u)
                ? unchecked((int)u) : (int.TryParse(s, out int i) ? i : 0);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>Everything loaded for one map's NPCs, and the write-back logic.</summary>
    public sealed class MapNpcs
    {
        public string MapId;
        public string MapDir;
        public string PckPath;   // [MapId].pck (for OnTalk XQ), or null
        public T2bFile NpcSet; public string NpcSetPath;
        public T2bFile NpcTalk; public string NpcTalkPath;   // <MapId>_npc_talk_0.01 (TALK_INFO/CONFIG/PAGE), or null
        public readonly Dictionary<int, (T2bFile file, string path)> Talk = new Dictionary<int, (T2bFile, string)>(); // chapter 1..11
        public List<ExistingNpc> Npcs = new List<ExistingNpc>();
    }

    public static class ExistingNpcs
    {
        // ENCOUNT/NPC schema (validated on real files).
        private const int Base_NpcId = 0, Base_BaseId = 2, Base_Type = 4;
        private const int Appear_Name = 0, Appear_Cond = 3;
        private const int NpcTriggerType = 11, Trig_Type = 0, Trig_NpcId = 1, Trig_Func = 6;
        private const int Talk_NpcId = 0;
        // npc_talk_0.01 record layout (see yw3-npc-dailyfight): TALK_INFO = [npcId, cfgStart, cfgLen];
        // TALK_CONFIG field[8] = ConditionalCond, field[9] = TrigCond (a RunTrigger blob → trigger id).
        private const int TalkInfo_Npc = 0, TalkInfo_CfgStart = 1, TalkInfo_CfgLen = 2, TalkCfg_Trig = 9;

        /// <summary>Load a map's NPCs from its npc_set (+ base_talk chapters + trigger for XQ funcId).</summary>
        public static MapNpcs Load(string mapDir, string mapId)
        {
            string setPath = Directory.EnumerateFiles(mapDir, mapId + "_npc_set*").FirstOrDefault();
            if (setPath == null) return null;
            var m = new MapNpcs { MapId = mapId, MapDir = mapDir, NpcSetPath = setPath, NpcSet = T2bReader.ReadFile(setPath) };

            // Keep the first NPC_BASE per npcId. An edited npc_set can contain duplicate ids (or several
            // records whose id field isn't an int, all keying to 0) — ToDictionary would throw on those.
            var bases = new Dictionary<int, T2bEntry>();
            foreach (var e in m.NpcSet.Records("NPC_BASE"))
            {
                int k = e.GetInt(Base_NpcId) ?? 0;
                if (!bases.ContainsKey(k)) bases[k] = e;
            }

            // chapter talk files (exclude *_text*) — skip any file that fails to parse (edited/corrupt)
            for (int ch = 1; ch <= 11; ch++)
            {
                string tp = Directory.EnumerateFiles(mapDir, $"{mapId}_npc_base_talk_c{ch:00}*")
                    .FirstOrDefault(x => x.IndexOf("_text", StringComparison.OrdinalIgnoreCase) < 0);
                if (tp == null) continue;
                try { m.Talk[ch] = (T2bReader.ReadFile(tp), tp); }
                catch { /* a broken chapter-talk file just means that chapter's toggles are unknown */ }
            }

            // XQ triggers, from [MapId].pck if present.
            //  • funcByNpc     — a type-11 trigger links an NPC directly (field[1] = npcId): Lycoris/simple NPCs.
            //  • funcByTrigId  — any DATA_ITEM by field[1] (a RunTrigger id), used to resolve VANILLA/talk NPCs
            //                    whose talk is wired through npc_talk (TALK_CONFIG.TrigCond → this id → funcId).
            var funcByNpc = new Dictionary<int, int>();
            var funcByTrigId = new Dictionary<int, int>();
            string pckPath = Path.Combine(mapDir, mapId + ".pck");
            m.PckPath = File.Exists(pckPath) ? pckPath : null;
            if (File.Exists(pckPath))
            {
                try
                {
                    var pck = Xpck.Read(File.ReadAllBytes(pckPath));
                    var trig = pck.FirstOrDefault(x => x.Name.IndexOf("_trigger", StringComparison.OrdinalIgnoreCase) >= 0 && x.Name.IndexOf("quest", StringComparison.OrdinalIgnoreCase) < 0);
                    if (trig != null)
                        foreach (var e in T2bReader.Read(trig.Data).Records("DATA_ITEM"))
                        {
                            int id = e.GetInt(Trig_NpcId) ?? 0, func = e.GetInt(Trig_Func) ?? -1;
                            if (!funcByTrigId.ContainsKey(id)) funcByTrigId[id] = func;
                            if ((e.GetInt(Trig_Type) ?? 0) == NpcTriggerType && !funcByNpc.ContainsKey(id)) funcByNpc[id] = func;
                        }
                }
                catch { /* pck unreadable — no XQ info */ }
            }

            // npc_talk_0.01: needed to resolve vanilla talk NPCs' funcId (and to edit their talk configs).
            string talkCfgPath = Directory.EnumerateFiles(mapDir, mapId + "_npc_talk_0.01*")
                .FirstOrDefault(x => x.IndexOf("_text", StringComparison.OrdinalIgnoreCase) < 0);
            if (talkCfgPath != null) { try { m.NpcTalk = T2bReader.ReadFile(talkCfgPath); m.NpcTalkPath = talkCfgPath; } catch { } }

            // Vanilla talk chain: TALK_INFO(npcId → cfgStart,len) → each TALK_CONFIG.TrigCond (RunTrigger blob,
            // id at offset 19) → funcByTrigId → the RunCmd_Map funcId.
            var talkFuncByNpc = new Dictionary<int, int>();
            if (m.NpcTalk != null)
            {
                var configs = m.NpcTalk.Records("TALK_CONFIG").ToList();
                foreach (var info in m.NpcTalk.Records("TALK_INFO"))
                {
                    int nid = info.GetInt(TalkInfo_Npc) ?? 0, start = info.GetInt(TalkInfo_CfgStart) ?? 0, len = info.GetInt(TalkInfo_CfgLen) ?? 0;
                    for (int k = start; k < start + len && k >= 0 && k < configs.Count; k++)
                    {
                        var vals = configs[k].Values;
                        string blob = vals.Count > TalkCfg_Trig && vals[TalkCfg_Trig].Type == VT.String ? vals[TalkCfg_Trig].Value as string : null;
                        if (string.IsNullOrEmpty(blob)) continue;
                        int? tid = YwCond.ReadParamId(blob, 19);
                        if (tid.HasValue && funcByTrigId.TryGetValue(tid.Value, out int fn) && fn >= 0 && !talkFuncByNpc.ContainsKey(nid)) { talkFuncByNpc[nid] = fn; break; }
                    }
                }
            }

            foreach (var appear in m.NpcSet.Records("NPC_APPEAR"))
            {
                string name = appear.GetString(Appear_Name);
                int npcId = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(name ?? "")));
                var npc = new ExistingNpc { ModelName = name, NpcId = npcId, AppearEntry = appear, AppearCond = appear.GetString(Appear_Cond) };
                if (bases.TryGetValue(npcId, out var b)) { npc.BaseEntry = b; npc.BaseId = b.GetInt(Base_BaseId) ?? 0; npc.NpcType = b.GetInt(Base_Type) ?? 0; }
                for (int ch = 1; ch <= 11; ch++)
                    npc.Chapters[ch] = m.Talk.TryGetValue(ch, out var t) && t.file.Records("BASE_TALK_INFO").Any(e => (e.GetInt(Talk_NpcId) ?? 0) == npcId);
                if (funcByNpc.TryGetValue(npcId, out int fid)) npc.FuncId = fid;
                else if (talkFuncByNpc.TryGetValue(npcId, out int tfid)) npc.FuncId = tfid;   // vanilla talk NPC
                npc.IsDirty = npc.OnTalkDirty = npc.ChaptersDirty = false;
                m.Npcs.Add(npc);
            }
            return m;
        }

        /// <summary>
        /// Write back the edited NPC set (baseId/type/appearCond) and chapter talk toggles into the mod.
        /// <paramref name="mirror"/> maps a source file path to its mod write target. Returns files written.
        /// </summary>
        public static List<string> Save(MapNpcs m, Func<string, string> mirror)
        {
            var written = new List<string>();

            bool setDirty = m.Npcs.Any(n => n.IsDirty);
            foreach (var n in m.Npcs.Where(n => n.IsDirty))
            {
                if (n.BaseEntry != null) { Set(n.BaseEntry, Base_BaseId, n.BaseId); Set(n.BaseEntry, Base_Type, n.NpcType); }
                if (n.AppearEntry != null) SetStr(n.AppearEntry, Appear_Cond, n.AppearCond);
            }
            if (setDirty)
            {
                string outPath = mirror(m.NpcSetPath);
                T2bWriter.WriteFile(m.NpcSet, outPath); written.Add(outPath);
            }

            // chapter talk add/remove
            foreach (var n in m.Npcs.Where(n => n.ChaptersDirty))
                for (int ch = 1; ch <= 11; ch++)
                {
                    if (!m.Talk.TryGetValue(ch, out var t)) continue;
                    bool present = t.file.Records("BASE_TALK_INFO").Any(e => (e.GetInt(Talk_NpcId) ?? 0) == n.NpcId);
                    if (n.Chapters[ch] && !present) AddTalk(t.file, n.NpcId);
                    else if (!n.Chapters[ch] && present) RemoveTalk(t.file, n.NpcId);
                    else continue;
                    string outPath = mirror(t.path);
                    if (!written.Contains(outPath)) { T2bWriter.WriteFile(t.file, outPath); written.Add(outPath); }
                }
            return written;
        }

        private const int Preset_AppearIdx = 1;

        /// <summary>
        /// Remove an NPC from the map: its NPC_APPEAR (+ re-index every NPC_PRESET that pointed past it),
        /// NPC_BASE, NPC_PRESET(s), chapter BASE_TALK_INFO, and its type-11 trigger. The .npcbin is left in
        /// npc.pck (harmless when unreferenced). Deleting a story/event NPC can break events that spawn it.
        /// </summary>
        public static List<string> Delete(MapNpcs m, ExistingNpc npc, Func<string, string> mirror)
        {
            var written = new List<string>();
            var set = m.NpcSet;
            int npcId = npc.NpcId;

            // index of this NPC's NPC_APPEAR (presets reference it by index).
            var appears = set.Records("NPC_APPEAR").ToList();
            int k = npc.AppearEntry != null ? appears.IndexOf(npc.AppearEntry) : -1;
            if (k < 0) throw new InvalidOperationException("This NPC's placement was not found in npc_set.");

            // remove the appear, then fix preset indices.
            set.Entries.Remove(npc.AppearEntry);
            Bump(set, "NPC_APPEAR_BEGIN", -1);

            foreach (var pr in set.Records("NPC_PRESET").ToList())
                if ((pr.GetInt(Preset_AppearIdx) ?? -1) == k) { set.Entries.Remove(pr); Bump(set, "NPC_PRESET_BEGIN", -1); }
            foreach (var pr in set.Records("NPC_PRESET"))
            {
                int ai = pr.GetInt(Preset_AppearIdx) ?? -1;
                if (ai > k) Set(pr, Preset_AppearIdx, ai - 1);
            }

            // remove the NPC_BASE (keyed by npcId).
            var baseE = set.Records("NPC_BASE").FirstOrDefault(e => (e.GetInt(Base_NpcId) ?? 0) == npcId);
            if (baseE != null && set.Entries.Remove(baseE)) Bump(set, "NPC_BASE_BEGIN", -1);

            string setOut = mirror(m.NpcSetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(setOut));
            T2bWriter.WriteFile(set, setOut); written.Add(setOut);

            // chapter talk files.
            foreach (var kv in m.Talk)
            {
                var (tf, tp) = kv.Value;
                var hits = tf.Records("BASE_TALK_INFO").Where(e => (e.GetInt(Talk_NpcId) ?? 0) == npcId).ToList();
                if (hits.Count == 0) continue;
                foreach (var e in hits) tf.Entries.Remove(e);
                Bump(tf, "BASE_TALK_INFO_BEGIN", -hits.Count);
                string o = mirror(tp);
                Directory.CreateDirectory(Path.GetDirectoryName(o));
                if (!written.Contains(o)) { T2bWriter.WriteFile(tf, o); written.Add(o); }
            }

            // type-11 trigger in the map .pck (DATA_COUNT + flat DATA_ITEM list).
            if (m.PckPath != null && File.Exists(m.PckPath))
            {
                try
                {
                    var pck = Xpck.Read(File.ReadAllBytes(m.PckPath));
                    var trig = pck.FirstOrDefault(x => x.Name.IndexOf("_trigger", StringComparison.OrdinalIgnoreCase) >= 0 && x.Name.IndexOf("quest", StringComparison.OrdinalIgnoreCase) < 0);
                    if (trig != null)
                    {
                        var tf = T2bReader.Read(trig.Data);
                        var items = tf.Records("DATA_ITEM").Where(e => (e.GetInt(Trig_Type) ?? 0) == NpcTriggerType && (e.GetInt(Trig_NpcId) ?? 0) == npcId).ToList();
                        if (items.Count > 0)
                        {
                            foreach (var e in items) tf.Entries.Remove(e);
                            var count = tf.Entries.FirstOrDefault(x => x.Name == "DATA_COUNT");
                            if (count != null && count.Values.Count > 0 && count.Values[0].Value is int c) count.Values[0].Value = c - items.Count;
                            Xpck.AddOrReplace(pck, trig.Name, T2bWriter.Write(tf));
                            string o = mirror(m.PckPath);
                            Directory.CreateDirectory(Path.GetDirectoryName(o));
                            File.WriteAllBytes(o, Xpck.Write(pck)); written.Add(o);
                        }
                    }
                }
                catch { /* pck unreadable — trigger left as-is */ }
            }

            // npc_talk_0.01: drop this NPC's TALK_INFO so a talk NPC stops talking after deletion. The game looks
            // up TALK_INFO by npcId, so removing just the TALK_INFO (+ decrementing its count) is safe — the now
            // unreferenced TALK_CONFIG/TALK_PAGE it pointed at are left in place (harmless, like an orphaned
            // npcbin); removing them would require re-indexing every other TALK_INFO's ConfigStartPos.
            if (m.NpcTalk != null && m.NpcTalkPath != null)
            {
                var infos = m.NpcTalk.Records("TALK_INFO").Where(e => (e.GetInt(TalkInfo_Npc) ?? 0) == npcId).ToList();
                if (infos.Count > 0)
                {
                    foreach (var e in infos) m.NpcTalk.Entries.Remove(e);
                    Bump(m.NpcTalk, "TALK_INFO_BEGIN", -infos.Count);
                    string o = mirror(m.NpcTalkPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(o));
                    if (!written.Contains(o)) { T2bWriter.WriteFile(m.NpcTalk, o); written.Add(o); }
                }
            }

            m.Npcs.Remove(npc);
            return written;
        }

        /// <summary>Remove an NPC's TALK_INFO from npc_talk (safe — TALK_INFO is looked up by npcId, so no
        /// reindexing of the configs/pages it pointed at is needed; those become harmless orphans). Returns the
        /// number removed. Used by delete and by the daily-fight patch (which then appends fresh talk wiring).</summary>
        public static int RemoveTalkInfo(T2bFile npcTalk, int npcId)
        {
            if (npcTalk == null) return 0;
            var infos = npcTalk.Records("TALK_INFO").Where(e => (e.GetInt(TalkInfo_Npc) ?? 0) == npcId).ToList();
            foreach (var e in infos) npcTalk.Entries.Remove(e);
            if (infos.Count > 0) Bump(npcTalk, "TALK_INFO_BEGIN", -infos.Count);
            return infos.Count;
        }

        /// <summary>The RunTrigger ids referenced by an NPC's talk configs (TALK_CONFIG.TrigCond, id at offset 19)
        /// — i.e. the trigger DATA_ITEMs that belong to this NPC's old talk wiring. Used by the daily patch to
        /// remove the old (broken) triggers before appending fresh ones, so nothing double-fires.</summary>
        public static HashSet<int> TalkTriggerIds(T2bFile npcTalk, int npcId)
        {
            var ids = new HashSet<int>();
            var info = npcTalk?.Records("TALK_INFO").FirstOrDefault(e => (e.GetInt(TalkInfo_Npc) ?? 0) == npcId);
            if (info == null) return ids;
            int start = info.GetInt(TalkInfo_CfgStart) ?? 0, len = info.GetInt(TalkInfo_CfgLen) ?? 0;
            var configs = npcTalk.Records("TALK_CONFIG").ToList();
            for (int k = start; k < start + len && k >= 0 && k < configs.Count; k++)
            {
                var v = configs[k].Values;
                string blob = v.Count > TalkCfg_Trig && v[TalkCfg_Trig].Type == VT.String ? v[TalkCfg_Trig].Value as string : null;
                if (string.IsNullOrEmpty(blob)) continue;
                int? id = YwCond.ReadParamId(blob, 19);
                if (id.HasValue) ids.Add(id.Value);
            }
            return ids;
        }

        /// <summary>Remove trigger DATA_ITEMs that belong to an NPC's OLD daily wiring: any whose field[1] is one of
        /// <paramref name="trigIds"/> (old talk triggers), plus win/lose (type 80/81) items matching
        /// <paramref name="battleId"/> (they route by battle id, not by a config — so an old pair on the same
        /// battle would double-fire alongside the new one). Decrements DATA_COUNT.</summary>
        public static void RemoveTriggerItems(T2bFile trigger, HashSet<int> trigIds, int battleId)
        {
            if (trigger == null) return;
            var rm = trigger.Records("DATA_ITEM").Where(e =>
            {
                int type = e.GetInt(Trig_Type) ?? 0, f1 = e.GetInt(Trig_NpcId) ?? 0;
                return (trigIds != null && trigIds.Contains(f1)) || ((type == 80 || type == 81) && battleId != 0 && f1 == battleId);
            }).ToList();
            if (rm.Count == 0) return;
            foreach (var e in rm) trigger.Entries.Remove(e);
            var count = trigger.Entries.FirstOrDefault(x => x.Name == "DATA_COUNT");
            if (count != null && count.Values.Count > 0 && count.Values[0].Value is int c) count.Values[0].Value = c - rm.Count;
        }

        /// <summary>Issue-1 retrofit: gate an NPC's talk to once-a-day. Sets a GetOneDayBitFlag(flagId)
        /// ConditionalCond on the NPC's talk config(s) that don't already carry a String cond (so an existing
        /// GetGlobalBitFlag isn't clobbered); if all already have one, the first config is used. Returns the
        /// number of configs changed. The caller registers the flag (FLAG_INFO_6) and writes the files.</summary>
        public static int SetOnceADayCond(MapNpcs m, ExistingNpc npc, int flagId)
        {
            if (m?.NpcTalk == null) throw new InvalidOperationException("This map has no npc_talk_0.01 file.");
            var info = m.NpcTalk.Records("TALK_INFO").FirstOrDefault(e => (e.GetInt(TalkInfo_Npc) ?? 0) == npc.NpcId)
                       ?? throw new InvalidOperationException("This NPC has no TALK_INFO (npc_talk) entry to gate.");
            int start = info.GetInt(TalkInfo_CfgStart) ?? 0, len = info.GetInt(TalkInfo_CfgLen) ?? 0;
            var configs = m.NpcTalk.Records("TALK_CONFIG").ToList();
            string cond = NpcDailyFight.BuildOneDayConfigCond(flagId);

            const int TalkCfg_Cond = 8;
            var targets = new List<T2bEntry>();
            for (int k = start; k < start + len && k >= 0 && k < configs.Count; k++)
            {
                var v = configs[k].Values;
                if (v.Count > TalkCfg_Cond && v[TalkCfg_Cond].Type != VT.String) targets.Add(configs[k]); // empty cond slot
            }
            if (targets.Count == 0 && start >= 0 && start < configs.Count) targets.Add(configs[start]);   // all occupied → first
            foreach (var cfg in targets)
                if (cfg.Values.Count > TalkCfg_Cond) { cfg.Values[TalkCfg_Cond].Type = VT.String; cfg.Values[TalkCfg_Cond].Value = cond; }
            return targets.Count;
        }

        private static void AddTalk(T2bFile talk, int npcId)
        {
            var tpl = talk.Records("BASE_TALK_INFO").FirstOrDefault();
            if (tpl == null) return;
            var e = tpl.Clone();
            SetForce(e, 0, npcId); SetForce(e, 1, 0); SetForce(e, 2, 1); SetForce(e, 3, 1); SetForce(e, 4, 1);
            SetForce(e, 5, 2); SetForce(e, 6, 1); SetForce(e, 7, 3); SetForce(e, 8, 1);
            int endIdx = talk.Entries.FindIndex(x => x.Name == "BASE_TALK_INFO_END");
            if (endIdx < 0) talk.Entries.Add(e); else talk.Entries.Insert(endIdx, e);
            Bump(talk, "BASE_TALK_INFO_BEGIN", +1);
        }

        private static void RemoveTalk(T2bFile talk, int npcId)
        {
            var e = talk.Records("BASE_TALK_INFO").FirstOrDefault(x => (x.GetInt(0) ?? 0) == npcId);
            if (e != null && talk.Entries.Remove(e)) Bump(talk, "BASE_TALK_INFO_BEGIN", -1);
        }

        private static void Bump(T2bFile f, string begin, int d)
        {
            var b = f.Entries.FirstOrDefault(x => x.Name == begin);
            if (b != null && b.Values.Count > 0 && b.Values[0].Value is int c) b.Values[0].Value = c + d;
        }

        private static void Set(T2bEntry e, int i, int v) { if (i < e.Values.Count && e.Values[i].Type == VT.Integer && e.Values[i].Value is int cur && cur == v) return; if (i < e.Values.Count) { e.Values[i].Type = VT.Integer; e.Values[i].Value = v; } }
        private static void SetForce(T2bEntry e, int i, int v) { if (i < e.Values.Count) { e.Values[i].Type = VT.Integer; e.Values[i].Value = v; } }
        private static void SetStr(T2bEntry e, int i, string v) { if (i < e.Values.Count) { e.Values[i].Type = VT.String; e.Values[i].Value = v ?? ""; } }
    }
}
