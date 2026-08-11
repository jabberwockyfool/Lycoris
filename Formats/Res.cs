using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lycoris.Formats
{
    /// <summary>
    /// Minimal reader/writer for a Level-5 RES.bin (the resource manifest inside a .xc). Enough to
    /// ADD an MTNINF (type 400) split entry — needed when adding a new animation slot to an archive:
    /// the game registers slots from this table, so a new .mtninf must have a matching RES entry.
    /// Ported from studio_eleven res.py (read_data / write_res); re-emitted as a Level-5 "stored"
    /// block (method 0), which the game accepts (same as the fork's _relabel_res).
    ///
    /// Header (after 8-byte magic): u16 strOff/4, u16 unk1, u16 matTableOff/4, u16 matCount,
    /// u16 nodeOff/4, u16 nodeCount. Each table entry (8B): u16 dataOff/4, u16 count, u16 type, u16 len.
    /// Section data = count × len bytes. MTNINF entry (8B) = u32 slotId + u32 stringOffset.
    /// </summary>
    public sealed class Res
    {
        public const int MTNINF = 400;

        public sealed class Section
        {
            public int Type;
            public int Length;               // per-entry byte length
            public List<byte[]> Entries = new List<byte[]>();
        }

        public byte[] Magic;                 // 8 bytes ("CHRC00\0\0")
        public ushort Unk1;
        public readonly List<Section> Materials = new List<Section>();
        public readonly List<Section> Nodes = new List<Section>();
        public byte[] StringTable = new byte[0];

        public static Res Read(byte[] compressed)
        {
            byte[] d = Imgc.DecompressLevel5(compressed);
            var r = new Res();
            r.Magic = new byte[8];
            Array.Copy(d, 0, r.Magic, 0, 8);

            int strOff = U16(d, 8) << 2;
            r.Unk1 = (ushort)U16(d, 10);
            int matOff = U16(d, 12) << 2;
            int matCount = U16(d, 14);
            int nodeOff = U16(d, 16) << 2;
            int nodeCount = U16(d, 18);

            ReadTable(d, matOff, matCount, r.Materials);
            ReadTable(d, nodeOff, nodeCount, r.Nodes);

            int strLen = d.Length - strOff;
            r.StringTable = new byte[strLen > 0 ? strLen : 0];
            if (strLen > 0) Array.Copy(d, strOff, r.StringTable, 0, strLen);
            return r;
        }

        private static void ReadTable(byte[] d, int tableOff, int count, List<Section> into)
        {
            for (int i = 0; i < count; i++)
            {
                int p = tableOff + i * 8;
                int dataOff = U16(d, p) << 2;
                int cnt = U16(d, p + 2);
                int type = U16(d, p + 4);
                int len = U16(d, p + 6);
                var sec = new Section { Type = type, Length = len };
                for (int j = 0; j < cnt; j++)
                {
                    var e = new byte[len];
                    Array.Copy(d, dataOff + j * len, e, 0, len);
                    sec.Entries.Add(e);
                }
                into.Add(sec);
            }
        }

        /// <summary>Add a split entry to the MTNINF section (creating it if absent): slotId + a new
        /// cosmetic name in the string table. The slotId must equal the new .mtninf's slot (@0x1C).</summary>
        public void AddMtninf(byte[] slotId4, string name)
        {
            var sec = Nodes.Find(s => s.Type == MTNINF);
            if (sec == null)
            {
                sec = new Section { Type = MTNINF, Length = 8 };
                Nodes.Add(sec);
            }
            int strOffset = StringTable.Length;
            byte[] nmeBytes = Encoding.UTF8.GetBytes((name ?? "slot") + "\0");
            var ns = new byte[StringTable.Length + nmeBytes.Length];
            Array.Copy(StringTable, ns, StringTable.Length);
            Array.Copy(nmeBytes, 0, ns, StringTable.Length, nmeBytes.Length);
            StringTable = ns;

            var entry = new byte[8];
            Array.Copy(slotId4, 0, entry, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)strOffset), 0, entry, 4, 4);
            sec.Entries.Add(entry);
        }

        public byte[] Write()
        {
            var table = new MemoryStream();
            var data = new MemoryStream();
            int sectionCount = Materials.Count + Nodes.Count;
            int headerPos = 20;
            int dataPos = 20 + sectionCount * 8;

            int matTableOff = 0, nodeTableOff = 0;
            if (Materials.Count > 0) matTableOff = headerPos >> 2;
            WriteSections(Materials, table, data, ref headerPos, ref dataPos);
            if (Nodes.Count > 0) nodeTableOff = headerPos >> 2;
            WriteSections(Nodes, table, data, ref headerPos, ref dataPos);

            int stringOffset = ((int)data.Length + 20 + sectionCount * 8) >> 2;
            data.Write(StringTable, 0, StringTable.Length);

            var res = new MemoryStream();
            res.Write(Magic, 0, 8);
            var w = new BinaryWriter(res);
            w.Write((ushort)stringOffset);
            w.Write(Unk1);
            w.Write((ushort)matTableOff);
            w.Write((ushort)Materials.Count);
            w.Write((ushort)nodeTableOff);
            w.Write((ushort)Nodes.Count);
            var tableBytes = table.ToArray();
            var dataBytes = data.ToArray();
            res.Write(tableBytes, 0, tableBytes.Length);
            res.Write(dataBytes, 0, dataBytes.Length);
            return Imgc.StoreLevel5(res.ToArray());
        }

        private static void WriteSections(List<Section> secs, MemoryStream table, MemoryStream data,
                                          ref int headerPos, ref int dataPos)
        {
            var tw = new BinaryWriter(table);
            foreach (var s in secs)
            {
                int len = s.Entries.Count > 0 ? s.Entries[0].Length : s.Length;
                tw.Write((ushort)(dataPos >> 2));
                tw.Write((ushort)s.Entries.Count);
                tw.Write((ushort)s.Type);
                tw.Write((ushort)len);
                headerPos += 8;
                foreach (var e in s.Entries) { data.Write(e, 0, e.Length); dataPos += e.Length; }
            }
        }

        private static int U16(byte[] d, int o) => d[o] | (d[o + 1] << 8);
    }
}
