using System;
using System.Collections.Generic;

namespace Lycoris.Formats
{
    /// <summary>
    /// Level-5 <b>XF</b> bitmap font (e.g. Yo-kai Watch's <c>ft_nrm.xf</c>) — a direct port of Kuriimu's
    /// game_yokai_watch XF reader. The file is an XPCK holding an XI texture (decoded via <see cref="Imgc"/>)
    /// and a <c>fnt.bin</c> with three Level-5-compressed tables: glyph sizes, and the large/small glyph maps.
    /// <see cref="DrawChar"/> blits one glyph (a single texture colour-channel used as coverage, tinted to the
    /// requested colour) into a BGRA buffer, exactly like the game's renderer.
    /// </summary>
    public sealed class XfFont
    {
        private struct SizeInfo { public sbyte OffX, OffY; public byte W, H; }
        private struct Glyph { public int SizeIndex, Width, Channel, X, Y; }

        private readonly ImageRgba _tex;
        private readonly SizeInfo[] _sizes;
        private readonly Dictionary<char, Glyph> _large = new Dictionary<char, Glyph>();
        private readonly Dictionary<char, Glyph> _small = new Dictionary<char, Glyph>();

        public XfFont(byte[] xf)
        {
            // --- XPCK header (offsets/sizes stored >>2) ---
            if (xf.Length < 0x14 || xf[0] != 'X' || xf[1] != 'P' || xf[2] != 'C' || xf[3] != 'K')
                throw new System.IO.InvalidDataException("Not an XF (XPCK) font.");
            int fileCount = xf[4];
            int dataOffset = U16(xf, 10) * 4;

            // --- file entries (0x0C each) right after the 0x14 header ---
            var offs = new int[fileCount];
            var sizes = new int[fileCount];
            for (int i = 0; i < fileCount; i++)
            {
                int e = 0x14 + i * 0x0C;
                int foLow = U16(xf, e + 6);
                int fsLow = U16(xf, e + 8);
                int foHigh = xf[e + 10];
                int fsHigh = xf[e + 11];
                offs[i] = ((foHigh << 16) | foLow) << 2;   // full offset/size (high byte included) — matches Xpck.Read
                sizes[i] = (fsHigh << 16) | fsLow;
            }

            // --- file 0 = XI texture, file 1 = fnt.bin (right after the texture + 4 padding) ---
            byte[] xi = Slice(xf, dataOffset + offs[0], sizes[0]);
            _tex = Imgc.Decode(xi);

            int fntStart = dataOffset + offs[1];      // use the entry table's offset (robust to XPCK re-padding)
            byte[] fnt = Slice(xf, fntStart, sizes[1]);

            // --- fnt.bin: three Level-5 blocks from 0x28, each 4-aligned ---
            int pos = 0x28;
            byte[] buf1 = DecompressBlock(fnt, ref pos); pos = (pos + 3) & ~3;
            byte[] buf2 = DecompressBlock(fnt, ref pos); pos = (pos + 3) & ~3;
            byte[] buf3 = DecompressBlock(fnt, ref pos);

            _sizes = new SizeInfo[buf1.Length / 4];
            for (int i = 0; i < _sizes.Length; i++)
                _sizes[i] = new SizeInfo { OffX = (sbyte)buf1[i * 4], OffY = (sbyte)buf1[i * 4 + 1], W = buf1[i * 4 + 2], H = buf1[i * 4 + 3] };

            ReadGlyphs(buf2, _large);
            ReadGlyphs(buf3, _small);
        }

        private static void ReadGlyphs(byte[] buf, Dictionary<char, Glyph> dict)
        {
            for (int i = 0; i + 8 <= buf.Length; i += 8)
            {
                char cp = (char)U16(buf, i);
                int charSize = U16(buf, i + 2);
                int imageOffset = (int)U32(buf, i + 4);
                dict[cp] = new Glyph
                {
                    SizeIndex = charSize % 1024,
                    Width = charSize / 1024,
                    Channel = imageOffset % 16,
                    X = imageOffset / 16 % 16384,
                    Y = imageOffset / 16 / 16384,
                };
            }
        }

        /// <summary>The character codes present in the large/small glyph maps (for inspecting a font's coverage).</summary>
        public ICollection<char> LargeChars => _large.Keys;
        public ICollection<char> SmallChars => _small.Keys;
        public int TexWidth => _tex.Width;
        public int TexHeight => _tex.Height;

        /// <summary>The advance width of a character (before the game's -0.85 kerning tweak).</summary>
        public int CharWidth(char c, bool small) => Lookup(c, small).Width;

        /// <summary>Blit one glyph into a BGRA buffer, tinted (tR,tG,tB), at (x,y). Returns the advance width.</summary>
        public int DrawChar(byte[] dst, int dstW, int dstH, char c, byte tR, byte tG, byte tB, float x, float y, bool small)
        {
            var g = Lookup(c, small);
            if (g.SizeIndex < 0 || g.SizeIndex >= _sizes.Length) return g.Width;
            var s = _sizes[g.SizeIndex];
            int chanOff = ChannelOffset(g.Channel);      // ARGB channel -> BGRA byte offset
            int px = (int)Math.Round(x) + s.OffX, py = (int)Math.Round(y) + s.OffY;

            for (int gy = 0; gy < s.H; gy++)
                for (int gx = 0; gx < s.W; gx++)
                {
                    int tx = g.X + gx, ty = g.Y + gy;
                    if (tx < 0 || ty < 0 || tx >= _tex.Width || ty >= _tex.Height) continue;
                    byte cov = _tex.Bgra[(ty * _tex.Width + tx) * 4 + chanOff];
                    if (cov == 0) continue;
                    int dx = px + gx, dy = py + gy;
                    if (dx < 0 || dy < 0 || dx >= dstW || dy >= dstH) continue;
                    int o = (dy * dstW + dx) * 4;
                    float a = cov / 255f, ia = 1 - a;
                    dst[o] = (byte)(tB * a + dst[o] * ia);
                    dst[o + 1] = (byte)(tG * a + dst[o + 1] * ia);
                    dst[o + 2] = (byte)(tR * a + dst[o + 2] * ia);
                    dst[o + 3] = (byte)(cov + dst[o + 3] * ia);
                }
            return g.Width;
        }

        private Glyph Lookup(char c, bool small)
        {
            var d = small ? _small : _large;
            if (d.TryGetValue(c, out var g)) return g;
            if (d.TryGetValue('?', out g)) return g;
            return default;
        }

        // ARGB channel index (0=R,1=G,2=B,3=A) -> byte offset in a B,G,R,A pixel.
        private static int ChannelOffset(int argbChannel)
        {
            switch (argbChannel & 3) { case 0: return 2; case 1: return 1; case 2: return 0; default: return 3; }
        }

        // ---- sequential Level-5 decompression (tracks the consumed length) ----

        private static byte[] DecompressBlock(byte[] data, ref int pos)
        {
            uint header = U32(data, pos);
            int size = (int)(header >> 3);
            int method = (int)(header & 7);
            switch (method)
            {
                case 0: { var r = Slice(data, pos + 4, size); pos += 4 + size; return r; }
                case 1: return Lz10(data, ref pos, size);
                case 2: return Huffman(data, ref pos, size, 4);
                case 3: return Huffman(data, ref pos, size, 8);
                default: throw new NotSupportedException($"XF font compression method {method} not supported.");
            }
        }

        private static byte[] Lz10(byte[] data, ref int pos, int size)
        {
            int p = pos + 4, mask = 0, flag = 0;
            var output = new List<byte>(size);
            while (output.Count < size && p < data.Length)
            {
                if (mask == 0) { flag = data[p++]; mask = 0x80; }
                if ((flag & mask) == 0) { output.Add(data[p++]); }
                else
                {
                    int dat = (data[p] << 8) | data[p + 1]; p += 2;
                    int back = (dat & 0x0FFF) + 1, length = (dat >> 12) + 3;
                    for (int i = 0; i < length && output.Count < size; i++)
                        output.Add(output.Count - back >= 0 ? output[output.Count - back] : (byte)0);
                }
                mask >>= 1;
            }
            pos = p;
            return output.ToArray();
        }

        private static byte[] Huffman(byte[] data, ref int start, int decompressedSize, int bitDepth)
        {
            var result = new byte[decompressedSize * 8 / bitDepth];
            int p = start + 4;
            byte treeSize = data[p++];
            byte treeRoot = data[p++];
            int treeBase = p;
            p += treeSize * 2;

            int code = 0, next = 0, node = treeRoot;
            for (int i = 0, rp = 0; rp < result.Length; i++)
            {
                if (i % 32 == 0) { code = (int)U32(data, p); p += 4; }
                next += ((node & 0x3F) << 1) + 2;
                int dir = (code >> (31 - (i % 32))) % 2 == 0 ? 2 : 1;
                bool leaf = (node >> 5 >> dir) % 2 != 0;
                node = data[treeBase + next - dir];
                if (leaf) { result[rp++] = (byte)node; node = treeRoot; next = 0; }
            }
            start = p;
            if (bitDepth == 8) return result;
            var combined = new byte[decompressedSize];
            for (int j = 0; j < decompressedSize; j++) combined[j] = (byte)(result[2 * j] | (result[2 * j + 1] << 4));
            return combined;
        }

        // ---- primitive readers ----
        private static int U16(byte[] d, int o) => d[o] | (d[o + 1] << 8);
        private static uint U32(byte[] d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
        private static byte[] Slice(byte[] d, int o, int len) { var r = new byte[len]; Array.Copy(d, o, r, 0, len); return r; }
    }
}
