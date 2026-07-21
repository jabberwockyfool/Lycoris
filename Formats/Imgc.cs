using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lycoris.Formats
{
    /// <summary>Decoded image as a top-down BGRA32 pixel buffer (WPF-friendly).</summary>
    public sealed class ImageRgba
    {
        public int Width;
        public int Height;
        public byte[] Bgra; // Width*Height*4, order B,G,R,A
    }

    /// <summary>
    /// Level-5 IMGC (.xi) image decoder — a standalone port of Albatross' pipeline:
    /// header → two Level-5-compressed sections (tile index table + pixel data) → detile →
    /// ETC1A4/RGBA8/RGBA4 pixel decode → 3DS 8×8 z-order un-swizzle → BGRA bitmap.
    /// No external dependencies.
    /// </summary>
    public static class Imgc
    {
        public static ImageRgba Decode(byte[] file)
        {
            if (file == null || file.Length < 0x48 || file[0] != 'I' || file[1] != 'M' || file[2] != 'G' || file[3] != 'C')
                throw new InvalidDataException("Not an IMGC (.xi) file.");

            byte imageFormat = file[0x0A];
            byte bitDepth = file[0x0D];
            int width = BitConverter.ToInt16(file, 0x10);
            int height = BitConverter.ToInt16(file, 0x12);
            int tileOffset = BitConverter.ToInt32(file, 0x1C);
            int tileSize1 = BitConverter.ToInt32(file, 0x34);
            int tileSize2 = BitConverter.ToInt32(file, 0x38);
            int imageSize = BitConverter.ToInt32(file, 0x3C);

            byte[] tileData = Decompress(Section(file, tileOffset, tileSize1));
            byte[] pixelData = Decompress(Section(file, tileOffset + tileSize2, imageSize));

            int blockSize = 64 * bitDepth / 8;
            byte[] linear = Detile(tileData, pixelData, blockSize);

            var fmt = ColorFormat.Get(imageFormat);
            byte[] pic = fmt.Name == "ETC1A4" ? Etc1A4.Decompress(linear, width, height) : linear;

            return Unswizzle(pic, fmt, width, height);
        }

        // ---------------- detile ----------------

        private static byte[] Detile(byte[] table, byte[] tex, int blockSize)
        {
            var ms = new MemoryStream();
            int pos = 0;
            int entryLength = 2;
            if (table.Length >= 2 && BitConverter.ToUInt16(table, 0) == 0x453)
            {
                pos = 8; // skip entryStart header
                entryLength = 4;
            }
            for (int i = pos; i + entryLength <= table.Length; i += entryLength)
            {
                uint entry = entryLength == 2 ? BitConverter.ToUInt16(table, i) : BitConverter.ToUInt32(table, i);
                if (entry == 0xFFFF || entry == 0xFFFFFFFF)
                {
                    for (int j = 0; j < blockSize; j++) ms.WriteByte(0);
                }
                else
                {
                    long src = (long)entry * blockSize;
                    if (src + blockSize <= tex.Length) ms.Write(tex, (int)src, blockSize);
                    else for (int j = 0; j < blockSize; j++) ms.WriteByte(0);
                }
            }
            return ms.ToArray();
        }

        // ---------------- un-swizzle (3DS 8x8 z-order) ----------------

        private static readonly (int, int)[] BitField =
            { (0, 1), (1, 0), (0, 2), (2, 0), (0, 4), (4, 0) };

        /// <summary>Raster (x,y) that linear pre-swizzle index <paramref name="i"/> maps to.</summary>
        private static void ZDest(int i, int widthInTiles, out int x, out int y)
        {
            int macro = i / 64;
            x = (macro % widthInTiles) * 8;
            y = (macro / widthInTiles) * 8;
            for (int j = 0; j < 6; j++)
                if (((i >> j) & 1) == 1) { x ^= BitField[j].Item1; y ^= BitField[j].Item2; }
        }

        private static ImageRgba Unswizzle(byte[] pic, ColorFormat fmt, int width, int height)
        {
            int padW = (width + 7) & ~7;
            int padH = (height + 7) & ~7;
            int widthInTiles = padW / 8;

            var bgra = new byte[width * height * 4];
            int available = pic.Length / fmt.Size;

            for (int i = 0; i < padW * padH; i++)
            {
                if (i >= available) break;
                fmt.Decode(pic, i * fmt.Size, out byte r, out byte g, out byte b, out byte a);
                ZDest(i, widthInTiles, out int x, out int y);
                if (x < width && y < height)
                {
                    int o = (y * width + x) * 4;
                    bgra[o] = b; bgra[o + 1] = g; bgra[o + 2] = r; bgra[o + 3] = a;
                }
            }
            return new ImageRgba { Width = width, Height = height, Bgra = bgra };
        }

        // ---------------- encoder (BGRA -> RGBA4 .xi, matching YW3 vanilla format) ----------------

        // 0x48-byte header template from a vanilla RGBA4 64x64 face icon; size/offset fields patched.
        private static readonly byte[] HeaderTemplate =
        {
            0x49,0x4D,0x47,0x43, 0x30,0x30,0x00,0x00, 0x30,0x00,0x01,0x01, 0x01,0x10,0x80,0x00,
            0x40,0x00,0x40,0x00, 0x30,0x00,0x00,0x00, 0x30,0x00,0x01,0x00, 0x48,0x00,0x00,0x00,
            0x03,0x00,0x00,0x00, 0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00, 0x78,0x00,0x00,0x00, 0x78,0x00,0x00,0x00, 0x08,0x0C,0x00,0x00,
            0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00
        };

        /// <summary>
        /// Encode a top-down BGRA buffer into a valid IMGC (.xi) in RGBA4 (format 1, bitDepth 16) —
        /// the format YW3 face icons use. Sections are stored uncompressed (method 0); the game reads
        /// the method from each section header. width/height must be multiples of 8.
        /// </summary>
        public static byte[] EncodeXi(byte[] bgra, int width, int height)
        {
            if ((width & 7) != 0 || (height & 7) != 0)
                throw new ArgumentException("Width/height must be multiples of 8.");
            int padW = width, padH = height, widthInTiles = padW / 8;
            int pixels = padW * padH;

            // pic: linear pre-swizzle RGBA4, 2 bytes/pixel.
            var pic = new byte[pixels * 2];
            for (int i = 0; i < pixels; i++)
            {
                ZDest(i, widthInTiles, out int x, out int y);
                int o = (y * width + x) * 4;
                int b = bgra[o], g = bgra[o + 1], r = bgra[o + 2], a = bgra[o + 3];
                ushort v = (ushort)(((r >> 4) << 12) | ((g >> 4) << 8) | ((b >> 4) << 4) | (a >> 4));
                pic[i * 2] = (byte)(v & 0xFF);
                pic[i * 2 + 1] = (byte)(v >> 8);
            }

            // tile table: sequential block indices, one 8x8 tile (128 bytes) per entry.
            int blockSize = 64 * 16 / 8; // 128
            int tileCount = pic.Length / blockSize;
            var tileTable = new byte[tileCount * 2];
            for (int t = 0; t < tileCount; t++)
            {
                tileTable[t * 2] = (byte)(t & 0xFF);
                tileTable[t * 2 + 1] = (byte)(t >> 8);
            }

            byte[] tileSection = NoCompress(tileTable);
            byte[] pixelSection = NoCompress(pic);

            var outBuf = new byte[0x48 + tileSection.Length + pixelSection.Length];
            Array.Copy(HeaderTemplate, outBuf, 0x48);
            WriteI16(outBuf, 0x10, (short)width);
            WriteI16(outBuf, 0x12, (short)height);
            WriteI32(outBuf, 0x1C, 0x48);                       // TileOffset
            WriteI32(outBuf, 0x34, tileSection.Length);          // TileSize1
            WriteI32(outBuf, 0x38, tileSection.Length);          // TileSize2 (delta to pixel data)
            WriteI32(outBuf, 0x3C, pixelSection.Length);         // ImageSize
            Array.Copy(tileSection, 0, outBuf, 0x48, tileSection.Length);
            Array.Copy(pixelSection, 0, outBuf, 0x48 + tileSection.Length, pixelSection.Length);
            return outBuf;
        }

        private static byte[] NoCompress(byte[] data)
        {
            var s = new byte[data.Length + 4];
            uint header = (uint)(data.Length << 3); // method 0 in low 3 bits
            s[0] = (byte)header; s[1] = (byte)(header >> 8); s[2] = (byte)(header >> 16); s[3] = (byte)(header >> 24);
            Array.Copy(data, 0, s, 4, data.Length);
            return s;
        }

        private static void WriteI16(byte[] d, int o, short v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }
        private static void WriteI32(byte[] d, int o, int v)
        { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24); }

        // ---------------- color formats ----------------

        private sealed class ColorFormat
        {
            public string Name;
            public int Size;

            public static ColorFormat Get(byte id)
            {
                switch (id)
                {
                    case 0: return new ColorFormat { Name = "RGBA8", Size = 4 };
                    case 1: return new ColorFormat { Name = "RGBA4", Size = 2 };
                    case 28: return new ColorFormat { Name = "ETC1A4", Size = 4 };
                    default: throw new NotSupportedException($"IMGC image format {id} not supported.");
                }
            }

            public void Decode(byte[] d, int off, out byte r, out byte g, out byte b, out byte a)
            {
                switch (Name)
                {
                    case "RGBA8": // stored [A,B,G,R]
                        a = d[off]; b = d[off + 1]; g = d[off + 2]; r = d[off + 3];
                        break;
                    case "RGBA4": // little-endian ushort, nibbles R,G,B,A high->low, x16
                        ushort v = (ushort)((d[off + 1] << 8) | d[off]);
                        r = (byte)(((v >> 12) & 0xF) * 16);
                        g = (byte)(((v >> 8) & 0xF) * 16);
                        b = (byte)(((v >> 4) & 0xF) * 16);
                        a = (byte)((v & 0xF) * 16);
                        break;
                    default: // ETC1A4 already decoded to [R,G,B,A]
                        r = d[off]; g = d[off + 1]; b = d[off + 2]; a = d[off + 3];
                        break;
                }
            }
        }

        // ---------------- ETC1A4 decode ----------------

        private static class Etc1A4
        {
            private static readonly int[][] Modifiers =
            {
                new[] { 2, 8, -2, -8 }, new[] { 5, 17, -5, -17 }, new[] { 9, 29, -9, -29 },
                new[] { 13, 42, -13, -42 }, new[] { 18, 60, -18, -60 }, new[] { 24, 80, -24, -80 },
                new[] { 33, 106, -33, -106 }, new[] { 47, 183, -47, -183 }
            };
            private static readonly int[] PixelOrder = { 0, 4, 1, 5, 8, 12, 9, 13, 2, 6, 3, 7, 10, 14, 11, 15 };

            public static byte[] Decompress(byte[] data, int width, int height)
            {
                var result = new byte[width * height * 4];
                int offset = 0, write = 0;
                for (int by = 0; by < height; by += 4)
                    for (int bx = 0; bx < width; bx += 4)
                    {
                        if (offset + 16 > data.Length) { write += 16 * 4; continue; }
                        byte[] alphaBlock = new byte[8]; Array.Copy(data, offset, alphaBlock, 0, 8); offset += 8;
                        byte[] colorBlock = new byte[8]; Array.Copy(data, offset, colorBlock, 0, 8); offset += 8;
                        byte[] alphas = DecodeAlphas(alphaBlock);
                        byte[] colors = DecodeColors(colorBlock);
                        for (int i = 0; i < 16; i++)
                        {
                            result[write] = colors[i * 3];
                            result[write + 1] = colors[i * 3 + 1];
                            result[write + 2] = colors[i * 3 + 2];
                            result[write + 3] = alphas[i];
                            write += 4;
                        }
                    }
                return result;
            }

            private static byte[] DecodeColors(byte[] data)
            {
                var result = new byte[48];
                ushort lsb = (ushort)(data[0] | (data[1] << 8));
                ushort msb = (ushort)(data[2] | (data[3] << 8));
                byte flags = data[4];
                int B = data[5], G = data[6], R = data[7];

                bool flip = (flags & 1) == 1;
                bool diff = (flags & 2) == 2;
                int depth = diff ? 32 : 16;
                int table0 = (flags >> 5) & 7;
                int table1 = (flags >> 2) & 7;

                var color0 = new Rgb(R * depth / 256, G * depth / 256, B * depth / 256);
                Rgb color1;
                if (!diff) color1 = new Rgb(R % 16, G % 16, B % 16);
                else color1 = new Rgb(color0.R + Sign3(R % 8), color0.G + Sign3(G % 8), color0.B + Sign3(B % 8));

                color0 = color0.Scale(depth);
                color1 = color1.Scale(depth);

                int flipmask = flip ? 2 : 8;
                int t = 0;
                foreach (int i in PixelOrder)
                {
                    var basec = (i & flipmask) == 0 ? color0 : color1;
                    int[] mod = Modifiers[(i & flipmask) == 0 ? table0 : table1];
                    var c = basec.Add(mod[(msb >> i) % 2 * 2 + (lsb >> i) % 2]);
                    result[t] = c.R; result[t + 1] = c.G; result[t + 2] = c.B;
                    t += 3;
                }
                return result;
            }

            private static byte[] DecodeAlphas(byte[] blockData)
            {
                ulong a = BitConverter.ToUInt64(blockData, 0);
                var alphas = new byte[16];
                int t = 0;
                foreach (int i in PixelOrder)
                    alphas[t++] = (byte)((a >> (4 * i)) % 16 * 17);
                return alphas;
            }

            private static int Sign3(int n) => (n + 4) % 8 - 4;

            private struct Rgb
            {
                public byte R, G, B;
                public Rgb(int r, int g, int b) { R = (byte)Clamp(r); G = (byte)Clamp(g); B = (byte)Clamp(b); }
                public Rgb Add(int mod) => new Rgb(R + mod, G + mod, B + mod);
                public Rgb Scale(int limit) => limit == 16
                    ? new Rgb(R * 17, G * 17, B * 17)
                    : new Rgb((R << 3) | (R >> 2), (G << 3) | (G >> 2), (B << 3) | (B >> 2));
                private static int Clamp(int n) => Math.Max(0, Math.Min(n, 255));
            }
        }

        // ---------------- Level-5 compression (methods 0 and 1) ----------------

        private static byte[] Section(byte[] file, int offset, int size)
        {
            var s = new byte[size];
            Array.Copy(file, offset, s, 0, size);
            return s;
        }

        private static byte[] Decompress(byte[] data)
        {
            if (data.Length < 4) return data;
            uint header = BitConverter.ToUInt32(data, 0);
            int size = (int)(header >> 3);
            uint method = header & 0x7;
            switch (method)
            {
                case 0: return data.Skip(4).Take(size).ToArray();          // NoCompression
                case 1: return Lz10(data).Take(size).ToArray();            // LZ10
                case 2: return Huffman(data, 4).Take(size).ToArray();      // Huffman 4-bit
                case 3: return Huffman(data, 8).Take(size).ToArray();      // Huffman 8-bit
                default: throw new NotSupportedException($"Compression method {method} not supported for .xi.");
            }
        }

        /// <summary>Level-5 Huffman decode (port of Kuriimu2 HuffmanHeaderlessDecoder).</summary>
        private static byte[] Huffman(byte[] data, int bitDepth)
        {
            int decompressedSize = (data[0] >> 3) | (data[1] << 5) | (data[2] << 13) | (data[3] << 21);
            var result = new byte[decompressedSize * 8 / bitDepth];

            int p = 4;
            byte treeSize = data[p++];
            byte treeRoot = data[p++];
            int treeBase = p;
            p += treeSize * 2;

            int code = 0, next = 0, pos = treeRoot;
            for (int i = 0, resultPos = 0; resultPos < result.Length; i++)
            {
                if (i % 32 == 0) { code = BitConverter.ToInt32(data, p); p += 4; }
                next += ((pos & 0x3F) << 1) + 2;
                int direction = (code >> (31 - (i % 32))) % 2 == 0 ? 2 : 1;
                bool leaf = (pos >> 5 >> direction) % 2 != 0;
                pos = data[treeBase + next - direction];
                if (leaf) { result[resultPos++] = (byte)pos; pos = treeRoot; next = 0; }
            }

            if (bitDepth == 8) return result;
            // 4-bit, LowNibbleFirst
            var combined = new byte[decompressedSize];
            for (int j = 0; j < decompressedSize; j++)
                combined[j] = (byte)(result[2 * j] | (result[2 * j + 1] << 4));
            return combined;
        }

        private static byte[] Lz10(byte[] data)
        {
            int p = 4, op = 0, mask = 0, flag = 0;
            var output = new List<byte>();
            while (p < data.Length)
            {
                if (mask == 0) { flag = data[p++]; mask = 0x80; }
                if ((flag & mask) == 0)
                {
                    if (p >= data.Length) break;
                    output.Add(data[p++]); op++;
                }
                else
                {
                    if (p + 2 > data.Length) break;
                    int dat = (data[p] << 8) | data[p + 1];
                    p += 2;
                    int pos = (dat & 0x0FFF) + 1;
                    int length = (dat >> 12) + 3;
                    for (int i = 0; i < length; i++)
                    {
                        if (op - pos >= 0)
                        {
                            output.Add(op - pos < output.Count ? output[op - pos] : (byte)0);
                            op++;
                        }
                    }
                }
                mask >>= 1;
            }
            return output.ToArray();
        }
    }
}
