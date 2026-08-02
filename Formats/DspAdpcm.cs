using System;

namespace Lycoris.Formats
{
    /// <summary>
    /// GameCube/3DS <b>DSP-ADPCM</b> encoder — a faithful C# port of the public-domain reference encoder
    /// (jackoalan/gc-dspadpcm-encode "grok.c", itself after BrawlLib's AudioConverter). Generates the 8 predictor
    /// coefficient pairs from PCM and encodes 14-sample frames into 8-byte ADPCM frames. Used to build BCSTM voice
    /// files from a WAV (see <see cref="Bcstm.FromWav"/>). Verified by round-trip against <see cref="Bcstm"/>.
    /// </summary>
    internal static class DspAdpcm
    {
        // ---- coefficient generation -------------------------------------------------------------------------

        private static void InnerProductMerge(double[] vecOut, short[] buf, int b)
        {
            for (int i = 0; i <= 2; i++)
            {
                vecOut[i] = 0.0;
                for (int x = 0; x < 14; x++)
                    vecOut[i] -= buf[b + x - i] * buf[b + x];
            }
        }

        private static void OuterProductMerge(double[][] mtxOut, short[] buf, int b)
        {
            for (int x = 1; x <= 2; x++)
                for (int y = 1; y <= 2; y++)
                {
                    mtxOut[x][y] = 0.0;
                    for (int z = 0; z < 14; z++)
                        mtxOut[x][y] += buf[b + z - x] * buf[b + z - y];
                }
        }

        private static bool AnalyzeRanges(double[][] mtx, int[] vecIdxsOut)
        {
            double[] recips = new double[3];
            double val, tmp, min, max;

            for (int x = 1; x <= 2; x++)
            {
                val = Math.Max(Math.Abs(mtx[x][1]), Math.Abs(mtx[x][2]));
                if (val < double.Epsilon) return true;
                recips[x] = 1.0 / val;
            }

            int maxIndex = 0;
            for (int i = 1; i <= 2; i++)
            {
                for (int x = 1; x < i; x++)
                {
                    tmp = mtx[x][i];
                    for (int y = 1; y < x; y++)
                        tmp -= mtx[x][y] * mtx[y][i];
                    mtx[x][i] = tmp;
                }

                val = 0.0;
                for (int x = i; x <= 2; x++)
                {
                    tmp = mtx[x][i];
                    for (int y = 1; y < i; y++)
                        tmp -= mtx[x][y] * mtx[y][i];
                    mtx[x][i] = tmp;
                    tmp = Math.Abs(tmp) * recips[x];
                    if (tmp >= val) { val = tmp; maxIndex = x; }
                }

                if (maxIndex != i)
                {
                    for (int y = 1; y <= 2; y++)
                    {
                        tmp = mtx[maxIndex][y];
                        mtx[maxIndex][y] = mtx[i][y];
                        mtx[i][y] = tmp;
                    }
                    recips[maxIndex] = recips[i];
                }

                vecIdxsOut[i] = maxIndex;
                if (mtx[i][i] == 0.0) return true;

                if (i != 2)
                {
                    tmp = 1.0 / mtx[i][i];
                    for (int x = i + 1; x <= 2; x++)
                        mtx[x][i] *= tmp;
                }
            }

            min = 1.0e10; max = 0.0;
            for (int i = 1; i <= 2; i++)
            {
                tmp = Math.Abs(mtx[i][i]);
                if (tmp < min) min = tmp;
                if (tmp > max) max = tmp;
            }
            return min / max < 1.0e-10;
        }

        private static void BidirectionalFilter(double[][] mtx, int[] vecIdxs, double[] vecOut)
        {
            double tmp;
            for (int i = 1, x = 0; i <= 2; i++)
            {
                int index = vecIdxs[i];
                tmp = vecOut[index];
                vecOut[index] = vecOut[i];
                if (x != 0)
                    for (int y = x; y <= i - 1; y++)
                        tmp -= vecOut[y] * mtx[i][y];
                else if (tmp != 0.0)
                    x = i;
                vecOut[i] = tmp;
            }

            for (int i = 2; i > 0; i--)
            {
                tmp = vecOut[i];
                for (int y = i + 1; y <= 2; y++)
                    tmp -= vecOut[y] * mtx[i][y];
                vecOut[i] = tmp / mtx[i][i];
            }
            vecOut[0] = 1.0;
        }

        private static bool QuadraticMerge(double[] v)
        {
            double v0, v1, v2 = v[2];
            double tmp = 1.0 - (v2 * v2);
            if (tmp == 0.0) return true;
            v0 = (v[0] - (v2 * v2)) / tmp;
            v1 = (v[1] - (v[1] * v2)) / tmp;
            v[0] = v0; v[1] = v1;
            return Math.Abs(v1) > 1.0;
        }

        private static void FinishRecord(double[] inv, double[] outv)
        {
            for (int z = 1; z <= 2; z++)
            {
                if (inv[z] >= 1.0) inv[z] = 0.9999999999;
                else if (inv[z] <= -1.0) inv[z] = -0.9999999999;
            }
            outv[0] = 1.0;
            outv[1] = (inv[2] * inv[1]) + inv[1];
            outv[2] = inv[2];
        }

        private static void MatrixFilter(double[] src, double[] dst)
        {
            double[][] mtx = NewMtx();
            mtx[2][0] = 1.0;
            for (int i = 1; i <= 2; i++) mtx[2][i] = -src[i];

            for (int i = 2; i > 0; i--)
            {
                double val = 1.0 - (mtx[i][i] * mtx[i][i]);
                for (int y = 1; y <= i; y++)
                    mtx[i - 1][y] = ((mtx[i][i] * mtx[i][y]) + mtx[i][y]) / val;
            }

            dst[0] = 1.0;
            for (int i = 1; i <= 2; i++)
            {
                dst[i] = 0.0;
                for (int y = 1; y <= i; y++)
                    dst[i] += mtx[i][y] * dst[i - y];
            }
        }

        private static void MergeFinishRecord(double[] src, double[] dst)
        {
            double[] tmp = new double[3];
            double val = src[0];

            dst[0] = 1.0;
            for (int i = 1; i <= 2; i++)
            {
                double v2 = 0.0;
                for (int y = 1; y < i; y++)
                    v2 += dst[y] * src[i - y];

                dst[i] = val > 0.0 ? -(v2 + src[i]) / val : 0.0;
                tmp[i] = dst[i];

                for (int y = 1; y < i; y++)
                    dst[y] += dst[i] * dst[i - y];

                val *= 1.0 - (dst[i] * dst[i]);
            }
            FinishRecord(tmp, dst);
        }

        private static double ContrastVectors(double[] s1, double[] s2)
        {
            double val = (s2[2] * s2[1] + -s2[1]) / (1.0 - s2[2] * s2[2]);
            double val1 = (s1[0] * s1[0]) + (s1[1] * s1[1]) + (s1[2] * s1[2]);
            double val2 = (s1[0] * s1[1]) + (s1[1] * s1[2]);
            double val3 = s1[0] * s1[2];
            return val1 + (2.0 * val * val2) + (2.0 * (-s2[1] * val + -s2[2]) * val3);
        }

        private static void FilterRecords(double[][] vecBest, int exp, double[][] records, int recordCount)
        {
            double[][] bufferList = new double[8][];
            for (int i = 0; i < 8; i++) bufferList[i] = new double[3];
            int[] buffer1 = new int[8];
            double[] buffer2 = new double[3];
            int index; double value, tempVal;

            for (int x = 0; x < 2; x++)
            {
                for (int y = 0; y < exp; y++)
                {
                    buffer1[y] = 0;
                    for (int i = 0; i <= 2; i++) bufferList[y][i] = 0.0;
                }
                for (int z = 0; z < recordCount; z++)
                {
                    index = 0; value = 1.0e30;
                    for (int i = 0; i < exp; i++)
                    {
                        tempVal = ContrastVectors(vecBest[i], records[z]);
                        if (tempVal < value) { value = tempVal; index = i; }
                    }
                    buffer1[index]++;
                    MatrixFilter(records[z], buffer2);
                    for (int i = 0; i <= 2; i++) bufferList[index][i] += buffer2[i];
                }
                for (int i = 0; i < exp; i++)
                    if (buffer1[i] > 0)
                        for (int y = 0; y <= 2; y++) bufferList[i][y] /= buffer1[i];
                for (int i = 0; i < exp; i++)
                    MergeFinishRecord(bufferList[i], vecBest[i]);
            }
        }

        /// <summary>Generate the 8 coefficient pairs (16 shorts) for a PCM stream.</summary>
        public static void CorrelateCoefs(short[] source, int samples, short[] coefsOut)
        {
            int frameSamples;
            short[] blockBuffer = new short[0x3800];
            short[] hist = new short[28];               // [0..13]=prev 14, [14..27]=current 14 (base index 14)

            double[] vec1 = new double[3], vec2 = new double[3];
            double[][] mtx = NewMtx();
            int[] vecIdxs = new int[3];

            int numFrames = (samples + 13) / 14;
            double[][] records = new double[numFrames * 2 + 1][];
            for (int i = 0; i < records.Length; i++) records[i] = new double[3];
            int recordCount = 0;

            double[][] vecBest = new double[8][];
            for (int i = 0; i < 8; i++) vecBest[i] = new double[3];

            int srcPos = 0;
            for (int x = samples; x > 0;)
            {
                if (x > 0x3800) { frameSamples = 0x3800; x -= 0x3800; }
                else
                {
                    frameSamples = x;
                    for (int z = 0; z < 14 && z + frameSamples < 0x3800; z++) blockBuffer[frameSamples + z] = 0;
                    x = 0;
                }
                Array.Copy(source, srcPos, blockBuffer, 0, frameSamples);
                srcPos += frameSamples;

                for (int i = 0; i < frameSamples;)
                {
                    for (int z = 0; z < 14; z++) hist[z] = hist[14 + z];       // shift current -> prev
                    for (int z = 0; z < 14; z++) hist[14 + z] = blockBuffer[i++];

                    InnerProductMerge(vec1, hist, 14);
                    if (Math.Abs(vec1[0]) > 10.0)
                    {
                        OuterProductMerge(mtx, hist, 14);
                        if (!AnalyzeRanges(mtx, vecIdxs))
                        {
                            BidirectionalFilter(mtx, vecIdxs, vec1);
                            if (!QuadraticMerge(vec1))
                                FinishRecord(vec1, records[recordCount++]);
                        }
                    }
                }
            }

            vec1[0] = 1.0; vec1[1] = 0.0; vec1[2] = 0.0;
            for (int z = 0; z < recordCount; z++)
            {
                MatrixFilter(records[z], vecBest[0]);
                for (int y = 1; y <= 2; y++) vec1[y] += vecBest[0][y];
            }
            if (recordCount > 0)
                for (int y = 1; y <= 2; y++) vec1[y] /= recordCount;

            MergeFinishRecord(vec1, vecBest[0]);

            int exp = 1;
            for (int w = 0; w < 3;)
            {
                vec2[0] = 0.0; vec2[1] = -1.0; vec2[2] = 0.0;
                for (int i = 0; i < exp; i++)
                    for (int y = 0; y <= 2; y++)
                        vecBest[exp + i][y] = (0.01 * vec2[y]) + vecBest[i][y];
                ++w;
                exp = 1 << w;
                FilterRecords(vecBest, exp, records, recordCount);
            }

            for (int z = 0; z < 8; z++)
            {
                coefsOut[z * 2] = Clamp16(-vecBest[z][1] * 2048.0);
                coefsOut[z * 2 + 1] = Clamp16(-vecBest[z][2] * 2048.0);
            }
        }

        // ---- frame encoding ---------------------------------------------------------------------------------

        /// <summary>Encode up to 14 samples. pcmInOut[0..1] = history (yn2,yn1); [2..] = input samples, overwritten
        /// with the decoded result so the next frame can carry [count],[count+1] as its history.</summary>
        public static void EncodeFrame(short[] pcmInOut, int sampleCount, byte[] adpcmOut, short[] coefs)
        {
            int[][] inSamples = new int[8][]; int[][] outSamples = new int[8][];
            for (int i = 0; i < 8; i++) { inSamples[i] = new int[16]; outSamples[i] = new int[14]; }
            int bestIndex = 0;
            int[] scale = new int[8];
            double[] distAccum = new double[8];

            for (int i = 0; i < 8; i++)
            {
                int c0 = coefs[i * 2], c1 = coefs[i * 2 + 1];      // c0 for yn1 (more recent), c1 for yn2
                int v1, v2, v3, distance, index;

                inSamples[i][0] = pcmInOut[0];
                inSamples[i][1] = pcmInOut[1];

                distance = 0;
                for (int s = 0; s < sampleCount; s++)
                {
                    inSamples[i][s + 2] = v1 = ((pcmInOut[s] * c1) + (pcmInOut[s + 1] * c0)) / 2048;
                    v2 = pcmInOut[s + 2] - v1;
                    v3 = (v2 >= 32767) ? 32767 : (v2 <= -32768) ? -32768 : v2;
                    if (Math.Abs(v3) > Math.Abs(distance)) distance = v3;
                }

                for (scale[i] = 0; (scale[i] <= 12) && ((distance > 7) || (distance < -8)); scale[i]++, distance /= 2) { }
                scale[i] = (scale[i] <= 1) ? -1 : scale[i] - 2;

                do
                {
                    scale[i]++;
                    distAccum[i] = 0; index = 0;
                    for (int s = 0; s < sampleCount; s++)
                    {
                        v1 = ((inSamples[i][s] * c1) + (inSamples[i][s + 1] * c0));
                        v2 = ((pcmInOut[s + 2] << 11) - v1) / 2048;
                        v3 = (v2 > 0) ? (int)((double)v2 / (1 << scale[i]) + 0.4999999) : (int)((double)v2 / (1 << scale[i]) - 0.4999999);

                        if (v3 < -8) { if (index < (v3 = -8 - v3)) index = v3; v3 = -8; }
                        else if (v3 > 7) { if (index < (v3 -= 7)) index = v3; v3 = 7; }

                        outSamples[i][s] = v3;
                        v1 = (v1 + ((v3 * (1 << scale[i])) << 11) + 1024) >> 11;
                        inSamples[i][s + 2] = v2 = (v1 >= 32767) ? 32767 : (v1 <= -32768) ? -32768 : v1;
                        v3 = pcmInOut[s + 2] - v2;
                        distAccum[i] += v3 * (double)v3;
                    }
                    for (int x = index + 8; x > 256; x >>= 1)
                        if (++scale[i] >= 12) scale[i] = 11;
                } while ((scale[i] < 12) && (index > 1));
            }

            double min = double.MaxValue;
            for (int i = 0; i < 8; i++)
                if (distAccum[i] < min) { min = distAccum[i]; bestIndex = i; }

            for (int s = 0; s < sampleCount; s++) pcmInOut[s + 2] = (short)inSamples[bestIndex][s + 2];

            adpcmOut[0] = (byte)((bestIndex << 4) | (scale[bestIndex] & 0xF));
            for (int s = sampleCount; s < 14; s++) outSamples[bestIndex][s] = 0;
            for (int y = 0; y < 7; y++)
                adpcmOut[y + 1] = (byte)((outSamples[bestIndex][y * 2] << 4) | (outSamples[bestIndex][y * 2 + 1] & 0xF));
        }

        private static double[][] NewMtx()
        {
            var m = new double[3][];
            for (int i = 0; i < 3; i++) m[i] = new double[3];
            return m;
        }

        private static short Clamp16(double d)
        {
            if (d > 0.0) return d > 32767.0 ? (short)32767 : (short)Math.Round(d, MidpointRounding.AwayFromZero);
            return d < -32768.0 ? (short)-32768 : (short)Math.Round(d, MidpointRounding.AwayFromZero);
        }
    }
}
