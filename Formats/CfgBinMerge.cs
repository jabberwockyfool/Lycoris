using System;
using System.Collections.Generic;
using System.Linq;

namespace Lycoris.Formats
{
    /// <summary>Which side wins when two mods change the same record differently.</summary>
    public enum MergePolicy { PreferA, PreferB }

    /// <summary>Outcome of merging one cfg.bin (T2B) file.</summary>
    public sealed class CfgMergeResult
    {
        public T2bFile Merged;
        public int Added;        // records present in B but not A -> inserted
        public int Overwritten;  // records where B's values replaced A's (edit taken from B)
        public int Conflicts;    // records that diverged on both sides -> resolved by policy
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Structural union of two cfg.bin files that both derive from the same vanilla base.
    /// Records are aligned by (enclosing group, record name, first value). Records only in B are
    /// inserted into the matching group of A and the group's child-count field is bumped; records
    /// in both are compared and, when they differ, resolved with an optional 3-way base or the policy.
    ///
    /// The result keeps A's exact structure (markers, name table, encoding, value width): B only
    /// contributes additions and, where allowed, value edits. The group child-count index is
    /// auto-detected (the integer slot equal to the group's original child count) so this works for
    /// param files (count at [0]) and shop-style files (count elsewhere) alike.
    /// </summary>
    public static class CfgBinMerge
    {
        public static CfgMergeResult Merge(T2bFile a, T2bFile b, T2bFile baseline, MergePolicy policy)
        {
            var r = new CfgMergeResult { Merged = Clone(a) };
            var result = r.Merged;

            // Index A's records by identity so B's records can be matched against them.
            var aIndex = IndexRecords(result);
            var bRecords = EnumerateRecords(b);
            var baseIndex = baseline != null ? IndexRecords(baseline) : null;

            // Group B's additions by their enclosing group name so we can insert + bump counts once.
            var additions = new Dictionary<string, List<T2bEntry>>(); // groupName ("" = top-level) -> entries
            var occ = new Dictionary<string, int>();

            foreach (var rec in bRecords)
            {
                string key = KeyOf(rec.GroupName, rec.Entry, occ);
                if (aIndex.TryGetValue(key, out var aEntry))
                {
                    if (ValuesEqual(aEntry, rec.Entry)) continue; // same record, nothing to do

                    // Records differ. Decide which side to keep.
                    bool take = false;
                    if (baseIndex != null && baseIndex.TryGetValue(key, out var baseEntry))
                    {
                        bool aChanged = !ValuesEqual(aEntry, baseEntry);
                        bool bChanged = !ValuesEqual(rec.Entry, baseEntry);
                        if (!bChanged) take = false;                    // B untouched -> keep A
                        else if (!aChanged) { take = true; r.Overwritten++; } // only B changed -> take B
                        else { take = policy == MergePolicy.PreferB; r.Conflicts++;
                               r.Notes.Add($"conflict: '{Describe(rec.Entry, rec.GroupName)}' changed on both sides — kept {(take ? "B" : "A")}."); }
                    }
                    else
                    {
                        take = policy == MergePolicy.PreferB; r.Conflicts++;
                        r.Notes.Add($"conflict: '{Describe(rec.Entry, rec.GroupName)}' differs (no base) — kept {(take ? "B" : "A")}.");
                    }

                    if (take) OverwriteValues(aEntry, rec.Entry);
                }
                else
                {
                    string g = rec.GroupName ?? "";
                    if (!additions.TryGetValue(g, out var list)) additions[g] = list = new List<T2bEntry>();
                    list.Add(rec.Entry.Clone());
                }
            }

            // Apply additions group by group (recomputing indices after each group, since inserts shift them).
            foreach (var kv in additions)
            {
                var clones = kv.Value;
                if (kv.Key.Length == 0)
                {
                    foreach (var e in clones) result.Entries.Add(e);   // top-level: append at end
                    r.Added += clones.Count;
                    r.Notes.Add($"added {clones.Count} top-level record(s).");
                    continue;
                }
                InsertIntoGroup(result, kv.Key, clones, r);
                r.Added += clones.Count;
            }

            return r;
        }

        // ---- record enumeration + identity ------------------------------------------------

        private struct Rec { public T2bEntry Entry; public string GroupName; }

        /// <summary>Every non-marker record, tagged with the name of its innermost enclosing group (or null).</summary>
        private static List<Rec> EnumerateRecords(T2bFile f)
        {
            var groups = GroupSpans(f);
            var recs = new List<Rec>();
            for (int i = 0; i < f.Entries.Count; i++)
            {
                var n = f.Entries[i].Name;
                if (T2bTree.IsGroupOpen(n) || T2bTree.IsGroupClose(n)) continue;
                recs.Add(new Rec { Entry = f.Entries[i], GroupName = Enclosing(groups, i) });
            }
            return recs;
        }

        private static Dictionary<string, T2bEntry> IndexRecords(T2bFile f)
        {
            var map = new Dictionary<string, T2bEntry>();
            var occ = new Dictionary<string, int>();
            foreach (var rec in EnumerateRecords(f))
            {
                string key = KeyOf(rec.GroupName, rec.Entry, occ);
                if (!map.ContainsKey(key)) map[key] = rec.Entry;
            }
            return map;
        }

        /// <summary>Identity of a record: enclosing group + record name + first value, disambiguated by occurrence.</summary>
        private static string KeyOf(string groupName, T2bEntry e, Dictionary<string, int> occ)
        {
            string first = e.Values.Count > 0 ? ((int)e.Values[0].Type) + ":" + (e.Values[0].Value?.ToString() ?? "") : "∅";
            string baseKey = (groupName ?? "") + "" + (e.Name ?? ("0x" + e.Crc.ToString("X8"))) + "" + first;
            occ.TryGetValue(baseKey, out int n);
            occ[baseKey] = n + 1;
            return baseKey + "#" + n;
        }

        // ---- group structure --------------------------------------------------------------

        private struct Span { public int Begin, End; public string Name; }

        /// <summary>All group spans (begin/end indices), nesting-aware.</summary>
        private static List<Span> GroupSpans(T2bFile f)
        {
            var res = new List<Span>();
            var stack = new Stack<int>();
            for (int i = 0; i < f.Entries.Count; i++)
            {
                var n = f.Entries[i].Name;
                if (T2bTree.IsGroupOpen(n)) stack.Push(i);
                else if (T2bTree.IsGroupClose(n) && stack.Count > 0)
                {
                    int b = stack.Pop();
                    res.Add(new Span { Begin = b, End = i, Name = f.Entries[b].Name });
                }
            }
            return res;
        }

        /// <summary>Name of the innermost group containing index <paramref name="idx"/>, or null.</summary>
        private static string Enclosing(List<Span> groups, int idx)
        {
            string name = null; int best = int.MaxValue;
            foreach (var g in groups)
            {
                if (g.Begin < idx && idx < g.End)
                {
                    int span = g.End - g.Begin;
                    if (span < best) { best = span; name = g.Name; }
                }
            }
            return name;
        }

        /// <summary>Insert clones before the matching group's _END and update its child-count field.</summary>
        private static void InsertIntoGroup(T2bFile f, string beginName, List<T2bEntry> clones, CfgMergeResult r)
        {
            int beginIdx = f.Entries.FindIndex(e => e.Name == beginName);
            if (beginIdx < 0)
            {
                foreach (var e in clones) f.Entries.Add(e);
                r.Notes.Add($"group '{beginName}' not found in base mod — appended {clones.Count} record(s) at end.");
                return;
            }
            int endIdx = MatchingEnd(f, beginIdx);
            if (endIdx < 0)
            {
                foreach (var e in clones) f.Entries.Add(e);
                r.Notes.Add($"group '{beginName}' has no _END — appended {clones.Count} record(s) at end.");
                return;
            }

            int childCount = CountChildren(f, beginIdx, endIdx);
            var begin = f.Entries[beginIdx];
            int countIdx = DetectCountIndex(begin, childCount);

            for (int i = 0; i < clones.Count; i++) f.Entries.Insert(endIdx + i, clones[i]);

            if (countIdx >= 0)
            {
                begin.Values[countIdx].Value = childCount + clones.Count;
                r.Notes.Add($"group '{beginName}': +{clones.Count} record(s), count[{countIdx}] {childCount} -> {childCount + clones.Count}.");
            }
            else
            {
                r.Notes.Add($"group '{beginName}': +{clones.Count} record(s) but no child-count field matched {childCount} — VERIFY the header count manually.");
            }
        }

        private static int MatchingEnd(T2bFile f, int beginIdx)
        {
            int depth = 0;
            for (int i = beginIdx + 1; i < f.Entries.Count; i++)
            {
                var n = f.Entries[i].Name;
                if (T2bTree.IsGroupOpen(n)) depth++;
                else if (T2bTree.IsGroupClose(n)) { if (depth == 0) return i; depth--; }
            }
            return -1;
        }

        private static int CountChildren(T2bFile f, int beginIdx, int endIdx)
        {
            int depth = 0, count = 0;
            for (int i = beginIdx + 1; i < endIdx; i++)
            {
                var n = f.Entries[i].Name;
                if (T2bTree.IsGroupOpen(n)) { depth++; continue; }
                if (T2bTree.IsGroupClose(n)) { depth--; continue; }
                if (depth == 0) count++;   // direct children only
            }
            return count;
        }

        /// <summary>The integer value slot of a group header equal to its child count (the count field), or -1.</summary>
        private static int DetectCountIndex(T2bEntry begin, int childCount)
        {
            for (int i = 0; i < begin.Values.Count; i++)
                if (begin.Values[i].Type == ValueType.Integer && begin.Values[i].Value is int iv && iv == childCount)
                    return i;
            return -1;
        }

        // ---- value helpers ----------------------------------------------------------------

        private static bool ValuesEqual(T2bEntry x, T2bEntry y)
        {
            if (x.Values.Count != y.Values.Count) return false;
            for (int i = 0; i < x.Values.Count; i++)
            {
                if (x.Values[i].Type != y.Values[i].Type) return false;
                if (!Equals(x.Values[i].Value, y.Values[i].Value)) return false;
            }
            return true;
        }

        private static void OverwriteValues(T2bEntry dst, T2bEntry src)
        {
            dst.Values.Clear();
            foreach (var v in src.Values) dst.Values.Add(v.Clone());
        }

        private static string Describe(T2bEntry e, string group)
        {
            string name = e.Name ?? ("0x" + e.Crc.ToString("X8"));
            string first = e.Values.Count > 0 ? e.Values[0].ToString() : "";
            return (string.IsNullOrEmpty(group) ? "" : group + "/") + name + (first.Length > 0 ? " [" + first + "]" : "");
        }

        private static T2bFile Clone(T2bFile f)
        {
            var c = new T2bFile
            {
                ValueLength = f.ValueLength,
                HashType = f.HashType,
                Encoding = f.Encoding,
            };
            foreach (var e in f.Entries) c.Entries.Add(e.Clone());
            c.Names.AddRange(f.Names);
            return c;
        }
    }
}
