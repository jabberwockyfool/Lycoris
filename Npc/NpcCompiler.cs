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
        }

        /// <summary>
        /// Compile the NPC. Always writes a portable &lt;NpcName&gt;_output/&lt;MapID&gt;/ folder under
        /// <paramref name="outRoot"/>. If <paramref name="mergeMapDir"/> is given, the same files are also
        /// copied there (the map folder inside the mod) — the auto-merge.
        /// </summary>
        public static Result Compile(NpcModel npc, string mapFolder, string outRoot, string mergeMapDir = null)
        {
            if (string.IsNullOrWhiteSpace(npc.NpcName)) throw new InvalidOperationException("Le NPC doit avoir un nom.");
            string mapDir = ResolveMapDir(mapFolder, npc.MapID);
            if (mapDir == null)
                throw new InvalidOperationException($"Dossier map introuvable pour « {npc.MapID} » (npc.pck absent).");

            string npcPckPath = Path.Combine(mapDir, "npc.pck");
            string mapPckPath = Path.Combine(mapDir, npc.MapID + ".pck");
            string npcSetPath = FindByPrefix(mapDir, npc.MapID + "_npc_set");
            string talkPath = FindByPrefix(mapDir, npc.MapID + "_npc_base_talk_" + npc.ChapterCode);
            foreach (var (p, label) in new[] { (npcPckPath, "npc.pck"), (mapPckPath, npc.MapID + ".pck") })
                if (!File.Exists(p)) throw new InvalidOperationException($"Fichier requis manquant: {label}");
            if (npcSetPath == null) throw new InvalidOperationException($"Fichier requis manquant: {npc.MapID}_npc_set*");
            if (talkPath == null) throw new InvalidOperationException($"Fichier requis manquant: {npc.MapID}_npc_base_talk_{npc.ChapterCode}*");

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

            // --- npc_base_talk: BASE_TALK_INFO ---
            var talk = T2bReader.Read(File.ReadAllBytes(talkPath));
            var talkE = CloneRecord(talk, "BASE_TALK_INFO");
            SetInts(talkE, res.NpcId, 0, 1, 1, 1, 2, 1, 3, 1);
            AddToGroup(talk, "BASE_TALK_INFO_BEGIN", "BASE_TALK_INFO_END", talkE);

            // --- .npcbin from a vanilla template + inject into npc.pck ---
            var npcPck = Xpck.Read(File.ReadAllBytes(npcPckPath));
            var template = npcPck.FirstOrDefault(f => f.Name.EndsWith(".npcbin", StringComparison.OrdinalIgnoreCase))
                           ?? LooseNpcbinTemplate(mapDir);
            if (template == null) throw new InvalidOperationException("Aucun .npcbin modèle trouvé (npc.pck vide).");
            var npcbin = T2bReader.Read(template.Data);
            SetPoint(npcbin, npc);
            byte[] npcbinBytes = T2bWriter.Write(npcbin);
            Xpck.AddOrReplace(npcPck, npc.NpcName + ".npcbin", npcbinBytes);
            byte[] npcPckOut = Xpck.Write(npcPck);

            // --- map .pck: OnTalk into the .xq (+ trigger link) ---
            var mapPck = Xpck.Read(File.ReadAllBytes(mapPckPath));
            byte[] mapPckOut;
            if (!string.IsNullOrWhiteSpace(npc.OnTalk))
            {
                var xqFile = mapPck.FirstOrDefault(f => f.Name == npc.MapID + ".xq")
                             ?? throw new InvalidOperationException($"{npc.MapID}.xq introuvable dans {npc.MapID}.pck");
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
            mapPckOut = Xpck.Write(mapPck);

            // --- write outputs (mirroring the <MapID> folder so it merges into a mod's res/map) ---
            string root = Path.Combine(outRoot, SafeName(npc.NpcName) + "_output");
            string outMap = Path.Combine(root, npc.MapID);
            Directory.CreateDirectory(outMap);
            res.OutputDir = root;
            WriteOut(outMap, Path.GetFileName(npcSetPath), T2bWriter.Write(npcSet), res);
            WriteOut(outMap, Path.GetFileName(talkPath), T2bWriter.Write(talk), res);
            WriteOut(outMap, "npc.pck", npcPckOut, res);
            WriteOut(outMap, npc.MapID + ".pck", mapPckOut, res);
            WriteOut(outMap, npc.NpcName + ".npcbin", npcbinBytes, res);

            // Auto-merge: copy the produced files into the mod's map folder.
            if (!string.IsNullOrEmpty(mergeMapDir))
            {
                Directory.CreateDirectory(mergeMapDir);
                foreach (var src in res.Files)
                    File.Copy(src, Path.Combine(mergeMapDir, Path.GetFileName(src)), overwrite: true);
                res.MergedDir = mergeMapDir;
            }
            return res;
        }

        // ---------- CfgBin editing helpers ----------

        private static T2bEntry CloneRecord(T2bFile f, string name)
        {
            var tpl = f.Entries.FirstOrDefault(e => e.Name == name)
                      ?? throw new InvalidOperationException($"Enregistrement modèle « {name} » introuvable.");
            return tpl.Clone();
        }

        private static void AddToGroup(T2bFile f, string beginName, string endName, T2bEntry entry)
        {
            int endIdx = f.Entries.FindIndex(e => e.Name == endName);
            if (endIdx < 0) throw new InvalidDataException($"Marqueur de groupe « {endName} » introuvable.");
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
                     ?? throw new InvalidOperationException("Entrée POINT absente du .npcbin modèle.");
            // POINT = [X, Y, Z, rotation], types preserved from the template ([Float, Int, Float, Int]).
            double[] vals = { npc.NpcX, npc.NpcY, npc.NpcZ, npc.NpcRotation };
            for (int i = 0; i < 4 && i < pt.Values.Count; i++)
            {
                if (pt.Values[i].Type == VT.FloatingPoint) pt.Values[i].Value = (float)vals[i];
                else { pt.Values[i].Type = VT.Integer; pt.Values[i].Value = (int)Math.Round(vals[i]); }
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
            Directory.EnumerateFiles(dir, prefix + "*")
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

        private static XpckFile LooseNpcbinTemplate(string mapDir)
        {
            var path = Directory.EnumerateFiles(mapDir, "*.npcbin").FirstOrDefault();
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
