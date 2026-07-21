using System;
using System.Collections.Generic;

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
