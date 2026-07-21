using System;

namespace Lycoris.Yokai
{
    /// <summary>
    /// Maps a 1–10 "morphology" level to a coherent chara_scale (Scale1..Scale7), interpolating
    /// between four real YW3 body archetypes:
    ///   1  Jibanyan  (Nyan — chibi cat)
    ///   4  Usapyon   (mascotte)
    ///   7  Mark Evans(humain — interpolated, humans ship no chara_scale)
    ///   10 Lord Enma (humanoïde)
    /// Anchor values are the actual scale rows read from the game; Scale1/Scale2 are always 1.
    /// </summary>
    public static class ScaleCurve
    {
        private static readonly double[] S1 = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
        private static readonly double[] S2 = { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };
        private static readonly double[] S3 = { 1.08, 1.05, 1.03, 1.00, 0.92, 0.83, 0.75, 0.69, 0.64, 0.58 };
        private static readonly double[] S4 = { 2.20, 2.20, 2.20, 2.20, 2.07, 1.93, 1.80, 1.70, 1.60, 1.50 };
        private static readonly double[] S5 = { 1.20, 1.13, 1.07, 1.00, 0.94, 0.88, 0.82, 0.78, 0.74, 0.70 };
        private static readonly double[] S6 = { 0.00, 0.00, 0.00, 0.00, -0.10, -0.20, -0.30, -0.37, -0.43, -0.50 };
        private static readonly double[] S7 = { 1.00, 1.00, 1.00, 1.00, 0.97, 0.94, 0.91, 0.89, 0.87, 0.85 };

        public static void Apply(YokaiInfo y, int level)
        {
            int i = Math.Max(1, Math.Min(10, level)) - 1;
            y.Scale1 = S1[i]; y.Scale2 = S2[i]; y.Scale3 = S3[i]; y.Scale4 = S4[i];
            y.Scale5 = S5[i]; y.Scale6 = S6[i]; y.Scale7 = S7[i];
        }

        /// <summary>Human-readable archetype for a level, shown next to the slider.</summary>
        public static string Morphology(int level)
        {
            if (level <= 2) return "Nyan (Jibanyan)";
            if (level <= 4) return "Mascotte (Usapyon)";
            if (level <= 7) return "Humain (Mark Evans)";
            return "Humanoïde (Enma)";
        }
    }
}
