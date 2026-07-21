using System;
using System.Collections.Generic;
using System.Linq;

namespace Lycoris.Yokai
{
    /// <summary>
    /// Builds the face-icon filename from a yo-kai's base-record model fields, ported from
    /// Albatross' GameSupport.GetFileModelText. Filename = letter + number(3 digits) + variant(3),
    /// e.g. (6,105,1) -> "y105010". Validated against the vanilla YW3 face_icon set.
    /// </summary>
    public static class IconNaming
    {
        private static readonly Dictionary<int, char> PrefixLetter = new Dictionary<int, char>
        {
            { 0, 'c' }, { 5, 'x' }, { 6, 'y' }, { 7, 'z' },
        };

        /// <summary>Returns the 7-char base name (no extension), or null if unmappable.</summary>
        public static string GetFileModelText(int prefix, int number, int variant)
        {
            if (!PrefixLetter.TryGetValue(prefix, out char letter)) return null;
            string v = FormatVariant(variant);
            if (v == null) return null;
            return letter + number.ToString("D3") + v;
        }

        private static readonly System.Collections.Generic.Dictionary<char, int> LetterPrefix =
            new System.Collections.Generic.Dictionary<char, int> { { 'c', 0 }, { 'x', 5 }, { 'y', 6 }, { 'z', 7 } };

        /// <summary>Parse a model name like "y152900" back to prefix/number/variant. Inverse of GetFileModelText.</summary>
        public static bool TryParse(string name, out int prefix, out int number, out int variant)
        {
            prefix = number = variant = 0;
            if (string.IsNullOrEmpty(name) || name.Length != 7) return false;
            if (!LetterPrefix.TryGetValue(char.ToLowerInvariant(name[0]), out prefix)) return false;
            if (!int.TryParse(name.Substring(1, 3), out number)) return false;
            string v = name.Substring(4, 3);
            if (v.Length != 3 || !v.All(char.IsDigit)) return false;
            if (v[2] == '0')
                variant = v[0] == '0' ? (v[1] - '0') : (v[0] - '0') * 10 + (v[1] - '0');
            else
                variant = int.Parse(new string(v.Reverse().ToArray()));
            return true;
        }

        private static string FormatVariant(int x)
        {
            if (x >= 0 && x < 10) return "0" + x + "0";
            if (x >= 10 && x < 100) return x + "0";
            if (x >= 100 && x < 1000)
            {
                char[] c = x.ToString().ToCharArray();
                Array.Reverse(c);
                return new string(c);
            }
            return null;
        }
    }
}
