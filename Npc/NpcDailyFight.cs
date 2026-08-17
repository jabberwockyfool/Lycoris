using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lycoris.Formats;
using VT = Lycoris.Formats.ValueType;

namespace Lycoris.Npc
{
    /// <summary>
    /// Builds the "daily-fight" talk wiring for an NPC that triggers a once-a-day battle: the 4 talk events
    /// (1st-interaction / repeat / battle-win / battle-lose). Reproduces the vanilla layout reverse-engineered
    /// from t001d57 (see the yw3-npc-dailyfight notes): appends records to the map's own npc_talk / trigger /
    /// text / flag files and generates 4 RunCmd_Map functions. Cond blobs are cloned from the real donor and
    /// only their embedded flag/trigger ids are swapped (via <see cref="YwCond"/>), so they stay byte-valid.
    /// </summary>
    public static class NpcDailyFight
    {
        // ---- donor cond-blob templates (real t001d57 bytes) + the donor id embedded in each (to remap) ----
        // Two different functions: GetGlobalBitFlag (permanent "seen" flag, func 2A3D4543) for config #1, and
        // GetOneDayBitFlag (resets daily "battled today" flag, func 08B21CED) for config #2 and win/lose triggers.
        //
        // GetGlobalBitFlag(id) != true — TALK_CONFIG context (trailing 0x79). Donor id = "seen" flag 0xB483FAA3.
        private const string TplGlobalConfigCtx = "AAAAABgFNSo9RUMACgEoAAYCNLSD+qMyAAAAAXk=";
        private static readonly int TplGlobalConfigDonor = unchecked((int)0xB483FAA3);
        // GetOneDayBitFlag(id) != true — TALK_CONFIG context (trailing 0x79). Donor id = "day done" flag 0xF2A22B7C.
        private const string TplOneDayConfigCtx = "AAAAABgFNQiyHO0ACgEoAAYCNPKiK3wyAAAAAXk=";
        private static readonly int TplOneDayConfigDonor = unchecked((int)0xF2A22B7C);
        // GetOneDayBitFlag(id) != true — TRIGGER context (trailing 0x78). Donor id = "day done" flag 0xF2A22B7C.
        private const string TplOneDayTrigCtx = "AAAAABgFNQiyHO0ACgEoAAYCNPKiK3wyAAAAAXg=";
        private static readonly int TplOneDayTrigDonor = unchecked((int)0xF2A22B7C);
        // RunTrigger(id) — TALK_CONFIG context. Donor id = talk-first trigger 0x19B40A96.
        private const string TplRunTrigger = "AAAAABICNWmE468ACgEoAAYCNBm0CpY=";
        private static readonly int TplRunTriggerDonor = 0x19B40A96;

        // flag_config groups, identified by their BEGIN field[0] (a category id, NOT a position): "seen" is a
        // permanent GetGlobalBitFlag flag → category 0 (the group shown as "FLAG_INFO_0"); "day done" is a
        // GetOneDayBitFlag flag → category 23 (the 7th group, shown as "FLAG_INFO_6"). CONFIRMED on vanilla:
        // the daily flag 0xF2A22B7C lives in the field[0]==23 group. Registering it elsewhere makes
        // GetOneDayBitFlag never see it (→ can re-fight same day, no win/lose).
        private const int FlagGroupGlobal = 0, FlagGroupOneDay = 23;

        // Trigger record types (constant for every daily NPC).
        private const int TrigTalkFirst = 12, TrigTalkRepeat = 12, TrigBattleWin = 80, TrigBattleLose = 81;

        public sealed class Result
        {
            public int[] FuncIds = new int[4];      // RunCmd_Map ids: first, repeat, win, lose
            public int FlagSeen;                    // set in RunCmd_Map#1, checked by the 1st config
            public int FlagDayDone;                 // = the event's sub14025 flag (CRC32 of the event)
            public int TrigFirst, TrigRepeat, TrigBattle; // trigger ids (win+lose share TrigBattle)
            public string[] Events = new string[4]; // ev+0/+10/+20/+30
            public int TextId;
            public byte[] NewXq;
            public string XqLog = "";
        }

        /// <summary>
        /// Apply the daily-fight wiring in-place to the loaded files and return the ids/new xq. The caller owns
        /// reading/writing the files and repacking the map .pck.
        /// </summary>
        public static Result Apply(NpcModel npc, int npcId, int baseId,
            T2bFile npcTalk, T2bFile trigger, byte[] mapXq,
            T2bFile textEn, T2bFile textMap, T2bFile flagConfig, int dayDoneOverride = 0)
        {
            if (string.IsNullOrWhiteSpace(npc.DailyFightEvent))
                throw new InvalidOperationException("A daily-fight NPC needs a base event name (evXX_YYY0).");

            var r = new Result();
            string ev = npc.DailyFightEvent.Trim();
            r.Events = new[] { ev, StepEvent(ev, 10), StepEvent(ev, 20), StepEvent(ev, 30) };

            // Deterministic, unique ids derived from the NPC name. The day-done flag defaults to CRC32(eventName)
            // (what Lycoris-generated events set via sub14025); a patch of REUSED events can override it with the
            // flag those events actually set, so the win/lose + "come tomorrow" gate matches.
            r.FlagSeen = UniqueFlag(flagConfig, Crc(npc.NpcName + "_daily_seen"));
            r.FlagDayDone = dayDoneOverride != 0 ? dayDoneOverride : Crc(ev);
            r.TrigFirst = Crc(npc.NpcName + "_dtrig_first");
            r.TrigRepeat = Crc(npc.NpcName + "_dtrig_repeat");
            // The win/lose triggers are routed by the BATTLE id: the game matches the battle that just ended
            // (its id = the load_battle_ev value) to a type-80/81 trigger's field[1]. So it MUST equal the
            // battle id, not an arbitrary hash. (Confirmed on vanilla: CRC32("enc_day_y780000_01") = the id.)
            r.TrigBattle = string.IsNullOrWhiteSpace(npc.DailyBattle)
                ? Crc(npc.NpcName + "_dtrig_battle")
                : BattleId(npc.DailyBattle);
            r.TextId = Crc(npc.NpcName + "_daily_tomorrow");
            int talker = baseId != 0 ? baseId : npcId;

            // --- flag_config: "seen" is a permanent flag (FLAG_INFO_0); "day done" is a once-a-day flag
            //     (FLAG_INFO_6) — the same flag the event's sub14025 toggles. ---
            AddFlagIfAbsent(flagConfig, r.FlagSeen, FlagGroupGlobal);
            AddFlagIfAbsent(flagConfig, r.FlagDayDone, FlagGroupOneDay);

            // --- map .xq: 4 RunCmd_Map (first sets the "seen" flag), each RunEvent(ev+N) ---
            string setFlag = $"\t$local1 = set_global_bit_flag(0x{unchecked((uint)r.FlagSeen):X8}h, 1);\n";
            var bodies = new List<string>
            {
                setFlag + RunEvent(r.Events[0]),
                RunEvent(r.Events[1]),
                RunEvent(r.Events[2]),
                RunEvent(r.Events[3]),
            };
            r.NewXq = NpcXq.AppendFunctions(mapXq, bodies, out int firstFunc, out r.XqLog);
            for (int i = 0; i < 4; i++) r.FuncIds[i] = firstFunc + i;

            // --- <MapID>_trigger: 4 items (talk-first/repeat = type 12; win/lose = 80/81 sharing TrigBattle) ---
            string winLoseCond = YwCond.RemapBase64(TplOneDayTrigCtx, TplOneDayTrigDonor, r.FlagDayDone);
            AddTrigger(trigger, TrigTalkFirst, r.TrigFirst, IntV(0), r.FuncIds[0]);
            AddTrigger(trigger, TrigTalkRepeat, r.TrigRepeat, IntV(0), r.FuncIds[1]);
            AddTrigger(trigger, TrigBattleWin, r.TrigBattle, StrV(winLoseCond), r.FuncIds[2]);
            AddTrigger(trigger, TrigBattleLose, r.TrigBattle, StrV(winLoseCond), r.FuncIds[3]);

            // --- npc_talk_0.01: TALK_PAGE (new text) -> 3 TALK_CONFIG -> TALK_INFO ---
            int cfgStart = GroupCount(npcTalk, "TALK_CONFIG_BEGIN", 0);
            int pageStart = GroupCount(npcTalk, "TALK_PAGE_BEGIN", 0);

            AddRecord(npcTalk, "TALK_PAGE", "TALK_PAGE_BEGIN", "TALK_PAGE_END", 0,
                IntV(r.TextId), IntV(-1));

            string condSeen = YwCond.RemapBase64(TplGlobalConfigCtx, TplGlobalConfigDonor, r.FlagSeen);
            string condDayDone = YwCond.RemapBase64(TplOneDayConfigCtx, TplOneDayConfigDonor, r.FlagDayDone);
            string trigFirstCond = YwCond.RemapBase64(TplRunTrigger, TplRunTriggerDonor, r.TrigFirst);
            string trigRepeatCond = YwCond.RemapBase64(TplRunTrigger, TplRunTriggerDonor, r.TrigRepeat);

            // config #1: talk-first — GetGlobalBitFlag("seen"); runs the first trigger.
            AddConfig(npcTalk, pageStart, 0, StrV(condSeen), StrV(trigFirstCond));
            // config #2: repeat — GetOneDayBitFlag("day done"); runs the repeat trigger.
            AddConfig(npcTalk, pageStart, 0, StrV(condDayDone), StrV(trigRepeatCond));
            // config #3: fallback — shows the "come back tomorrow" page (no cond).
            AddConfig(npcTalk, pageStart, 1, IntV(0), IntV(0));

            AddRecord(npcTalk, "TALK_INFO", "TALK_INFO_BEGIN", "TALK_INFO_END", 0,
                IntV(npcId), IntV(cfgStart), IntV(3));

            // --- text: the "come back tomorrow" dialogue (one page per line; "model|text" sets a page's speaker) ---
            var tomorrow = ParseLines(npc.TomorrowText, talker);
            if (tomorrow.Count == 0) tomorrow.Add((talker, ""));
            for (int page = 0; page < tomorrow.Count; page++)
            {
                AddRecord(textEn, "TEXT_INFO", "TEXT_INFO_BEGIN", "TEXT_INFO_END", 0,
                    IntV(r.TextId), IntV(page), StrV(tomorrow[page].text), IntV(0));
                AddRecord(textMap, "TEXT_WASHA_MAP", "TEXT_WASHA_MAP_BEGIN", "TEXT_WASHA_MAP_END", 0,
                    IntV(r.TextId), IntV(page), IntV(tomorrow[page].talker), IntV(0), IntV(-1), IntV(0));
            }

            return r;
        }

        // ---------- record builders ----------

        private static void AddConfig(T2bFile f, int pageStart, int pageLen, T2bValue conditional, T2bValue trig)
        {
            AddRecord(f, "TALK_CONFIG", "TALK_CONFIG_BEGIN", "TALK_CONFIG_END", 0,
                IntV(1), IntV(0), IntV(pageStart), IntV(pageLen), IntV(0), IntV(0), IntV(-1), IntV(-1),
                conditional, trig);
        }

        private static void AddTrigger(T2bFile f, int type, int id, T2bValue field3, int funcId)
        {
            // DATA_COUNT + flat DATA_ITEM list (no _END): append then bump DATA_COUNT.
            var tpl = f.Records("DATA_ITEM").LastOrDefault()
                      ?? throw new InvalidDataException("Trigger has no DATA_ITEM to clone.");
            var e = tpl.Clone();
            e.Values = new List<T2bValue> { IntV(type), IntV(id), IntV(0), field3, IntV(0), IntV(0), IntV(funcId) };
            int at = f.Entries.FindLastIndex(x => x.Name == "DATA_ITEM");
            f.Entries.Insert(at + 1, e);
            var count = f.Entries.FirstOrDefault(x => x.Name == "DATA_COUNT");
            if (count != null && count.Values.Count > 0 && count.Values[0].Value is int c) count.Values[0].Value = c + 1;
        }

        /// <summary>Clone a same-named record (for its name/crc), set its values, insert before the group _END,
        /// and bump the count stored at <paramref name="countField"/> of the _BEGIN marker.</summary>
        private static void AddRecord(T2bFile f, string recordName, string beginName, string endName,
            int countField, params T2bValue[] values)
        {
            var tpl = f.Records(recordName).FirstOrDefault()
                      ?? throw new InvalidDataException($"No {recordName} to clone in this file.");
            var e = tpl.Clone();
            e.Values = new List<T2bValue>(values);
            int endIdx = f.Entries.FindIndex(x => x.Name == endName);
            if (endIdx < 0) f.Entries.Add(e); else f.Entries.Insert(endIdx, e);
            Bump(f, beginName, countField, +1);
        }

        // ---------- flags ----------

        /// <summary>Build the "once-a-day" ConditionalCond blob (GetOneDayBitFlag(flagId) != true, TALK_CONFIG
        /// context) for a talk config — same blob the daily maker's config #2 uses. Register the flag with
        /// <see cref="AddOneDayFlag"/> so GetOneDayBitFlag recognises it.</summary>
        public static string BuildOneDayConfigCond(int flagId) =>
            YwCond.RemapBase64(TplOneDayConfigCtx, TplOneDayConfigDonor, flagId);

        /// <summary>Register a once-a-day flag in the GetOneDayBitFlag group (FLAG_INFO_6, BEGIN field[0]==23).</summary>
        public static void AddOneDayFlag(T2bFile flagConfig, int flagId) => AddFlagIfAbsent(flagConfig, flagId, FlagGroupOneDay);

        /// <summary>Register a flag id in flag_config's group whose BEGIN field[0]==groupIndex (0 = permanent
        /// GlobalBitFlag), assigning the next free slot and bumping the group count. No-op if already present.
        /// Reused by the Mirapo/warp generator so its warp_&lt;mapid&gt; flag is recognised/persistent.</summary>
        public static void AddFlagIfAbsent(T2bFile flagConfig, int flagId, int groupIndex)
        {
            if (flagConfig == null) return;
            if (flagConfig.Records("FLAG_INFO").Any(e => (e.GetInt(1) ?? 0) == flagId)) return; // already registered

            // FLAG_INFO_<n> = the group whose BEGIN field[0] == n (0 = permanent flags, 6 = once-a-day flags).
            if (!FindGroup(flagConfig, "FLAG_INFO_BEGIN", "FLAG_INFO_END",
                    g => (flagConfig.Entries[g].GetInt(0) ?? -1) == groupIndex, out int begin, out int end))
                return;

            int maxSlot = -1;
            for (int i = begin + 1; i < end; i++)
                if (flagConfig.Entries[i].Name == "FLAG_INFO")
                    maxSlot = Math.Max(maxSlot, flagConfig.Entries[i].GetInt(0) ?? -1);

            var tpl = flagConfig.Records("FLAG_INFO").First().Clone();
            tpl.Values = new List<T2bValue> { IntV(maxSlot + 1), IntV(flagId) };
            flagConfig.Entries.Insert(end, tpl);
            // FLAG_INFO_BEGIN stores the group's count at field[1] (field[0] is the group index).
            var b = flagConfig.Entries[begin];
            if (b.Values.Count > 1 && b.Values[1].Value is int c) b.Values[1].Value = c + 1;
        }

        // Find a _BEGIN whose predicate holds and its matching _END; returns their indices.
        private static bool FindGroup(T2bFile f, string beginName, string endName, Func<int, bool> beginOk,
            out int beginIdx, out int endIdx)
        {
            beginIdx = endIdx = -1;
            for (int i = 0; i < f.Entries.Count; i++)
            {
                if (f.Entries[i].Name != beginName || !beginOk(i)) continue;
                for (int j = i + 1; j < f.Entries.Count; j++)
                    if (f.Entries[j].Name == endName) { beginIdx = i; endIdx = j; return true; }
            }
            return false;
        }

        // ---------- small helpers ----------

        private static int GroupCount(T2bFile f, string beginName, int field)
        {
            var b = f.Entries.FirstOrDefault(e => e.Name == beginName);
            return b != null ? (b.GetInt(field) ?? 0) : 0;
        }

        private static void Bump(T2bFile f, string beginName, int field, int d)
        {
            var b = f.Entries.FirstOrDefault(e => e.Name == beginName);
            if (b != null && field < b.Values.Count && b.Values[field].Value is int c) b.Values[field].Value = c + d;
        }

        private static int UniqueFlag(T2bFile flagConfig, int candidate)
        {
            if (flagConfig == null) return candidate;
            var used = new HashSet<int>(flagConfig.Records("FLAG_INFO").Select(e => e.GetInt(1) ?? 0));
            int id = candidate, guard = 0;
            while (used.Contains(id) && guard++ < 1000) id = unchecked((int)((uint)id * 31u + 0x9E3779B1u));
            return id;
        }

        private static string RunEvent(string ev) => $"\t$local1 = seq.prog_common_001.RunEvent(\"{ev}\");";

        /// <summary>Add <paramref name="add"/> to the trailing decimal run of an event name, keeping its width
        /// (e.g. ev75_5840 + 10 → ev75_5850).</summary>
        internal static string StepEvent(string name, int add)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int i = name.Length;
            while (i > 0 && char.IsDigit(name[i - 1])) i--;
            string num = name.Substring(i);
            if (num.Length == 0) return name;
            long v = long.Parse(num) + add;
            return name.Substring(0, i) + v.ToString(new string('0', num.Length));
        }

        /// <summary>The battle's identifying id from the picker text: a hash (from a "(0x…)" label or a hex
        /// entry), else CRC32 of the plain battle name (what the game derives from load_battle_ev("name")).
        /// This must equal the load_battle_ev value so the win/lose triggers match the battle that ended.</summary>
        public static int BattleId(string battle)
        {
            string t = (battle ?? "").Trim();
            int lp = t.LastIndexOf("(0x", StringComparison.OrdinalIgnoreCase);
            if (lp >= 0) { int rp = t.IndexOf(')', lp); if (rp > lp && TryHex(t.Substring(lp + 1, rp - lp - 1), out uint idb)) return unchecked((int)idb); }
            if (TryHex(t, out uint id)) return unchecked((int)id);
            return Crc(t);
        }

        private static bool TryHex(string s, out uint v)
        {
            v = 0; s = (s ?? "").Trim().TrimEnd('h', 'H');
            if (s.StartsWith("0x") || s.StartsWith("0X")) s = s.Substring(2);
            return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        /// <summary>Split a dialogue block into (talker, text) per non-empty line. A leading "model|" / "0xHEX|"
        /// sets that line's speaker (only a plausible id prefix is treated as one); otherwise the default talker.</summary>
        private static List<(int talker, string text)> ParseLines(string block, int defaultTalker)
        {
            var res = new List<(int talker, string text)>();
            foreach (var raw in (block ?? "").Replace("\r", "").Split('\n'))
            {
                if (raw.Trim().Length == 0) continue;
                int talker = defaultTalker; string text = raw;
                int bar = raw.IndexOf('|');
                if (bar > 0)
                {
                    string pre = raw.Substring(0, bar).Trim();
                    if (System.Text.RegularExpressions.Regex.IsMatch(pre, @"^(0x[0-9A-Fa-f]{1,8}|[A-Za-z]\d{6}(_\d{1,2})?)$"))
                    {
                        talker = pre.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                            ? unchecked((int)uint.Parse(pre.Substring(2), System.Globalization.NumberStyles.HexNumber))
                            : Crc(pre);
                        text = raw.Substring(bar + 1);
                    }
                }
                res.Add((talker, text));
            }
            return res;
        }

        private static int Crc(string s) => unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(s ?? "")));
        private static T2bValue IntV(int v) => new T2bValue(VT.Integer, v);
        private static T2bValue StrV(string v) => new T2bValue(VT.String, v ?? "");
    }
}
