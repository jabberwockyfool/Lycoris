using System.Collections.Generic;

namespace Lycoris.Formats
{
    /// <summary>Type tag stored as 2 bits per value inside an entry.</summary>
    public enum ValueType : byte
    {
        String = 0,
        Integer = 1,
        FloatingPoint = 2
        // 3 is invalid / used as a parse-validity guard
    }

    /// <summary>Width of every value in a file: 4 bytes (Int) or 8 bytes (Long). Auto-detected, not stored.</summary>
    public enum ValueLength
    {
        Int = 4,
        Long = 8
    }

    /// <summary>Text encoding of the string tables, taken from the footer.</summary>
    public enum StringEncoding : short
    {
        Sjis = 0,
        Utf8 = 1,
        Utf8_2 = 256,
        Utf8_3 = 257
    }

    /// <summary>A single positional value inside an entry.</summary>
    public sealed class T2bValue
    {
        public ValueType Type;
        public object Value;

        public T2bValue() { }
        public T2bValue(ValueType type, object value)
        {
            Type = type;
            Value = value;
        }

        public T2bValue Clone() => new T2bValue(Type, Value);

        public override string ToString() => Value?.ToString() ?? "<null>";
    }

    /// <summary>
    /// One entry = a named record. The file stores only the CRC32 of the name in the
    /// entry section; the literal name lives in a separate checksum/name string table
    /// and is matched back by CRC. That CRC32 is what Level-5 tooling calls the key/ParamID.
    /// </summary>
    public sealed class T2bEntry
    {
        public string Name;             // resolved from the checksum table (may be null if unknown)
        public uint Crc;                // the stored key
        public List<T2bValue> Values = new List<T2bValue>();

        /// <summary>Deep copy — used as a template when adding a new record of the same kind.</summary>
        public T2bEntry Clone()
        {
            var c = new T2bEntry { Name = Name, Crc = Crc };
            foreach (var v in Values) c.Values.Add(v.Clone());
            return c;
        }

        public override string ToString() => Name ?? $"0x{Crc:X8}";
    }

    /// <summary>A checksum-table row: the CRC key and the name text it maps to.</summary>
    public struct T2bName
    {
        public uint Crc;
        public string Name;
        public T2bName(uint crc, string name) { Crc = crc; Name = name; }
    }

    /// <summary>Full in-memory representation of a .cfg.bin (T2B) file.</summary>
    public sealed class T2bFile
    {
        public List<T2bEntry> Entries = new List<T2bEntry>();

        /// <summary>
        /// The checksum/name table exactly as read, in file order. Preserved so the writer
        /// can reproduce it faithfully (a mod usually edits values, not the name table).
        /// </summary>
        public List<T2bName> Names = new List<T2bName>();

        public ValueLength ValueLength = ValueLength.Int;
        public HashType HashType = HashType.Crc32Standard;
        public StringEncoding Encoding = StringEncoding.Utf8;
    }
}
