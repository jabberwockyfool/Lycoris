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
        internal T2bValue TransValue;     // translation-mode: the live string value to write on Save (bypasses TEXT_INFO)
        internal string Original;         // translation-mode: the untranslated source string (reference)
        public int KeyId;
        public int Page;
        public int Variant;
        public string KeyLabel;           // resolved block key (e.g. "_010") or the hex hash
        public string SpeakerName;        // resolved model name for the talker, or null

        private string _text;
        private int _talker;
        public string Text { get => _text; set { _text = value; Raise(nameof(Text)); Raise(nameof(Preview)); Raise(nameof(TransPreview)); } }
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

        /// <summary>Translation-mode list label: a done/todo marker + the original source string.</summary>
        public string TransPreview
        {
            get
            {
                string o = (Original ?? "").Replace("\r", " ").Replace("\n", " ");
                if (o.Length > 60) o = o.Substring(0, 60) + "…";
                bool done = !string.IsNullOrEmpty(_text) && _text != Original;
                return (done ? "✔  " : "•  ") + o;
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

        /// <summary>
        /// Make sure <paramref name="row"/> has a name-box (TEXT_WASHA_MAP) entry so its speaker can be set:
        /// if the event has no washamap at all, create one from <paramref name="templateWashaPath"/> (a vanilla
        /// *_map.cfg.bin, cloned then emptied); then append an entry for the row with the given talker.
        /// No-op if the row already has a washa entry.
        /// </summary>
        public static void EnsureWasha(DialogueFile f, DialogueLineRow row, string templateWashaPath, int talkerBaseId)
        {
            if (row == null) return;

            T2bEntry recTemplate;
            if (f.WashaData == null)
            {
                if (string.IsNullOrEmpty(templateWashaPath) || !File.Exists(templateWashaPath))
                    throw new InvalidOperationException("No vanilla *_map.cfg.bin found to use as a name-box template.");
                f.WashaData = T2bReader.ReadFile(templateWashaPath);
                recTemplate = f.WashaData.Records(WmRec).FirstOrDefault()?.Clone()
                              ?? throw new InvalidOperationException("The name-box template has no TEXT_WASHA_MAP record.");
                // The template came from another event — empty its records so we start clean.
                f.WashaData.Entries.RemoveAll(e => e.Name == WmRec);
                var beg = f.WashaData.Entries.FirstOrDefault(e => e.Name == WmBeg);
                if (beg != null && beg.Values.Count > 0) { beg.Values[0].Type = VT.Integer; beg.Values[0].Value = 0; }
                f.WashaPath = null; // written to the mod washa path on Save
            }
            else
            {
                recTemplate = f.WashaData.Records(WmRec).FirstOrDefault()?.Clone()
                              ?? throw new InvalidOperationException("The washamap has no TEXT_WASHA_MAP record to clone.");
            }

            if (row.WashaEntry != null) { row.TalkerBaseId = talkerBaseId; SetInt(row.WashaEntry, W_Talker, talkerBaseId); return; }

            var we = recTemplate;
            SetInt(we, W_Key, row.KeyId); SetInt(we, W_Page, row.Page); SetInt(we, W_Talker, talkerBaseId);
            SetInt(we, W_Var, row.Variant); SetInt(we, W_U4, -1); SetInt(we, W_U5, 0);
            InsertBefore(f.WashaData, WmEnd, we); Bump(f.WashaData, WmBeg, 1);
            row.WashaEntry = we; row.TalkerBaseId = talkerBaseId;
        }

        /// <summary>Move a line to another block (suffix, e.g. "_010" → "_020"): the key becomes
        /// CRC32(eventName + suffix) on both the text and its washamap entry. Events only (needs the event name).</summary>
        public static void SetBlock(DialogueLineRow row, string eventName, string suffix)
        {
            if (row == null || string.IsNullOrEmpty(suffix)) return;
            int key = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes((eventName ?? "") + suffix)));
            row.KeyId = key;
            row.KeyLabel = suffix;
            SetInt(row.TextEntry, T_Key, key);
            if (row.WashaEntry != null) SetInt(row.WashaEntry, W_Key, key);
        }

        /// <summary>
        /// Replace all pages of a block (identified by <paramref name="keyId"/>) with <paramref name="lines"/>,
        /// one page per line (page 0,1,2…), each line's Speaker → TalkerBaseID (CRC32). Creates the washamap /
        /// entries as needed (from <paramref name="washaTemplatePath"/> when the event has none). Returns the new rows.
        /// </summary>
        public static List<DialogueLineRow> ReplaceBlock(DialogueFile f, int keyId, string keyLabel,
            IList<DialogueLine> lines, string washaTemplatePath, YokaiDatabase db)
        {
            foreach (var r in f.Rows.Where(r => r.KeyId == keyId).ToList()) RemoveRow(f, r);

            var talkerNames = BuildTalkerMap(db);
            var added = new List<DialogueLineRow>();
            foreach (var l in lines ?? new List<DialogueLine>())
            {
                int talker = string.IsNullOrWhiteSpace(l.Speaker)
                    ? 0 : unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(l.Speaker.Trim())));
                var row = AddRow(f, keyId, keyLabel, l.Text ?? "", talker);   // appends at the next free page
                if (talker != 0 && row.WashaEntry == null) EnsureWasha(f, row, washaTemplatePath, talker);
                if (talker != 0 && talkerNames.TryGetValue(talker, out var nm)) row.SpeakerName = nm;
                added.Add(row);
            }
            return added;
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

    /// <summary>A browsable dialogue source: an event's text (data/txt/ev) or a map's NPC text (data/res/map).
    /// Carries its resolved source paths and where to write the edited copies in the mod.</summary>
    public sealed class DialogueTarget
    {
        public string Label;         // shown in the list
        public string Kind;          // "event" or "map"
        public string EventName;     // event name (key-label resolution); null for map targets
        public string TextPath;      // source text file (mod first, else reference)
        public string WashaPath;     // source washamap (may be null)
        public bool FromMod;
        public string ModTextPath;   // where the edited text is written in the mod
        public string ModWashaPath;  // where the edited washamap is written in the mod
        public override string ToString() => Label;
    }

    /// <summary>Finds and lists per-event dialogue files (mod's data/txt/ev, else the reference's ev).</summary>
    public static class DialoguePaths
    {
        /// <summary>Every editable dialogue source: event dialogues (data/txt/ev) + map NPC texts (data/res/map).</summary>
        public static List<DialogueTarget> AllTargets(YokaiDatabase db)
        {
            var list = new List<DialogueTarget>();
            foreach (var ev in EventNames(db))
                list.Add(new DialogueTarget
                {
                    Label = ev, Kind = "event", EventName = ev,
                    TextPath = FindText(db, ev, out bool fromMod), WashaPath = FindWasha(db, ev), FromMod = fromMod,
                    ModTextPath = ModTextPath(db, ev), ModWashaPath = ModWashaPath(db, ev),
                });
            list.AddRange(MapTextTargets(db));
            return list;
        }

        private static IEnumerable<string> MapRoots(YokaiDatabase db)
        {
            string inc = IncBase(db);
            if (inc != null) yield return Path.Combine(inc, "data", "res", "map");   // mod first
            if (db?.ReferenceFolder != null)
            {
                yield return Path.Combine(db.ReferenceFolder, "res", "map");
                yield return Path.Combine(db.ReferenceFolder, "data", "res", "map");
                yield return Path.Combine(db.ReferenceFolder, "include", "data", "res", "map");   // reorg: cfg/include/…
            }
        }

        // Map NPC text = <mapid>_..._c_en.cfg.bin, washamap = the same base with _map_c.cfg.bin.
        private static IEnumerable<DialogueTarget> MapTextTargets(YokaiDatabase db)
        {
            const string txtSuffix = "_c_en.cfg.bin", wmSuffix = "_map_c.cfg.bin";
            string inc = IncBase(db);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in MapRoots(db))
            {
                if (!Directory.Exists(root)) continue;
                foreach (var mapDir in Directory.EnumerateDirectories(root))
                {
                    string mapId = Path.GetFileName(mapDir);
                    foreach (var textFile in Directory.EnumerateFiles(mapDir, "*" + txtSuffix))
                    {
                        string fn = Path.GetFileName(textFile);
                        string baseName = fn.Substring(0, fn.Length - txtSuffix.Length); // e.g. t001d57_npc_text
                        if (!seen.Add(mapId + "/" + baseName)) continue;                 // mod copy already listed
                        string washaFile = Path.Combine(mapDir, baseName + wmSuffix);
                        yield return new DialogueTarget
                        {
                            Label = "🗺 " + baseName + "  (" + mapId + ")",
                            Kind = "map", EventName = null,
                            TextPath = textFile,
                            WashaPath = File.Exists(washaFile) ? washaFile : null,
                            FromMod = inc != null && textFile.StartsWith(inc, StringComparison.OrdinalIgnoreCase),
                            ModTextPath = inc == null ? null : Path.Combine(inc, "data", "res", "map", mapId, baseName + txtSuffix),
                            ModWashaPath = inc == null ? null : Path.Combine(inc, "data", "res", "map", mapId, baseName + wmSuffix),
                        };
                    }
                }
            }
        }

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
                yield return Path.Combine(db.ReferenceFolder, "include", "data", "txt", "ev", "en");  // reorg: cfg/include/…
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
                db?.ReferenceFolder != null ? Path.Combine(db.ReferenceFolder, "data", "txt", "ev") : null,
                db?.ReferenceFolder != null ? Path.Combine(db.ReferenceFolder, "include", "data", "txt", "ev") : null })
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
