using System.Linq;

namespace Lycoris.Formats
{
    /// <summary>Typed, bounds-safe accessors for positional entry values.</summary>
    public static class T2bEntryExtensions
    {
        public static int? GetInt(this T2bEntry e, int index)
        {
            if (e == null || index < 0 || index >= e.Values.Count) return null;
            object v = e.Values[index].Value;
            switch (v)
            {
                case int i: return i;
                case long l: return unchecked((int)l);
                case float f: return (int)f;
                default: return null;
            }
        }

        public static float? GetFloat(this T2bEntry e, int index)
        {
            if (e == null || index < 0 || index >= e.Values.Count) return null;
            object v = e.Values[index].Value;
            switch (v)
            {
                case float f: return f;
                case double d: return (float)d;
                case int i: return i;
                default: return null;
            }
        }

        public static string GetString(this T2bEntry e, int index)
        {
            if (e == null || index < 0 || index >= e.Values.Count) return null;
            return e.Values[index].Value as string;
        }

        /// <summary>First String-typed, non-null value — the human text in a noun/text record.</summary>
        public static string FirstText(this T2bEntry e)
        {
            return e?.Values
                .Where(v => v.Type == ValueType.String && v.Value is string s && s.Length > 0)
                .Select(v => (string)v.Value)
                .FirstOrDefault();
        }

        /// <summary>First Integer-typed value — the key/hash in a noun/text record.</summary>
        public static int? FirstIntKey(this T2bEntry e)
        {
            var first = e?.Values.FirstOrDefault(v => v.Type == ValueType.Integer);
            return first?.Value is int i ? i : (first?.Value is long l ? (int?)unchecked((int)l) : null);
        }
    }
}
