using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lycoris.Formats;
using VT = Lycoris.Formats.ValueType;

namespace Lycoris.Yokai
{
    /// <summary>One registered event in event_set_config (17 int fields; field[0] = CRC32(event name)).</summary>
    public sealed class EventEntry
    {
        public int Hash;
        public string Name;              // resolved from a matching .xq filename, else null
        public T2bEntry Entry;
        public string HashHex => $"0x{unchecked((uint)Hash):X8}";
        public string Display => Name != null ? Name : HashHex;
        public string Label => Name != null ? $"{Name}   ({HashHex})" : HashHex;
    }

    /// <summary>
    /// event_set_config_0.01: the list of events the game knows about. Event ids are stored as CRC32 hashes of
    /// the event name; names are recovered by hashing the .xq filenames under seq/event. Also builds and
    /// compiles a "Daily Fight" event script (an evXX_YYY0.xq).
    /// </summary>
    public sealed class EventSet
    {
        public T2bFile File;
        public string ConfigPath;
        public readonly List<EventEntry> Events = new List<EventEntry>();

        private const string Rec = "EVENT_SET_CONFIG";
        private const string Begin = "EVENT_SET_CONFIG_LIST_BEG";
        private const string End = "EVENT_SET_CONFIG_LIST_END";
        private const int NameHashField = 0;

        public static int NameHash(string eventName) =>
            unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(eventName ?? "")));

        public static EventSet Load(string configPath, IEnumerable<string> eventXqDirs)
        {
            var es = new EventSet { ConfigPath = configPath, File = T2bReader.ReadFile(configPath) };
            var names = BuildNameMap(eventXqDirs);
            foreach (var e in es.File.Records(Rec))
            {
                int h = e.GetInt(NameHashField) ?? 0;
                names.TryGetValue(h, out string nm);
                es.Events.Add(new EventEntry { Hash = h, Name = nm, Entry = e });
            }
            return es;
        }

        private static Dictionary<int, string> BuildNameMap(IEnumerable<string> dirs)
        {
            var map = new Dictionary<int, string>();
            foreach (var dir in dirs.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)))
                foreach (var f in Directory.EnumerateFiles(dir, "*.xq"))
                {
                    string nm = Path.GetFileNameWithoutExtension(f);
                    int h = NameHash(nm);
                    if (!map.ContainsKey(h)) map[h] = nm;
                }
            return map;
        }

        public bool Contains(int hash) => Events.Any(e => e.Hash == hash);

        /// <summary>Register an event id in the config, cloning the vanilla daily-fight flag pattern.</summary>
        public EventEntry AddEvent(int hash, string name)
        {
            var tpl = File.Records(Rec).FirstOrDefault()
                      ?? throw new InvalidOperationException("event_set_config has no records to clone.");
            var e = tpl.Clone();
            for (int i = 0; i < e.Values.Count; i++) { e.Values[i].Type = VT.Integer; e.Values[i].Value = 0; }
            // Daily-fight flag pattern observed on ev75_5100: fields 3,5,9,15 = 1, rest 0.
            SetInt(e, 0, hash);
            SetInt(e, 3, 1); SetInt(e, 5, 1); SetInt(e, 9, 1); SetInt(e, 15, 1);
            int endIdx = File.Entries.FindIndex(x => x.Name == End);
            if (endIdx < 0) File.Entries.Add(e); else File.Entries.Insert(endIdx, e);
            Bump(1);
            var entry = new EventEntry { Hash = hash, Name = name, Entry = e };
            Events.Add(entry);
            return entry;
        }

        /// <summary>Add a blank event (all flags 0, field[0] = hash).</summary>
        public EventEntry AddBlankEvent(int hash, string name)
        {
            var tpl = File.Records(Rec).FirstOrDefault()
                      ?? throw new InvalidOperationException("event_set_config has no records to clone.");
            var e = tpl.Clone();
            for (int i = 0; i < e.Values.Count; i++) { e.Values[i].Type = VT.Integer; e.Values[i].Value = 0; }
            SetInt(e, 0, hash);
            InsertBeforeEnd(e);
            Bump(1);
            var entry = new EventEntry { Hash = hash, Name = name, Entry = e };
            Events.Add(entry);
            return entry;
        }

        /// <summary>Clone an existing event's record with a new id/name.</summary>
        public EventEntry DuplicateEvent(EventEntry src, int newHash, string newName)
        {
            var e = src.Entry.Clone();
            SetInt(e, 0, newHash);
            InsertBeforeEnd(e);
            Bump(1);
            var entry = new EventEntry { Hash = newHash, Name = newName, Entry = e };
            Events.Add(entry);
            return entry;
        }

        public void RemoveEvent(EventEntry ev)
        {
            if (ev?.Entry == null) return;
            if (File.Entries.Remove(ev.Entry)) Bump(-1);
            Events.Remove(ev);
        }

        /// <summary>Rename (re-hash) an event in place: field[0] = new hash.</summary>
        public void SetEventHash(EventEntry ev, int newHash, string newName)
        {
            SetInt(ev.Entry, 0, newHash);
            ev.Hash = newHash;
            ev.Name = newName;
        }

        /// <summary>Edit one of the 17 config fields of an event.</summary>
        public void SetField(EventEntry ev, int index, int value) => SetInt(ev.Entry, index, value);
        public int GetField(EventEntry ev, int index) => ev.Entry.GetInt(index) ?? 0;

        private void InsertBeforeEnd(T2bEntry e)
        {
            int endIdx = File.Entries.FindIndex(x => x.Name == End);
            if (endIdx < 0) File.Entries.Add(e); else File.Entries.Insert(endIdx, e);
        }

        private static void SetInt(T2bEntry e, int i, int v) { if (i < e.Values.Count) { e.Values[i].Type = VT.Integer; e.Values[i].Value = v; } }
        private void Bump(int d)
        {
            var b = File.Entries.FirstOrDefault(x => x.Name == Begin);
            if (b != null && b.Values.Count > 0 && b.Values[0].Value is int c) b.Values[0].Value = c + d;
        }

        // ---------------------------------------------------------------- daily-fight XQ source

        /// <summary>
        /// Build the compile-form XQ source of a "Daily Fight" event, mirroring vanilla ev75_5100:
        /// bustups, intro/accept/decline dialogue (evName_010/_020/_030), an autosave prompt, a per-fight
        /// flag, and load_battle_ev(battleId). Compile with NpcXq.CompileScript.
        /// </summary>
        public static string BuildDailyFightSource(string eventName, IEnumerable<string> bustups, string battleExpr,
            uint flag, string girlBustup = null, string boyBustup = null, bool genderDialogue = false)
        {
            var sb = new StringBuilder();
            void C(string line) => sb.Append('\t').Append(line).Append('\n');
            sb.Append("Main()\n{\n");
            C("$local1 = seq.prog_common_001.StartTalkEvent();");
            C("$local1 = seq.prog_common_001.LoadEncountEffectType(1);");
            EmitBustups(sb, C, bustups, girlBustup, boyBustup);
            C("$local1 = seq.prog_common_001.WaitBGBuild();");
            C("$local1 = seq.prog_common_001.StartBrightFade(-1, 0f, 15, 1);");
            C("$local1 = seq.prog_common_001.EventTalk_CameraZoomStartNoWait();");
            EmitTalk(sb, C, eventName, "_010", genderDialogue, "ti");
            C("$object0 = seq.prog_common_001.EventTalkRun(\"sys_autosave_battle\");");
            C("$local1 = $object0 == 0;");
            C("if not $local1 goto \"@000@\"h;");
            C($"$local1 = seq.prog_common_001.EventTalkRun(\"{eventName}_020\");");
            C($"$local1 = sub14025(0x{flag:X8}h, 1);");
            C("$local1 = seq.prog_menu_00501.SaveWindowOnly(1, 1);");
            C("$local1 = seq.prog_common_001.RunEvEncountEffectType(1);");
            C($"$local1 = load_battle_ev({battleExpr});");
            sb.Append("\"@000@\":\n");
            C("$local1 = $object0 == 1;");
            C("if not $local1 goto \"@001@\"h;");
            C($"$local1 = seq.prog_common_001.EventTalkRun(\"{eventName}_030\");");
            C("$local1 = seq.prog_common_001.WaitFrame2(15);");
            C("$local1 = seq.prog_common_001.EventTalk_CameraZoomEndNoWait();");
            C("$local1 = seq.prog_common_001.EventTalk_CameraZoomWait();");
            sb.Append("\"@001@\":\n");
            C("$local1 = seq.prog_common_001.EndTalkEvent();");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>
        /// Build a dialogue-only daily-fight event (the battle-WIN or battle-LOSE result event), mirroring
        /// vanilla ev75_5420: bustups (with an optional Katie/Nate branch), face the NPC, one dialogue block
        /// (evName_010), no battle. <paramref name="turnTarget"/> is the NPC model faced (PlayerTurnTargetStart).
        /// </summary>
        public static string BuildDailyDialogueSource(string eventName, IEnumerable<string> bustups,
            string turnTarget, string girlBustup = null, string boyBustup = null, bool genderDialogue = false)
        {
            var sb = new StringBuilder();
            void C(string line) => sb.Append('\t').Append(line).Append('\n');
            sb.Append("Main()\n{\n");
            C("$local1 = seq.prog_common_001.StartTalkEvent();");
            C("$local1 = sub26051();");
            EmitBustups(sb, C, bustups, girlBustup, boyBustup);
            if (!string.IsNullOrWhiteSpace(turnTarget))
            {
                C("$local1 = seq.prog_common_001.MoveCharaRotToPlayerWithLookAt();");
                C($"$local1 = seq.prog_common_001.PlayerTurnTargetStart(\"{turnTarget.Trim()}\");");
            }
            C("$local1 = seq.prog_common_001.EventTalk_CameraZoomDirect();");
            C("$local1 = seq.prog_common_001.WaitBGBuild();");
            C("$local1 = seq.prog_common_001.StartBrightFade(-1, 0f, 15, 1);");
            EmitTalk(sb, C, eventName, "_010", genderDialogue, "td");
            C("$local1 = seq.prog_common_001.WaitFrame2(15);");
            C("$local1 = seq.prog_common_001.EventTalk_CameraZoomEndNoWait();");
            C("$local1 = seq.prog_common_001.EventTalk_CameraZoomWait();");
            C("$local1 = seq.prog_common_001.EndTalkEvent();");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>The male dialogue block key for a female block (e.g. "_010" → "_011"): increment the last digit.</summary>
        public static string MaleBlock(string block)
        {
            if (string.IsNullOrEmpty(block)) return block;
            char last = block[block.Length - 1];
            char bumped = char.IsDigit(last) && last != '9' ? (char)(last + 1) : last;
            return block.Substring(0, block.Length - 1) + bumped;
        }

        /// <summary>Emit an EventTalkRun for a dialogue block, optionally branching on get_player_type() so the
        /// girl (==2) sees {block} and the boy sees {MaleBlock(block)}. labelTag must be unique in the script.</summary>
        private static void EmitTalk(StringBuilder sb, Action<string> C, string eventName, string block,
            bool gendered, string labelTag)
        {
            if (!gendered)
            {
                C($"$local1 = seq.prog_common_001.EventTalkRun(\"{eventName}{block}\");");
                return;
            }
            C("$local2 = get_player_type();");
            C("$local1 = $local2 == 2;");
            C($"if not $local1 goto \"@{labelTag}m@\"h;");
            C($"$local1 = seq.prog_common_001.EventTalkRun(\"{eventName}{block}\");");
            C($"goto \"@{labelTag}e@\"h;");
            sb.Append($"\"@{labelTag}m@\":\n");
            C($"$local1 = seq.prog_common_001.EventTalkRun(\"{eventName}{MaleBlock(block)}\");");
            sb.Append($"\"@{labelTag}e@\":\n");
        }

        /// <summary>Emit BustupAssign lines, optionally wrapping a girl/boy pair in a get_player_type() branch
        /// (==2 → Katie/Hailey). Labels @g0@/@g1@ are distinct from the daily-fight source's @000@/@001@.</summary>
        private static void EmitBustups(StringBuilder sb, Action<string> C, IEnumerable<string> bustups,
            string girlBustup, string boyBustup)
        {
            foreach (var m in (bustups ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)))
                C($"$local1 = seq.prog_common_001.BustupAssign(\"{m.Trim()}\", -1, -1);");
            if (!string.IsNullOrWhiteSpace(girlBustup) && !string.IsNullOrWhiteSpace(boyBustup))
            {
                C("$local2 = get_player_type();");
                C("$local1 = $local2 == 2;");
                C("if not $local1 goto \"@g0@\"h;");
                C($"$local1 = seq.prog_common_001.BustupAssign(\"{girlBustup.Trim()}\", -1, -1);");
                C("goto \"@g1@\"h;");
                sb.Append("\"@g0@\":\n");
                C($"$local1 = seq.prog_common_001.BustupAssign(\"{boyBustup.Trim()}\", -1, -1);");
                sb.Append("\"@g1@\":\n");
            }
        }

        /// <summary>
        /// Build a simple/blank event: a single dialogue block (evName_010) with a camera zoom, no battle.
        /// Uses the bare-function form (StartTalkEvent(), etc.) — xtractquery resolves these to prog_common.
        /// </summary>
        public static string BuildBlankEventSource(string eventName)
        {
            var sb = new StringBuilder();
            void C(string line) => sb.Append('\t').Append(line).Append('\n');
            sb.Append("Main()\n{\n");
            C("$local1 = StartTalkEvent();");
            C("$local1 = BrightFadeAllN(30f);");
            C("$local1 = EventTalk_CameraZoomStartNoWait();");
            C($"$local1 = EventTalkRun(\"{eventName}_010\");");
            C("$local1 = EventTalk_CameraZoomEndNoWait();");
            C("$local1 = EventTalk_CameraZoomWait();");
            C("$local1 = EndTalkEvent();");
            C("$local1 = sub2502(1, -1f, 0f);");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>The XQ an NPC's OnTalk uses to trigger an event.</summary>
        public static string NpcRunSnippet(string eventName) => $"$local1 = RunEvent(\"{eventName}\");";

        /// <summary>
        /// Convert xtractquery's DECOMPILED namespaces (YW3.prog_common_0.07.10.…, which won't recompile
        /// because of the dotted version) into the COMPILE form (seq.prog_common_001.…) so the source can be
        /// edited and recompiled. Verified to round-trip real events byte-for-byte.
        /// </summary>
        public static string ToCompilable(string decompiled)
        {
            if (string.IsNullOrEmpty(decompiled)) return decompiled ?? "";
            string s = decompiled
                .Replace("YW3.prog_common_0.07.10", "seq.prog_common_001")
                .Replace("YW3.prog_menu_00501", "seq.prog_menu_00501");
            // best-effort for any other decompiled namespace (e.g. map scripts): swap the YW3. prefix.
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\bYW3\.", "seq.");
            return s;
        }

        /// <summary>
        /// A blank battle-event script (the .xq named in a battle's BattleScript field). Defines the standard
        /// BattleEvent_* entry points the engine calls, with empty bodies you can fill in.
        /// </summary>
        public static string BuildBlankBattleSource()
        {
            return
"BattleEventInit()\n{\n\t$local1 = log(\"BattleEventInit\");\n}\n\n" +
"BattleEvent_Finalize()\n{\n\t$local1 = log(\"BattleEvent_Finalize\");\n}\n\n" +
"BattleEvent_OnStartEvent()\n{\n}\n\n" +
"BattleEvent_OnBattleEndEvent($param0, $param1, $param2)\n{\n}\n\n" +
"BattleEvent_OnHit($param0, $param1, $param2, $param3, $param4)\n{\n}\n\n" +
"BattleEvent_OnLastHit($param0, $param1, $param2, $param3, $param4, $param5)\n{\n}\n\n" +
"BattleEvent_OnActStart($param0, $param1)\n{\n}\n\n" +
"BattleEvent_OnActEnd($param0, $param1, $param2, $param3, $param4)\n{\n}\n\n" +
"BattleEvent_OnTurnStart($param0, $param1)\n{\n}\n\n" +
"BattleEvent_OnExecuteCommand($param0, $param1, $param2, $param3, $param4)\n{\n}\n\n" +
"BattleEvent_OnCmdEnd($param0, $param1, $param2, $param3, $param4)\n{\n}\n\n" +
"BattleEvent_OnSpSkillDemoStart($param0, $param1)\n{\n}\n\n" +
"BattleEvent_OnSpSkillDemoEnd($param0, $param1)\n{\n}\n\n" +
"BattleEvent_OnExtraObjectHit($param0, $param1)\n{\n}\n";
        }
    }

    /// <summary>One spoken page: the text and the speaker model whose name shows in the box.</summary>
    public sealed class DialogueLine
    {
        public string Speaker;   // model name (c001000, y597000…); TalkerBaseID = CRC32(model)
        public string Text;
    }

    /// <summary>
    /// Builds a per-event dialogue text file (TEXT_INFO) and washamap (TEXT_WASHA_MAP) for the three daily-fight
    /// blocks. Each block's key = CRC32(eventName + "_010"/"_020"/"_030") — the same key the .xq's EventTalkRun
    /// passes. Files are built by cloning real vanilla files (to keep the exact CfgBin structure) then swapping
    /// the records. Both key on KeyID + page index, so they must stay in lockstep.
    /// </summary>
    public static class EventDialogue
    {
        private const string TxtRec = "TEXT_INFO", TxtBeg = "TEXT_INFO_BEGIN", TxtEnd = "TEXT_INFO_END";
        private const string WmRec = "TEXT_WASHA_MAP", WmBeg = "TEXT_WASHA_MAP_BEGIN", WmEnd = "TEXT_WASHA_MAP_END";

        public static readonly string[] Blocks = { "_010", "_020", "_030" };

        public static int Talker(string model) =>
            unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(model ?? "")));

        /// <summary>Fill <paramref name="textFile"/>/<paramref name="washaFile"/> (templates) with the three
        /// standard blocks (_010/_020/_030).</summary>
        public static void Build(T2bFile textFile, T2bFile washaFile, string eventName,
            IList<DialogueLine> intro, IList<DialogueLine> accept, IList<DialogueLine> decline)
        {
            BuildBlocks(textFile, washaFile, eventName, new[]
            {
                ("_010", intro), ("_020", accept), ("_030", decline),
            });
        }

        /// <summary>Fill the templates with an arbitrary set of (block suffix, lines) — used for gendered
        /// dialogue where a block has a female key (_010) and a male key (_011).</summary>
        public static void BuildBlocks(T2bFile textFile, T2bFile washaFile, string eventName,
            IEnumerable<(string suffix, IList<DialogueLine> lines)> blocks)
        {
            var txtTpl = textFile.Records(TxtRec).FirstOrDefault()?.Clone()
                         ?? throw new InvalidOperationException("Text template has no TEXT_INFO record to clone.");
            var wmTpl = washaFile.Records(WmRec).FirstOrDefault()?.Clone()
                        ?? throw new InvalidOperationException("Washamap template has no TEXT_WASHA_MAP record to clone.");

            ClearGroup(textFile, TxtRec, TxtBeg);
            ClearGroup(washaFile, WmRec, WmBeg);

            foreach (var (suffix, lines) in blocks)
            {
                if (lines == null) continue;
                int key = EventSet.NameHash(eventName + suffix);
                for (int i = 0; i < lines.Count; i++)
                {
                    var l = lines[i];
                    if (l == null || string.IsNullOrEmpty(l.Text)) continue;

                    var tr = txtTpl.Clone();
                    SetI(tr, 0, key); SetI(tr, 1, i); SetS(tr, 2, l.Text); SetI(tr, 3, 0);
                    InsertBefore(textFile, TxtEnd, tr); Bump(textFile, TxtBeg, 1);

                    var wr = wmTpl.Clone();
                    SetI(wr, 0, key); SetI(wr, 1, i); SetI(wr, 2, Talker(l.Speaker));
                    SetI(wr, 3, 0); SetI(wr, 4, -1); SetI(wr, 5, 0);
                    InsertBefore(washaFile, WmEnd, wr); Bump(washaFile, WmBeg, 1);
                }
            }
        }

        private static void ClearGroup(T2bFile f, string rec, string begin)
        {
            f.Entries.RemoveAll(e => e.Name == rec);
            var b = f.Entries.FirstOrDefault(e => e.Name == begin);
            if (b != null && b.Values.Count > 0) { b.Values[0].Type = VT.Integer; b.Values[0].Value = 0; }
        }
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
        private static void SetI(T2bEntry e, int i, int v) { if (i < e.Values.Count) { e.Values[i].Type = VT.Integer; e.Values[i].Value = v; } }
        private static void SetS(T2bEntry e, int i, string v) { if (i < e.Values.Count) { e.Values[i].Type = VT.String; e.Values[i].Value = v ?? ""; } }
    }
}
