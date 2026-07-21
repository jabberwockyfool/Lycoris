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

        public static string Apply(YokaiDatabase db, YokaiInfo y, int element, int power, bool blasterT)
        {
            var parts = new List<string>();

            var attack = Pick(db, Physical, power);
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
            int p = Math.Max(1, Math.Min(10, power));
            int idx = (int)Math.Round((list.Count - 1) * (p - 1) / 9.0);
            return list[idx];
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
