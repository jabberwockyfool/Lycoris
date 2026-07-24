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

        /// <summary>Load a map's NPCs from its npc_set (+ base_talk chapters + trigger for XQ funcId).</summary>
        public static MapNpcs Load(string mapDir, string mapId)
        {
            string setPath = Directory.EnumerateFiles(mapDir, mapId + "_npc_set*").FirstOrDefault();
            if (setPath == null) return null;
            var m = new MapNpcs { MapId = mapId, MapDir = mapDir, NpcSetPath = setPath, NpcSet = T2bReader.ReadFile(setPath) };

            var bases = m.NpcSet.Records("NPC_BASE").ToDictionary(e => e.GetInt(Base_NpcId) ?? 0, e => e);

            // chapter talk files (exclude *_text*)
            for (int ch = 1; ch <= 11; ch++)
            {
                string tp = Directory.EnumerateFiles(mapDir, $"{mapId}_npc_base_talk_c{ch:00}*")
                    .FirstOrDefault(x => x.IndexOf("_text", StringComparison.OrdinalIgnoreCase) < 0);
                if (tp != null) m.Talk[ch] = (T2bReader.ReadFile(tp), tp);
            }

            // XQ triggers (npcId -> funcId), from [MapId].pck if present
            var funcByNpc = new Dictionary<int, int>();
            string pckPath = Path.Combine(mapDir, mapId + ".pck");
            m.PckPath = File.Exists(pckPath) ? pckPath : null;
            if (File.Exists(pckPath))
            {
                try
                {
                    var pck = Xpck.Read(File.ReadAllBytes(pckPath));
                    var trig = pck.FirstOrDefault(x => x.Name.IndexOf("_trigger", StringComparison.OrdinalIgnoreCase) >= 0 && x.Name.IndexOf("quest", StringComparison.OrdinalIgnoreCase) < 0);
                    if (trig != null)
                        foreach (var e in T2bReader.Read(trig.Data).Records("DATA_ITEM").Where(e => (e.GetInt(Trig_Type) ?? 0) == NpcTriggerType))
                        { int id = e.GetInt(Trig_NpcId) ?? 0; if (!funcByNpc.ContainsKey(id)) funcByNpc[id] = e.GetInt(Trig_Func) ?? -1; }
                }
                catch { /* pck unreadable — no XQ info */ }
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
