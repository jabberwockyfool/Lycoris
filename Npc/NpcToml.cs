using System;
using System.Globalization;
using System.Text;

namespace Lycoris.Npc
{
    /// <summary>
    /// Minimal reader/writer for the flat NPCMake TOML schema (11 top-level keys, no tables/arrays). Not a
    /// general TOML library — just enough to round-trip the NPC config and stay compatible with NPCMake
    /// (which parses with Tomlyn). BaseId is emitted in hex, OnTalk as a multiline basic string.
    /// </summary>
    public static class NpcToml
    {
        public static string Write(NpcModel n)
        {
            var sb = new StringBuilder();
            sb.Append("# NPCMake TOML — généré par Lycoris\n");
            sb.Append("# Voir le guide NPCMake sur yo-docs pour le détail des champs.\n\n");
            sb.Append($"NpcName = {BasicString(n.NpcName)}\n");
            sb.Append($"BaseId = 0x{unchecked((uint)n.BaseId):X}\n");
            sb.Append($"NpcX = {Num(n.NpcX)}\n");
            sb.Append($"NpcY = {Num(n.NpcY)}\n");
            sb.Append($"NpcZ = {Num(n.NpcZ)}\n");
            sb.Append($"NpcRotation = {n.NpcRotation}\n");
            sb.Append($"ChapterCode = {BasicString(n.ChapterCode)}\n");
            sb.Append($"MapID = {BasicString(n.MapID)}\n");
            sb.Append($"OnTalk = \"\"\"\n{MultilineBody(n.OnTalk)}\"\"\"\n");
            sb.Append($"AppearCond = {BasicString(n.AppearCond)}\n");
            sb.Append($"IsYw1 = {(n.IsYw1 ? "true" : "false")}\n");
            sb.Append($"NpcType = {NpcTypeValue(n.NpcType)}\n");
            return sb.ToString();
        }

        public static NpcModel Parse(string text)
        {
            var n = new NpcModel();
            if (text == null) return n;
            var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                if (val.StartsWith("\"\"\""))
                {
                    string body = ReadMultiline(lines, ref i, val);
                    Assign(n, key, body, alreadyLiteral: true);
                }
                else
                {
                    Assign(n, key, val, alreadyLiteral: false);
                }
            }
            return n;
        }

        // ---- writing helpers ----

        private static string Num(double d) =>
            d == Math.Floor(d) && !double.IsInfinity(d)
                ? ((long)d).ToString(CultureInfo.InvariantCulture)
                : d.ToString("R", CultureInfo.InvariantCulture);

        private static string NpcTypeValue(string t) =>
            int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? t : BasicString(t);

        private static string BasicString(string s)
        {
            s = s ?? "";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\r': break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // Multiline basic string body: escape backslashes; keep newlines/quotes literal (but never """).
        private static string MultilineBody(string s)
        {
            s = (s ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\\", "\\\\").Replace("\"\"\"", "\\\"\\\"\\\"");
            return s.EndsWith("\n") ? s : s + "\n";
        }

        // ---- parsing helpers ----

        private static string ReadMultiline(string[] lines, ref int i, string firstVal)
        {
            var sb = new StringBuilder();
            string rest = firstVal.Substring(3);
            int close = rest.IndexOf("\"\"\"", StringComparison.Ordinal);
            if (close >= 0) return TrimLeadingNewline(rest.Substring(0, close));

            if (rest.Length > 0) sb.Append(rest).Append('\n');
            for (i++; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf("\"\"\"", StringComparison.Ordinal);
                if (c >= 0) { sb.Append(lines[i].Substring(0, c)); break; }
                sb.Append(lines[i]).Append('\n');
            }
            return TrimLeadingNewline(sb.ToString());
        }

        private static string TrimLeadingNewline(string s)
        {
            // TOML trims a newline immediately following the opening """.
            if (s.StartsWith("\n")) s = s.Substring(1);
            // Also drop the single trailing newline the writer adds, so write→read→write is stable.
            if (s.EndsWith("\n")) s = s.Substring(0, s.Length - 1);
            return s;
        }

        private static void Assign(NpcModel n, string key, string raw, bool alreadyLiteral)
        {
            string text = alreadyLiteral ? UnescapeMultiline(raw) : StripComment(raw).Trim();
            switch (key)
            {
                case "NpcName": n.NpcName = alreadyLiteral ? text : Unquote(text); break;
                case "BaseId": n.BaseId = ParseInt(text); break;
                case "NpcX": n.NpcX = ParseDouble(text); break;
                case "NpcY": n.NpcY = ParseDouble(text); break;
                case "NpcZ": n.NpcZ = ParseDouble(text); break;
                case "NpcRotation": n.NpcRotation = ParseInt(text); break;
                case "ChapterCode": n.ChapterCode = Unquote(text); break;
                case "MapID": n.MapID = Unquote(text); break;
                case "OnTalk": n.OnTalk = alreadyLiteral ? text : Unquote(text); break;
                case "AppearCond": n.AppearCond = Unquote(text); break;
                case "IsYw1": n.IsYw1 = text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case "NpcType": n.NpcType = text.StartsWith("\"") ? Unquote(text) : text.Trim(); break;
            }
        }

        private static string StripComment(string s)
        {
            // Only strip a comment for non-quoted scalars; if it looks quoted, leave it.
            if (s.StartsWith("\"")) return s;
            int h = s.IndexOf('#');
            return h >= 0 ? s.Substring(0, h) : s;
        }

        private static int ParseInt(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x") || s.StartsWith("0X"))
                return uint.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint u) ? unchecked((int)u) : 0;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : 0;
        }

        private static double ParseDouble(string s) =>
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0;

        private static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2);
            return s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
        }

        private static string UnescapeMultiline(string s) =>
            s.Replace("\\\"\\\"\\\"", "\"\"\"").Replace("\\\\", "\\");
    }
}
