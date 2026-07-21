using System;

namespace Lycoris.Yokai
{
    /// <summary>
    /// Maps a 1–10 "power" level to a coherent Min/Max stat set. The per-power Max values are
    /// anchored on the real YW3 stat distribution (power 1 ≈ 5th percentile, 5 ≈ median,
    /// 10 ≈ 99th percentile) so a chosen level yields believable, in-range stats. Min values use
    /// each stat's typical min/max ratio observed in the game data.
    /// </summary>
    public static class StatCurve
    {
        // Max value per power level (index 0 = power 1 … index 9 = power 10).
        private static readonly int[] Hp  = { 280, 320, 350, 375, 395, 420, 440, 455, 475, 650 };
        private static readonly int[] Str = { 40, 47, 55, 90, 125, 160, 190, 215, 240, 360 };
        private static readonly int[] Spr = { 40, 48, 58, 105, 155, 180, 200, 220, 240, 300 };
        private static readonly int[] Def = { 60, 80, 100, 115, 125, 145, 160, 190, 225, 282 };
        private static readonly int[] Spd = { 65, 90, 115, 132, 150, 165, 180, 200, 220, 270 };

        // Median Min/Max ratio per stat (HP, Str, Spr, Def, Spd) from the game data.
        private const double RHp = 0.13, RStr = 0.14, RSpr = 0.14, RDef = 0.17, RSpd = 0.11;

        public static void Apply(YokaiInfo y, int power)
        {
            int i = Math.Max(1, Math.Min(10, power)) - 1;
            y.MaxHp = Hp[i];        y.MinHp = R(Hp[i], RHp);
            y.MaxStrength = Str[i]; y.MinStrength = R(Str[i], RStr);
            y.MaxSpirit = Spr[i];   y.MinSpirit = R(Spr[i], RSpr);
            y.MaxDefense = Def[i];  y.MinDefense = R(Def[i], RDef);
            y.MaxSpeed = Spd[i];    y.MinSpeed = R(Spd[i], RSpd);
        }

        private static int R(int max, double ratio) => (int)Math.Round(max * ratio);
    }
}
