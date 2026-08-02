using System;
using System.IO;

namespace Lycoris.Formats
{
    /// <summary>
    /// Decoder for Nintendo 3DS <b>BCSTM</b> streamed audio (magic "CSTM"), DSP-ADPCM codec — enough to preview
    /// Yo-kai Watch character voices (pv_&lt;model&gt;_NN_en.dspadpcm.bcstm). Decodes to 16-bit PCM and wraps it
    /// as a WAV so it can play through System.Media.SoundPlayer. Supports the common case (mono/stereo, block
    /// layout). Non-DSP-ADPCM codecs are rejected.
    /// </summary>
    public static class Bcstm
    {
        /// <summary>Decode a .bcstm to a PCM WAV byte[] (16-bit), or throws on an unsupported file.</summary>
        public static byte[] ToWav(byte[] file)
        {
            Decode(file, out short[][] channels, out int sampleRate, out int sampleCount);
            return BuildWav(channels, sampleRate, sampleCount);
        }

        /// <summary>Encode a 16-bit PCM WAV to a mono DSP-ADPCM .bcstm (stereo input is down-mixed to mono).</summary>
        public static byte[] FromWav(byte[] wav)
        {
            ReadWav(wav, out short[] pcm, out int sampleRate);
            return Encode(pcm, sampleRate);
        }

        private static void ReadWav(byte[] w, out short[] pcm, out int sampleRate)
        {
            if (w.Length < 12 || w[0] != 'R' || w[1] != 'I' || w[2] != 'F' || w[3] != 'F' ||
                w[8] != 'W' || w[9] != 'A' || w[10] != 'V' || w[11] != 'E')
                throw new InvalidDataException("Not a WAV (RIFF/WAVE) file.");
            int channels = 1, bits = 16, rate = 0, dataOff = -1, dataLen = 0;
            int p = 12;
            while (p + 8 <= w.Length)
            {
                string id = new string(new[] { (char)w[p], (char)w[p + 1], (char)w[p + 2], (char)w[p + 3] });
                int len = I32(w, p + 4);
                int body = p + 8;
                if (id == "fmt ")
                {
                    channels = U16(w, body + 2);
                    rate = I32(w, body + 4);
                    bits = U16(w, body + 14);
                }
                else if (id == "data") { dataOff = body; dataLen = Math.Min(len, w.Length - body); break; }
                p = body + len + (len & 1);
            }
            if (dataOff < 0 || rate == 0) throw new InvalidDataException("WAV has no fmt/data chunk.");
            if (bits != 16) throw new NotSupportedException("Only 16-bit PCM WAV is supported (convert first).");
            sampleRate = rate;
            int frames = dataLen / (2 * channels);
            pcm = new short[frames];
            for (int i = 0; i < frames; i++)
            {
                if (channels == 1) pcm[i] = (short)U16(w, dataOff + i * 2);
                else
                {
                    int sum = 0;
                    for (int c = 0; c < channels; c++) sum += (short)U16(w, dataOff + (i * channels + c) * 2);
                    pcm[i] = (short)(sum / channels);       // down-mix to mono
                }
            }
        }

        private const int SamplesPerBlock = 0x3800;   // 14336 (= 1024 frames)
        private const int BlockBytes = 0x2000;        // 8192  (= 1024 frames × 8)

        private static byte[] Encode(short[] pcm, int sampleRate)
        {
            int n = pcm.Length;
            if (n == 0) throw new InvalidDataException("Empty audio.");
            short[] coefs = new short[16];
            DspAdpcm.CorrelateCoefs(pcm, n, coefs);

            int blockCount = (n + SamplesPerBlock - 1) / SamplesPerBlock;
            var data = new System.Collections.Generic.List<byte>(n / 14 * 8 + 64);
            var seek = new System.Collections.Generic.List<short>(blockCount * 2);
            short[] buf = new short[16];
            byte[] frame = new byte[8];
            short yn1 = 0, yn2 = 0;
            int pos = 0;
            for (int b = 0; b < blockCount; b++)
            {
                seek.Add(yn1); seek.Add(yn2);                       // per-block seek history
                int blockSamples = Math.Min(SamplesPerBlock, n - b * SamplesPerBlock);
                int blockStart = data.Count;
                for (int s = 0; s < blockSamples;)
                {
                    int count = Math.Min(14, blockSamples - s);
                    buf[0] = yn2; buf[1] = yn1;
                    for (int k = 0; k < count; k++) buf[2 + k] = pcm[pos + k];
                    DspAdpcm.EncodeFrame(buf, count, frame, coefs);
                    for (int k = 0; k < 8; k++) data.Add(frame[k]);
                    yn2 = buf[count]; yn1 = buf[count + 1];         // carry last two decoded samples
                    pos += count; s += count;
                }
                while ((data.Count - blockStart) % 0x20 != 0) data.Add(0);   // pad block to 0x20
            }

            int lastBlockSamples = n - (blockCount - 1) * SamplesPerBlock;
            int lastFrames = (lastBlockSamples + 13) / 14;
            int lastBlockSize = lastFrames * 8;
            int lastPadded = Align(lastBlockSize, 0x20);

            // ---- INFO block (0xC0), byte layout mirrors vanilla YW3 BCSTM ----
            byte[] info = new byte[0xC0];
            Ascii(info, 0, "INFO"); W32(info, 4, 0xC0);
            W16(info, 8, 0x4100); W32(info, 0x0C, 0x18);            // -> streamInfo (rel 0x08)
            W16(info, 0x10, 0x0000); W32(info, 0x14, unchecked((int)0xFFFFFFFF));
            W16(info, 0x18, 0x0101); W32(info, 0x1C, 0x64);        // -> channel table (rel 0x08)
            int si = 0x20;                                          // streamInfo (file 0x60)
            info[si] = 2; info[si + 1] = 0; info[si + 2] = 1;      // codec, loop, channels
            W32(info, si + 4, sampleRate);
            W32(info, si + 8, 0);                                   // loopStart
            W32(info, si + 0x0C, n);                                // sampleCount
            W32(info, si + 0x10, blockCount);
            W32(info, si + 0x14, BlockBytes);
            W32(info, si + 0x18, SamplesPerBlock);
            W32(info, si + 0x1C, lastBlockSize);
            W32(info, si + 0x20, lastBlockSamples);
            W32(info, si + 0x24, lastPadded);
            W32(info, si + 0x28, 2);                                // seek "sample size"
            W32(info, si + 0x2C, SamplesPerBlock);                  // seek interval
            W16(info, si + 0x30, 0x1F00); W32(info, si + 0x34, 0x18); // sample-data ref -> DATA body +0x18
            int ct = 0x6C;                                          // channel table (file 0xAC)
            W32(info, ct, 1);                                       // channel count
            W16(info, ct + 4, 0x4102); W32(info, ct + 8, 0x0C);    // -> chInfo (rel table)
            int ci = ct + 0x0C;                                     // chInfo (file 0xB8)
            W16(info, ci, 0x0300); W32(info, ci + 4, 0x08);        // -> ADPCM ctx (rel chInfo)
            int ad = ci + 0x08;                                     // ADPCM ctx (file 0xC0): 16 coefs
            for (int k = 0; k < 16; k++) W16(info, ad + k * 2, coefs[k]);
            // gain/predScale/yn1/yn2/loop* left zero

            // ---- SEEK block ----
            int seekSize = Align(8 + seek.Count * 2, 0x20);
            byte[] seekBlk = new byte[seekSize];
            Ascii(seekBlk, 0, "SEEK"); W32(seekBlk, 4, seekSize);
            for (int k = 0; k < seek.Count; k++) W16(seekBlk, 8 + k * 2, seek[k]);

            // ---- DATA block: "DATA" + size + pad to 0x20 + adpcm ----
            int dataSize = 0x20 + data.Count;
            byte[] dataBlk = new byte[dataSize];
            Ascii(dataBlk, 0, "DATA"); W32(dataBlk, 4, dataSize);
            for (int k = 0; k < data.Count; k++) dataBlk[0x20 + k] = data[k];

            // ---- header ----
            int infoOff = 0x40, seekOff = infoOff + info.Length, dataOff = seekOff + seekBlk.Length;
            int fileSize = dataOff + dataBlk.Length;
            byte[] head = new byte[0x40];
            Ascii(head, 0, "CSTM"); head[4] = 0xFF; head[5] = 0xFE; W16(head, 6, 0x40);
            head[8] = 0x00; head[9] = 0x01; head[0x0A] = 0x03; head[0x0B] = 0x02;   // version (vanilla)
            W32(head, 0x0C, fileSize); W16(head, 0x10, 3);
            W16(head, 0x14, 0x4000); W32(head, 0x18, infoOff); W32(head, 0x1C, info.Length);
            W16(head, 0x20, 0x4001); W32(head, 0x24, seekOff); W32(head, 0x28, seekBlk.Length);
            W16(head, 0x2C, 0x4002); W32(head, 0x30, dataOff); W32(head, 0x34, dataBlk.Length);

            byte[] outBuf = new byte[fileSize];
            Buffer.BlockCopy(head, 0, outBuf, 0, head.Length);
            Buffer.BlockCopy(info, 0, outBuf, infoOff, info.Length);
            Buffer.BlockCopy(seekBlk, 0, outBuf, seekOff, seekBlk.Length);
            Buffer.BlockCopy(dataBlk, 0, outBuf, dataOff, dataBlk.Length);
            return outBuf;
        }

        private static void Ascii(byte[] d, int o, string s) { for (int i = 0; i < s.Length; i++) d[o + i] = (byte)s[i]; }
        private static void W16(byte[] d, int o, int v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }
        private static void W32(byte[] d, int o, int v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24); }

        private static void Decode(byte[] f, out short[][] outChannels, out int sampleRate, out int sampleCount)
        {
            if (f.Length < 0x40 || f[0] != 'C' || f[1] != 'S' || f[2] != 'T' || f[3] != 'M')
                throw new InvalidDataException("Not a BCSTM (CSTM) file.");

            // --- block references (from 0x14: type u16, pad u16, offset u32, size u32) ---
            int blockCount = U16(f, 0x10);
            int infoOff = -1, dataOff = -1;
            for (int i = 0; i < blockCount; i++)
            {
                int r = 0x14 + i * 12;
                int type = U16(f, r);
                int off = I32(f, r + 4);
                if (type == 0x4000) infoOff = off;
                else if (type == 0x4002) dataOff = off;
            }
            if (infoOff < 0 || dataOff < 0) throw new InvalidDataException("BCSTM missing INFO/DATA block.");

            // --- INFO: references at infoOff+8 (streamInfo, trackInfo, channelInfo) ---
            int refBase = infoOff + 8;
            int streamInfo = refBase + I32(f, refBase + 0 * 8 + 4);
            int channelInfoTable = refBase + I32(f, refBase + 2 * 8 + 4);

            int codec = f[streamInfo + 0];
            int channelCount = f[streamInfo + 2];
            sampleRate = I32(f, streamInfo + 4);
            sampleCount = I32(f, streamInfo + 0x0C);
            int blkCount = I32(f, streamInfo + 0x10);
            int blockSize = I32(f, streamInfo + 0x14);
            int samplesPerBlock = I32(f, streamInfo + 0x18);
            int lastBlockSize = I32(f, streamInfo + 0x1C);
            int lastBlockSamples = I32(f, streamInfo + 0x20);
            // sample-data reference at streamInfo+0x30 (type 0x1F00 u16, pad u16, offset u32) -> offset into DATA body
            int sampleDataOffset = I32(f, streamInfo + 0x34);
            if (codec != 2) throw new NotSupportedException("BCSTM codec is not DSP-ADPCM.");

            // --- channel info: count then per-channel refs; ALL ref offsets are relative to the table start. ---
            var coefs = new short[channelCount][];
            var yn1 = new short[channelCount];
            var yn2 = new short[channelCount];
            for (int c = 0; c < channelCount; c++)
            {
                int chInfo = channelInfoTable + I32(f, channelInfoTable + 4 + c * 8 + 4);  // per-channel info entry
                int adpcm = chInfo + I32(f, chInfo + 4);                                   // ref -> ADPCM context
                var cc = new short[16];
                for (int k = 0; k < 16; k++) cc[k] = (short)U16(f, adpcm + k * 2);
                coefs[c] = cc;
                yn1[c] = (short)U16(f, adpcm + 0x22);
                yn2[c] = (short)U16(f, adpcm + 0x24);
            }

            // --- DATA: samples start at dataOff+8 + sampleDataOffset ---
            int dataStart = dataOff + 8 + sampleDataOffset;
            outChannels = new short[channelCount][];
            for (int c = 0; c < channelCount; c++) outChannels[c] = new short[sampleCount];

            for (int c = 0; c < channelCount; c++)
            {
                short h1 = yn1[c], h2 = yn2[c];
                int outPos = 0;
                for (int b = 0; b < blkCount && outPos < sampleCount; b++)
                {
                    bool last = b == blkCount - 1;
                    int thisSize = last ? lastBlockSize : blockSize;              // bytes per channel this block
                    int thisSamples = last ? lastBlockSamples : samplesPerBlock;
                    int blockStart = dataStart + b * blockSize * channelCount + c * (last ? Align(lastBlockSize, 0x20) : blockSize);
                    DecodeBlock(f, blockStart, thisSize, thisSamples, coefs[c], ref h1, ref h2, outChannels[c], ref outPos, sampleCount);
                }
            }
        }

        // Decode DSP-ADPCM: 8-byte frames = 1 header (scale<<0 | coefIndex<<4) + 7 data bytes (14 nibbles/samples).
        private static void DecodeBlock(byte[] f, int start, int byteLen, int sampleLen, short[] coef,
            ref short h1, ref short h2, short[] outp, ref int outPos, int total)
        {
            int produced = 0;
            int p = start;
            int end = start + byteLen;
            while (produced < sampleLen && outPos < total && p < f.Length)
            {
                byte header = f[p++];
                int scale = 1 << (header & 0x0F);
                int ci = (header >> 4) & 0x0F;
                int c1 = coef[ci * 2], c2 = coef[ci * 2 + 1];
                for (int i = 0; i < 14 && produced < sampleLen && outPos < total; i++)
                {
                    if (p >= end && (i & 1) == 0) break;
                    int nibble;
                    if ((i & 1) == 0) nibble = (f[p] >> 4) & 0x0F;
                    else nibble = f[p++] & 0x0F;
                    int s = nibble >= 8 ? nibble - 16 : nibble;               // sign-extend 4-bit
                    int pred = (s * scale << 11) + c1 * h1 + c2 * h2;
                    int sample = Clamp((pred + 1024) >> 11);
                    h2 = h1; h1 = (short)sample;
                    outp[outPos++] = (short)sample;
                    produced++;
                }
            }
        }

        private static byte[] BuildWav(short[][] channels, int sampleRate, int sampleCount)
        {
            int ch = channels.Length;
            int dataLen = sampleCount * ch * 2;
            var ms = new MemoryStream(44 + dataLen);
            var w = new BinaryWriter(ms);
            w.Write(new[] { 'R', 'I', 'F', 'F' }); w.Write(36 + dataLen);
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' }); w.Write(16); w.Write((short)1); w.Write((short)ch);
            w.Write(sampleRate); w.Write(sampleRate * ch * 2); w.Write((short)(ch * 2)); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' }); w.Write(dataLen);
            for (int i = 0; i < sampleCount; i++)
                for (int c = 0; c < ch; c++)
                    w.Write(channels[c][i]);
            return ms.ToArray();
        }

        private static int Align(int v, int a) => (v + a - 1) / a * a;
        private static int Clamp(int v) => v < -32768 ? -32768 : v > 32767 ? 32767 : v;
        private static int U16(byte[] d, int o) => d[o] | (d[o + 1] << 8);
        private static int I32(byte[] d, int o) => d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24;
    }
}
