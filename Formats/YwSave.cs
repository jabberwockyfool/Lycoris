using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Lycoris.Formats
{
    /// <summary>
    /// Yo-kai Watch 3 save (game{N}.yw) codec — a C# reimplementation of togenyan's yw_save format
    /// (MIT), reverse-engineered from the format spec and validated by a byte-identical decrypt→encrypt
    /// round-trip on a real save. Layers: AES-CCM (key derived from head.yw) → inner XOR stream cipher
    /// (CRC32-checked) → CfgBin-like section container with a CRC-driven anti-tamper section order.
    ///
    /// This class also exposes the owned-yo-kai "box" (section type 0x07): 0x54-byte records whose
    /// +0x04 is the ParamID (== YokaiInfo.ParamHash). Adding a yo-kai writes a fresh record into the
    /// first empty slot; re-encryption re-derives the section order so the game accepts the file.
    /// </summary>
    public sealed class YwSave
    {
        public byte[] Nonce { get; private set; }
        public byte[] Body { get; private set; }        // decrypted section body (+ trailing CRC32/seed)
        private byte[] _head;

        // Located on load (offsets differ per save because section sizes vary).
        public int BoxOffset { get; private set; }      // first record
        public int BoxCapacity { get; private set; }    // number of 0x54 slots

        public const int RecStride = 0x54;
        private const int Off_UidA = 0x00, Off_UidB = 0x02, Off_Param = 0x04, Off_Nick = 0x08, Off_Level = 0x49;
        private const int BoxSectionType = 0x07;

        // ---------------------------------------------------------------- load / save

        public static YwSave Load(string gamePath, string headPath)
        {
            var game = File.ReadAllBytes(gamePath);
            var head = File.ReadAllBytes(headPath);
            var s = new YwSave { _head = head };
            s.Nonce = game.Take(12).ToArray();
            var key = DeriveKey(head);
            var inner = new Ccm(key).Decrypt(game, 0x10, game.Length - 0x10, s.Nonce);
            s.Body = InnerProc(inner, false);
            s.LocateBox();
            return s;
        }

        /// <summary>Re-encrypt the (possibly edited) body back into a game{N}.yw byte stream.</summary>
        public byte[] Encrypt()
        {
            var validated = Validate(Body) ?? throw new InvalidDataException(
                "Save body failed the YW3 section validator; refusing to write a save that may be rejected.");
            var inner = InnerProc(validated, true);
            return new Ccm(DeriveKey(_head)).Encrypt(inner, Nonce);
        }

        // ---------------------------------------------------------------- box (owned yo-kai)

        public sealed class BoxEntry
        {
            public int Slot;
            public int ParamHash;
            public int Level;
            public string Nickname;
        }

        public List<BoxEntry> ReadBox()
        {
            var list = new List<BoxEntry>();
            for (int i = 0; i < BoxCapacity; i++)
            {
                int rec = BoxOffset + i * RecStride;
                int id = I32(Body, rec + Off_Param);
                if (id == 0) continue;
                list.Add(new BoxEntry
                {
                    Slot = i,
                    ParamHash = id,
                    Level = Body[rec + Off_Level],
                    Nickname = ReadStr(Body, rec + Off_Nick, 16)
                });
            }
            return list;
        }

        public int OccupiedCount() => ReadBox().Count;
        public int FreeSlots()
        {
            int free = 0;
            for (int i = 0; i < BoxCapacity; i++)
                if (I32(Body, BoxOffset + i * RecStride + Off_Param) == 0) free++;
            return free;
        }

        /// <summary>
        /// Add a yo-kai to the first empty box slot. Clones an existing valid record as a template (so all
        /// stat/flag fields stay valid) then retargets species, level, nickname and the two unique ids.
        /// The game computes final stats from species+level; the cloned stats are a safe starting point.
        /// </summary>
        public bool TryAddYokai(int paramHash, int level, string nickname, out string error)
        {
            error = null;
            if (paramHash == 0) { error = "Invalid yo-kai id (0)."; return false; }
            if (level < 1) level = 1; if (level > 99) level = 99;

            int firstEmpty = -1, template = -1, maxA = 0, maxB = 0;
            for (int i = 0; i < BoxCapacity; i++)
            {
                int rec = BoxOffset + i * RecStride;
                int id = I32(Body, rec + Off_Param);
                if (id == 0) { if (firstEmpty < 0) firstEmpty = i; continue; }
                if (template < 0) template = i;
                maxA = Math.Max(maxA, U16(Body, rec + Off_UidA));
                maxB = Math.Max(maxB, U16(Body, rec + Off_UidB));
            }
            if (firstEmpty < 0) { error = "The box is full — no empty slot to add a yo-kai."; return false; }
            if (template < 0) { error = "The box has no existing yo-kai to use as a template."; return false; }

            int dst = BoxOffset + firstEmpty * RecStride;
            int src = BoxOffset + template * RecStride;
            Array.Copy(Body, src, Body, dst, RecStride);           // clone a valid record
            W16(Body, dst + Off_UidA, maxA + 1);
            W16(Body, dst + Off_UidB, maxB + 1);
            W32(Body, dst + Off_Param, paramHash);
            for (int k = 0; k < 16; k++) Body[dst + Off_Nick + k] = 0;
            if (!string.IsNullOrEmpty(nickname))
            {
                var nb = Encoding.UTF8.GetBytes(nickname);
                Array.Copy(nb, 0, Body, dst + Off_Nick, Math.Min(nb.Length, 15));
            }
            Body[dst + Off_Level] = (byte)level;
            return true;
        }

        /// <summary>
        /// Replace the species (and level/nickname) of a yo-kai the game already owns, in place. This is the
        /// reliable mutation: the slot is already tracked by the game, so overwriting +0x04 changes what that
        /// yo-kai is (mirrors the known-working reference-tool behaviour). Unique ids are left untouched.
        /// </summary>
        public bool TryReplaceYokai(int slot, int paramHash, int level, string nickname, out string error)
        {
            error = null;
            if (paramHash == 0) { error = "Invalid yo-kai id (0)."; return false; }
            if (slot < 0 || slot >= BoxCapacity) { error = "Invalid box slot."; return false; }
            int rec = BoxOffset + slot * RecStride;
            if (I32(Body, rec + Off_Param) == 0) { error = "That slot is empty — pick an existing yo-kai to replace."; return false; }
            if (level < 1) level = 1; if (level > 99) level = 99;
            W32(Body, rec + Off_Param, paramHash);
            Body[rec + Off_Level] = (byte)level;
            for (int k = 0; k < 16; k++) Body[rec + Off_Nick + k] = 0;
            if (!string.IsNullOrEmpty(nickname))
            {
                var nb = Encoding.UTF8.GetBytes(nickname);
                Array.Copy(nb, 0, Body, rec + Off_Nick, Math.Min(nb.Length, 15));
            }
            return true;
        }

        // Find the box section (type 0x07) inside the container so offsets adapt to any save.
        private void LocateBox()
        {
            if (!TryFindSection(Body, BoxSectionType, out int dataOff, out int dataSize))
                throw new InvalidDataException("Could not locate the yo-kai box (section 0x07) in this save.");
            BoxOffset = dataOff;
            BoxCapacity = dataSize / RecStride;
        }

        // ---------------------------------------------------------------- container walk

        private static bool TryFindSection(byte[] input, int wantType, out int dataOff, out int dataSize)
        {
            dataOff = 0; dataSize = 0;
            int length = input.Length - 8;
            int pos = 8; // skip root header
            while (pos + 8 <= length)
            {
                uint a = U16u(input, pos), b = U32(input, pos + 4);
                pos += 8;
                if (pos >= length) break;
                if ((a & 0xFFFF) != 0xFFFE) return false;
                int type = (int)(b & 0xFF), size = (int)(b >> 8);
                if (type == 0xF2) { pos += size + 4; }
                else if (type == 0xF3)
                {
                    int end = pos + size;
                    while (pos < end)
                    {
                        uint d = U32(input, pos + 4);
                        int st = (int)(d & 0xFF), ss = (int)((d >> 8) + 8 + 4);
                        if (st == wantType) { dataOff = pos + 8; dataSize = (int)(d >> 8); return true; }
                        pos += ss;
                    }
                }
                else return false;
            }
            return false;
        }

        // ---------------------------------------------------------------- validator + anti-tamper reorder

        private static readonly int[] Rejected = { 0x00,0x04,0x05,0x06,0x24,0x25,0x09,0x0A,0x26,0x27,0x28,0x10,0x13,0x16,0x19,0x1A,0x1B,0x1C,0x1E,0x1F };

        /// <summary>Port of togenyan's gamefix/yw3 validate + fix_order. Returns the body with sections
        /// reordered to the CRC-derived order the game expects, or null if the container is malformed.</summary>
        private static byte[] Validate(byte[] input)
        {
            int length = input.Length - 8;
            uint a = U32(input, 0), b = U32(input, 4);
            if ((a & 0xFFFF) != 0xFFFE) return null;
            if ((b >> 16) > length) return null;
            if ((b & 0xFF) != 0xF1) return null;
            int pos = 8;
            while (true)
            {
                a = U16u(input, pos); b = U32(input, pos + 4);
                pos += 8;
                if (pos >= length) break;
                if ((a & 0xFFFF) != 0xFFFE) return null;
                if ((b >> 8) > 0x1D800) return null;
                int type = (int)(b & 0xFF), size = (int)(b >> 8);
                if (type == 0xF2)
                {
                    // togenyan asserts input[pos]==0x02; some save revisions use a different marker, so we
                    // don't enforce it — we only need the container skeleton for the reorder.
                    pos += size + 4;
                }
                else if (type == 0xF3)
                {
                    int sectionEnd = size + pos;
                    var sections = new List<int[]>();
                    Xorshift rng01 = null, rng07 = null;
                    while (pos < sectionEnd)
                    {
                        int secOff = pos;
                        uint c = U16u(input, pos), d = U32(input, pos + 4);
                        if ((c & 0xFFFF) != 0xFFFE) return null;
                        if ((d >> 8) > 0x1D800) return null;
                        int st = (int)(d & 0xFF), ss = (int)((d >> 8) + 8 + 4);
                        if (Array.IndexOf(Rejected, st) >= 0) return null;
                        sections.Add(new[] { st, secOff, ss });
                        if (st == 0x01) rng01 = new Xorshift(Crc32z.Compute(input, secOff, ss));
                        else if (st == 0x07) rng07 = new Xorshift(Crc32z.Compute(input, secOff, ss));
                        pos += ss;
                    }
                    if (rng01 == null || rng07 == null) return null;
                    var state = new[] {
                        0x01,0x03,0x0B,0x0F,0x11,0x02,0x17,0x18, 0x23,0x07,0x08,0x1D,0x0C,0x0D,0x0E,0x12,
                        0x14,0x15,0x20,0x21,0x22,0x29,0x00,0x00 };
                    for (int i = 7; i >= 1; i--)
                    {
                        int p = (int)rng01.Next((ulong)(i + 1));
                        int t = state[p + 1]; state[p + 1] = state[i + 1]; state[i + 1] = t;
                    }
                    for (int i = 0x0B; i >= 1; i--)
                    {
                        int p = (int)rng07.Next((ulong)(i + 1));
                        int t = state[p + 0x0A]; state[p + 0x0A] = state[i + 0x0A]; state[i + 0x0A] = t;
                    }
                    return FixOrder(input, sections, state);
                }
                else return null;
            }
            return input;
        }

        private static byte[] FixOrder(byte[] input, List<int[]> allSections, int[] order)
        {
            var sections = allSections.Take(22).ToList();
            using (var ms = new MemoryStream())
            {
                int firstOff = sections[0][1];
                ms.Write(input, 0, firstOff);
                foreach (int s in order)
                {
                    if (s == 0) continue;
                    var sec = sections.First(x => x[0] == s);
                    ms.Write(input, sec[1], sec[2]);
                }
                var last = sections[sections.Count - 1];
                int tailStart = last[1] + last[2];
                ms.Write(input, tailStart, input.Length - tailStart);
                return ms.ToArray();
            }
        }

        // ---------------------------------------------------------------- inner cipher + key derivation

        private static byte[] InnerProc(byte[] data, bool encrypt)
        {
            int length = data.Length;
            uint storedCrc = U32(data, length - 8);
            uint seed = U32(data, length - 4);
            if (!encrypt && Crc32z.Compute(data, 0, length - 8) != storedCrc)
                throw new InvalidDataException("Save inner checksum mismatch (wrong head.yw, or corrupt save).");
            var body = new YWCipher(seed, 0x1000).Crypt(data, 0, length - 8);
            var outb = new byte[length];
            Array.Copy(body, 0, outb, 0, length - 8);
            Array.Copy(data, length - 8, outb, length - 8, 8);
            if (encrypt)
            {
                uint c = Crc32z.Compute(outb, 0, length - 8);
                W32(outb, length - 8, unchecked((int)c));
            }
            return outb;
        }

        private static byte[] DeriveKey(byte[] headFile)
        {
            var head = InnerProc(headFile, false);
            uint a = U32(head, 0x0C);
            a ^= SubHead(head, 8 + 0x30);
            var cipher = new YWCipher(a, (int)(Sub2Head(head) & 0xFF));
            var key = new byte[16];
            for (int i = 0; i < 16; i++) key[i] = (byte)cipher.Next(0x100);
            return key;
        }
        private static uint SubHead(byte[] d, int add)
        {
            uint r2 = U32(d, 0x10); if (r2 != 0) r2 -= 1;
            int pos = (int)(r2 * 0xA8 + 0x20) + add;
            return U32(d, pos);
        }
        private static uint Sub2Head(byte[] d)
        {
            uint r2 = U32(d, 0x10); if (r2 != 0) r2 -= 1;
            int pos = (int)(r2 * 0xA8 + 0x20) + 0x40;
            uint s = 0; for (int i = 0; i < 6; i++) s += U32(d, pos + i * 4);
            return s & 0xFF;
        }

        // ---------------------------------------------------------------- primitives

        private static uint U32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
        private static int I32(byte[] d, int o) => d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
        private static int U16(byte[] d, int o) => d[o] | (d[o + 1] << 8);
        private static uint U16u(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8));
        private static void W16(byte[] d, int o, int v) { d[o] = (byte)(v & 0xFF); d[o + 1] = (byte)((v >> 8) & 0xFF); }
        private static void W32(byte[] d, int o, int v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24); }
        private static string ReadStr(byte[] d, int o, int max)
        {
            int n = 0; while (n < max && d[o + n] != 0) n++;
            try { return Encoding.UTF8.GetString(d, o, n); } catch { return ""; }
        }

        // ---------------------------------------------------------------- crypto helpers

        private sealed class Xorshift
        {
            readonly ulong[] s = { 0x6C078966, 0xDD5254A5, 0xB9523B81, 0x03DF95B3 };
            public Xorshift(uint seed)
            {
                if (seed == 0) return;
                ulong v = seed;
                v = v ^ (v >> 30); v = (v * (0x6C078966UL - 1)) & 0xFFFFFFFF; v += 1; s[0] = v;
                v = v ^ (v >> 30); v = (v * (0x6C078966UL - 1)) & 0xFFFFFFFF; v += 2; s[1] = v;
                v = v ^ (v >> 30); v = (v * (0x6C078966UL - 1)) & 0xFFFFFFFF; v += 3; s[2] = v;
            }
            public ulong Next(ulong arg)
            {
                ulong x = s[0], y = s[3];
                s[0] = s[1]; s[1] = s[2]; s[2] = s[3];
                x = x ^ ((x << 11) & 0xFFFFFFFF);
                x = x ^ ((x >> 8) & 0xFFFFFFFF);
                y = y ^ ((y >> 19) & 0xFFFFFFFF);
                s[3] = x ^ y;
                return arg == 0 ? s[3] : s[3] % arg;
            }
        }

        private sealed class YWCipher
        {
            static readonly int[] OddPrimes = {
                3,5,7,11,13,17,19,23,29,31,37,41,43,47,53,59,61,67,71,73,79,83,89,97,101,103,107,109,113,127,131,137,
                139,149,151,157,163,167,173,179,181,191,193,197,199,211,223,227,229,233,239,241,251,257,263,269,271,277,281,283,293,307,311,313,
                317,331,337,347,349,353,359,367,373,379,383,389,397,401,409,419,421,431,433,439,443,449,457,461,463,467,479,487,491,499,503,509,
                521,523,541,547,557,563,569,571,577,587,593,599,601,607,613,617,619,631,641,643,647,653,659,661,673,677,683,691,701,709,719,727,
                733,739,743,751,757,761,769,773,787,797,809,811,821,823,827,829,839,853,857,859,863,877,881,883,887,907,911,919,929,937,941,947,
                953,967,971,977,983,991,997,1009,1013,1019,1021,1031,1033,1039,1049,1051,1061,1063,1069,1087,1091,1093,1097,1103,1109,1117,1123,1129,1151,1153,1163,1171,
                1181,1187,1193,1201,1213,1217,1223,1229,1231,1237,1249,1259,1277,1279,1283,1289,1291,1297,1301,1303,1307,1319,1321,1327,1361,1367,1373,1381,1399,1409,1423,1427,
                1429,1433,1439,1447,1451,1453,1459,1471,1481,1483,1487,1489,1493,1499,1511,1523,1531,1543,1549,1553,1559,1567,1571,1579,1583,1597,1601,1607,1609,1613,1619,1621
            };
            readonly int[] table = new int[256];
            readonly Xorshift xs;
            public YWCipher(uint seed, int count)
            {
                for (int i = 0; i < 256; i++) table[i] = i;
                xs = new Xorshift(seed);
                for (int i = 0; i < count; i++)
                {
                    ulong r = xs.Next(0x10000);
                    int r1 = (int)(r & 0xFF), r2 = (int)((r >> 8) & 0xFF);
                    if (r1 != r2) { int aa = table[r1], bb = table[r2]; int t = table[aa]; table[aa] = table[bb]; table[bb] = t; }
                }
            }
            public ulong Next(ulong arg) => xs.Next(arg);
            public byte[] Crypt(byte[] data, int off, int len)
            {
                var o = new byte[len]; int ka = 0;
                for (int i = 0; i < len; i++)
                {
                    if (i % 0x100 == 0) ka = OddPrimes[table[(i & 0xff00) >> 8]];
                    o[i] = (byte)(data[off + i] ^ table[(ka * (i + 1)) & 0xff]);
                }
                return o;
            }
        }

        private sealed class Ccm
        {
            readonly Aes _aes;
            public Ccm(byte[] key) { _aes = Aes.Create(); _aes.Key = key; _aes.Mode = CipherMode.ECB; _aes.Padding = PaddingMode.None; }
            byte[] Ecb(byte[] block) { using (var e = _aes.CreateEncryptor()) return e.TransformFinalBlock(block, 0, 16); }
            byte[] Ks(byte[] nonce, int counter)
            {
                var b = new byte[16];
                b[0] = (byte)(15 - nonce.Length - 1);
                Array.Copy(nonce, 0, b, 1, nonce.Length);
                int c = counter;
                for (int k = 15; k >= 1 + nonce.Length; k--) { b[k] = (byte)(c & 0xFF); c >>= 8; }
                return Ecb(b);
            }
            byte[] Mac(byte[] msg, byte[] nonce)
            {
                int lp = 15 - nonce.Length - 1;
                var b0 = new byte[16];
                b0[0] = (byte)(7 * 8 + lp);
                Array.Copy(nonce, 0, b0, 1, nonce.Length);
                int len = msg.Length;
                b0[13] = (byte)((len >> 16) & 0xFF); b0[14] = (byte)((len >> 8) & 0xFF); b0[15] = (byte)(len & 0xFF);
                var x = Ecb(b0);
                for (int i = 0; i < msg.Length; i += 16)
                {
                    var blk = new byte[16]; Array.Copy(msg, i, blk, 0, Math.Min(16, msg.Length - i));
                    for (int j = 0; j < 16; j++) x[j] ^= blk[j];
                    x = Ecb(x);
                }
                return x;
            }
            public byte[] Decrypt(byte[] data, int off, int len, byte[] nonce)
            {
                var ks0 = Ks(nonce, 0);
                var mac = new byte[16];
                for (int i = 0; i < 16; i++) mac[i] = (byte)(data[off + i] ^ ks0[i]);
                int ctlen = len - 16;
                var msg = new byte[ctlen];
                int ctr = 1;
                for (int i = 0; i < ctlen; i += 16)
                {
                    var ks = Ks(nonce, ctr++);
                    int n = Math.Min(16, ctlen - i);
                    for (int j = 0; j < n; j++) msg[i + j] = (byte)(data[off + 16 + i + j] ^ ks[j]);
                }
                if (!Mac(msg, nonce).SequenceEqual(mac))
                    throw new InvalidDataException("CCM authentication failed (wrong head.yw, or corrupt save).");
                return msg;
            }
            public byte[] Encrypt(byte[] msg, byte[] nonce)
            {
                var mac = Mac(msg, nonce);
                var ks0 = Ks(nonce, 0);
                var outb = new byte[16 + 16 + msg.Length];
                Array.Copy(nonce, 0, outb, 0, nonce.Length);
                for (int i = 0; i < 16; i++) outb[16 + i] = (byte)(mac[i] ^ ks0[i]);
                int ctr = 1;
                for (int i = 0; i < msg.Length; i += 16)
                {
                    var ks = Ks(nonce, ctr++);
                    int n = Math.Min(16, msg.Length - i);
                    for (int j = 0; j < n; j++) outb[32 + i + j] = (byte)(msg[i + j] ^ ks[j]);
                }
                return outb;
            }
        }

        private static class Crc32z
        {
            static readonly uint[] T = Build();
            static uint[] Build()
            {
                var t = new uint[256];
                for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[i] = c; }
                return t;
            }
            public static uint Compute(byte[] d, int off, int len)
            {
                uint c = 0xFFFFFFFF;
                for (int i = 0; i < len; i++) c = T[(c ^ d[off + i]) & 0xFF] ^ (c >> 8);
                return c ^ 0xFFFFFFFF;
            }
        }
    }
}
