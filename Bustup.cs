using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lycoris.Formats;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Assembles a character's dialogue portrait ("bustup") from cfg/bustup/&lt;model&gt;.xa, faithfully — a port
    /// of onepiecefreak's Level5RessourceEditor (ANMC). The .xa (XPCK) holds an XI atlas, a `.pvb` (XPVB) list of
    /// vertices [float x,y,z,u,v], `.pbi` files (XPVI) of vertex indices — 6 per quad-part — and RES.bin (ANMC)
    /// that names each pbi (e.g. "0103_01_0"). A dialogue `<A01/03>` selects part set "0103". Each part is drawn
    /// as an atlas UV rectangle placed relative to the image centre.
    /// </summary>
    internal static class Bustup
    {
        private sealed class Vertex { public float X, Y, U, V; }
        private sealed class Set
        {
            public ImageRgba Atlas;
            public List<List<Vertex>> Pbis;            // per pbi file: its vertices (multiple of 6)
            public Dictionary<string, int> NameToPbi;  // ANMC name -> pbi index
        }

        private static Dictionary<int, string> _idToModel;
        private static List<string> _dirs;
        private static readonly Dictionary<string, Set> _sets = new Dictionary<string, Set>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, ImageSource> _imgCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Model name (e.g. "c001000") for a washamap talker id (CRC32 of model), or null if unknown.</summary>
        public static string ModelFor(YokaiDatabase db, int talker)
        {
            EnsureMap(db);
            return _idToModel != null && _idToModel.TryGetValue(talker, out string m) ? m : null;
        }

        /// <summary>Expression codes available for a talker's bustup, i.e. the NN in &lt;A01/NN&gt; — parsed from the
        /// ANMC part names (#01NN_01 …). Empty if the model has no bustup. Always includes "01" (neutral).</summary>
        public static List<string> Expressions(YokaiDatabase db, int talker)
        {
            var list = new List<string>();
            string model = ModelFor(db, talker);
            if (model == null) return list;
            var set = GetSet(model);
            if (set?.NameToPbi == null) return list;
            var seen = new HashSet<string>();
            foreach (var name in set.NameToPbi.Keys)
            {
                var m = Regex.Match(name, @"#(\d{2})(\d{2})_01");   // #AANN_01 <channel>
                if (m.Success && seen.Add(m.Groups[2].Value)) list.Add(m.Groups[2].Value);
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        /// <summary>Portrait for a washamap talker id (CRC32 of model), with the expression from the line's
        /// &lt;A..&gt; code (default neutral). Null if no bustup exists.</summary>
        public static ImageSource Get(YokaiDatabase db, int talker, string text = null)
        {
            EnsureMap(db);
            if (talker == 0 || _idToModel == null || !_idToModel.TryGetValue(talker, out string model)) return null;
            string part = ExpressionName(text);                     // e.g. "0103" or null (default)
            string key = model + "|" + (part ?? "");
            if (_imgCache.TryGetValue(key, out var cached)) return cached;
            var img = Render(GetSet(model), part);
            _imgCache[key] = img;
            return img;
        }

        // "<A01/03>" -> "0103" (base set + 2-digit expression). null if no code.
        private static string ExpressionName(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = Regex.Match(text, @"<A(\d{2})/(\d{1,2})>");
            return m.Success ? m.Groups[1].Value + int.Parse(m.Groups[2].Value).ToString("00") : null;
        }

        private static void EnsureMap(YokaiDatabase db)
        {
            if (_idToModel != null) return;
            _idToModel = new Dictionary<int, string>();
            _dirs = FindDirs(db);
            foreach (var dir in _dirs)
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*.xa"))
                    {
                        string name = Path.GetFileNameWithoutExtension(f);
                        int crc = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(name)));
                        if (!_idToModel.ContainsKey(crc)) _idToModel[crc] = name;   // reference wins; mod adds customs
                    }
                }
                catch { }
        }

        // Bustup dirs to search, REFERENCE (vanilla cfg) first then MOD (customs not in the reference).
        private static List<string> FindDirs(YokaiDatabase db)
        {
            var dirs = new List<string>();
            foreach (var root in new[] { db?.ReferenceFolder, db?.ModFolder })
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var cand in new[] {
                    Path.Combine(root, "data", "menu", "bustup"),          // real romfs path
                    Path.Combine(root, "include", "data", "menu", "bustup"),
                    Path.Combine(root, "bustup"), Path.Combine(root, "data", "bustup") })
                    if (Directory.Exists(cand) && !dirs.Contains(cand)) { dirs.Add(cand); break; }
            }
            return dirs;
        }

        private static Set GetSet(string model)
        {
            if (_sets.TryGetValue(model, out var s)) return s;
            s = Parse(model);
            _sets[model] = s;
            return s;
        }

        private static Set Parse(string model)
        {
            try
            {
                // Reference (vanilla) first, then mod (custom); first match wins.
                string path = _dirs.Select(d => Path.Combine(d, model + ".xa")).FirstOrDefault(File.Exists);
                if (path == null) return null;
                var files = Xpck.Read(File.ReadAllBytes(path));

                var xiFile = files.FirstOrDefault(f => f.Name.EndsWith(".xi", StringComparison.OrdinalIgnoreCase));
                var pvbFile = files.FirstOrDefault(f => f.Name.EndsWith(".pvb", StringComparison.OrdinalIgnoreCase));
                var resFile = files.FirstOrDefault(f => f.Name.IndexOf("RES.bin", StringComparison.OrdinalIgnoreCase) >= 0);
                var pbiFiles = files.Where(f => f.Name.EndsWith(".pbi", StringComparison.OrdinalIgnoreCase))
                                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (xiFile == null || pvbFile == null || resFile == null || pbiFiles.Count == 0) return null;

                var atlas = Imgc.Decode(xiFile.Data);
                var verts = ParsePvb(pvbFile.Data);
                var pbis = pbiFiles.Select(f => ParsePbi(f.Data, verts)).ToList();
                var names = ParseRes(resFile.Data);

                return new Set { Atlas = atlas, Pbis = pbis, NameToPbi = names };
            }
            catch { return null; }
        }

        // XPVB: verticesOffset(u16@8), verticesCount(i32@0xC); a Level-5 block at verticesOffset = count*[5 floats].
        private static List<Vertex> ParsePvb(byte[] pvb)
        {
            int vOff = U16(pvb, 8), count = I32(pvb, 0x0C);
            byte[] data = Imgc.DecompressLevel5(Slice(pvb, vOff, pvb.Length - vOff));
            var list = new List<Vertex>(count);
            for (int i = 0; i < count && (i + 1) * 20 <= data.Length; i++)
                list.Add(new Vertex { X = F32(data, i * 20), Y = F32(data, i * 20 + 4), U = F32(data, i * 20 + 12), V = F32(data, i * 20 + 16) });
            return list;
        }

        // XPVI: pointCount(i32@8); a Level-5 block at 0xC = count u16 indices into the pvb vertices.
        private static List<Vertex> ParsePbi(byte[] pbi, List<Vertex> verts)
        {
            int count = I32(pbi, 8);
            byte[] data = Imgc.DecompressLevel5(Slice(pbi, 0x0C, pbi.Length - 0x0C));
            var list = new List<Vertex>(count);
            for (int i = 0; i < count && i * 2 + 2 <= data.Length; i++)
            {
                int idx = U16(data, i * 2);
                list.Add(idx >= 0 && idx < verts.Count ? verts[idx] : new Vertex());
            }
            return list;
        }

        // RES.bin (ANMC): tableCluster2[1] entries (60B) name pbi i in order; string at stringTable + ptr.offset.
        private static Dictionary<string, int> ParseRes(byte[] resRaw)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // RES.bin is Level-5 (LZ10) compressed at the file level (unlike the raw .xi/.pvb/.pbi).
            byte[] res = (resRaw.Length >= 4 && resRaw[0] == 'A' && resRaw[1] == 'N' && resRaw[2] == 'M' && resRaw[3] == 'C')
                ? resRaw : SafeDecompress(resRaw);
            int b = Find(res, Encoding.ASCII.GetBytes("ANMC"));
            if (b < 0) return map;
            int stringTable = b + (U16(res, b + 8) << 2);
            int imageTablesCount = U16(res, b + 0x0E);
            int cluster2Base = b + 0x14 + imageTablesCount * 8;
            // ResTableEntry = offset(u16), entryCount(u16), unk(u16), entrySize(u16)
            int tc1 = cluster2Base + 1 * 8;
            int entriesOff = b + (U16(res, tc1) << 2);
            int entryCount = U16(res, tc1 + 2);
            var sjis = Encoding.GetEncoding(932);
            for (int i = 0; i < entryCount; i++)
            {
                int e = entriesOff + i * 60;                       // TableCluster2Table2
                if (e + 6 > res.Length) break;
                int strOff = (short)U16(res, e + 4);               // stringPointer.offset
                string name = ReadCString(res, stringTable + strOff, sjis);
                if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name)) map[name] = i;
            }
            return map;
        }

        // Render a bustup: draw the parts of the "<set>_01_1" body then "<set>_01_0" face (or a neutral default).
        private static ImageSource Render(Set s, string part)
        {
            if (s == null) return null;
            var pbiIdx = new List<int>();
            void Add(string name) { if (name != null && s.NameToPbi.TryGetValue(name, out int idx) && !pbiIdx.Contains(idx)) pbiIdx.Add(idx); }

            // RES names look like "#0103_01 0" (face/expression) and "#0103_01 1" (body). Draw body then face.
            if (part != null) { Add("#" + part + "_01 1"); Add("#" + part + "_01 0"); }
            if (pbiIdx.Count == 0)   // default / unknown expression → neutral, else first named pbi
            {
                Add("#0101_01 1"); Add("#0101_01 0");
                if (pbiIdx.Count == 0 && s.NameToPbi.Count > 0) pbiIdx.Add(s.NameToPbi.Values.Min());
            }

            // collect parts (each = 6 verts), compute bounds (positions are relative to image centre).
            var parts = new List<Vertex[]>();
            foreach (int idx in pbiIdx.Distinct())
            {
                var vs = s.Pbis[idx];
                for (int m = 0; m + 6 <= vs.Count; m += 6) parts.Add(vs.Skip(m).Take(6).ToArray());
            }
            if (parts.Count == 0) return null;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in parts)
            {
                float x0 = p[0].X, y0 = p[0].Y, x1 = p[3].X, y1 = p[3].Y;
                minX = Math.Min(minX, Math.Min(x0, x1)); minY = Math.Min(minY, Math.Min(y0, y1));
                maxX = Math.Max(maxX, Math.Max(x0, x1)); maxY = Math.Max(maxY, Math.Max(y0, y1));
            }
            int ox = (int)Math.Floor(minX), oy = (int)Math.Floor(minY);
            int w = (int)Math.Ceiling(maxX) - ox, h = (int)Math.Ceiling(maxY) - oy;
            if (w <= 0 || h <= 0 || w > 2048 || h > 2048) return null;

            var dst = new byte[w * h * 4];
            foreach (var p in parts) Blit(dst, w, h, s.Atlas, p, ox, oy);
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, dst, w * 4);
            bmp.Freeze();
            return bmp;
        }

        // Draw one part-rect: atlas UV rect (m0.uv → m3.uv) into the dest rect (m0.xy → m3.xy), offset by (ox,oy).
        private static void Blit(byte[] dst, int dw, int dh, ImageRgba atlas, Vertex[] p, int ox, int oy)
        {
            float dx0 = p[0].X - ox, dy0 = p[0].Y - oy, dx1 = p[3].X - ox, dy1 = p[3].Y - oy;
            float u0 = p[0].U, v0 = p[0].V, u1 = p[3].U, v1 = p[3].V;
            int rx0 = (int)Math.Round(Math.Min(dx0, dx1)), ry0 = (int)Math.Round(Math.Min(dy0, dy1));
            int rx1 = (int)Math.Round(Math.Max(dx0, dx1)), ry1 = (int)Math.Round(Math.Max(dy0, dy1));
            int rw = Math.Max(1, rx1 - rx0), rh = Math.Max(1, ry1 - ry0);
            float uw = u1 - u0, vh = v1 - v0;

            for (int py = 0; py < rh; py++)
                for (int px = 0; px < rw; px++)
                {
                    int dx = rx0 + px, dy = ry0 + py;
                    if (dx < 0 || dy < 0 || dx >= dw || dy >= dh) continue;
                    int sx = (int)(u0 + (px + 0.5f) / rw * uw);
                    int sy = (int)(v0 + (py + 0.5f) / rh * vh);
                    if (sx < 0 || sy < 0 || sx >= atlas.Width || sy >= atlas.Height) continue;
                    int so = (sy * atlas.Width + sx) * 4;
                    byte a = atlas.Bgra[so + 3];
                    if (a == 0) continue;
                    int o = (dy * dw + dx) * 4;
                    float af = a / 255f, ia = 1 - af;
                    dst[o] = (byte)(atlas.Bgra[so] * af + dst[o] * ia);
                    dst[o + 1] = (byte)(atlas.Bgra[so + 1] * af + dst[o + 1] * ia);
                    dst[o + 2] = (byte)(atlas.Bgra[so + 2] * af + dst[o + 2] * ia);
                    dst[o + 3] = (byte)(a + dst[o + 3] * ia);
                }
        }

        // ---- primitives ----
        private static int U16(byte[] d, int o) => d[o] | (d[o + 1] << 8);
        private static int I32(byte[] d, int o) => d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24;
        private static float F32(byte[] d, int o) => BitConverter.ToSingle(d, o);
        private static byte[] Slice(byte[] d, int o, int len) { var r = new byte[len]; Array.Copy(d, o, r, 0, len); return r; }
        private static byte[] SafeDecompress(byte[] d) { try { return Imgc.DecompressLevel5(d); } catch { return d; } }
        private static string ReadCString(byte[] d, int o, Encoding enc)
        {
            if (o < 0 || o >= d.Length) return null;
            int e = o; while (e < d.Length && d[e] != 0) e++;
            return enc.GetString(d, o, e - o);
        }
        private static int Find(byte[] d, byte[] pat)
        {
            for (int i = 0; i + pat.Length <= d.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < pat.Length; j++) if (d[i + j] != pat[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }
    }
}
