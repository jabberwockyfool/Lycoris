using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lycoris.Formats;

namespace Lycoris.Yokai
{
    /// <summary>
    /// Ports a whole yo-kai from one mod into another (object-level): merges its records across the standard
    /// config files (chara_param / base / text / desc / scale / battle / hackslash) by key, and copies its model
    /// and icon assets (include-tree files whose name carries the model id). Records are matched by ParamHash /
    /// BaseHash / NameHash / DescHash — replaced in place if the target already has them, else appended to the
    /// group. The caller reloads the mod afterwards so the in-memory db reflects the ported data.
    /// </summary>
    public static class YokaiPort
    {
        public sealed class Result
        {
            public List<string> Files = new List<string>();   // config files merged into
            public List<string> Assets = new List<string>();  // model/icon files copied
        }

        private enum Key { Param, Base, Name, Desc }

        private struct Spec
        {
            public string Prefix, Exclude, Record, Beg, End;
            public Key Key;
            public Spec(string p, string x, string r, string b, string e, Key k)
            { Prefix = p; Exclude = x; Record = r; Beg = b; End = e; Key = k; }
        }

        private static readonly Spec[] Specs =
        {
            new Spec("chara_param", "hackslash", "CHARA_PARAM_INFO", "CHARA_PARAM_INFO_LIST_BEG", "CHARA_PARAM_INFO_LIST_END", Key.Param),
            new Spec("chara_base", null, "CHARA_BASE_YOKAI_INFO", "CHARA_BASE_YOKAI_INFO_BEGIN", "CHARA_BASE_YOKAI_INFO_END", Key.Base),
            new Spec("chara_text", null, "NOUN_INFO", "NOUN_INFO_BEGIN", "NOUN_INFO_END", Key.Name),
            new Spec("chara_desc_text", null, "TEXT_INFO", "TEXT_INFO_BEGIN", "TEXT_INFO_END", Key.Desc),
            new Spec("chara_scale", null, "CHARA_SCALE_INFO", "CHARA_SCALE_INFO_LIST_BEG", "CHARA_SCALE_INFO_LIST_END", Key.Base),
            new Spec("battle_chara_param", null, "BATTLE_CHARA_PARAM_INFO", "BATTLE_CHARA_PARAM_INFO_LIST_BEG", "BATTLE_CHARA_PARAM_INFO_LIST_END", Key.Param),
            new Spec("hackslash_chara_param", null, "HACKSLASH_CHARA_PARAM_INFO", "HACKSLASH_CHARA_PARAM_INFO_LIST_BEG", "HACKSLASH_CHARA_PARAM_INFO_LIST_END", Key.Param),
        };

        public static Result Apply(YokaiDatabase db, string sourceMod, int paramHash, int baseHash, int nameHash, int? descHash, string modelName)
        {
            var res = new Result();
            string modFolder = db?.ModFolder, reference = db?.ReferenceFolder;
            if (string.IsNullOrEmpty(modFolder)) throw new InvalidOperationException("No mod loaded (target).");
            if (string.IsNullOrEmpty(sourceMod) || !Directory.Exists(sourceMod)) throw new InvalidOperationException("Source mod folder not found.");

            foreach (var spec in Specs)
            {
                int key = spec.Key == Key.Param ? paramHash : spec.Key == Key.Base ? baseHash : spec.Key == Key.Name ? nameHash : (descHash ?? 0);
                if (spec.Key == Key.Desc && !descHash.HasValue) continue;
                MergeFile(db, sourceMod, modFolder, reference, spec, key, res);
            }

            CopyAssets(sourceMod, modFolder, modelName, res);
            return res;
        }

        private static void MergeFile(YokaiDatabase db, string sourceMod, string modFolder, string reference, Spec spec, int key, Result res)
        {
            string src = Newest(sourceMod, spec.Prefix, spec.Exclude);
            if (src == null) return;
            T2bFile srcFile;
            try { srcFile = T2bReader.ReadFile(src); } catch { return; }
            var picked = srcFile.Records(spec.Record).Where(e => GI(e, 0) == key).ToList();
            if (picked.Count == 0) return;

            // Target = the mod's own copy first; else mirror the reference file into the mod and edit that.
            string tgt = Newest(modFolder, spec.Prefix, spec.Exclude);
            if (tgt == null)
            {
                string refFile = reference != null ? Newest(reference, spec.Prefix, spec.Exclude) : null;
                if (refFile == null) return;
                tgt = db.MirrorToMod(refFile);
                if (tgt == null) return;
                if (!File.Exists(tgt)) { Directory.CreateDirectory(Path.GetDirectoryName(tgt)); File.Copy(refFile, tgt); }
            }
            T2bFile tf;
            try { tf = T2bReader.ReadFile(tgt); } catch { return; }

            foreach (var r in picked)
            {
                var existing = tf.Records(spec.Record).FirstOrDefault(e => GI(e, 0) == GI(r, 0));
                if (existing != null) existing.Values = r.Clone().Values;   // overwrite in place
                else
                {
                    int endIdx = tf.Entries.FindIndex(x => x.Name == spec.End);
                    var clone = r.Clone();
                    if (endIdx < 0) tf.Entries.Add(clone); else tf.Entries.Insert(endIdx, clone);
                    Bump(tf, spec.Beg, +1);
                }
            }
            T2bWriter.WriteFile(tf, tgt);
            if (!res.Files.Contains(Path.GetFileName(tgt))) res.Files.Add(Path.GetFileName(tgt));
        }

        /// <summary>Copy the yo-kai's model/icon assets: include-tree files whose name carries the model id
        /// (e.g. y152000_p00.xc, its .xi icons), preserving their relative path into the target mod.</summary>
        private static void CopyAssets(string sourceMod, string modFolder, string modelName, Result res)
        {
            if (string.IsNullOrWhiteSpace(modelName)) return;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(sourceMod, "*", SearchOption.AllDirectories); }
            catch { return; }
            foreach (var f in files)
            {
                string name = Path.GetFileName(f);
                if (name.IndexOf(modelName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (name.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase)) continue;   // configs handled by record merge
                string rel = f.Substring(sourceMod.Length).TrimStart('\\', '/');
                if (rel.IndexOf("include", StringComparison.OrdinalIgnoreCase) < 0) continue;   // only romfs assets
                string tgt = Path.Combine(modFolder, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(tgt));
                    File.Copy(f, tgt, overwrite: true);
                    res.Assets.Add(rel);
                }
                catch { }
            }
        }

        private static string Newest(string root, string prefix, string exclude)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, prefix + "*.cfg.bin", SearchOption.AllDirectories); }
            catch { return null; }
            return files
                .Where(p =>
                {
                    string n = Path.GetFileName(p);
                    return n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                           && (exclude == null || n.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) < 0);
                })
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static int GI(T2bEntry e, int i) => i < e.Values.Count && e.Values[i].Value is int v ? v : 0;
        private static void Bump(T2bFile f, string beg, int d)
        {
            var b = f.Entries.FirstOrDefault(e => e.Name == beg);
            if (b != null && b.Values.Count > 0 && b.Values[0].Value is int c) b.Values[0].Value = c + d;
        }
    }
}
