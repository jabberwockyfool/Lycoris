using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Lycoris.Formats;

namespace Lycoris
{
    /// <summary>
    /// Builds a custom dialogue portrait (.xa bustup) from a single image, by reusing a vanilla bustup as a
    /// template and grafting the new picture onto the NEUTRAL expression (&lt;A01/01&gt;). It keeps the template's
    /// ANMC (RES.bin) / PBI / vertex-format intact — so the game still parses it — and only: (1) replaces the
    /// atlas (000.xi) with the new image, (2) APPENDS one full-image quad to the vertex buffer, and (3) repoints
    /// the neutral body/face PBIs at that quad. Other expressions are left untouched (they map old atlas coords
    /// and are unused for a custom portrait). Round-trip-verified against the bustup renderer.
    /// </summary>
    internal static class BustupBuilder
    {
        /// <summary>Build the .xa bytes. <paramref name="bgra"/> is top-down BGRA of width×height (both multiples
        /// of 8, as required by the XI encoder).</summary>
        public static byte[] Build(byte[] templateXa, byte[] bgra, int width, int height)
        {
            var files = Xpck.Read(templateXa);
            var xi = files.FirstOrDefault(f => f.Name.EndsWith(".xi", StringComparison.OrdinalIgnoreCase));
            var pvb = files.FirstOrDefault(f => f.Name.EndsWith(".pvb", StringComparison.OrdinalIgnoreCase));
            var res = files.FirstOrDefault(f => f.Name.IndexOf("RES.bin", StringComparison.OrdinalIgnoreCase) >= 0);
            var pbis = files.Where(f => f.Name.EndsWith(".pbi", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (xi == null || pvb == null || res == null || pbis.Count == 0)
                throw new InvalidOperationException("Template .xa is missing an XI/PVB/RES/PBI part.");

            // 1. new atlas
            xi.Data = Imgc.EncodeXi(bgra, width, height);

            // 2. append a full-image quad to the vertex buffer
            int vOff = U16(pvb.Data, 8), count = I32(pvb.Data, 0x0C);
            byte[] verts = Imgc.DecompressLevel5(Slice(pvb.Data, vOff, pvb.Data.Length - vOff));
            var nv = new List<byte>(verts);                    // vertex stride is 20 bytes (x,y,z,u,v floats)
            float hw = width / 2f, hh = height / 2f;           // positions relative to image centre
            AddVertex(nv, -hw, -hh, 0, 0);                     // +0 TL
            AddVertex(nv, hw, -hh, width, 0);                  // +1 TR
            AddVertex(nv, -hw, hh, 0, height);                 // +2 BL
            AddVertex(nv, hw, hh, width, height);              // +3 BR
            int baseIdx = count;
            byte[] header = Slice(pvb.Data, 0, vOff);
            W32(header, 0x0C, count + 4);                       // patch vertex count
            pvb.Data = Concat(header, Imgc.StoreLevel5(nv.ToArray()));

            // 3. repoint the neutral body/face PBIs at the new quad (learned order: TL,BL,TR,BR,TR,BL)
            ushort[] quad = { (ushort)(baseIdx + 0), (ushort)(baseIdx + 2), (ushort)(baseIdx + 1),
                              (ushort)(baseIdx + 3), (ushort)(baseIdx + 1), (ushort)(baseIdx + 2) };
            var names = ParseResNames(res.Data);
            var targets = new List<int>();
            foreach (var key in new[] { "#0101_01 1", "#0101_01 0" })
                if (names.TryGetValue(key, out int i)) targets.Add(i);
            if (targets.Count == 0) targets.Add(0);            // fallback: first part
            foreach (int idx in targets.Distinct())
                if (idx >= 0 && idx < pbis.Count)
                    pbis[idx].Data = BuildPbi(pbis[idx].Data, quad);

            return Xpck.Write(files);
        }

        private static void AddVertex(List<byte> b, float x, float y, float u, float v)
        {
            b.AddRange(BitConverter.GetBytes(x));
            b.AddRange(BitConverter.GetBytes(y));
            b.AddRange(BitConverter.GetBytes(0f));             // z
            b.AddRange(BitConverter.GetBytes(u));
            b.AddRange(BitConverter.GetBytes(v));
        }

        // Rebuild an XPVI PBI: keep its 0x0C-byte header, patch point count, write a Level-5 "stored" index block.
        private static byte[] BuildPbi(byte[] template, ushort[] indices)
        {
            int blockOff = U16(template, 6);                   // where the index block starts (0x0C)
            if (blockOff < 0x0C || blockOff > template.Length) blockOff = 0x0C;
            byte[] header = Slice(template, 0, blockOff);
            W32(header, 8, indices.Length);                    // point count
            var raw = new byte[indices.Length * 2];
            for (int i = 0; i < indices.Length; i++) { raw[i * 2] = (byte)indices[i]; raw[i * 2 + 1] = (byte)(indices[i] >> 8); }
            return Concat(header, Imgc.StoreLevel5(raw));
        }

        // ANMC name -> pbi index (mirrors Bustup.ParseRes: tableCluster2[1], 60-byte entries, SJIS strings).
        private static Dictionary<string, int> ParseResNames(byte[] resRaw)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            byte[] r = (resRaw.Length >= 4 && resRaw[0] == 'A' && resRaw[1] == 'N' && resRaw[2] == 'M' && resRaw[3] == 'C')
                ? resRaw : SafeDecompress(resRaw);
            int b = Find(r, Encoding.ASCII.GetBytes("ANMC"));
            if (b < 0) return map;
            int stringTable = b + (U16(r, b + 8) << 2);
            int imageTablesCount = U16(r, b + 0x0E);
            int cluster2Base = b + 0x14 + imageTablesCount * 8;
            int tc1 = cluster2Base + 1 * 8;
            int entriesOff = b + (U16(r, tc1) << 2);
            int entryCount = U16(r, tc1 + 2);
            var sjis = Encoding.GetEncoding(932);
            for (int i = 0; i < entryCount; i++)
            {
                int e = entriesOff + i * 60;
                if (e + 6 > r.Length) break;
                int strOff = (short)U16(r, e + 4);
                string name = ReadCString(r, stringTable + strOff, sjis);
                if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name)) map[name] = i;
            }
            return map;
        }

        private static string ReadCString(byte[] d, int o, Encoding enc)
        {
            if (o < 0 || o >= d.Length) return null;
            int e = o; while (e < d.Length && d[e] != 0) e++;
            return enc.GetString(d, o, e - o);
        }

        private static byte[] SafeDecompress(byte[] d) { try { return Imgc.DecompressLevel5(d); } catch { return d; } }
        private static int Find(byte[] h, byte[] n)
        {
            for (int i = 0; i + n.Length <= h.Length; i++) { int j = 0; while (j < n.Length && h[i + j] == n[j]) j++; if (j == n.Length) return i; }
            return -1;
        }
        private static int U16(byte[] d, int o) => d[o] | (d[o + 1] << 8);
        private static int I32(byte[] d, int o) => d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24;
        private static void W32(byte[] d, int o, int v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24); }
        private static byte[] Slice(byte[] d, int o, int len) { var r = new byte[len]; Array.Copy(d, o, r, 0, len); return r; }
        private static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; Buffer.BlockCopy(a, 0, r, 0, a.Length); Buffer.BlockCopy(b, 0, r, a.Length, b.Length); return r; }
    }
}
