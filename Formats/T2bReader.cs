using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lycoris.Formats
{
    /// <summary>
    /// Reads a Level-5 T2B (.cfg.bin) file into a <see cref="T2bFile"/>.
    /// Standalone: no external binary-IO or crypto dependencies.
    /// </summary>
    public static class T2bReader
    {
        private const uint FooterMagic = 0x62327401; // "\x01t2b"

        public static T2bFile Read(byte[] data)
        {
            if (data == null || data.Length < 0x30)
                throw new InvalidDataException("File too small to be a T2B (.cfg.bin).");

            // --- Footer (last 0x10 bytes) ---
            int footerPos = data.Length - 0x10;
            uint magic = ReadU32(data, footerPos);
            if (magic != FooterMagic)
                throw new InvalidDataException("Not a T2B file: footer magic mismatch.");

            short encodingRaw = ReadS16(data, footerPos + 6);
            var stringEncoding = (StringEncoding)encodingRaw;
            Encoding enc = stringEncoding == StringEncoding.Sjis
                ? Encoding.GetEncoding(932)
                : new UTF8Encoding(false);

            // --- Entry header (0x00) ---
            uint entryCount = ReadU32(data, 0x00);
            uint stringDataOffset = ReadU32(data, 0x04);
            uint stringDataLength = ReadU32(data, 0x08);
            // stringDataCount at 0x0C — not needed for reading.

            // --- Detect value width, then read entries ---
            ValueLength valueLength = DetectValueLength(data, (int)entryCount, (int)stringDataOffset, enc);
            var rawEntries = ReadEntries(data, (int)entryCount, valueLength, (int)stringDataOffset, enc, out _);

            // --- Checksum / name section: crc -> name lookup ---
            int checksumSectionOffset = Align16((int)(stringDataOffset + stringDataLength));
            var orderedNames = new List<T2bName>();
            Dictionary<uint, string> names = ReadChecksumSection(data, checksumSectionOffset, enc, orderedNames);

            // --- Detect hash type from a known (crc, name) pair ---
            HashType hashType = orderedNames.Count > 0
                ? DetectHashType(orderedNames[0].Crc, orderedNames[0].Name, enc)
                : HashType.Crc32Standard;

            // --- Resolve names onto entries ---
            var file = new T2bFile
            {
                ValueLength = valueLength,
                HashType = hashType,
                Encoding = stringEncoding,
                Names = orderedNames
            };
            foreach (var e in rawEntries)
            {
                names.TryGetValue(e.Crc, out string name);
                e.Name = name;
                file.Entries.Add(e);
            }
            return file;
        }

        public static T2bFile ReadFile(string path) => Read(File.ReadAllBytes(path));

        // -------------------------------------------------------------------

        private static List<T2bEntry> ReadEntries(byte[] data, int entryCount, ValueLength valueLength,
            int valueStringTableOffset, Encoding enc, out int endPos)
        {
            var entries = new List<T2bEntry>(entryCount);
            int pos = 0x10;
            int vlen = (int)valueLength;

            for (int i = 0; i < entryCount; i++)
            {
                var entry = new T2bEntry { Crc = ReadU32(data, pos) };
                pos += 4;

                int count = data[pos];
                pos += 1;

                // Packed type bits: ceil(count/4) bytes, 2 bits per value, then align to 4.
                var types = new ValueType[count];
                int typeBytes = (count + 3) / 4;
                for (int j = 0; j < count; j += 4)
                {
                    byte chunk = data[pos + j / 4];
                    for (int h = 0; h < 4 && j + h < count; h++)
                        types[j + h] = (ValueType)((chunk >> (h * 2)) & 0x3);
                }
                pos += typeBytes;
                pos = Align(pos, 4);

                // Values.
                for (int v = 0; v < count; v++)
                {
                    long raw = vlen == 4 ? ReadS32(data, pos) : ReadS64(data, pos);
                    pos += vlen;

                    object value;
                    switch (types[v])
                    {
                        case ValueType.String:
                            value = raw < 0 ? null : ReadCString(data, valueStringTableOffset + (int)raw, enc);
                            break;
                        case ValueType.FloatingPoint:
                            value = vlen == 4
                                ? (object)Int32BitsToSingle((int)raw)
                                : Int64BitsToDouble(raw);
                            break;
                        default: // Integer
                            value = vlen == 4 ? (object)(int)raw : raw;
                            break;
                    }
                    entry.Values.Add(new T2bValue(types[v], value));
                }

                entries.Add(entry);
            }

            endPos = pos;
            return entries;
        }

        /// <summary>
        /// Trial-parse the entry array at width 4 then 8, keeping the width whose parse
        /// lands cleanly just before the value string table and never yields an invalid type.
        /// </summary>
        private static ValueLength DetectValueLength(byte[] data, int entryCount, int stringDataOffset, Encoding enc)
        {
            foreach (var candidate in new[] { ValueLength.Int, ValueLength.Long })
            {
                if (TryParseEntries(data, entryCount, candidate, out int endPos)
                    && endPos <= stringDataOffset
                    && stringDataOffset - endPos < 0x10)
                {
                    return candidate;
                }
            }
            // Fall back to Int if neither cleanly matched (best effort).
            return ValueLength.Int;
        }

        private static bool TryParseEntries(byte[] data, int entryCount, ValueLength valueLength, out int endPos)
        {
            endPos = 0;
            try
            {
                int pos = 0x10;
                int vlen = (int)valueLength;
                for (int i = 0; i < entryCount; i++)
                {
                    pos += 4; // crc
                    int count = data[pos];
                    pos += 1;
                    int typeBytes = (count + 3) / 4;
                    // Guard: reject invalid type value 3.
                    for (int j = 0; j < count; j += 4)
                    {
                        byte chunk = data[pos + j / 4];
                        for (int h = 0; h < 4 && j + h < count; h++)
                            if (((chunk >> (h * 2)) & 0x3) == 3)
                                return false;
                    }
                    pos += typeBytes;
                    pos = Align(pos, 4);
                    pos += count * vlen;
                    if (pos > data.Length) return false;
                }
                endPos = pos;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<uint, string> ReadChecksumSection(byte[] data, int sectionOffset,
            Encoding enc, List<T2bName> ordered)
        {
            var map = new Dictionary<uint, string>();
            if (sectionOffset + 0x10 > data.Length) return map;

            uint count = ReadU32(data, sectionOffset + 0x04);
            uint stringOffset = ReadU32(data, sectionOffset + 0x08);
            int nameTable = sectionOffset + (int)stringOffset;

            int pos = sectionOffset + 0x10;
            for (int i = 0; i < count; i++)
            {
                uint crc = ReadU32(data, pos);
                uint strOff = ReadU32(data, pos + 4);
                pos += 8;
                string name = ReadCString(data, nameTable + (int)strOff, enc);
                ordered.Add(new T2bName(crc, name));
                if (!map.ContainsKey(crc)) map[crc] = name;
            }
            return map;
        }

        /// <summary>Recompute a known name both ways and keep whichever reproduces its stored crc.</summary>
        private static HashType DetectHashType(uint crc, string name, Encoding enc)
        {
            if (name == null) return HashType.Crc32Standard;
            if (Crc32.Standard(enc.GetBytes(name)) == crc) return HashType.Crc32Standard;
            if (Crc32.Jam(enc.GetBytes(name)) == crc) return HashType.Crc32Jam;
            return HashType.Crc32Standard;
        }

        // ---- primitive readers ----

        private static int Align(int value, int alignment)
        {
            int mask = alignment - 1;
            return (value + mask) & ~mask;
        }

        private static int Align16(int value) => Align(value, 0x10);

        private static uint ReadU32(byte[] d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
        private static int ReadS32(byte[] d, int o) => d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24;
        private static short ReadS16(byte[] d, int o) => (short)(d[o] | d[o + 1] << 8);

        private static long ReadS64(byte[] d, int o)
        {
            uint lo = ReadU32(d, o);
            uint hi = ReadU32(d, o + 4);
            return (long)((ulong)hi << 32 | lo);
        }

        private static string ReadCString(byte[] d, int o, Encoding enc)
        {
            if (o < 0 || o >= d.Length) return null;
            int end = o;
            while (end < d.Length && d[end] != 0) end++;
            return enc.GetString(d, o, end - o);
        }

        // .NET Framework 4.7.2 lacks BitConverter.Int32BitsToSingle / Int64BitsToDouble.
        private static float Int32BitsToSingle(int value) => BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
        private static double Int64BitsToDouble(long value) => BitConverter.Int64BitsToDouble(value);
    }
}
