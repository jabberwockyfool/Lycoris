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
    /// Reimplementation of NPCMake's YW3 "make" pipeline, fully in-process except the XQ step (xtractquery).
    /// Edits npc_set / npc_base_talk (CfgBin), builds the .npcbin from a vanilla template, injects it into
    /// npc.pck, and edits the map .pck (xq + trigger). Writes a &lt;NpcName&gt;_output/&lt;MapID&gt;/ folder to merge
    /// into a mod. Record layouts calibrated against real YW3 files (NPC_BASE/NPC_PRESET/NPC_APPEAR, etc.).
    /// </summary>
    public static class NpcCompiler
    {
        public sealed class Result
        {
            public string OutputDir;
            public string MergedDir;              // where files were merged into the mod (null if not merged)
            public int NpcId;
            public int FuncId = -1;
            public string XqLog = "";
            public List<string> Files = new List<string>();
            public string NpcIdHex => $"0x{unchecked((uint)NpcId):X8}";
            public NpcDailyFight.Result Daily;    // set for a daily-fight NPC
        }

        /// <summary>Locations of the global flag_config for a daily-fight NPC (resolved by the caller, which
        /// knows the mod/reference layout): where to READ it and where to WRITE the edited copy in the mod.</summary>
        public sealed class DailyPaths
        {
            public string FlagConfigSrc;   // path to read flag_config_0.01r.cfg.bin (mod copy first, else reference)
            public string FlagConfigDst;   // path to write the edited flag_config into the mod
        }

        /// <summary>
        /// Compile the NPC. Always writes a portable &lt;NpcName&gt;_output/&lt;MapID&gt;/ folder under
        /// <paramref name="outRoot"/>. If <paramref name="mergeMapDir"/> is given, the same files are also
        /// copied there (the map folder inside the mod) — the auto-merge.
        /// </summary>
        public static Result Compile(NpcModel npc, string mapFolder, string outRoot, string mergeMapDir = null,
            DailyPaths daily = null)
        {
            if (string.IsNullOrWhiteSpace(npc.NpcName)) throw new InvalidOperationException("The NPC must have a name.");
            string mapDir = ResolveMapDir(mapFolder, npc.MapID);
            if (mapDir == null)
                throw new InvalidOperationException($"Map folder not found for \"{npc.MapID}\" (npc.pck missing).");

            // Read each file from the mod's accumulated copy (mergeMapDir) first, so repeated compiles build on
            // top of previously-added NPCs; fall back to the base/vanilla map for anything not yet in the mod.
            string npcPckPath = PickFile(mergeMapDir, mapDir, "npc.pck");
            string mapPckPath = PickFile(mergeMapDir, mapDir, npc.MapID + ".pck");
            string npcSetPath = PickByPrefix(mergeMapDir, mapDir, npc.MapID + "_npc_set");
            string talkPath = PickByPrefix(mergeMapDir, mapDir, npc.MapID + "_npc_base_talk_" + npc.ChapterCode);
            foreach (var (p, label) in new[] { (npcPckPath, "npc.pck"), (mapPckPath, npc.MapID + ".pck") })
                if (p == null || !File.Exists(p)) throw new InvalidOperationException($"Required file missing: {label}");
            if (npcSetPath == null) throw new InvalidOperationException($"Required file missing: {npc.MapID}_npc_set*");
            if (!npc.IsDailyFight && talkPath == null)
                throw new InvalidOperationException($"Required file missing: {npc.MapID}_npc_base_talk_{npc.ChapterCode}*");

            var res = new Result();
            res.NpcId = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(npc.NpcName)));
            int npcType = ParseNpcType(npc.NpcType);

            // --- npc_set: NPC_BASE, NPC_APPEAR, NPC_PRESET ---
            var npcSet = T2bReader.Read(File.ReadAllBytes(npcSetPath));

            var baseE = CloneRecord(npcSet, "NPC_BASE");
            SetInts(baseE, res.NpcId, 0, npc.BaseId, 0, npcType, 0, 0, 0, 0, 0, 1);
            AddToGroup(npcSet, "NPC_BASE_BEGIN", "NPC_BASE_END", baseE);

            int appearIndex = GroupCount(npcSet, "NPC_APPEAR_BEGIN"); // new appear's 0-based index
            var appearE = CloneRecord(npcSet, "NPC_APPEAR");
            SetStr(appearE, 0, npc.NpcName);
            SetInt(appearE, 1, -1); SetInt(appearE, 2, -1);
            SetStr(appearE, 3, npc.AppearCond ?? "0");
            SetInt(appearE, 4, -1); SetInt(appearE, 5, 0); SetInt(appearE, 6, -1);
            AddToGroup(npcSet, "NPC_APPEAR_BEGIN", "NPC_APPEAR_END", appearE);

            var presetE = CloneRecord(npcSet, "NPC_PRESET");
            SetInts(presetE, res.NpcId, appearIndex, 1);
            AddToGroup(npcSet, "NPC_PRESET_BEGIN", "NPC_PRESET_END", presetE);

            // --- npc_base_talk: BASE_TALK_INFO (simple talk NPC only; daily-fight uses npc_talk_0.01) ---
            T2bFile talk = null;
            if (!npc.IsDailyFight)
            {
                talk = T2bReader.Read(File.ReadAllBytes(talkPath));
                var talkE = CloneRecord(talk, "BASE_TALK_INFO");
                SetInts(talkE, res.NpcId, 0, 1, 1, 1, 2, 1, 3, 1);
                AddToGroup(talk, "BASE_TALK_INFO_BEGIN", "BASE_TALK_INFO_END", talkE);
            }

            // --- .npcbin from a vanilla template + inject into npc.pck ---
            // Clone a proper placed NPC (npc_*), NOT an ambient object (ani_*/car_*/mob_*/…) — those have a
            // different ACT_TYPE/BASE_STATE/SET_FLAG structure and make the NPC spawn/behave wrongly.
            var npcPck = Xpck.Read(File.ReadAllBytes(npcPckPath));
            var template = PickNpcbinTemplate(npcPck) ?? LooseNpcbinTemplate(mapDir);
            if (template == null) throw new InvalidOperationException("No template .npcbin found (npc.pck empty).");
            var npcbin = T2bReader.Read(template.Data);
            SetPoint(npcbin, npc);
            byte[] npcbinBytes = T2bWriter.Write(npcbin);
            Xpck.AddOrReplace(npcPck, npc.NpcName + ".npcbin", npcbinBytes);
            byte[] npcPckOut = Xpck.Write(npcPck);

            // --- map .pck ---
            var mapPck = Xpck.Read(File.ReadAllBytes(mapPckPath));

            // Daily-fight extra files (kept for writing below).
            T2bFile npcTalk = null, textEn = null, textMap = null, flagConfig = null;
            string npcTalkPath = null, textEnPath = null, textMapPath = null;

            if (npc.IsDailyFight)
            {
                npcTalkPath = PickByPrefix(mergeMapDir, mapDir, npc.MapID + "_npc_talk_0.01");
                textEnPath = PickByPrefix(mergeMapDir, mapDir, npc.MapID + "_npc_text_c_en");
                textMapPath = PickByPrefix(mergeMapDir, mapDir, npc.MapID + "_npc_text_map_c");
                if (npcTalkPath == null) throw new InvalidOperationException($"Required file missing: {npc.MapID}_npc_talk_0.01*");
                if (textEnPath == null) throw new InvalidOperationException($"Required file missing: {npc.MapID}_npc_text_c_en*");
                if (textMapPath == null) throw new InvalidOperationException($"Required file missing: {npc.MapID}_npc_text_map_c*");
                npcTalk = T2bReader.ReadFile(npcTalkPath);
                textEn = T2bReader.ReadFile(textEnPath);
                textMap = T2bReader.ReadFile(textMapPath);
                if (daily?.FlagConfigSrc != null && File.Exists(daily.FlagConfigSrc))
                    flagConfig = T2bReader.ReadFile(daily.FlagConfigSrc);

                var xqFile = mapPck.FirstOrDefault(f => f.Name == npc.MapID + ".xq")
                             ?? throw new InvalidOperationException($"{npc.MapID}.xq not found in {npc.MapID}.pck");
                var trigFile = mapPck.FirstOrDefault(f => f.Name == npc.MapID + "_trigger.cfg.bin")
                             ?? throw new InvalidOperationException($"{npc.MapID}_trigger.cfg.bin not found in {npc.MapID}.pck");
                var trig = T2bReader.Read(trigFile.Data);

                var dr = NpcDailyFight.Apply(npc, res.NpcId, npc.BaseId, npcTalk, trig, xqFile.Data, textEn, textMap, flagConfig);
                res.Daily = dr; res.FuncId = dr.FuncIds[0]; res.XqLog = dr.XqLog;
                Xpck.AddOrReplace(mapPck, xqFile.Name, dr.NewXq);
                Xpck.AddOrReplace(mapPck, trigFile.Name, T2bWriter.Write(trig));
            }
            else if (!string.IsNullOrWhiteSpace(npc.OnTalk))
            {
                // Simple talk NPC: OnTalk into the .xq (+ trigger link).
                var xqFile = mapPck.FirstOrDefault(f => f.Name == npc.MapID + ".xq")
                             ?? throw new InvalidOperationException($"{npc.MapID}.xq not found in {npc.MapID}.pck");
                byte[] newXq = NpcXq.AddOnTalkFunction(xqFile.Data, npc.OnTalk, out int funcId, out string xqLog);
                res.FuncId = funcId; res.XqLog = xqLog;
                Xpck.AddOrReplace(mapPck, xqFile.Name, newXq);

                var trigFile = mapPck.FirstOrDefault(f => f.Name == npc.MapID + "_trigger.cfg.bin");
                if (trigFile != null)
                {
                    var trig = T2bReader.Read(trigFile.Data);
                    AddTriggerItem(trig, res.NpcId, funcId);
                    Xpck.AddOrReplace(mapPck, trigFile.Name, T2bWriter.Write(trig));
                }
            }
            byte[] mapPckOut = Xpck.Write(mapPck);

            // --- write outputs (mirroring the <MapID> folder so it merges into a mod's res/map) ---
            string root = Path.Combine(outRoot, SafeName(npc.NpcName) + "_output");
            string outMap = Path.Combine(root, npc.MapID);
            Directory.CreateDirectory(outMap);
            res.OutputDir = root;
            WriteOut(outMap, Path.GetFileName(npcSetPath), T2bWriter.Write(npcSet), res);
            if (talk != null) WriteOut(outMap, Path.GetFileName(talkPath), T2bWriter.Write(talk), res);
            WriteOut(outMap, "npc.pck", npcPckOut, res);
            WriteOut(outMap, npc.MapID + ".pck", mapPckOut, res);
            WriteOut(outMap, npc.NpcName + ".npcbin", npcbinBytes, res);
            if (npc.IsDailyFight)
            {
                WriteOut(outMap, Path.GetFileName(npcTalkPath), T2bWriter.Write(npcTalk), res);
                WriteOut(outMap, Path.GetFileName(textEnPath), T2bWriter.Write(textEn), res);
                WriteOut(outMap, Path.GetFileName(textMapPath), T2bWriter.Write(textMap), res);
            }

            // Auto-merge: copy the produced map files into the mod's map folder.
            if (!string.IsNullOrEmpty(mergeMapDir))
            {
                Directory.CreateDirectory(mergeMapDir);
                foreach (var src in res.Files)
                    File.Copy(src, Path.Combine(mergeMapDir, Path.GetFileName(src)), overwrite: true);
                res.MergedDir = mergeMapDir;
            }

            // flag_config lives outside the map folder — write it straight to its mod destination.
            if (npc.IsDailyFight && flagConfig != null && !string.IsNullOrEmpty(daily?.FlagConfigDst))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(daily.FlagConfigDst));
                File.WriteAllBytes(daily.FlagConfigDst, T2bWriter.Write(flagConfig));
                res.Files.Add(daily.FlagConfigDst);
            }
            return res;
        }

        /// <summary>
        /// Place a Mirapo warp NPC in a map, reusing the NPC-wiring helpers. The mirapo is a built-in TYPE-9 NPC
        /// (no xq/trigger): NPC_BASE id = CRC32("warp_"+mapid) (the same value as warp_config field[0]) with base =
        /// CRC32("y130000") (the Mirapo model), plus an NPC_APPEAR mirror (a y130000 npcbin placed at the given
        /// position) and an NPC_PRESET linking them. Writes the edited npc_set + npc.pck for the map.
        /// </summary>
        public static Result CompileWarpNpc(string mapId, string mapFolder, string outRoot, string mergeMapDir,
            double mx, double my, double mz, double mrot, byte[] mirrorTemplate)
        {
            if (mirrorTemplate == null) throw new InvalidOperationException("Mirror npcbin template not available.");
            // A warp id may carry a point suffix (_02, _03…) — the warp id uses the FULL id, but the actual map
            // (folder, npc_set, npc.pck) is the BASE map. Multiple warp points thus share one map's npc_set.
            string baseMap = WarpBaseMapId(mapId);
            string mapDir = ResolveMapDir(mapFolder, baseMap);
            if (mapDir == null) throw new InvalidOperationException($"Map folder not found for \"{baseMap}\" (npc.pck missing in the mod or reference).");
            string npcPckPath = PickFile(mergeMapDir, mapDir, "npc.pck");
            string npcSetPath = PickByPrefix(mergeMapDir, mapDir, baseMap + "_npc_set");
            if (npcPckPath == null || !File.Exists(npcPckPath)) throw new InvalidOperationException("Required file missing: npc.pck");
            if (npcSetPath == null) throw new InvalidOperationException($"Required file missing: {mapId}_npc_set*");

            var res = new Result();
            int warpId = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes("warp_" + mapId)));
            int model = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes("y130000")));   // Mirapo yo-kai model
            res.NpcId = warpId;

            var npcSet = T2bReader.Read(File.ReadAllBytes(npcSetPath));
            if (npcSet.Records("NPC_BASE").Any(e => (e.GetInt(0) ?? 0) == warpId))
                throw new InvalidOperationException($"This map already has a Mirapo warp NPC for \"{mapId}\" (0x{unchecked((uint)warpId):X8}). " +
                    $"To add ANOTHER warp point to the same map, use a suffixed id like \"{baseMap}_02\" (\"{baseMap}_03\", …).");
            var npcPck = Xpck.Read(File.ReadAllBytes(npcPckPath));
            string mirName = UniqueMirrorName(npcSet, npcPck);

            // NPC_BASE: [warpId, 0, model, 0, 9(warp type), 0,0,0,0,0, 0]
            var baseE = CloneRecord(npcSet, "NPC_BASE");
            SetInts(baseE, warpId, 0, model, 0, 9, 0, 0, 0, 0, 0, 0);
            AddToGroup(npcSet, "NPC_BASE_BEGIN", "NPC_BASE_END", baseE);

            // NPC_APPEAR: the mirror model (always visible: cond "0")
            int appearIndex = GroupCount(npcSet, "NPC_APPEAR_BEGIN");
            var appearE = CloneRecord(npcSet, "NPC_APPEAR");
            SetStr(appearE, 0, mirName);
            SetInt(appearE, 1, -1); SetInt(appearE, 2, -1);
            SetStr(appearE, 3, "0");
            SetInt(appearE, 4, -1); SetInt(appearE, 5, 0); SetInt(appearE, 6, -1);
            AddToGroup(npcSet, "NPC_APPEAR_BEGIN", "NPC_APPEAR_END", appearE);

            // NPC_PRESET: link base warpId -> the mirror appear (group 2, as vanilla)
            var presetE = CloneRecord(npcSet, "NPC_PRESET");
            SetInts(presetE, warpId, appearIndex, 2);
            AddToGroup(npcSet, "NPC_PRESET_BEGIN", "NPC_PRESET_END", presetE);

            // Mirror npcbin (y130000) placed at the given position, injected into npc.pck.
            var npcbin = T2bReader.Read(mirrorTemplate);
            SetPointCoords(npcbin, mx, my, mz, mrot);
            byte[] npcbinBytes = T2bWriter.Write(npcbin);
            Xpck.AddOrReplace(npcPck, mirName + ".npcbin", npcbinBytes);
            byte[] npcPckOut = Xpck.Write(npcPck);

            string root = Path.Combine(outRoot, "warp_" + mapId + "_output");
            string outMap = Path.Combine(root, baseMap);   // mirror the real (base) map folder
            Directory.CreateDirectory(outMap);
            res.OutputDir = root;
            WriteOut(outMap, Path.GetFileName(npcSetPath), T2bWriter.Write(npcSet), res);
            WriteOut(outMap, "npc.pck", npcPckOut, res);
            WriteOut(outMap, mirName + ".npcbin", npcbinBytes, res);

            if (!string.IsNullOrEmpty(mergeMapDir))
            {
                Directory.CreateDirectory(mergeMapDir);
                foreach (var src in res.Files)
                    File.Copy(src, Path.Combine(mergeMapDir, Path.GetFileName(src)), overwrite: true);
                res.MergedDir = mergeMapDir;
            }
            return res;
        }

        /// <summary>Load the bundled mir001 mirror npcbin template (Mirapo model y130000).</summary>
        public static byte[] MirrorTemplate()
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream("Lycoris.Resources.mir001.npcbin"))
            {
                if (s == null) return null;
                var b = new byte[s.Length];
                int off = 0, n;
                while (off < b.Length && (n = s.Read(b, off, b.Length - off)) > 0) off += n;
                return b;
            }
        }

        // Strip a warp-point suffix (_02, _03…) to the base map id (self-contained copy of WarpSet.BaseMapId).
        private static string WarpBaseMapId(string mapId) =>
            System.Text.RegularExpressions.Regex.Replace(mapId ?? "", @"_\d+$", "");

        private static string UniqueMirrorName(T2bFile npcSet, List<XpckFile> npcPck)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in npcSet.Records("NPC_APPEAR"))
                if (e.Values.Count > 0 && e.Values[0].Type == VT.String) used.Add((string)e.Values[0].Value);
            foreach (var f in npcPck)
                if (f.Name.EndsWith(".npcbin", StringComparison.OrdinalIgnoreCase)) used.Add(Path.GetFileNameWithoutExtension(f.Name));
            for (int i = 1; i < 1000; i++) { string n = "mir" + i.ToString("000"); if (!used.Contains(n)) return n; }
            return "mir001";
        }

        private static void SetPointCoords(T2bFile npcbin, double x, double y, double z, double rot)
        {
            var pt = npcbin.Entries.FirstOrDefault(e => e.Name == "POINT")
                     ?? throw new InvalidOperationException("POINT entry missing from the mirror .npcbin.");
            double[] vals = { x, y, z, rot };   // POINT = [X, height(Y), Z, rotation]
            for (int i = 0; i < 4 && i < pt.Values.Count; i++)
            {
                double v = vals[i];
                if (Math.Abs(v - Math.Round(v)) < 1e-4) { pt.Values[i].Type = VT.Integer; pt.Values[i].Value = (int)Math.Round(v); }
                else { pt.Values[i].Type = VT.FloatingPoint; pt.Values[i].Value = (float)v; }
            }
        }

        // ---------- CfgBin editing helpers ----------

        private static T2bEntry CloneRecord(T2bFile f, string name)
        {
            var tpl = f.Entries.FirstOrDefault(e => e.Name == name)
                      ?? throw new InvalidOperationException($"Template record \"{name}\" not found.");
            return tpl.Clone();
        }

        private static void AddToGroup(T2bFile f, string beginName, string endName, T2bEntry entry)
        {
            int endIdx = f.Entries.FindIndex(e => e.Name == endName);
            if (endIdx < 0) throw new InvalidDataException($"Group marker \"{endName}\" not found.");
            f.Entries.Insert(endIdx, entry);
            var begin = f.Entries.FirstOrDefault(e => e.Name == beginName);
            if (begin != null && begin.Values.Count > 0 && begin.Values[0].Value is int c)
                begin.Values[0].Value = c + 1;
        }

        private static int GroupCount(T2bFile f, string beginName)
        {
            var begin = f.Entries.FirstOrDefault(e => e.Name == beginName);
            return begin != null && begin.Values.Count > 0 && begin.Values[0].Value is int c ? c : 0;
        }

        /// <summary>Trigger = DATA_COUNT (count) + flat DATA_ITEM list (no END marker): append + bump count.</summary>
        private static void AddTriggerItem(T2bFile f, int npcId, int funcId)
        {
            var last = f.Entries.Last(e => e.Name == "DATA_ITEM");
            var entry = last.Clone();
            SetInts(entry, 11, npcId, 0, 0, 0, 0, funcId); // NPC_TRIGGER_TYPE = 11
            int at = f.Entries.FindLastIndex(e => e.Name == "DATA_ITEM");
            f.Entries.Insert(at + 1, entry);
            var count = f.Entries.FirstOrDefault(e => e.Name == "DATA_COUNT");
            if (count != null && count.Values.Count > 0 && count.Values[0].Value is int c)
                count.Values[0].Value = c + 1;
        }

        private static void SetPoint(T2bFile npcbin, NpcModel npc)
        {
            var pt = npcbin.Entries.FirstOrDefault(e => e.Name == "POINT")
                     ?? throw new InvalidOperationException("POINT entry missing from the template .npcbin.");
            // In the game the POINT record is [X, height, Z, rotation] (field 1 is the vertical/height, a small
            // value). NPCMake maps the TOML as X, Z, Y (the Y/Z are swapped); Lycoris labels NpcZ as the height
            // ("hauteur"), so NpcZ -> POINT[1] (height) and NpcY -> POINT[2] (horizontal Z).
            // Each value keeps CfgBin's convention: whole numbers as Integer, fractional as FloatingPoint.
            double[] vals = { npc.NpcX, npc.NpcZ, npc.NpcY, npc.NpcRotation };
            for (int i = 0; i < 4 && i < pt.Values.Count; i++)
            {
                double v = vals[i];
                if (Math.Abs(v - Math.Round(v)) < 1e-4) { pt.Values[i].Type = VT.Integer; pt.Values[i].Value = (int)Math.Round(v); }
                else { pt.Values[i].Type = VT.FloatingPoint; pt.Values[i].Value = (float)v; }
            }
        }

        private static void SetInts(T2bEntry e, params int[] values)
        {
            for (int i = 0; i < values.Length && i < e.Values.Count; i++)
            {
                e.Values[i].Type = VT.Integer;
                e.Values[i].Value = values[i];
            }
        }

        private static void SetInt(T2bEntry e, int i, int v)
        {
            if (i < e.Values.Count) { e.Values[i].Type = VT.Integer; e.Values[i].Value = v; }
        }

        private static void SetStr(T2bEntry e, int i, string v)
        {
            if (i < e.Values.Count) { e.Values[i].Type = VT.String; e.Values[i].Value = v ?? ""; }
        }

        private static int ParseNpcType(string t)
        {
            if (string.IsNullOrWhiteSpace(t)) return 2;
            if (t.Equals("HUMAN", StringComparison.OrdinalIgnoreCase)) return 2;
            if (t.Equals("YOKAI", StringComparison.OrdinalIgnoreCase)) return 0;
            return int.TryParse(t, out int n) ? n : 2;
        }

        // ---------- file/path helpers ----------

        private static string ResolveMapDir(string root, string mapId)
        {
            if (root == null) return null;
            foreach (var cand in new[] {
                root,
                Path.Combine(root, mapId),
                Path.Combine(root, "res", "map", mapId),
                Path.Combine(root, "data", "res", "map", mapId),
                Path.Combine(root, "include", "data", "res", "map", mapId),
            })
                if (Directory.Exists(cand) && File.Exists(Path.Combine(cand, "npc.pck"))) return cand;
            return null;
        }

        private static string FindByPrefix(string dir, string prefix) =>
            dir != null && Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, prefix + "*")
                    .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault()
                : null;

        /// <summary>Pick a file from the mod copy first (so compiles accumulate), else the base map.</summary>
        private static string PickFile(string modDir, string baseDir, string name)
        {
            if (!string.IsNullOrEmpty(modDir))
            {
                string p = Path.Combine(modDir, name);
                if (File.Exists(p)) return p;
            }
            string b = baseDir != null ? Path.Combine(baseDir, name) : null;
            return b != null && File.Exists(b) ? b : null;
        }

        private static string PickByPrefix(string modDir, string baseDir, string prefix) =>
            FindByPrefix(modDir, prefix) ?? FindByPrefix(baseDir, prefix);

        /// <summary>Choose a proper placed-NPC template (npc_* with an ACT_TYPE group), not an ambient object.</summary>
        private static XpckFile PickNpcbinTemplate(List<XpckFile> pck)
        {
            var bins = pck.Where(f => f.Name.EndsWith(".npcbin", StringComparison.OrdinalIgnoreCase)).ToList();
            if (bins.Count == 0) return null;
            // 1) npc_* that is a real talkable NPC (has ACT_TYPE).
            foreach (var b in bins.Where(f => f.Name.StartsWith("npc_", StringComparison.OrdinalIgnoreCase)))
                if (HasActType(b)) return b;
            // 2) any npcbin with ACT_TYPE.
            foreach (var b in bins)
                if (HasActType(b)) return b;
            // 3) else any npc_*, else the first.
            return bins.FirstOrDefault(f => f.Name.StartsWith("npc_", StringComparison.OrdinalIgnoreCase)) ?? bins[0];
        }

        private static bool HasActType(XpckFile b)
        {
            try { return T2bReader.Read(b.Data).Records("ACT_TYPE").Any(); } catch { return false; }
        }

        private static XpckFile LooseNpcbinTemplate(string mapDir)
        {
            var path = Directory.EnumerateFiles(mapDir, "npc_*.npcbin").FirstOrDefault()
                       ?? Directory.EnumerateFiles(mapDir, "*.npcbin").FirstOrDefault();
            return path == null ? null : new XpckFile(Path.GetFileName(path), File.ReadAllBytes(path));
        }

        private static void WriteOut(string dir, string name, byte[] data, Result res)
        {
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, data);
            res.Files.Add(path);
        }

        private static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "npc" : s;
        }
    }
}
