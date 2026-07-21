using System.Collections.Generic;
using System.Linq;

namespace Lycoris.Formats
{
    /// <summary>
    /// Level-5 cfg.bin files store a flat entry list, but by naming convention they form a
    /// tree: an entry whose name ends in _BEGIN/_BEG/PTREE opens a group, _END closes it,
    /// and the entries in between are its records. These helpers recover the records without
    /// building a full tree, which is all the yo-kai layer needs.
    /// </summary>
    public static class T2bTree
    {
        public static bool IsGroupOpen(string name) =>
            name != null && (name.EndsWith("_BEGIN") || name.EndsWith("_BEG") || name.EndsWith("PTREE"));

        public static bool IsGroupClose(string name) =>
            name != null && name.EndsWith("_END");

        /// <summary>
        /// Returns every entry whose resolved name equals <paramref name="recordName"/>.
        /// For record lists (e.g. "CHARA_PARAM_INFO_") every child shares the same name,
        /// so a flat filter is exactly the record set.
        /// </summary>
        public static IEnumerable<T2bEntry> Records(this T2bFile file, string recordName) =>
            file.Entries.Where(e => e.Name == recordName);

        /// <summary>Records that sit between the given group-open marker and its matching _END.</summary>
        public static IEnumerable<T2bEntry> RecordsInGroup(this T2bFile file, string groupOpenName)
        {
            bool inside = false;
            foreach (var e in file.Entries)
            {
                if (e.Name == groupOpenName) { inside = true; continue; }
                if (!inside) continue;
                if (IsGroupClose(e.Name)) yield break;
                if (!IsGroupOpen(e.Name)) yield return e;
            }
        }
    }
}
