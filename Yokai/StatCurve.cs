using System;

namespace Lycoris.Yokai
{
    /// <summary>
    /// Maps a yo-kai's <b>rank</b> (E→Z) to a coherent Min/Max stat set, so every yo-kai of a given
    /// rank ends up with believable, in-range stats for that rank ("consistent stats by rank").
    /// The per-rank Max values are anchored on the real YW3 per-rank stat medians (computed from the
    /// game data), smoothed to be non-decreasing across ranks. Min values use each stat's typical
    /// min/max ratio observed in the game data.
    ///
    /// A legacy 1–10 <see cref="Apply(YokaiInfo,int)"/> power overload is kept for callers that still
    /// want a manual tier.
    /// </summary>
    public static class StatCurve
    {
        // Rank codes (YokaiEnums): E=0 D=1 C=2 B=3 A=4 S=5 Z=9 (Unrank=15 → no curve).
        // Tier index 0..6 = E,D,C,B,A,S,Z.
        private static readonly int[] RankHp  = { 320, 345, 375, 395, 415, 450, 535 };
        private static readonly int[] RankStr = {  95, 108, 122, 140, 160, 185, 300 };
        private static readonly int[] RankSpr = { 138, 150, 162, 172, 186, 200, 245 };
        private static readonly int[] RankDef = { 106, 115, 120, 132, 145, 158, 215 };
        private static readonly int[] RankSpd = { 140, 148, 155, 162, 172, 200, 285 };

        // Legacy power (1–10) Max curve — anchored on the overall YW3 stat distribution.
        private static readonly int[] Hp  = { 280, 320, 350, 375, 395, 420, 440, 455, 475, 650 };
        private static readonly int[] Str = { 40, 47, 55, 90, 125, 160, 190, 215, 240, 360 };
        private static readonly int[] Spr = { 40, 48, 58, 105, 155, 180, 200, 220, 240, 300 };
        private static readonly int[] Def = { 60, 80, 100, 115, 125, 145, 160, 190, 225, 282 };
        private static readonly int[] Spd = { 65, 90, 115, 132, 150, 165, 180, 200, 220, 270 };

        // Median Min/Max ratio per stat (HP, Str, Spr, Def, Spd) from the game data.
        private const double RHp = 0.13, RStr = 0.14, RSpr = 0.14, RDef = 0.17, RSpd = 0.11;

        /// <summary>Rank code (0=E … 5=S, 9=Z) → tier index 0..6, or null for Unrank/unknown.</summary>
        public static int? TierOfRank(int? rank)
        {
            if (!rank.HasValue) return null;
            switch (rank.Value)
            {
                case 0: return 0; // E
                case 1: return 1; // D
                case 2: return 2; // C
                case 3: return 3; // B
                case 4: return 4; // A
                case 5: return 5; // S
                case 9: return 6; // Z
                default: return null; // 15 = Unrank (and anything else)
            }
        }

        /// <summary>
        /// Apply consistent stats for the yo-kai's own rank. Returns true when a rank curve was applied,
        /// false when the yo-kai is unranked (nothing changed).
        /// </summary>
        public static bool ApplyByRank(YokaiInfo y)
        {
            int? tier = TierOfRank(y.Rank);
            if (!tier.HasValue) return false;
            int i = tier.Value;
            y.MaxHp = RankHp[i];        y.MinHp = R(RankHp[i], RHp);
            y.MaxStrength = RankStr[i]; y.MinStrength = R(RankStr[i], RStr);
            y.MaxSpirit = RankSpr[i];   y.MinSpirit = R(RankSpr[i], RSpr);
            y.MaxDefense = RankDef[i];  y.MinDefense = R(RankDef[i], RDef);
            y.MaxSpeed = RankSpd[i];    y.MinSpeed = R(RankSpd[i], RSpd);
            return true;
        }

        /// <summary>Legacy: apply stats from a manual 1–10 power level.</summary>
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
