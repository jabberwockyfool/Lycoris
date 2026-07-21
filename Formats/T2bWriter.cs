using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lycoris.Formats
{
    /// <summary>
    /// Serialises a <see cref="T2bFile"/> back to Level-5 T2B (.cfg.bin) bytes.
    /// Layout: entry section -> value string table -> checksum(name) section -> footer,
    /// each 16-aligned. String tables dedup exact full strings (matching Level-5's own
    /// output) and alignment gaps are 0xFF, so real YW3 files round-trip byte-for-byte.
    /// </summary>
    public static class T2bWriter
    {
        public static void WriteFile(T2bFile file, string path) => File.WriteAllBytes(path, Write(file));

        public static byte[] Write(T2bFile file)
        {
            Encoding enc = file.Encoding == StringEncoding.Sjis
                ? Encoding.GetEncoding(932)
                : new UTF8Encoding(false);
            int vlen = (int)file.ValueLength;

            // --- Pass 1: entry section (also builds the value string pool in reference order) ---
            var valuePool = new StringPool(enc);
            var entryBytes = new MemoryStream();
            using (var bw = new BinaryWriter(entryBytes))
            {
                foreach (var entry in file.Entries)
                    WriteEntry(bw, entry, vlen, valuePool);
            }
            byte[] entrySection = entryBytes.ToArray();
            byte[] valueStrings = valuePool.Blob;

            int stringDataOffset = Align16(0x10 + entrySection.Length);
            int checksumPartition = Align16(stringDataOffset + valueStrings.Length);

            // --- Checksum (name) section ---
            var namePool = new StringPool(enc);
            var checksumEntries = new MemoryStream();
            using (var bw = new BinaryWriter(checksumEntries))
            {
                foreach (var n in file.Names)
                {
                    bw.Write(n.Crc);
                    bw.Write(namePool.Add(n.Name));
                }
            }
            byte[] checksumEntryBytes = checksumEntries.ToArray();
            int nameBlobOffset = Align16(0x10 + checksumEntryBytes.Length);
            byte[] nameStrings = namePool.Blob;
            int checksumSize = Align16(nameBlobOffset + nameStrings.Length);

            int totalSize = checksumPartition + checksumSize + 0x10; // + footer (16 bytes)
            var outBuf = new byte[totalSize];
            // Inter-section alignment gaps are 0xFF in the originals; pre-fill so any byte we
            // never explicitly write (i.e. only the padding) ends up 0xFF.
            for (int i = 0; i < outBuf.Length; i++) outBuf[i] = 0xFF;

            // --- Entry header (0x00) ---
            WriteU32(outBuf, 0x00, (uint)file.Entries.Count);
            WriteU32(outBuf, 0x04, (uint)stringDataOffset);
            WriteU32(outBuf, 0x08, (uint)valueStrings.Length);
            WriteU32(outBuf, 0x0C, (uint)valuePool.Count);

            Buffer.BlockCopy(entrySection, 0, outBuf, 0x10, entrySection.Length);
            Buffer.BlockCopy(valueStrings, 0, outBuf, stringDataOffset, valueStrings.Length);

            // --- Checksum header + body ---
            WriteU32(outBuf, checksumPartition + 0x00, (uint)checksumSize);
            WriteU32(outBuf, checksumPartition + 0x04, (uint)file.Names.Count);
            WriteU32(outBuf, checksumPartition + 0x08, (uint)nameBlobOffset);
            WriteU32(outBuf, checksumPartition + 0x0C, (uint)nameStrings.Length);
            Buffer.BlockCopy(checksumEntryBytes, 0, outBuf, checksumPartition + 0x10, checksumEntryBytes.Length);
            Buffer.BlockCopy(nameStrings, 0, outBuf, checksumPartition + nameBlobOffset, nameStrings.Length);

            // --- Footer ---
            int footer = checksumPartition + checksumSize;
            outBuf[footer + 0] = 0x01;
            outBuf[footer + 1] = (byte)'t';
            outBuf[footer + 2] = (byte)'2';
            outBuf[footer + 3] = (byte)'b';
            WriteU16(outBuf, footer + 4, 0x01FE);
            WriteU16(outBuf, footer + 6, (ushort)file.Encoding);
            WriteU16(outBuf, footer + 8, 0x0001);
            for (int i = footer + 10; i < footer + 0x10; i++) outBuf[i] = 0xFF;

            return outBuf;
        }

        private static void WriteEntry(BinaryWriter bw, T2bEntry entry, int vlen, StringPool valuePool)
        {
            int count = entry.Values.Count;
            bw.Write(entry.Crc);
            bw.Write((byte)count);

            // Packed 2-bit type tags, LSB-first within each byte.
            int typeBytes = (count + 3) / 4;
            var packed = new byte[typeBytes];
            for (int i = 0; i < count; i++)
                packed[i / 4] |= (byte)(((int)entry.Values[i].Type & 0x3) << ((i % 4) * 2));
            bw.Write(packed);

            // Pad (count byte + type bytes) region to 4-byte alignment. Original fills 0xFF.
            int written = 1 + typeBytes;
            for (int p = written; (p & 3) != 0; p++) bw.Write((byte)0xFF);

            // Values.
            foreach (var v in entry.Values)
            {
                switch (v.Type)
                {
                    case ValueType.String:
                        WriteScalar(bw, v.Value == null ? -1 : valuePool.Add((string)v.Value), vlen);
                        break;
                    case ValueType.FloatingPoint:
                        if (vlen == 4)
                            WriteScalar(bw, SingleToInt32Bits(Convert.ToSingle(v.Value)), vlen);
                        else
                            bw.Write(BitConverter.DoubleToInt64Bits(Convert.ToDouble(v.Value)));
                        break;
                    default: // Integer
                        if (vlen == 4) WriteScalar(bw, Convert.ToInt32(v.Value), vlen);
                        else bw.Write(Convert.ToInt64(v.Value));
                        break;
                }
            }
        }

        private static void WriteScalar(BinaryWriter bw, long value, int vlen)
        {
            if (vlen == 4) bw.Write((int)value);
            else bw.Write(value);
        }

        /// <summary>
        /// Append-only string table with exact full-string dedup (Level-5's scheme).
        /// Returns the byte offset of the string, reusing an identical earlier one.
        /// </summary>
        private sealed class StringPool
        {
            private readonly Encoding _enc;
            private readonly MemoryStream _blob = new MemoryStream();
            private readonly Dictionary<string, int> _cache = new Dictionary<string, int>(StringComparer.Ordinal);

            public StringPool(Encoding enc) { _enc = enc; }

            public int Count { get; private set; }
            public byte[] Blob => _blob.ToArray();

            public int Add(string s)
            {
                if (s == null) return -1;
                if (_cache.TryGetValue(s, out int existing)) return existing;

                int offset = (int)_blob.Length;
                byte[] bytes = _enc.GetBytes(s);
                _blob.Write(bytes, 0, bytes.Length);
                _blob.WriteByte(0);
                Count++;
                _cache[s] = offset;
                return offset;
            }
        }

        private static int Align16(int value) => (value + 0xF) & ~0xF;

        private static void WriteU32(byte[] d, int o, uint v)
        {
            d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24);
        }

        private static void WriteU16(byte[] d, int o, ushort v)
        {
            d[o] = (byte)v; d[o + 1] = (byte)(v >> 8);
        }

        private static int SingleToInt32Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
    }
}
