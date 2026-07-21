namespace Lycoris.Yokai
{
    /// <summary>
    /// Field indices for Yo-kai Watch 3, taken from Albatross' YW3 logic classes.
    /// Centralised here so they are trivial to correct after validating against a real
    /// YW3 .cfg.bin. If a future game needs different offsets, add another schema instance.
    /// </summary>
    public sealed class YokaiSchema
    {
        // Entry (record) names inside the cfg.bin files. The key table stores them WITHOUT
        // the trailing underscore Albatross uses for its StartsWith matching.
        public string ParamRecord = "CHARA_PARAM_INFO";
        public string BaseYokaiRecord = "CHARA_BASE_YOKAI_INFO";
        public string NounRecord = "NOUN_INFO";

        // File name prefixes inside an extracted folder (newest numbered file wins).
        public string ParamFilePrefix = "chara_param";
        public string BaseFilePrefix = "chara_base";
        public string TextFilePrefix = "chara_text";       // names   (glob excludes chara_desc_text*)
        public string DescFilePrefix = "chara_desc_text";  // descriptions
        public string ScaleFilePrefix = "chara_scale";     // model scale, keyed by BaseHash
        public string SkillTextFilePrefix = "skill_text";  // skill/move name text container
        public string AbilityFilePrefix = "chara_ability"; // ability config (exclude *_text)
        public string AbilityTextFilePrefix = "chara_ability_text";
        public string SkillConfigFilePrefix = "skill_config"; // maps move hash -> skill name hash

        // Config records: key at [0], a NameHash field elsewhere (auto-detected against the text table).
        public string AbilityConfigRecord = "CHARA_ABILITY_CONFIG_INFO"; // key[0], name[1]
        public string SkillConfigRecord = "SKILL_CONFIG_INFO";           // key[0], name[3]
        public int Skill_NameHashIndex = 3;
        public int Skill_PowerIndex = 6;      // move power (0-1000)
        public int Skill_ElementIndex = 8;    // element = Attributes enum (0-9): 8=Strong Attack (physical), 9=Restoration

        // --- Blaster T (Hackslash) — editable, keyed by ParamHash ---
        public string HackslashParamFilePrefix = "hackslash_chara_param";
        public string HackslashRecord = "HACKSLASH_CHARA_PARAM_INFO";
        public int Hs_AbilityIndex = 3;      // Blaster-T ability -> hackslash_chara_ability config
        public int Hs_SoultimateIndex = 4;   // -> hackslash_technic
        public int Hs_AttackAIndex = 5;
        public int Hs_AttackYIndex = 6;
        public int Hs_AttackXIndex = 7;
        public string HackslashTechnicFilePrefix = "hackslash_technic";       // config (exclude *_text)
        public string HackslashTechnicRecord = "HACKSLASH_TECHNIC_INFO";
        public string HackslashTechnicTextFilePrefix = "hackslash_technic_text";
        public string HackslashAbilityFilePrefix = "hackslash_chara_ability";  // config (exclude *_text)
        public string HackslashAbilityTextFilePrefix = "hackslash_chara_ability_text";

        // --- Drops / rewards (battle_chara_param) — editable, keyed by ParamHash ---
        public string BattleParamFilePrefix = "battle_chara_param";
        public string BattleRecord = "BATTLE_CHARA_PARAM_INFO";
        public int B_MoneyIndex = 3;
        public int B_ExpIndex = 4;
        public int B_Drop1Index = 5;
        public int B_Drop1RateIndex = 6;
        public int B_Drop2Index = 7;
        public int B_Drop2RateIndex = 8;

        // --- Items (drop names) ---
        public string ItemConfigFilePrefix = "item_config";
        public string ItemTextFilePrefix = "item_text";

        // chara_scale: CHARA_SCALE_INFO keyed by BaseHash at [0].
        public string ScaleRecord = "CHARA_SCALE_INFO";
        public string ScaleGroupBegin = "CHARA_SCALE_INFO_LIST_BEG";
        public int Scale_BaseHashIndex = 0;

        // Group-begin markers (value[0] = child count) for the delete-yokai path.
        public string HackslashGroupBegin = "HACKSLASH_CHARA_PARAM_INFO_LIST_BEG";
        public string BattleGroupBegin = "BATTLE_CHARA_PARAM_INFO_LIST_BEG";

        // CHARA_PARAM_INFO_ field layout (YW3).
        public int ParamHashIndex = 0;
        public int Param_BaseHashIndex = 1;
        public int ShowInMedaliumIndex = 2;   // 0/1 -> "Show" checkbox
        public int MedaliumOffsetIndex = 3;   // "Medal" number
        public int MinHpIndex = 5;
        public int MinStrengthIndex = 6;
        public int MinSpiritIndex = 7;
        public int MinSpeedIndex = 8;
        public int MinDefenseIndex = 9;
        public int MaxHpIndex = 10;
        public int MaxStrengthIndex = 11;
        public int MaxSpiritIndex = 12;
        public int MaxDefenseIndex = 13;
        public int MaxSpeedIndex = 14;
        public int ResistanceIndex = 16;      // Strongest attribute
        public int WeaknessIndex = 17;
        // Moves: hash + percentage pairs (percentages are stored as ints in YW3).
        public int AttackHashIndex = 19;
        public int AttackPctIndex = 20;
        public int TechniqueHashIndex = 21;
        public int TechniquePctIndex = 22;
        public int InspiritHashIndex = 23;
        public int InspiritPctIndex = 24;
        public int GuardHashIndex = 25;
        public int GuardPctIndex = 26;
        public int SoultimateHashIndex = 27;
        public int AbilityHashIndex = 28;

        // CHARA_BASE_YOKAI_INFO field layout (YW3, 0-indexed; validated against real data).
        public int Base_BaseHashIndex = 0;
        public int Base_FileNamePrefixIndex = 1;   // model/icon filename letter
        public int Base_FileNameNumberIndex = 2;
        public int Base_FileNameVariantIndex = 3;
        public int Base_NameHashIndex = 4;
        public int Base_DescriptionHashIndex = 10;
        public int Base_MedalPosXIndex = 11;
        public int Base_MedalPosYIndex = 12;
        public int Base_RankIndex = 14;
        public int Base_IsRareIndex = 15;
        public int Base_IsLegendIndex = 16;
        public int Base_IsPionnerIndex = 17;
        public int Base_IsCommandantIndex = 18;
        public int Base_FavoriteFoodIndex = 19;
        public int Base_HatedFoodIndex = 20;
        public int Base_TribeIndex = 23;
        public int Base_IsClassicIndex = 24;
        public int Base_IsMericanIndex = 25;
        public int Base_RoleIndex = 26;
        public int Base_IsDevaIndex = 28;
        public int Base_IsMysteryIndex = 29;
        public int Base_IsTreasureIndex = 30;

        public int EvolveOffsetIndex = 38;     // -1 = no evolution, else index into CHARA_EVOLVE_INFO
        // CHARA_EVOLVE_INFO record (in chara_param): [0]=target ParamHash, [1]=level.
        public string EvolveRecord = "CHARA_EVOLVE_INFO";
        public string EvolveGroupBegin = "CHARA_EVOLVE_INFO_LIST_BEG";
        public string EvolveGroupEnd = "CHARA_EVOLVE_INFO_LIST_END";
        public int Evolve_TargetIndex = 0;
        public int Evolve_LevelIndex = 1;

        // Group markers whose value[0] stores the child count (validated against real data).
        public string ParamGroupBegin = "CHARA_PARAM_INFO_LIST_BEG";
        public string ParamGroupEnd = "CHARA_PARAM_INFO_LIST_END";
        public string BaseGroupBegin = "CHARA_BASE_YOKAI_INFO_BEGIN";
        public string BaseGroupEnd = "CHARA_BASE_YOKAI_INFO_END";

        // Names live in chara_text as NOUN_INFO (key at [0], text at [5]). NounRecord above.
        public string NounGroupBegin = "NOUN_INFO_BEGIN";
        public string NounGroupEnd = "NOUN_INFO_END";
        public int NounKeyIndex = 0;
        public int NounTextIndex = 5;

        // Descriptions live in chara_desc_text as TEXT_INFO (key at [0], text at [2]).
        public string DescRecord = "TEXT_INFO";
        public string DescGroupBegin = "TEXT_INFO_BEGIN";
        public string DescGroupEnd = "TEXT_INFO_END";
        public int DescKeyIndex = 0;
        public int DescTextIndex = 2;

        public static readonly YokaiSchema Yw3 = new YokaiSchema();
    }
}
