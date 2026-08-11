using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lycoris.Formats;

namespace Lycoris
{
    /// <summary>What happened to one file during a merge (for the report).</summary>
    public sealed class MergeFileResult
    {
        public string RelativePath;
        public string Outcome;     // "A only", "B only", "identical", "merged", "conflict (kept A/B)", "error"
        public string Detail;
        public bool IsProblem;     // conflicts / errors that deserve the user's attention
    }

    /// <summary>Aggregate report of a whole-mod merge.</summary>
    public sealed class MergeReport
    {
        public readonly List<MergeFileResult> Files = new List<MergeFileResult>();
        public int CopiedAOnly, CopiedBOnly, Identical, Merged, BinaryConflicts, Errors;
        public int RecordsAdded, RecordsOverwritten, RecordConflicts;

        public IEnumerable<MergeFileResult> Problems => Files.Where(f => f.IsProblem);
    }

    /// <summary>
    /// Merges two extracted YWML mods into a new output mod. Files unique to one side are copied; files
    /// in both are byte-compared, and when they differ a .cfg.bin is structurally merged (union of records,
    /// see <see cref="CfgBinMerge"/>) while any other binary is resolved by the conflict policy. When a
    /// reference (vanilla) extract is supplied, cfg.bin merges use it as a 3-way base for smarter conflict
    /// resolution. Nothing is mutated in place — the result is written under <c>outputFolder\include</c>.
    /// </summary>
    public static class ModMerger
    {
        /// <summary>The overlay root of a mod: its "include" folder if present, else the folder itself.</summary>
        public static string IncludeRoot(string modFolder)
        {
            if (string.IsNullOrEmpty(modFolder)) return modFolder;
            string inc = Path.Combine(modFolder, "include");
            return Directory.Exists(inc) ? inc : modFolder;
        }

        public static MergeReport Merge(string modA, string modB, string outputFolder,
                                        string referenceFolder, MergePolicy policy)
        {
            var report = new MergeReport();

            string rootA = IncludeRoot(modA);
            string rootB = IncludeRoot(modB);
            string outRoot = Path.Combine(outputFolder, "include");
            string refRoot = referenceFolder != null ? Path.Combine(referenceFolder, "include") : null;
            if (refRoot != null && !Directory.Exists(refRoot)) refRoot = referenceFolder; // some extracts have no include/

            var relA = RelFiles(rootA);
            var relB = RelFiles(rootB);
            var all = new SortedSet<string>(relA.Keys, StringComparer.OrdinalIgnoreCase);
            all.UnionWith(relB.Keys);

            foreach (string rel in all)
            {
                bool inA = relA.ContainsKey(rel), inB = relB.ContainsKey(rel);
                string dst = Path.Combine(outRoot, rel);
                var res = new MergeFileResult { RelativePath = rel };
                try
                {
                    if (inA && !inB) { CopyTo(relA[rel], dst); res.Outcome = "A only"; report.CopiedAOnly++; }
                    else if (inB && !inA) { CopyTo(relB[rel], dst); res.Outcome = "B only"; report.CopiedBOnly++; }
                    else
                    {
                        byte[] ba = File.ReadAllBytes(relA[rel]);
                        byte[] bb = File.ReadAllBytes(relB[rel]);
                        if (ByteEqual(ba, bb)) { WriteBytes(dst, ba); res.Outcome = "identical"; report.Identical++; }
                        else if (IsCfgBin(rel)) MergeCfg(rel, ba, bb, refRoot, policy, dst, res, report);
                        else
                        {
                            byte[] pick = policy == MergePolicy.PreferB ? bb : ba;
                            WriteBytes(dst, pick);
                            res.Outcome = $"conflict (kept {(policy == MergePolicy.PreferB ? "B" : "A")})";
                            res.Detail = "binary file differs — cannot merge, used policy";
                            res.IsProblem = true;
                            report.BinaryConflicts++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    res.Outcome = "error"; res.Detail = ex.Message; res.IsProblem = true; report.Errors++;
                }
                report.Files.Add(res);
            }

            WriteManifest(outputFolder, modA, modB);
            return report;
        }

        private static void MergeCfg(string rel, byte[] ba, byte[] bb, string refRoot, MergePolicy policy,
                                     string dst, MergeFileResult res, MergeReport report)
        {
            T2bFile fa, fb;
            try { fa = T2bReader.Read(ba); fb = T2bReader.Read(bb); }
            catch
            {
                byte[] pick = policy == MergePolicy.PreferB ? bb : ba;
                WriteBytes(dst, pick);
                res.Outcome = $"conflict (kept {(policy == MergePolicy.PreferB ? "B" : "A")})";
                res.Detail = "cfg.bin failed to parse — used policy";
                res.IsProblem = true; report.BinaryConflicts++;
                return;
            }

            T2bFile baseline = null;
            if (refRoot != null)
            {
                string bp = Path.Combine(refRoot, rel);
                if (File.Exists(bp)) { try { baseline = T2bReader.ReadFile(bp); } catch { baseline = null; } }
            }

            var m = CfgBinMerge.Merge(fa, fb, baseline, policy);
            WriteBytes(dst, T2bWriter.Write(m.Merged));

            report.Merged++;
            report.RecordsAdded += m.Added;
            report.RecordsOverwritten += m.Overwritten;
            report.RecordConflicts += m.Conflicts;
            res.Outcome = "merged";
            res.Detail = $"+{m.Added} added, {m.Overwritten} updated, {m.Conflicts} conflict(s)"
                         + (baseline != null ? " [3-way]" : " [2-way]");
            res.IsProblem = m.Conflicts > 0;
            if (m.Notes.Count > 0) res.Detail += "  —  " + string.Join("; ", m.Notes);
        }

        // ---- filesystem helpers -----------------------------------------------------------

        private static Dictionary<string, string> RelFiles(string root)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root == null || !Directory.Exists(root)) return map;
            foreach (string p in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = p.Substring(root.Length).TrimStart('\\', '/');
                if (rel.Equals("ywml.json", StringComparison.OrdinalIgnoreCase)) continue;
                map[rel] = p;
            }
            return map;
        }

        private static bool IsCfgBin(string rel) => rel.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase);

        private static bool ByteEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void CopyTo(string src, string dst)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, overwrite: true);
        }

        private static void WriteBytes(string dst, byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.WriteAllBytes(dst, data);
        }

        private static void WriteManifest(string outputFolder, string modA, string modB)
        {
            try
            {
                string na = Ywml.FindName(modA) ?? ProjectStore.DefaultName(modA);
                string nb = Ywml.FindName(modB) ?? ProjectStore.DefaultName(modB);
                Ywml.Write(outputFolder, $"{na} + {nb}", "Lycoris merge", "v0.0.0");
            }
            catch { /* manifest is best-effort */ }
        }
    }
}
