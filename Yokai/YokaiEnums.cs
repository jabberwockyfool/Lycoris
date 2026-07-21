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

        // SKILL_CONFIG_INFO SkillType tags (from CfgBinEditor). Others are left editable as raw ints.
        public static readonly List<EnumEntry> SkillTypes = Build(new Dictionary<int, string>
        {
            {1, "Attaque normale"}, {3, "Technique (élémentaire)"}, {4, "Soultimate"}, {5, "Inspiration"},
        });

        public static readonly List<EnumEntry> Speeds = Build(new Dictionary<int, string>
        {
            {0, "Normal"}, {1, "Fast"}, {2, "Slow"},
        });

        public static readonly List<EnumEntry> Roles = Build(new Dictionary<int, string>
        {
            {0, "Unrole"}, {1, "Fighter"}, {2, "Tank"}, {3, "Healer"}, {4, "Ranger"},
        });

        // FoodsType.YW3 — sparse (0x0A and 0x1B skipped); the stored value is the KEY.
        public static readonly List<EnumEntry> Foods = Build(new Dictionary<int, string>
        {
            {0x00, "(aucun)"}, {0x01, "Rice Balls"}, {0x02, "Bread"}, {0x03, "Candy"}, {0x04, "Milk"},
            {0x05, "Juice"}, {0x06, "Hamburgers"}, {0x07, "Ramen"}, {0x08, "Sushi"}, {0x09, "Chinese Food"},
            {0x0B, "Vegetables"}, {0x0C, "Meat"}, {0x0D, "Seafood"}, {0x0E, "Curry"}, {0x0F, "Sweets"},
            {0x10, "Oden Stew"}, {0x11, "Soba"}, {0x12, "Snacks"}, {0x13, "Chocobars"}, {0x14, "Ice Cream"},
            {0x15, "Donut"}, {0x16, "Pizza"}, {0x17, "Hot Dog"}, {0x18, "Pasta"}, {0x19, "Tempura"},
            {0x1A, "Mega Tasty Bar"}, {0x1C, "Sukiyaki"},
        });

        private static readonly Dictionary<int, string> TribeName = ToMap(Tribes);
        private static readonly Dictionary<int, string> RankName = ToMap(Ranks);
        private static readonly Dictionary<int, string> AttributeName = ToMap(Attributes);
        private static readonly Dictionary<int, string> SkillTypeName = ToMap(SkillTypes);

        public static string Tribe(int? k) => Lookup(TribeName, k);
        public static string Rank(int? k) => Lookup(RankName, k);
        public static string Attribute(int? k) => Lookup(AttributeName, k);
        public static string SkillType(int? k) => k.HasValue && SkillTypeName.TryGetValue(k.Value, out var n) ? n : (k?.ToString() ?? "");

        /// <summary>
        /// Category (display order + label) a skill falls into for the skill editor's grouping. Empty-named
        /// skills — and any type outside the known tags (incl. type 0 Guard/misc) — go to "Non identifié".
        /// </summary>
        public static (int sort, string name) SkillCategoryInfo(int? type, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return (99, "Non identifié");
            switch (type)
            {
                case 1: return (0, "Attaque normale");
                case 3: return (1, "Technique (élémentaire)");
                case 5: return (2, "Inspiration");
                case 4: return (3, "Soultimate");
                default: return (99, "Non identifié");
            }
        }

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
