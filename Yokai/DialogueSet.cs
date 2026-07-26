using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Lycoris.Formats;
using VT = Lycoris.Formats.ValueType;

namespace Lycoris.Yokai
{
    /// <summary>One spoken line: a TEXT_INFO row (text) + its matching TEXT_WASHA_MAP row (the speaker name-box).</summary>
    public sealed class DialogueLineRow : INotifyPropertyChanged
    {
        internal T2bEntry TextEntry;
        internal T2bEntry WashaEntry;     // null if the event has no washamap entry for this line
        public int KeyId;
        public int Page;
        public int Variant;
        public string KeyLabel;           // resolved block key (e.g. "_010") or the hex hash
        public string SpeakerName;        // resolved model name for the talker, or null

        private string _text;
        private int _talker;
        public string Text { get => _text; set { _text = value; Raise(nameof(Text)); Raise(nameof(Preview)); } }
        public int TalkerBaseId { get => _talker; set { _talker = value; Raise(nameof(TalkerHex)); Raise(nameof(SpeakerLabel)); Raise(nameof(Preview)); } }

        public string TalkerHex => $"0x{unchecked((uint)_talker):X8}";
        public string SpeakerLabel => SpeakerName ?? TalkerHex;
        public string Preview
        {
            get
            {
                string t = (_text ?? "").Replace("\r", " ").Replace("\n", " ");
                if (t.Length > 60) t = t.Substring(0, 60) + "…";
                return $"{KeyLabel} p{Page}{(Variant > 0 ? "/" + Variant : "")}   [{SpeakerLabel}]  {t}";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>An event's dialogue: the text file (TEXT_INFO) + name-box washamap (TEXT_WASHA_MAP).</summary>
    public sealed class DialogueFile
    {
        public string EventName;
        public T2bFile TextData; public string TextPath;
        public T2bFile WashaData; public string WashaPath;   // may be null
        public readonly List<DialogueLineRow> Rows = new List<DialogueLineRow>();
    }

    public static class Dialogue
    {
        private const int T_Key = 0, T_Page = 1, T_Text = 2, T_Var = 3;
        private const int W_Key = 0, W_Page = 1, W_Talker = 2, W_Var = 3, W_U4 = 4, W_U5 = 5;
        private const string TxtRec = "TEXT_INFO", TxtBeg = "TEXT_INFO_BEGIN", TxtEnd = "TEXT_INFO_END";
        private const string WmRec = "TEXT_WASHA_MAP", WmBeg = "TEXT_WASHA_MAP_BEGIN", WmEnd = "TEXT_WASHA_MAP_END";

        public static DialogueFile Load(string eventName, string textPath, string washaPath, YokaiDatabase db)
        {
            var f = new DialogueFile { EventName = eventName, TextPath = textPath, TextData = T2bReader.ReadFile(textPath) };
            if (washaPath != null && File.Exists(washaPath)) { f.WashaPath = washaPath; f.WashaData = T2bReader.ReadFile(washaPath); }

            // washamap lookup by (key, page, variant)
            var washa = new Dictionary<(int, int, int), T2bEntry>();
            if (f.WashaData != null)
                foreach (var e in f.WashaData.Records(WmRec))
                {
                    var k = (e.GetInt(W_Key) ?? 0, e.GetInt(W_Page) ?? 0, e.GetInt(W_Var) ?? 0);
                    if (!washa.ContainsKey(k)) washa[k] = e;
                }

            var talkerNames = BuildTalkerMap(db);
            var keyLabels = BuildKeyLabels(eventName, f.TextData.Records(TxtRec).Select(e => e.GetInt(T_Key) ?? 0));

            foreach (var e in f.TextData.Records(TxtRec))
            {
                int key = e.GetInt(T_Key) ?? 0, page = e.GetInt(T_Page) ?? 0, var = e.GetInt(T_Var) ?? 0;
                var row = new DialogueLineRow
                {
                    TextEntry = e, KeyId = key, Page = page, Variant = var,
                    Text = e.GetString(T_Text) ?? "",
                    KeyLabel = keyLabels.TryGetValue(key, out var lbl) ? lbl : $"0x{unchecked((uint)key):X8}",
                };
                if (washa.TryGetValue((key, page, var), out var we)) { row.WashaEntry = we; row.TalkerBaseId = we.GetInt(W_Talker) ?? 0; }
                if (row.TalkerBaseId != 0 && talkerNames.TryGetValue(row.TalkerBaseId, out var nm)) row.SpeakerName = nm;
                f.Rows.Add(row);
            }
            return f;
        }

        public static void Save(DialogueFile f, string textOut, string washaOut)
        {
            foreach (var r in f.Rows)
            {
                SetStr(r.TextEntry, T_Text, r.Text);
                if (r.WashaEntry != null) SetInt(r.WashaEntry, W_Talker, r.TalkerBaseId);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(textOut));
            T2bWriter.WriteFile(f.TextData, textOut);
            if (f.WashaData != null && washaOut != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(washaOut));
                T2bWriter.WriteFile(f.WashaData, washaOut);
            }
        }

        /// <summary>Add a new line to a block (KeyId), on a new page after the block's current last page.</summary>
        public static DialogueLineRow AddRow(DialogueFile f, int keyId, string keyLabel, string text, int talkerBaseId)
        {
            var tpl = f.TextData.Records(TxtRec).FirstOrDefault();
            if (tpl == null) throw new InvalidOperationException("Text file has no TEXT_INFO to clone.");
            int page = f.Rows.Where(r => r.KeyId == keyId).Select(r => r.Page).DefaultIfEmpty(-1).Max() + 1;

            var te = tpl.Clone();
            SetInt(te, T_Key, keyId); SetInt(te, T_Page, page); SetStr(te, T_Text, text); SetInt(te, T_Var, 0);
            InsertBefore(f.TextData, TxtEnd, te); Bump(f.TextData, TxtBeg, 1);

            T2bEntry we = null;
            if (f.WashaData != null)
            {
                var wtpl = f.WashaData.Records(WmRec).FirstOrDefault();
                if (wtpl != null)
                {
                    we = wtpl.Clone();
                    SetInt(we, W_Key, keyId); SetInt(we, W_Page, page); SetInt(we, W_Talker, talkerBaseId);
                    SetInt(we, W_Var, 0); SetInt(we, W_U4, -1); SetInt(we, W_U5, 0);
                    InsertBefore(f.WashaData, WmEnd, we); Bump(f.WashaData, WmBeg, 1);
                }
            }
            var row = new DialogueLineRow { TextEntry = te, WashaEntry = we, KeyId = keyId, Page = page, Variant = 0, KeyLabel = keyLabel, Text = text, TalkerBaseId = talkerBaseId };
            f.Rows.Add(row);
            return row;
        }

        public static void RemoveRow(DialogueFile f, DialogueLineRow row)
        {
            if (f.TextData.Entries.Remove(row.TextEntry)) Bump(f.TextData, TxtBeg, -1);
            if (row.WashaEntry != null && f.WashaData != null && f.WashaData.Entries.Remove(row.WashaEntry)) Bump(f.WashaData, WmBeg, -1);
            f.Rows.Remove(row);
        }

        /// <summary>Distinct block keys present, resolved to their "_NNN" suffix where the hash matches.</summary>
        public static Dictionary<int, string> BuildKeyLabels(string eventName, IEnumerable<int> keyIds)
        {
            var wanted = new HashSet<int>(keyIds);
            var map = new Dictionary<int, string>();
            for (int n = 0; n < 1000 && map.Count < wanted.Count; n++)
            {
                string suffix = "_" + n.ToString("000");
                int h = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(eventName + suffix)));
                if (wanted.Contains(h) && !map.ContainsKey(h)) map[h] = suffix;
            }
            return map;
        }

        /// <summary>TalkerBaseID (CRC32 of a model name) → the model name, for yo-kai (charabase) speakers.</summary>
        public static Dictionary<int, string> BuildTalkerMap(YokaiDatabase db)
        {
            var m = new Dictionary<int, string>();
            if (db != null)
                foreach (var y in db.Yokai)
                    if (y.BaseHash != 0 && !string.IsNullOrEmpty(y.ModelName) && !m.ContainsKey(y.BaseHash))
                        m[y.BaseHash] = y.ModelName;
            return m;
        }

        // ---- helpers ----
        private static void InsertBefore(T2bFile f, string end, T2bEntry e)
        {
            int idx = f.Entries.FindIndex(x => x.Name == end);
            if (idx < 0) f.Entries.Add(e); else f.Entries.Insert(idx, e);
        }
        private static void Bump(T2bFile f, string begin, int d)
        {
            var b = f.Entries.FirstOrDefault(x => x.Name == begin);
            if (b != null && b.Values.Count > 0 && b.Values[0].Value is int c) b.Values[0].Value = c + d;
        }
        private static void SetInt(T2bEntry e, int i, int v) { if (i < e.Values.Count) { e.Values[i].Type = VT.Integer; e.Values[i].Value = v; } }
        private static void SetStr(T2bEntry e, int i, string v) { if (i < e.Values.Count) { e.Values[i].Type = VT.String; e.Values[i].Value = v ?? ""; } }
    }

    /// <summary>Finds and lists per-event dialogue files (mod's data/txt/ev, else the reference's ev).</summary>
    public static class DialoguePaths
    {
        public static string IncBase(YokaiDatabase db)
        {
            if (db == null || string.IsNullOrEmpty(db.ModFolder)) return null;
            string inc = Path.Combine(db.ModFolder, "include");
            return Directory.Exists(inc) ? inc : db.ModFolder;
        }

        private static IEnumerable<string> TextDirs(YokaiDatabase db, bool modOnly)
        {
            string inc = IncBase(db);
            if (inc != null) yield return Path.Combine(inc, "data", "txt", "ev", "en");
            if (!modOnly && db?.ReferenceFolder != null)
            {
                yield return Path.Combine(db.ReferenceFolder, "ev", "en");
                yield return Path.Combine(db.ReferenceFolder, "data", "txt", "ev", "en");
            }
        }

        /// <summary>All event names that have a dialogue text file (mod + reference), sorted.</summary>
        public static List<string> EventNames(YokaiDatabase db)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in TextDirs(db, false))
                if (Directory.Exists(dir))
                    foreach (var f in Directory.EnumerateFiles(dir, "*_en.cfg.bin"))
                    {
                        string n = Path.GetFileName(f);
                        set.Add(n.Substring(0, n.Length - "_en.cfg.bin".Length));
                    }
            return set.ToList();
        }

        public static string FindText(YokaiDatabase db, string ev, out bool fromMod)
        {
            fromMod = false;
            foreach (var dir in TextDirs(db, false))
            {
                string p = Path.Combine(dir, ev + "_en.cfg.bin");
                if (File.Exists(p)) { fromMod = IsMod(db, p); return p; }
            }
            return null;
        }

        public static string FindWasha(YokaiDatabase db, string ev)
        {
            string inc = IncBase(db);
            foreach (var dir in new[] {
                inc != null ? Path.Combine(inc, "data", "txt", "ev") : null,
                db?.ReferenceFolder != null ? Path.Combine(db.ReferenceFolder, "ev") : null,
                db?.ReferenceFolder != null ? Path.Combine(db.ReferenceFolder, "data", "txt", "ev") : null })
            {
                if (dir == null) continue;
                string p = Path.Combine(dir, ev + "_map.cfg.bin");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public static string ModTextPath(YokaiDatabase db, string ev)
        {
            string inc = IncBase(db);
            return inc == null ? null : Path.Combine(inc, "data", "txt", "ev", "en", ev + "_en.cfg.bin");
        }
        public static string ModWashaPath(YokaiDatabase db, string ev)
        {
            string inc = IncBase(db);
            return inc == null ? null : Path.Combine(inc, "data", "txt", "ev", ev + "_map.cfg.bin");
        }

        private static bool IsMod(YokaiDatabase db, string path)
        {
            string inc = IncBase(db);
            return inc != null && path.StartsWith(inc, StringComparison.OrdinalIgnoreCase);
        }
    }
}
