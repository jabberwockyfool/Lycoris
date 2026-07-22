using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Lycoris.Formats
{
    /// <summary>One file inside an XPCK archive.</summary>
    public sealed class XpckFile
    {
        public string Name;
        public byte[] Data;
        public XpckFile(string name, byte[] data) { Name = name; Data = data; }
    }

    /// <summary>
    /// Level-5 XPCK (.pck) archive reader/writer. Reimplemented from the on-disk format (magic "XPCK",
    /// 0x14 header, 0x0C entries sorted by CRC32-of-name, a Level-5-compressed Shift-JIS name table, and
    /// raw file data). All offsets/sizes are stored as value&gt;&gt;2. The name table is decoded with the
    /// existing Level-5 codec (LZ10) and re-emitted as a "stored" (method 0) container, which the game's
    /// loader also accepts. Used to inject an NPC's .npcbin into npc.pck and to repack the map .pck.
    /// </summary>
    public static class Xpck
    {
        private const uint Magic = 0x4B435058; // "XPCK"
        private static readonly Encoding Sjis = Encoding.GetEncoding(932);

        public static List<XpckFile> Read(byte[] file)
        {
            if (file.Length < 0x14 || BitConverter.ToUInt32(file, 0) != Magic)
                throw new InvalidDataException("Pas une archive XPCK.");

            byte fc1 = file[4], fc2 = file[5];
            int count = ((fc2 & 0x0F) << 8) | fc1;
            int fileInfoOffset = ReadU16(file, 6) << 2;
            int nameTableOffset = ReadU16(file, 8) << 2;
            int dataOffset = ReadU16(file, 10) << 2;
            int nameTableSize = ReadU16(file, 14) << 2;

            byte[] rawNames = Slice(file, nameTableOffset, nameTableSize);
            byte[] names = Imgc.DecompressLevel5(rawNames);

            var result = new List<XpckFile>();
            for (int i = 0; i < count; i++)
            {
                int e = fileInfoOffset + i * 0x0C;
                int nameOffset = ReadU16(file, e + 4);
                int foLow = ReadU16(file, e + 6);
                int fsLow = ReadU16(file, e + 8);
                int foHigh = file[e + 10];
                int fsHigh = file[e + 11];
                int fileOffset = ((foHigh << 16) | foLow) << 2;
                int fileSize = (fsHigh << 16) | fsLow;

                string name = ReadCString(names, nameOffset);
                byte[] data = Slice(file, dataOffset + fileOffset, fileSize);
                result.Add(new XpckFile(name, data));
            }
            // Present files in name-table order (stable, matches the original layout) for a clean re-pack.
            return result.OrderBy(f => NameOffsetOf(names, f.Name)).ToList();
        }

        public static byte[] Write(IList<XpckFile> files)
        {
            // Name table: names concatenated in the given order, each null-terminated (Shift-JIS).
            var nameBytes = new List<byte>();
            var nameOffsets = new int[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                nameOffsets[i] = nameBytes.Count;
                nameBytes.AddRange(Sjis.GetBytes(files[i].Name));
                nameBytes.Add(0);
            }
            byte[] storedNames = Imgc.StoreLevel5(nameBytes.ToArray());

            int fileInfoOffset = 0x14;
            int fileInfoSize = Align(files.Count * 0x0C, 4);
            int nameTableOffset = fileInfoOffset + fileInfoSize;
            int nameTableSize = Align(storedNames.Length, 16);
            int dataOffset = nameTableOffset + nameTableSize;

            // Lay out each file's data 16-aligned relative to dataOffset.
            var fileOffsets = new int[files.Count];
            int cursor = 0;
            for (int i = 0; i < files.Count; i++)
            {
                cursor = Align(cursor, 16);
                fileOffsets[i] = cursor;
                cursor += files[i].Data.Length;
            }
            int dataSize = Align(cursor, 16);

            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);

            // Header
            w.Write(Magic);
            var (fcHigh, fcLow) = FileCountToBytes(files.Count);
            w.Write(fcLow);                       // fc1
            w.Write(fcHigh);                      // fc2
            w.Write((ushort)(fileInfoOffset >> 2));
            w.Write((ushort)(nameTableOffset >> 2));
            w.Write((ushort)(dataOffset >> 2));
            w.Write((ushort)(fileInfoSize >> 2));
            w.Write((ushort)(nameTableSize >> 2));
            w.Write((uint)(dataSize >> 2));

            // File entries, sorted by CRC32 of the name.
            var order = Enumerable.Range(0, files.Count)
                .OrderBy(i => Crc32.Standard(Sjis.GetBytes(files[i].Name))).ToArray();
            foreach (int i in order)
            {
                int fo = fileOffsets[i] >> 2;
                int fs = files[i].Data.Length;
                w.Write(Crc32.Standard(Sjis.GetBytes(files[i].Name)));
                w.Write((ushort)nameOffsets[i]);
                w.Write((ushort)(fo & 0xFFFF));
                w.Write((ushort)(fs & 0xFFFF));
                w.Write((byte)((fo >> 16) & 0xFF));
                w.Write((byte)((fs >> 16) & 0xFF));
            }
            Pad(ms, fileInfoOffset + fileInfoSize);

            // Name table
            ms.Write(storedNames, 0, storedNames.Length);
            Pad(ms, nameTableOffset + nameTableSize);

            // File data (name-table order), each 16-aligned.
            for (int i = 0; i < files.Count; i++)
            {
                Pad(ms, dataOffset + fileOffsets[i]);
                ms.Write(files[i].Data, 0, files[i].Data.Length);
            }
            Pad(ms, dataOffset + dataSize);
            return ms.ToArray();
        }

        /// <summary>Add a file, or replace it if a file with the same name already exists.</summary>
        public static void AddOrReplace(IList<XpckFile> files, string name, byte[] data)
        {
            var existing = files.FirstOrDefault(f => f.Name == name);
            if (existing != null) existing.Data = data;
            else files.Add(new XpckFile(name, data));
        }

        // ---- helpers ----

        // File-count field: smallest i with (1<<i) >= count as the high nibble hint, count in the low 12 bits.
        private static (byte high, byte low) FileCountToBytes(int count)
        {
            int i = 0;
            while ((1 << i) < count && i < 11) i++;
            int result = ((i << 12) | count) & 0xFFFF;
            return ((byte)((result >> 8) & 0xFF), (byte)(result & 0xFF));
        }

        private static int Align(int v, int a) => (v + a - 1) / a * a;

        private static void Pad(MemoryStream ms, int target)
        {
            while (ms.Length < target) ms.WriteByte(0);
        }

        private static int ReadU16(byte[] d, int o) => d[o] | (d[o + 1] << 8);

        private static byte[] Slice(byte[] d, int off, int len)
        {
            var r = new byte[len];
            Array.Copy(d, off, r, 0, len);
            return r;
        }

        private static string ReadCString(byte[] d, int off)
        {
            int end = off;
            while (end < d.Length && d[end] != 0) end++;
            return Sjis.GetString(d, off, end - off);
        }

        private static int NameOffsetOf(byte[] names, string name)
        {
            byte[] target = Sjis.GetBytes(name);
            for (int i = 0; i + target.Length <= names.Length; i++)
            {
                bool match = (i == 0 || names[i - 1] == 0);
                for (int j = 0; match && j < target.Length; j++) if (names[i + j] != target[j]) match = false;
                if (match && (i + target.Length == names.Length || names[i + target.Length] == 0)) return i;
            }
            return int.MaxValue;
        }
    }
}
