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
