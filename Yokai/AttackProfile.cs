using System;
using System.Collections.Generic;
using System.Linq;

namespace Lycoris.Yokai
{
    /// <summary>
    /// Builds a coherent moveset for a yo-kai from its element and power level, using the real
    /// skill_config pools (skills grouped by element with a power value). The power level (1–10,
    /// shared with StatCurve) selects how strong the picked moves are.
    ///  - Attack     = a physical move (element 8 = Strong Attack) at the power tier.
    ///  - Technique  = an element move (the chosen element) at the power tier.
    ///  - Soultimate = the strongest move of the element.
    /// Blaster T (optional) is filled best-effort by matching the chosen move NAMES against the
    /// hackslash technic names (those configs carry no element/power, only names).
    /// </summary>
    public static class AttackProfile
    {
        private const int Physical = 8; // Attributes: Strong Attack

        // Generic basic-attack name markers, so the Attack slot gets a Punch/Kick-style move
        // (the escalating basic line) rather than a strong named special at high power.
        private static readonly string[] BasicKeywords =
        {
            "Punch", "Kick", "Bite", "Claw", "Paw", "Slash", "Scratch", "Peck", "Slap",
            "Tackle", "Headbutt", "Fist", "Chomp", "Fang", "Chop", "Stab", "Strike", "Jab"
        };

        public static string Apply(YokaiDatabase db, YokaiInfo y, int element, int power, bool blasterT)
        {
            var parts = new List<string>();

            var attack = PickBasic(db, power) ?? Pick(db, Physical, power);
            var technique = Pick(db, element, power) ?? Pick(db, Physical, power);
            var soul = Strongest(db, element) ?? Strongest(db, Physical);

            if (attack.HasValue) { y.AttackHash = attack.Value.Hash; parts.Add("Atk=" + attack.Value.Name); }
            if (technique.HasValue) { y.TechniqueHash = technique.Value.Hash; parts.Add("Tech=" + technique.Value.Name); }
            if (soul.HasValue) { y.SoultimateHash = soul.Value.Hash; parts.Add("Soul=" + soul.Value.Name); }

            if (blasterT && y.HasBlasterT)
            {
                int bt = 0;
                bt += SetBt(db, n => y.BtAttackAHash = n, attack);
                bt += SetBt(db, n => y.BtAttackYHash = n, technique);
                bt += SetBt(db, n => y.BtSoultimateHash = n, soul);
                parts.Add($"BlasterT: {bt}/3 par nom");
            }
            return parts.Count > 0 ? string.Join(", ", parts) : "aucun skill pour cet élément";
        }

        private static YokaiDatabase.SkillMove? Pick(YokaiDatabase db, int element, int power)
        {
            if (!db.SkillsByElement.TryGetValue(element, out var list) || list.Count == 0) return null;
            return AtTier(list, power);
        }

        /// <summary>Physical pool filtered to generic basic attacks (Punch/Kick/…), picked at the power tier.</summary>
        private static YokaiDatabase.SkillMove? PickBasic(YokaiDatabase db, int power)
        {
            if (!db.SkillsByElement.TryGetValue(Physical, out var all) || all.Count == 0) return null;
            var basics = all.Where(m => m.Power > 0 &&
                BasicKeywords.Any(k => m.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            return AtTier(basics.Count > 0 ? basics : all, power);
        }

        private static YokaiDatabase.SkillMove AtTier(List<YokaiDatabase.SkillMove> sorted, int power)
        {
            int p = Math.Max(1, Math.Min(10, power));
            int idx = (int)Math.Round((sorted.Count - 1) * (p - 1) / 9.0);
            return sorted[idx];
        }

        private static YokaiDatabase.SkillMove? Strongest(YokaiDatabase db, int element)
        {
            if (!db.SkillsByElement.TryGetValue(element, out var list) || list.Count == 0) return null;
            return list[list.Count - 1]; // sorted ascending by power
        }

        private static int SetBt(YokaiDatabase db, Action<int> set, YokaiDatabase.SkillMove? move)
        {
            if (move.HasValue && db.TechnicByName.TryGetValue(move.Value.Name, out int hash)) { set(hash); return 1; }
            return 0;
        }
    }
}
