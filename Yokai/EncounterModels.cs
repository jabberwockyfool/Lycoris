using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Lycoris.Formats;
using VT = Lycoris.Formats.ValueType;

namespace Lycoris.Yokai
{
    /// <summary>One wild yo-kai instance (ENCOUNT_CHARA): a yo-kai ParamID + a level. Referenced by tables via offset.</summary>
    public sealed class EncChara : INotifyPropertyChanged
    {
        internal T2bEntry Entry;
        private int _paramId;
        private int? _level;
        public int ParamId { get => _paramId; set { if (_paramId != value) { _paramId = value; IsDirty = true; Raise(nameof(ParamHex)); } } }
        public int? Level { get => _level; set { if (_level != value) { _level = value; IsDirty = true; Raise(nameof(Level)); } } }
        public bool IsDirty;
        public string YokaiName { get; set; }
        public string IconFile { get; set; }
        public string ParamHex => $"0x{unchecked((uint)_paramId):X8}";

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>One encounter/"Battle" (ENCOUNT_TABLE): up to 6 offsets into the ENCOUNT_CHARA list (-1 = empty).</summary>
    public sealed class EncTable
    {
        internal T2bEntry Entry;
        public int EncountId;
        public int Index;
        public readonly int[] Offsets = new int[6];
        public string BattleScript;   // ENCOUNT_TABLE[7] — the battle-event .xq name (common_enc only; null on maps)
        public string HashHex => $"0x{unchecked((uint)EncountId):X8}";
        public string Label => string.IsNullOrEmpty(BattleScript) ? $"Battle {Index}  ({HashHex})" : $"{BattleScript}  ({HashHex})";
        public override string ToString() => Label;
    }

    /// <summary>The parsed encounter file (extracted from a map's .pck) + everything needed to repack it.</summary>
    public sealed class EncounterSet
    {
        public List<XpckFile> PckFiles;
        public string EncFileName;
        public T2bFile EncT2b;
        public List<EncTable> Tables = new List<EncTable>();
        public List<EncChara> Charas = new List<EncChara>();
    }

    /// <summary>Locating common_enc in the mod / reference, and listing its battles (shared by the Battle editor
    /// and the Daily Fight maker). In the mod it lives at include/data/res/battle.</summary>
    public static class CommonEnc
    {
        public const string FileName = "common_enc_0.01.cfg.bin";

        public static string IncBase(YokaiDatabase db)
        {
            if (db == null || string.IsNullOrEmpty(db.ModFolder)) return null;
            string inc = Path.Combine(db.ModFolder, "include");
            return Directory.Exists(inc) ? inc : db.ModFolder;
        }

        private static string FindIn(string root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            foreach (var cand in new[] {
                Path.Combine(root, "data", "res", "battle", FileName),
                Path.Combine(root, "res", "battle", FileName),
                Path.Combine(root, "data", "res", "sys", FileName),
                Path.Combine(root, "res", "sys", FileName),
                Path.Combine(root, "data", "res", FileName),
                Path.Combine(root, FileName),
            })
                if (File.Exists(cand)) return cand;
            return null;
        }

        /// <summary>Save path in the mod (include/data/res/battle), or null if no mod is loaded.</summary>
        public static string ModSavePath(YokaiDatabase db)
        {
            string inc = IncBase(db);
            return inc == null ? null : Path.Combine(inc, "data", "res", "battle", FileName);
        }

        /// <summary>Where a battle's .xq script goes: include/seq/battle/encount.</summary>
        public static string BattleXqDir(YokaiDatabase db)
        {
            string inc = IncBase(db);
            return inc == null ? null : Path.Combine(inc, "seq", "battle", "encount");
        }

        /// <summary>Locate common_enc, the mod's copy first (include-aware), else the reference.</summary>
        public static string FindLoadPath(YokaiDatabase db, out bool fromMod)
        {
            string mod = FindIn(IncBase(db)) ?? FindIn(db?.ModFolder);
            fromMod = mod != null;
            return mod ?? FindIn(db?.ReferenceFolder);
        }

        /// <summary>All battles' BattleScript names (for pickers) — deduped, sorted. Empty if none/unavailable.</summary>
        public static List<string> BattleScriptNames(YokaiDatabase db)
        {
            try
            {
                string p = FindLoadPath(db, out _);
                if (p == null) return new List<string>();
                var set = Encounters.LoadCfg(p, db);
                return set.Tables.Select(t => t.BattleScript)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct().OrderBy(s => s, System.StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { return new List<string>(); }
        }
    }

    /// <summary>Read/edit the ENCOUNT_TABLE + ENCOUNT_CHARA data — from a map's .pck (wild encounters) or a
    /// raw .cfg.bin (common_enc, the shared event/story battle config).</summary>
    public static class Encounters
    {
        private const int BattleScriptField = 7;   // ENCOUNT_TABLE[7], common_enc only

        public static EncounterSet Load(string pckPath, YokaiDatabase db)
        {
            var pck = Xpck.Read(File.ReadAllBytes(pckPath));
            var encFile = pck.FirstOrDefault(x =>
                x.Name.IndexOf("_enc_", System.StringComparison.OrdinalIgnoreCase) >= 0 &&
                x.Name.IndexOf("pos", System.StringComparison.OrdinalIgnoreCase) < 0);
            if (encFile == null) return null;

            var t2b = T2bReader.Read(encFile.Data);
            var set = new EncounterSet { PckFiles = pck, EncFileName = encFile.Name, EncT2b = t2b };
            ParseInto(set, db);
            return set;
        }

        /// <summary>Load a raw encounter .cfg.bin (e.g. common_enc_0.01) — no pck wrapper.</summary>
        public static EncounterSet LoadCfg(string cfgPath, YokaiDatabase db)
        {
            var t2b = T2bReader.ReadFile(cfgPath);
            var set = new EncounterSet { PckFiles = null, EncFileName = null, EncT2b = t2b };
            ParseInto(set, db);
            return set;
        }

        private static void ParseInto(EncounterSet set, YokaiDatabase db)
        {
            var t2b = set.EncT2b;
            foreach (var e in t2b.Records("ENCOUNT_CHARA"))
            {
                var c = new EncChara { Entry = e, ParamId = e.GetInt(0) ?? 0, Level = e.GetInt(1), IsDirty = false };
                Resolve(c, db);
                set.Charas.Add(c);
            }
            int idx = 0;
            foreach (var e in t2b.Records("ENCOUNT_TABLE"))
            {
                var t = new EncTable { Entry = e, EncountId = e.GetInt(0) ?? 0, Index = idx++, BattleScript = e.GetString(BattleScriptField) };
                for (int i = 0; i < 6; i++) t.Offsets[i] = e.GetInt(1 + i) ?? -1;
                set.Tables.Add(t);
            }
        }

        public static void Resolve(EncChara c, YokaiDatabase db)
        {
            var y = db.Yokai.FirstOrDefault(k => k.ParamHash == c.ParamId);
            c.YokaiName = y != null ? y.DisplayName : c.ParamHex;
            c.IconFile = y?.IconFile;
        }

        /// <summary>Add a new empty battle (ENCOUNT_TABLE) with the given id/script, all 6 slots empty.</summary>
        public static EncTable AddTable(EncounterSet set, int encountId, string battleScript)
        {
            var tpl = set.EncT2b.Records("ENCOUNT_TABLE").FirstOrDefault();
            if (tpl == null) return null;
            var e = tpl.Clone();
            SetVal(e, 0, encountId);
            for (int i = 0; i < 6; i++) SetVal(e, 1 + i, -1);
            SetStr(e, 7, battleScript ?? "");
            InsertIntoGroup(set.EncT2b, "ENCOUNT_TABLE_BEGIN", "ENCOUNT_TABLE_END", e);
            var t = new EncTable { Entry = e, EncountId = encountId, BattleScript = battleScript ?? "", Index = set.Tables.Count };
            for (int i = 0; i < 6; i++) t.Offsets[i] = -1;
            set.Tables.Add(t);
            return t;
        }

        /// <summary>Duplicate a battle, cloning its yo-kai (ENCOUNT_CHARA) so the copy is independent.</summary>
        public static EncTable DuplicateTable(EncounterSet set, EncTable src, int newEncountId, string battleScript, YokaiDatabase db)
        {
            var e = src.Entry.Clone();                    // keeps MusicID/BattleRuleID/unks
            SetVal(e, 0, newEncountId);
            SetStr(e, 7, battleScript ?? src.BattleScript ?? "");
            var t = new EncTable { Entry = e, EncountId = newEncountId, BattleScript = battleScript ?? src.BattleScript ?? "", Index = set.Tables.Count };
            for (int i = 0; i < 6; i++)
            {
                int off = src.Offsets[i];
                if (off >= 0 && off < set.Charas.Count)
                {
                    var sc = set.Charas[off];
                    AddChara(set, sc.ParamId, sc.Level ?? 1, db);
                    t.Offsets[i] = set.Charas.Count - 1;
                }
                else t.Offsets[i] = -1;
                SetVal(e, 1 + i, t.Offsets[i]);
            }
            InsertIntoGroup(set.EncT2b, "ENCOUNT_TABLE_BEGIN", "ENCOUNT_TABLE_END", e);
            set.Tables.Add(t);
            return t;
        }

        /// <summary>Remove a battle (ENCOUNT_TABLE). Its yo-kai stay in the shared pool (harmless if unused).</summary>
        public static void RemoveTable(EncounterSet set, EncTable t)
        {
            if (t?.Entry == null) return;
            if (set.EncT2b.Entries.Remove(t.Entry))
            {
                var b = set.EncT2b.Entries.FirstOrDefault(x => x.Name == "ENCOUNT_TABLE_BEGIN");
                if (b != null && b.Values.Count > 0 && b.Values[0].Value is int c) b.Values[0].Value = c - 1;
            }
            set.Tables.Remove(t);
            for (int i = 0; i < set.Tables.Count; i++) set.Tables[i].Index = i;
        }

        /// <summary>Append a new ENCOUNT_CHARA and return it; its offset is the new last index in Charas.</summary>
        public static EncChara AddChara(EncounterSet set, int paramId, int level, YokaiDatabase db)
        {
            var tpl = set.EncT2b.Records("ENCOUNT_CHARA").FirstOrDefault();
            if (tpl == null) return null;
            var e = tpl.Clone();
            SetVal(e, 0, paramId);
            SetVal(e, 1, level);
            InsertIntoGroup(set.EncT2b, "ENCOUNT_CHARA_BEGIN", "ENCOUNT_CHARA_END", e);
            var c = new EncChara { Entry = e, ParamId = paramId, Level = level };
            Resolve(c, db);
            set.Charas.Add(c);
            return c;
        }

        /// <summary>Write all edits back into the CfgBin, repack the .pck and save it to <paramref name="outPckPath"/>.</summary>
        public static void Save(EncounterSet set, string outPckPath)
        {
            CommitEdits(set);
            Xpck.AddOrReplace(set.PckFiles, set.EncFileName, T2bWriter.Write(set.EncT2b));
            byte[] bytes = Xpck.Write(set.PckFiles);
            Directory.CreateDirectory(Path.GetDirectoryName(outPckPath));
            File.WriteAllBytes(outPckPath, bytes);
        }

        /// <summary>Write all edits back and save a raw encounter .cfg.bin (common_enc).</summary>
        public static void SaveCfg(EncounterSet set, string outPath)
        {
            CommitEdits(set);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            T2bWriter.WriteFile(set.EncT2b, outPath);
        }

        private static void CommitEdits(EncounterSet set)
        {
            foreach (var c in set.Charas)
            {
                SetVal(c.Entry, 0, c.ParamId);
                SetVal(c.Entry, 1, c.Level ?? 0);
            }
            foreach (var t in set.Tables)
            {
                for (int i = 0; i < 6; i++) SetVal(t.Entry, 1 + i, t.Offsets[i]);
                if (t.BattleScript != null) SetStr(t.Entry, BattleScriptField, t.BattleScript);
            }
        }

        private static void SetVal(T2bEntry e, int i, int v)
        {
            if (i < 0 || i >= e.Values.Count) return;
            e.Values[i].Type = VT.Integer;
            e.Values[i].Value = v;
        }

        private static void SetStr(T2bEntry e, int i, string v)
        {
            if (i < 0 || i >= e.Values.Count) return;
            e.Values[i].Type = VT.String;
            e.Values[i].Value = v ?? "";
        }

        private static void InsertIntoGroup(T2bFile file, string begin, string end, T2bEntry entry)
        {
            int endIdx = file.Entries.FindIndex(x => x.Name == end);
            if (endIdx < 0) { file.Entries.Add(entry); return; }
            file.Entries.Insert(endIdx, entry);
            var b = file.Entries.FirstOrDefault(x => x.Name == begin);
            if (b != null && b.Values.Count > 0 && b.Values[0].Value is int c) b.Values[0].Value = c + 1;
        }
    }
}
