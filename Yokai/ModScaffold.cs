using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lycoris.Yokai
{
    /// <summary>
    /// Populates a freshly-created mod with the base config files editors touch, copied from the reference at
    /// their in-game path (via <see cref="YokaiDatabase.MirrorToMod"/>), so every feature (yo-kai, items, skills,
    /// NPCs, character-switch, daily-fight flags/events…) works from the mod alone without depending on the
    /// reference. These are whole-file YWML overrides — copied only if not already in the mod.
    /// </summary>
    public static class ModScaffold
    {
        /// <summary>File-name prefixes of the base configs to pre-copy (newest match wins). Each may optionally
        /// exclude a substring (e.g. chara_ability must skip hackslash_chara_ability).</summary>
        private static readonly (string Prefix, string Exclude)[] BaseConfigs =
        {
            ("chara_param", "hackslash"),
            ("chara_base", null),
            ("chara_text", null),          // names (NOUN_INFO); "chara_desc_text" is a different prefix
            ("chara_desc_text", null),     // descriptions
            ("chara_scale", null),
            ("chara_ability", "hackslash"), // the character-switch ability
            ("battle_chara_param", null),   // drops / rewards
            ("hackslash_chara_param", null),// Blaster-T
            ("item_config", null),
            ("item_text", null),
            ("skill_config", "btl"),        // skill_config (skip skill_btl_config)
            ("skill_text", null),
            ("skill_desc_text", null),
            ("flag_config", null),          // daily-fight / warp flags
            ("event_set_config", null),     // events registry
        };

        /// <summary>Copy the base configs from the reference into the mod. Returns the list of file names copied.</summary>
        public static List<string> PreCopyBaseConfigs(YokaiDatabase db)
        {
            var copied = new List<string>();
            string reference = db?.ReferenceFolder;
            if (string.IsNullOrEmpty(reference) || !Directory.Exists(reference)) return copied;

            foreach (var (prefix, exclude) in BaseConfigs)
            {
                string src = NewestUnder(reference, prefix, exclude);
                if (src == null) continue;
                string target = db.MirrorToMod(src);
                if (target == null) continue;
                try
                {
                    if (!File.Exists(target))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(src, target);
                        copied.Add(Path.GetFileName(target));
                    }
                }
                catch { /* skip a file that can't be copied rather than abort the whole scaffold */ }
            }
            return copied;
        }

        /// <summary>Newest <c>&lt;prefix&gt;*.cfg.bin</c> under a root whose file name starts with the prefix and
        /// does not contain <paramref name="exclude"/> (case-insensitive), or null.</summary>
        private static string NewestUnder(string root, string prefix, string exclude)
        {
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
    }
}
