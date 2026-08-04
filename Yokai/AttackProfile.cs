using System;
using System.Collections.Generic;
using System.Linq;

namespace Lycoris.Yokai
{
    /// <summary>
    /// Builds a coherent, full moveset for a yo-kai from its element and rank tier, using the real
    /// skill_config pools grouped by <b>SkillType category</b> (the ground-truth mapping, validated
    /// against how real chara_param records reference each slot):
    ///   0 = Guard · 1 = Attack · 3 = Technique · 4 = Soultimate · 5 = Inspirit.
    /// Every battle slot is filled at the rank tier (E→Z ⇒ weak→strong along each category pool):
    ///   - Attack     = a generic physical move (Punch/Kick-style) at the tier.
    ///   - Technique  = a Technique move of the chosen element at the tier.
    ///   - Inspirit   = an Inspirit move (element-matched when possible).
    ///   - Guard      = a Guard move at the tier.
    ///   - Soultimate = the element's strongest Soultimate (falls back to the tier).
    /// Blaster T (optional) is filled best-effort by matching the chosen move NAMES against the
    /// hackslash technic names (those configs carry no element/power, only names).
    /// </summary>
    public static class AttackProfile
    {
        private const int CatGuard = 0, CatAttack = 1, CatTechnique = 3, CatSoultimate = 4, CatInspirit = 5;

        // Generic basic-attack name markers, so the Attack slot gets a Punch/Kick-style move
        // (the escalating basic line) rather than a strong named special at high tier.
        private static readonly string[] BasicKeywords =
        {
            "Punch", "Kick", "Bite", "Claw", "Paw", "Slash", "Scratch", "Peck", "Slap",
            "Tackle", "Headbutt", "Fist", "Chomp", "Fang", "Chop", "Stab", "Strike", "Jab"
        };

        /// <param name="tier">0..6 = E,D,C,B,A,S,Z (from <see cref="StatCurve.TierOfRank"/>).</param>
        public static string Apply(YokaiDatabase db, YokaiInfo y, int element, int tier, bool blasterT)
        {
            double pos = Math.Max(0, Math.Min(6, tier)) / 6.0;
            var parts = new List<string>();

            var attack = PickBasic(db, pos) ?? PickCat(db, CatAttack, element, pos, preferElement: false);
            var technique = PickCat(db, CatTechnique, element, pos) ?? PickCat(db, CatAttack, element, pos, false);
            var inspirit = PickCat(db, CatInspirit, element, pos);
            var guard = PickCat(db, CatGuard, element, pos, preferElement: false);
            var soul = Strongest(db, CatSoultimate, element) ?? PickCat(db, CatSoultimate, element, pos);

            SetSlot(y, attack, h => y.AttackHash = h, n => y.AttackName = n,
                    () => { if ((y.AttackPct ?? 0) == 0) y.AttackPct = 100; }, parts, "Atk");
            SetSlot(y, technique, h => y.TechniqueHash = h, n => y.TechniqueName = n,
                    () => { if ((y.TechniquePct ?? 0) == 0) y.TechniquePct = 100; }, parts, "Tech");
            SetSlot(y, inspirit, h => y.InspiritHash = h, n => y.InspiritName = n,
                    () => { if ((y.InspiritPct ?? 0) == 0) y.InspiritPct = 50; }, parts, "Insp");
            SetSlot(y, guard, h => y.GuardHash = h, n => y.GuardName = n,
                    () => { if ((y.GuardPct ?? 0) == 0) y.GuardPct = 50; }, parts, "Guard");
            SetSlot(y, soul, h => y.SoultimateHash = h, n => y.SoultimateName = n, null, parts, "Soul");

            if (blasterT && y.HasBlasterT)
            {
                int bt = 0;
                bt += SetBt(db, n => y.BtAttackAHash = n, attack);
                bt += SetBt(db, n => y.BtAttackYHash = n, technique);
                bt += SetBt(db, n => y.BtSoultimateHash = n, soul);
                parts.Add($"BlasterT: {bt}/3 par nom");
            }
            return parts.Count > 0 ? string.Join(", ", parts) : "no skill pools loaded";
        }

        private static void SetSlot(YokaiInfo y, YokaiDatabase.SkillMove? move, Action<int> setHash,
            Action<string> setName, Action setPctIfEmpty, List<string> parts, string label)
        {
            if (!move.HasValue) return;
            setHash(move.Value.Hash);
            setName(move.Value.Name);
            setPctIfEmpty?.Invoke();
            parts.Add(label + "=" + move.Value.Name);
        }

        /// <summary>Pick from a category pool at the tier position, preferring the chosen element when asked.</summary>
        private static YokaiDatabase.SkillMove? PickCat(YokaiDatabase db, int cat, int element, double pos,
            bool preferElement = true)
        {
            if (!db.SkillsByCategory.TryGetValue(cat, out var all) || all.Count == 0) return null;
            List<YokaiDatabase.SkillMove> pool = all;
            if (preferElement)
            {
                var el = all.Where(m => m.Element == element).ToList();
                if (el.Count > 0) pool = el;
            }
            return AtPos(pool, pos);
        }

        /// <summary>Attack pool filtered to generic basic attacks (Punch/Kick/…), picked at the tier position.</summary>
        private static YokaiDatabase.SkillMove? PickBasic(YokaiDatabase db, double pos)
        {
            if (!db.SkillsByCategory.TryGetValue(CatAttack, out var all) || all.Count == 0) return null;
            var basics = all.Where(m => BasicKeywords.Any(k => m.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            return AtPos(basics.Count > 0 ? basics : all, pos);
        }

        private static YokaiDatabase.SkillMove AtPos(List<YokaiDatabase.SkillMove> sorted, double pos)
        {
            int idx = (int)Math.Round((sorted.Count - 1) * Math.Max(0, Math.Min(1, pos)));
            return sorted[idx];
        }

        private static YokaiDatabase.SkillMove? Strongest(YokaiDatabase db, int cat, int element)
        {
            if (!db.SkillsByCategory.TryGetValue(cat, out var all) || all.Count == 0) return null;
            var el = all.Where(m => m.Element == element).ToList();
            var pool = el.Count > 0 ? el : all;
            return pool[pool.Count - 1]; // sorted ascending by power
        }

        private static int SetBt(YokaiDatabase db, Action<int> set, YokaiDatabase.SkillMove? move)
        {
            if (move.HasValue && db.TechnicByName.TryGetValue(move.Value.Name, out int hash)) { set(hash); return 1; }
            return 0;
        }
    }
}
