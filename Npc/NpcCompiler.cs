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
            string npcSetPath = PickMainNpcSet(mergeMapDir, mapDir, npc.MapID);   // MAIN npc_set (skip "_race" etc.)
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
            double mx, double my, double mz, double mrot, byte[] mirrorFull, byte[] mirrorSimple,
            string flagConfigSrc = null, string flagConfigDst = null, int regionFlag = 0, string mirrorModel = "y130000")
        {
            if (string.IsNullOrWhiteSpace(mirrorModel)) mirrorModel = "y130000";
            if (mirrorFull == null || mirrorSimple == null) throw new InvalidOperationException("Mirror npcbin templates not available.");
            // A warp id may carry a point suffix (_02, _03…) — the warp id uses the FULL id, but the actual map
            // (folder, npc_set, npc.pck) is the BASE map. Multiple warp points thus share one map's npc_set.
            string baseMap = WarpBaseMapId(mapId);
            // The map may be in the reference (vanilla) OR only in the mod (a CUSTOM map). Try the reference first,
            // then fall back to the mod's map folder (mergeMapDir) — the custom map lives there with its npc.pck.
            string mapDir = ResolveMapDir(mapFolder, baseMap);
            if (mapDir == null && !string.IsNullOrEmpty(mergeMapDir) && File.Exists(Path.Combine(mergeMapDir, "npc.pck")))
                mapDir = mergeMapDir;
            if (mapDir == null) throw new InvalidOperationException($"Map folder not found for \"{baseMap}\" — no npc.pck in the reference or the mod (<mod>/include/data/res/map/{baseMap}/).");
            string npcPckPath = PickFile(mergeMapDir, mapDir, "npc.pck");
            string npcSetPath = PickMainNpcSet(mergeMapDir, mapDir, baseMap);   // the MAIN npc_set, not "_race"/etc.
            string npcTalkPath = PickByPrefix(mergeMapDir, mapDir, baseMap + "_npc_talk_0.01");
            if (npcPckPath == null || !File.Exists(npcPckPath)) throw new InvalidOperationException("Required file missing: npc.pck");
            if (npcSetPath == null) throw new InvalidOperationException($"Required file missing: {baseMap}_npc_set*");
            if (npcTalkPath == null) throw new InvalidOperationException($"Required file missing: {baseMap}_npc_talk_0.01* (needed for the Mirapo's talk/warp-menu entry).");

            var res = new Result();
            int warpId = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes("warp_" + mapId)));
            int model = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(mirrorModel)));   // mirror yo-kai model (default y130000 Mirapo; e.g. y252000 Miradox — same rig/p90)
            res.NpcId = warpId;

            var npcSet = T2bReader.Read(File.ReadAllBytes(npcSetPath));
            if (npcSet.Records("NPC_BASE").Any(e => (e.GetInt(0) ?? 0) == warpId))
                throw new InvalidOperationException($"This map already has a Mirapo warp NPC for \"{mapId}\" (0x{unchecked((uint)warpId):X8}). " +
                    $"To add ANOTHER warp point to the same map, use a suffixed id like \"{baseMap}_02\" (\"{baseMap}_03\", …).");
            var npcPck = Xpck.Read(File.ReadAllBytes(npcPckPath));
            // A Mirapo point needs a PAIR of mirror npcbins at the SAME position (vanilla: mir001 full w/ motion +
            // mir002 simple w/o motion — e.g. Northbeech's 4 warps use 8 mir npcbins). One alone won't work.
            string mirA = UniqueMirrorName(npcSet, npcPck);
            string mirB = UniqueMirrorName(npcSet, npcPck, mirA);

            // NPC_BASE: [warpId, 0, model, 0, 9(warp type), 0,0,0,0,0, 0]
            var baseE = CloneRecord(npcSet, "NPC_BASE");
            SetInts(baseE, warpId, 0, model, 0, 9, 0, 0, 0, 0, 0, 0);
            AddToGroup(npcSet, "NPC_BASE_BEGIN", "NPC_BASE_END", baseE);

            // Appear conditions drive the dormant/AWAKENED pose: the vanilla mirror cond checks a per-region
            // "mirapo enabled" flag + GetGlobalBitFlag(warpId) (the warp being unlocked → awakened). Copy those
            // conds from an EXISTING type-9 mirapo in this map (so the region flag is correct) and remap its warp
            // id → ours. Fallback "0" (always visible but stuck dormant) when the map has no other mirapo.
            FindMirrorConds(npcSet, warpId, regionFlag, out string condFull, out string condSimple);

            // NPC_APPEAR ×2: the two mirrors (full + simple companion), consecutive.
            int appearIndex = GroupCount(npcSet, "NPC_APPEAR_BEGIN");
            AddMirrorAppear(npcSet, mirA, condFull);
            AddMirrorAppear(npcSet, mirB, condSimple);

            // NPC_PRESET: [warpId, firstAppearIndex, appearCount]. field[2] is the NUMBER of consecutive
            // NPC_APPEAR entries this preset spans — MUST equal the two mirrors we add (2), else the span runs
            // into a garbage/unrelated appear (its position) and the NPC fails to spawn / appears elsewhere.
            var presetE = CloneRecord(npcSet, "NPC_PRESET");
            SetInts(presetE, warpId, appearIndex, 2);
            AddToGroup(npcSet, "NPC_PRESET_BEGIN", "NPC_PRESET_END", presetE);

            // Both mirror npcbins at the SAME position (mirA = full model, mirB = simple companion).
            byte[] binA = MakeMirrorBin(mirrorFull, mirrorModel, mx, my, mz, mrot);
            byte[] binB = MakeMirrorBin(mirrorSimple, mirrorModel, mx, my, mz, mrot);
            Xpck.AddOrReplace(npcPck, mirA + ".npcbin", binA);
            Xpck.AddOrReplace(npcPck, mirB + ".npcbin", binB);
            byte[] npcPckOut = Xpck.Write(npcPck);

            // npc_talk: the TALK_INFO + 2 TALK_CONFIG (one of which opens the warp menu) that make the type-9 NPC
            // actually talkable — WITHOUT this, talking to the Mirapo does nothing.
            var npcTalk = T2bReader.Read(File.ReadAllBytes(npcTalkPath));
            AddMirapoTalk(npcTalk, warpId);

            string root = Path.Combine(outRoot, "warp_" + mapId + "_output");
            string outMap = Path.Combine(root, baseMap);   // mirror the real (base) map folder
            Directory.CreateDirectory(outMap);
            res.OutputDir = root;
            WriteOut(outMap, Path.GetFileName(npcSetPath), T2bWriter.Write(npcSet), res);
            WriteOut(outMap, Path.GetFileName(npcTalkPath), T2bWriter.Write(npcTalk), res);
            WriteOut(outMap, "npc.pck", npcPckOut, res);
            WriteOut(outMap, mirA + ".npcbin", binA, res);
            WriteOut(outMap, mirB + ".npcbin", binB, res);

            if (!string.IsNullOrEmpty(mergeMapDir))
            {
                Directory.CreateDirectory(mergeMapDir);
                foreach (var src in res.Files)
                    File.Copy(src, Path.Combine(mergeMapDir, Path.GetFileName(src)), overwrite: true);
                res.MergedDir = mergeMapDir;
            }

            // flag_config: register the warp flag "warp_<mapid>" in the GlobalBitFlag group (field[0]==0) so
            // SetGlobalBitFlag(warpId) is a recognised, persistent flag. Written straight to its mod destination.
            if (!string.IsNullOrEmpty(flagConfigSrc) && File.Exists(flagConfigSrc) && !string.IsNullOrEmpty(flagConfigDst))
            {
                var flagConfig = T2bReader.Read(File.ReadAllBytes(flagConfigSrc));
                NpcDailyFight.AddFlagIfAbsent(flagConfig, warpId, 0);   // 0 = permanent GlobalBitFlag group
                Directory.CreateDirectory(Path.GetDirectoryName(flagConfigDst));
                File.WriteAllBytes(flagConfigDst, T2bWriter.Write(flagConfig));
                res.Files.Add(flagConfigDst);
            }
            return res;
        }

        /// <summary>Load the bundled mir001 mirror npcbin template (full — Mirapo model y130000 with motion).</summary>
        public static byte[] MirrorTemplate() => LoadResource("Lycoris.Resources.mir001.npcbin");

        /// <summary>Load the bundled mir002 mirror npcbin template (simple companion — no motion).</summary>
        public static byte[] MirrorTemplateSimple() => LoadResource("Lycoris.Resources.mir002.npcbin");

        private static byte[] LoadResource(string name)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using (var s = asm.GetManifestResourceStream(name))
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

        private static string UniqueMirrorName(T2bFile npcSet, List<XpckFile> npcPck, params string[] alsoUsed)
        {
            var used = new HashSet<string>(alsoUsed ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (var e in npcSet.Records("NPC_APPEAR"))
                if (e.Values.Count > 0 && e.Values[0].Type == VT.String) used.Add((string)e.Values[0].Value);
            foreach (var f in npcPck)
                if (f.Name.EndsWith(".npcbin", StringComparison.OrdinalIgnoreCase)) used.Add(Path.GetFileNameWithoutExtension(f.Name));
            for (int i = 1; i < 1000; i++) { string n = "mir" + i.ToString("000"); if (!used.Contains(n)) return n; }
            return "mir001";
        }

        // Add a mirror NPC_APPEAR with the given condition blob ("0" = always visible).
        private static void AddMirrorAppear(T2bFile npcSet, string mirName, string cond)
        {
            var appearE = CloneRecord(npcSet, "NPC_APPEAR");
            SetStr(appearE, 0, mirName);
            SetInt(appearE, 1, -1); SetInt(appearE, 2, -1);
            SetStr(appearE, 3, string.IsNullOrEmpty(cond) ? "0" : cond);
            SetInt(appearE, 4, -1); SetInt(appearE, 5, 0); SetInt(appearE, 6, -1);
            AddToGroup(npcSet, "NPC_APPEAR_BEGIN", "NPC_APPEAR_END", appearE);
        }

        // Vanilla t101i02 mirror appear conds (region flag 0xA83CA9FF = Springdale, warp id 0x5A640058):
        //   full   = GetGlobalBitFlag(0xA83CA9FF)==1 && GetGlobalBitFlag(warpId)!=1  (dormant)
        //   simple = GetGlobalBitFlag(0xA83CA9FF)==1
        // Springdale's mirapo flag is set very early, so it works as a fallback for custom maps.
        private const string SpringdaleCondFull = "AAAAADALNSo9RUMACgEoAAYCNKg8qf8yAAAAAXg1Kj1FQwAKASgABgI0WmQAWDIAAAABeY8=";
        private const string SpringdaleCondSimple = "AAAAABgFNSo9RUMACgEoAAYCNKg8qf8yAAAAAXg=";
        private const int MirapoSpringdaleRegion = unchecked((int)0xA83CA9FF);   // region flag embedded in the templates above

        // Appear condition blobs (full + simple mirror) that drive the dormant/awakened pose (region flag + warp
        // flag). Priority: (1) an explicit regionFlag the caller passes (a flag KNOWN to be set when the player is
        // in this map) → build the dormant/awakened cond; (2) copy from an EXISTING type-9 mirapo in this map
        // (correct region flag) + remap its warp id; (3) fall back to "0" (always visible, but stuck dormant).
        // NB: a region flag that ISN'T set on the save gates the mirror OUT entirely (invisible) — hence "0" as the
        // safe default for standalone custom maps rather than assuming Springdale's flag.
        private static void FindMirrorConds(T2bFile npcSet, int warpId, int regionFlag, out string condFull, out string condSimple)
        {
            if (regionFlag != 0)
            {
                // Springdale template (region 0xA83CA9FF, warp 0x5A640058) → remap both to the caller's values.
                condFull = YwCond.RemapBase64(YwCond.RemapBase64(SpringdaleCondFull, MirapoSpringdaleRegion, regionFlag), MirapoVanillaWarpId, warpId);
                condSimple = YwCond.RemapBase64(SpringdaleCondSimple, MirapoSpringdaleRegion, regionFlag);
                return;
            }
            var exBase = npcSet.Records("NPC_BASE").FirstOrDefault(e => (e.GetInt(4) ?? 0) == 9 && (e.GetInt(0) ?? 0) != warpId);
            if (exBase != null)
            {
                int exWarpId = exBase.GetInt(0) ?? 0;
                var exPreset = npcSet.Records("NPC_PRESET").FirstOrDefault(p => (p.GetInt(0) ?? 0) == exWarpId);
                if (exPreset != null)
                {
                    int ai = exPreset.GetInt(1) ?? 0, cnt = exPreset.GetInt(2) ?? 0;
                    var appears = npcSet.Records("NPC_APPEAR").ToList();
                    string Cond(int i) => i >= 0 && i < appears.Count && appears[i].Values.Count > 3 && appears[i].Values[3].Type == VT.String
                        ? YwCond.RemapBase64((string)appears[i].Values[3].Value, exWarpId, warpId) : "0";
                    condFull = Cond(ai);
                    condSimple = cnt > 1 ? Cond(ai + 1) : "0";
                    return;
                }
            }
            // Custom map (no explicit flag, no other mirapo): reuse Springdale's region flag 0xA83CA9FF (set very
            // early, so it's on in a progressed save) → the dormant→awakened pose. If it's NOT set on the save, the
            // mirror is invisible — enter a different flag in the dialog, or the map needs its own region flag set.
            condFull = YwCond.RemapBase64(SpringdaleCondFull, MirapoVanillaWarpId, warpId);
            condSimple = SpringdaleCondSimple;
        }

        // Build a mirror npcbin from a template, retargeted to <model> and placed at (x,y,z,rot).
        private static byte[] MakeMirrorBin(byte[] template, string model, double x, double y, double z, double rot)
        {
            var bin = T2bReader.Read(template);
            var lm = bin.Records("LOAD_MOTION").FirstOrDefault();   // e.g. "y130000/y130000_p90" → "<model>/<model>_p90"
            if (lm != null && lm.Values.Count > 0 && lm.Values[0].Type == VT.String)
                lm.Values[0].Value = model + "/" + model + "_p90";
            SetPointCoords(bin, x, y, z, rot);
            return T2bWriter.Write(bin);
        }

        // --- Mirapo talk chain (fixed template; only the warp id varies) ---
        // Vanilla t101i02 blobs (warp id 0x5A640058): cond = GetGlobalBitFlag(warpId); trigA = open-warp-menu
        // command (carries warpId at offset 19); trigB + the two page text ids are CONSTANT across all maps.
        private const int MirapoVanillaWarpId = 0x5A640058;
        // CFG#1 gate: GetGlobalBitFlag(warpId)==NOT-set → "first time" (awaken). CFG#1 trig then does
        // SetGlobalBitFlag(warpId) (awaken + register) + RunTrigger(awaken intro/menu). Once the flag is set,
        // CFG#1's cond fails and CFG#2 (the repeat: direct warp menu) runs instead.
        private const string MirapoCond = "AAAAABgFNSo9RUMACgEoAAYCNFpkAFgyAAAAAXk=";
        private const string MirapoTrigA = "AAAAAC0FNRgrN1oAEwIoAAYCNFpkAFgoAAYCMgAAAAE1aYTjrwAKASgABgI0uDqdiI8=";
        private const string MirapoTrigB = "AAAAABICNWmE468ACgEoAAYCNCn5Ri0=";
        private static readonly int MirapoText1 = unchecked((int)0xAF6C8F02);
        private static readonly int MirapoText2 = 0x3665DEB8;

        /// <summary>Add the Mirapo's talk entry (TALK_INFO + 2 TALK_CONFIG + 2 TALK_PAGE) so the type-9 NPC is
        /// talkable and opens the warp menu. Indices are absolute positions in each group, computed before adding.</summary>
        private static void AddMirapoTalk(T2bFile talk, int warpId)
        {
            int cfgStart = GroupCount(talk, "TALK_CONFIG_BEGIN");
            int pageStart = GroupCount(talk, "TALK_PAGE_BEGIN");
            string cond = YwCond.RemapBase64(MirapoCond, MirapoVanillaWarpId, warpId);
            string trigA = YwCond.RemapBase64(MirapoTrigA, MirapoVanillaWarpId, warpId);

            // TALK_INFO = [warpId, cfgStart, 2]
            var info = CloneRecord(talk, "TALK_INFO");
            SetInts(info, warpId, cfgStart, 2);
            AddToGroup(talk, "TALK_INFO_BEGIN", "TALK_INFO_END", info);

            // TALK_CONFIG #1 = [1,0, pageStart, 1, 0,0,-1,-1, cond, trigA] — the FIRST-TIME (awaken) branch: its
            // cond passes only while GetGlobalBitFlag(warpId) is NOT set, and its trig SetGlobalBitFlag(warpId)
            // registers/awakens. Once set, CFG#1 no longer matches → CFG#2 (repeat: direct warp menu) runs. The
            // flag is declared in flag_config below so it persists (else the awaken would repeat forever).
            var c1 = CloneRecord(talk, "TALK_CONFIG");
            SetInts(c1, 1, 0, pageStart, 1, 0, 0, -1, -1);
            SetStr(c1, 8, cond); SetStr(c1, 9, trigA);
            AddToGroup(talk, "TALK_CONFIG_BEGIN", "TALK_CONFIG_END", c1);

            // TALK_CONFIG #2 = [1,0, pageStart+1, 1, 0,0,-1,-1, 0,    trigB]
            var c2 = CloneRecord(talk, "TALK_CONFIG");
            SetInts(c2, 1, 0, pageStart + 1, 1, 0, 0, -1, -1);
            SetInt(c2, 8, 0); SetStr(c2, 9, MirapoTrigB);
            AddToGroup(talk, "TALK_CONFIG_BEGIN", "TALK_CONFIG_END", c2);

            // TALK_PAGE ×2 = [textId, -1]
            var p1 = CloneRecord(talk, "TALK_PAGE"); SetInts(p1, MirapoText1, -1);
            AddToGroup(talk, "TALK_PAGE_BEGIN", "TALK_PAGE_END", p1);
            var p2 = CloneRecord(talk, "TALK_PAGE"); SetInts(p2, MirapoText2, -1);
            AddToGroup(talk, "TALK_PAGE_BEGIN", "TALK_PAGE_END", p2);
        }

        private static void SetPointCoords(T2bFile npcbin, double x, double y2d, double zHeight, double rot)
        {
            var pt = npcbin.Entries.FirstOrDefault(e => e.Name == "POINT")
                     ?? throw new InvalidOperationException("POINT entry missing from the mirror .npcbin.");
            // Same mapping as the daily-fight NPC's SetPoint: POINT = [X, Z(height), Y(2D horizontal), rotation]
            // — the game POINT is [X, height, Z, rot] and the input Y/Z are swapped (NPCMake convention).
            double[] vals = { x, zHeight, y2d, rot };
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

        /// <summary>Trigger = DATA_COUNT (count) + flat DATA_ITEM list (no END marker): append + bump count.
        /// Robust when the list is EMPTY (e.g. every NPC was deleted from a custom map) — builds a fresh
        /// DATA_ITEM from the file's name table instead of throwing (which used to block adding any new NPC).</summary>
        private static void AddTriggerItem(T2bFile f, int npcId, int funcId)
        {
            var last = f.Records("DATA_ITEM").LastOrDefault();
            T2bEntry entry;
            if (last != null) { entry = last.Clone(); }
            else
            {
                var nm = f.Names.FirstOrDefault(n => n.Name == "DATA_ITEM");
                uint crc = nm.Name == "DATA_ITEM" ? nm.Crc : Crc32.Standard(Encoding.UTF8.GetBytes("DATA_ITEM"));
                entry = new T2bEntry { Name = "DATA_ITEM", Crc = crc };
                for (int i = 0; i < 7; i++) entry.Values.Add(new T2bValue(VT.Integer, 0));
            }
            SetInts(entry, 11, npcId, 0, 0, 0, 0, funcId); // NPC_TRIGGER_TYPE = 11
            int at = f.Entries.FindLastIndex(e => e.Name == "DATA_ITEM");
            if (at < 0) at = f.Entries.FindIndex(e => e.Name == "DATA_COUNT");   // no items yet: place after DATA_COUNT
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

        /// <summary>The MAIN npc_set (<c>&lt;map&gt;_npc_set_&lt;version&gt;.cfg.bin</c>, version starting with a
        /// digit) — NOT variants like <c>&lt;map&gt;_npc_set_race_…</c> (a minigame's separate NPC list). Using the
        /// generic prefix + descending sort wrongly picks "_race" (r &gt; digit), so the NPC lands in the wrong list.</summary>
        private static string PickMainNpcSet(string modDir, string baseDir, string mapId)
        {
            string tag = mapId + "_npc_set_";
            string Find(string dir) => dir != null && Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, tag + "*.cfg.bin")
                    .Where(f => { string rest = Path.GetFileName(f).Substring(tag.Length); return rest.Length > 0 && char.IsDigit(rest[0]); })
                    .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
            return Find(modDir) ?? Find(baseDir);
        }

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
