using System.Collections.Generic;

namespace Lycoris.Yokai
{
    /// <summary>One selectable value in a dropdown: the stored integer and its display name.</summary>
    public sealed class EnumEntry
    {
        public int Key { get; }
        public string Name { get; }
        public EnumEntry(int key, string name) { Key = key; Name = name; }
        public override string ToString() => Name;
    }

    /// <summary>
    /// Yo-kai Watch 3 dropdown vocabularies, transcribed from Albatross' Common/*.cs.
    /// Tribes are YW3-specific; Ranks/Attributes/Speeds are shared across games in Albatross.
    /// </summary>
    public static class YokaiEnums
    {
        public static readonly List<EnumEntry> Tribes = Build(new Dictionary<int, string>
        {
            {0, "Untribe"}, {1, "Brave"}, {2, "Mysterious"}, {3, "Tough"}, {4, "Charming"},
            {5, "Heartful"}, {6, "Shady"}, {7, "Eerie"}, {8, "Slippery"}, {9, "Wicked"},
            {10, "Enma"}, {11, "Wandroid"}, {12, "Boss"},
        });

        public static readonly List<EnumEntry> Ranks = Build(new Dictionary<int, string>
        {
            {0, "E"}, {1, "D"}, {2, "C"}, {3, "B"}, {4, "A"}, {5, "S"}, {15, "Unrank"},
        });

        // Used for both Resistance (Strongest) and Weakness.
        public static readonly List<EnumEntry> Attributes = Build(new Dictionary<int, string>
        {
            {0, "Untype"}, {1, "Fire"}, {2, "Water"}, {3, "Lightning"}, {4, "Earth"},
            {5, "Ice"}, {6, "Wind"}, {7, "Drain"}, {8, "Strong Attack"}, {9, "Restoration"},
        });

        public static readonly List<EnumEntry> Speeds = Build(new Dictionary<int, string>
        {
            {0, "Normal"}, {1, "Fast"}, {2, "Slow"},
        });

        private static readonly Dictionary<int, string> TribeName = ToMap(Tribes);
        private static readonly Dictionary<int, string> RankName = ToMap(Ranks);
        private static readonly Dictionary<int, string> AttributeName = ToMap(Attributes);

        public static string Tribe(int? k) => Lookup(TribeName, k);
        public static string Rank(int? k) => Lookup(RankName, k);
        public static string Attribute(int? k) => Lookup(AttributeName, k);

        private static List<EnumEntry> Build(Dictionary<int, string> map)
        {
            var list = new List<EnumEntry>();
            foreach (var kv in map) list.Add(new EnumEntry(kv.Key, kv.Value));
            return list;
        }

        private static Dictionary<int, string> ToMap(List<EnumEntry> list)
        {
            var m = new Dictionary<int, string>();
            foreach (var e in list) m[e.Key] = e.Name;
            return m;
        }

        private static string Lookup(Dictionary<int, string> map, int? k) =>
            k.HasValue && map.TryGetValue(k.Value, out string n) ? n : (k?.ToString() ?? "");
    }
}
