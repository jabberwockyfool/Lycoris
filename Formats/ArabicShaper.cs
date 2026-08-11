using System;
using System.Collections.Generic;
using System.Text;

namespace Lycoris.Formats
{
    /// <summary>
    /// Arabic text shaper for Yo-kai Watch (a left-to-right engine with no Arabic shaping). Converts logical
    /// Arabic into the VISUAL, pre-shaped presentation-form string the game can render as correct RTL Arabic
    /// (contextual joining + lam-alef ligatures + right-to-left reordering). Latin/number runs and &lt;tags&gt;
    /// stay LTR; newlines split lines. Harakat are stripped (the injected font lacks combining marks).
    /// Ported from the YW1-AR toolkit's ArabicShaper (validated end-to-end).
    /// </summary>
    public static class ArabicShaper
    {
        public static bool UseArabicIndicDigits = false;   // keep Western 0-9 (the font's 0-9 glyphs are arabized)

        private sealed class L { public char J; public int Iso, Fin, Ini, Med; }
        private static readonly Dictionary<char, L> Letters = new Dictionary<char, L>();
        private static void A(int c, char j, int iso, int fin, int ini = 0, int med = 0) =>
            Letters[(char)c] = new L { J = j, Iso = iso, Fin = fin, Ini = ini, Med = med };

        static ArabicShaper()
        {
            A(0x0621, 'U', 0xFE80, 0);
            A(0x0622, 'R', 0xFE81, 0xFE82);
            A(0x0623, 'R', 0xFE83, 0xFE84);
            A(0x0624, 'R', 0xFE85, 0xFE86);
            A(0x0625, 'R', 0xFE87, 0xFE88);
            A(0x0626, 'D', 0xFE89, 0xFE8A, 0xFE8B, 0xFE8C);
            A(0x0627, 'R', 0xFE8D, 0xFE8E);
            A(0x0628, 'D', 0xFE8F, 0xFE90, 0xFE91, 0xFE92);
            A(0x0629, 'R', 0xFE93, 0xFE94);
            A(0x062A, 'D', 0xFE95, 0xFE96, 0xFE97, 0xFE98);
            A(0x062B, 'D', 0xFE99, 0xFE9A, 0xFE9B, 0xFE9C);
            A(0x062C, 'D', 0xFE9D, 0xFE9E, 0xFE9F, 0xFEA0);
            A(0x062D, 'D', 0xFEA1, 0xFEA2, 0xFEA3, 0xFEA4);
            A(0x062E, 'D', 0xFEA5, 0xFEA6, 0xFEA7, 0xFEA8);
            A(0x062F, 'R', 0xFEA9, 0xFEAA);
            A(0x0630, 'R', 0xFEAB, 0xFEAC);
            A(0x0631, 'R', 0xFEAD, 0xFEAE);
            A(0x0632, 'R', 0xFEAF, 0xFEB0);
            A(0x0633, 'D', 0xFEB1, 0xFEB2, 0xFEB3, 0xFEB4);
            A(0x0634, 'D', 0xFEB5, 0xFEB6, 0xFEB7, 0xFEB8);
            A(0x0635, 'D', 0xFEB9, 0xFEBA, 0xFEBB, 0xFEBC);
            A(0x0636, 'D', 0xFEBD, 0xFEBE, 0xFEBF, 0xFEC0);
            A(0x0637, 'D', 0xFEC1, 0xFEC2, 0xFEC3, 0xFEC4);
            A(0x0638, 'D', 0xFEC5, 0xFEC6, 0xFEC7, 0xFEC8);
            A(0x0639, 'D', 0xFEC9, 0xFECA, 0xFECB, 0xFECC);
            A(0x063A, 'D', 0xFECD, 0xFECE, 0xFECF, 0xFED0);
            A(0x0641, 'D', 0xFED1, 0xFED2, 0xFED3, 0xFED4);
            A(0x0642, 'D', 0xFED5, 0xFED6, 0xFED7, 0xFED8);
            A(0x0643, 'D', 0xFED9, 0xFEDA, 0xFEDB, 0xFEDC);
            A(0x0644, 'D', 0xFEDD, 0xFEDE, 0xFEDF, 0xFEE0);
            A(0x0645, 'D', 0xFEE1, 0xFEE2, 0xFEE3, 0xFEE4);
            A(0x0646, 'D', 0xFEE5, 0xFEE6, 0xFEE7, 0xFEE8);
            A(0x0647, 'D', 0xFEE9, 0xFEEA, 0xFEEB, 0xFEEC);
            A(0x0648, 'R', 0xFEED, 0xFEEE);
            A(0x0649, 'R', 0xFEEF, 0xFEF0);
            A(0x064A, 'D', 0xFEF1, 0xFEF2, 0xFEF3, 0xFEF4);
            A(0x0640, 'D', 0x0640, 0x0640, 0x0640, 0x0640);   // tatweel
        }

        private static readonly Dictionary<char, int[]> LamAlef = new Dictionary<char, int[]>
        {
            [(char)0x0622] = new[] { 0xFEF5, 0xFEF6 },
            [(char)0x0623] = new[] { 0xFEF7, 0xFEF8 },
            [(char)0x0625] = new[] { 0xFEF9, 0xFEFA },
            [(char)0x0627] = new[] { 0xFEFB, 0xFEFC },
        };

        /// <summary>True if the text contains base Arabic letters (0x0621-064A) but no presentation form
        /// (0xFE80-0xFEFC) — i.e. logical, not-yet-shaped Arabic. Used to skip already-shaped strings.</summary>
        public static bool NeedsShaping(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            bool baseArabic = false;
            foreach (char c in s)
            {
                if (c >= 0xFE70 && c <= 0xFEFC) return false;      // already shaped
                if (c >= 0x0621 && c <= 0x064A) baseArabic = true;
            }
            return baseArabic;
        }

        private static string StripHarakat(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (!((c >= 0x064B && c <= 0x0652) || c == 0x0670 || (c >= 0x0653 && c <= 0x0655)))
                    sb.Append(c);
            return sb.ToString();
        }

        private static string MapDigits(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c >= '0' && c <= '9' ? (char)(0x0660 + (c - '0')) : c);
            return sb.ToString();
        }

        private static bool IsDiacritic(char c) =>
            (c >= 0x064B && c <= 0x0652) || c == 0x0670 || (c >= 0x0653 && c <= 0x065F);
        private static bool IsArabicLetter(char c) => Letters.ContainsKey(c) && c != 0x0640;

        public static string Shape(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            input = StripHarakat(input);
            if (UseArabicIndicDigits) input = MapDigits(input);
            var sb = new StringBuilder(input.Length + 8);
            var lines = input.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++) { sb.Append(ShapeLine(lines[i])); if (i < lines.Length - 1) sb.Append('\n'); }
            return sb.ToString();
        }

        private static string ShapeLine(string line)
        {
            var tokens = Tokenise(line);
            for (int i = 0; i < tokens.Count; i++) if (!tokens[i].Tag) tokens[i].Text = ShapeSpan(tokens[i].Text);
            return Reorder(tokens);
        }

        private sealed class Tok { public string Text; public bool Tag; }

        private static List<Tok> Tokenise(string s)
        {
            var list = new List<Tok>(); int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '<') { int end = s.IndexOf('>', i); if (end >= 0) { list.Add(new Tok { Text = s.Substring(i, end - i + 1), Tag = true }); i = end + 1; continue; } }
                int j = i; while (j < s.Length && s[j] != '<') j++;
                list.Add(new Tok { Text = s.Substring(i, j - i), Tag = false }); i = j;
            }
            return list;
        }

        private static string ShapeSpan(string s)
        {
            var outp = new List<char>(s.Length); int n = s.Length;
            for (int i = 0; i < n; i++)
            {
                char c = s[i];
                if (IsDiacritic(c)) { outp.Add(c); continue; }
                if (c == 0x0644 && LamAlef.ContainsKey(SkipToLetter(s, i + 1, out int alefIdx)))
                {
                    bool jp = JoinsPrev(s, i);
                    outp.Add((char)LamAlef[s[alefIdx]][jp ? 1 : 0]); i = alefIdx; continue;
                }
                if (!IsArabicLetter(c)) { outp.Add(c); continue; }
                var L = Letters[c];
                bool joinsPrev = JoinsPrev(s, i), joinsNext = JoinsNext(s, i);
                int form = joinsPrev && joinsNext ? L.Med : joinsPrev ? L.Fin : joinsNext ? L.Ini : L.Iso;
                if (form == 0) form = joinsPrev ? L.Fin : L.Iso;
                if (form == 0) form = L.Iso;
                outp.Add((char)form);
            }
            return new string(outp.ToArray());
        }

        private static bool JoinsPrev(string s, int i)
        {
            for (int k = i - 1; k >= 0; k--) { if (IsDiacritic(s[k])) continue; return IsArabicLetter(s[k]) && Letters[s[k]].J == 'D'; }
            return false;
        }
        private static bool JoinsNext(string s, int i)
        {
            if (!IsArabicLetter(s[i]) || Letters[s[i]].J != 'D') return false;
            for (int k = i + 1; k < s.Length; k++) { if (IsDiacritic(s[k])) continue; return IsArabicLetter(s[k]); }
            return false;
        }
        private static char SkipToLetter(string s, int from, out int idx)
        {
            for (int k = from; k < s.Length; k++) if (!IsDiacritic(s[k])) { idx = k; return s[k]; }
            idx = from; return '\0';
        }

        private static string Reorder(List<Tok> tokens)
        {
            var runs = new List<KeyValuePair<string, bool>>();
            foreach (var t in tokens)
            {
                if (t.Tag) { runs.Add(new KeyValuePair<string, bool>(t.Text, false)); continue; }
                int i = 0;
                while (i < t.Text.Length)
                {
                    bool rtl = IsShapedArabic(t.Text[i]); int j = i;
                    while (j < t.Text.Length && IsShapedArabic(t.Text[j]) == rtl) j++;
                    runs.Add(new KeyValuePair<string, bool>(t.Text.Substring(i, j - i), rtl)); i = j;
                }
            }
            var sb = new StringBuilder();
            for (int r = runs.Count - 1; r >= 0; r--)
            {
                if (runs[r].Value) { var a = runs[r].Key.ToCharArray(); Array.Reverse(a); sb.Append(a); }
                else sb.Append(runs[r].Key);
            }
            return sb.ToString();
        }

        private static bool IsShapedArabic(char c) =>
            ((c >= 0xFB50 && c <= 0xFEFF) || (c >= 0x0600 && c <= 0x06FF))
            && !(c >= 0x0660 && c <= 0x0669);
    }
}
