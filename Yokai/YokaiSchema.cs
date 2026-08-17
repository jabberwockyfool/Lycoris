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
        public string AddmemberTextFilePrefix = "addmembermenu_text";  // befriend dialogue (TEXT_INFO, key[0], text[2])
        public string ScaleFilePrefix = "chara_scale";     // model scale, keyed by BaseHash
        public string SkillTextFilePrefix = "skill_text";  // skill/move NAME text container (NOUN)
        public string SkillDescTextFilePrefix = "skill_desc_text"; // skill DESCRIPTION text container (TEXT_INFO)
        public string AbilityFilePrefix = "chara_ability"; // ability config (exclude *_text)
        public string AbilityTextFilePrefix = "chara_ability_text";
        public string SkillConfigFilePrefix = "skill_config"; // maps move hash -> skill name hash

        // Config records: key at [0], a NameHash field elsewhere (auto-detected against the text table).
        public string AbilityConfigRecord = "CHARA_ABILITY_CONFIG_INFO"; // key[0], name[1]
        public string SkillConfigRecord = "SKILL_CONFIG_INFO";           // key[0], name[3]

        // Full SKILL_CONFIG_INFO field layout (13 fields; validated against real data + CfgBinEditor tags).
        public int Skill_IdIndex = 0;         // SkillConfigID (the record key)
        public int Skill_TypeIndex = 1;       // SkillType: 1=Attack, 3=Technique, 5=Inspirit, 4=Soultimate
        public int Skill_EffectIdIndex = 2;   // EffectID (hash)
        public int Skill_NameHashIndex = 3;   // NameID -> skill_text NOUN
        public int Skill_DescIdIndex = 4;     // DescID -> skill_text TEXT
        public int Skill_GrowthIndex = 5;
        public int Skill_PowerIndex = 6;      // move power (0-1000)
        public int Skill_HitsIndex = 7;       // n° hits
        public int Skill_ElementIndex = 8;    // element = Attributes enum (0-9): 8=Strong Attack (physical), 9=Restoration
        public int Skill_SoulChargeIndex = 9; // SoultChargeSpeed
        public int Skill_BattleAnimIndex = 10;// BattleAnimation (hash)
        public int Skill_SoulRangeIndex = 11; // SoultimateRange
        public int Skill_AbilityIndex = 12;   // SkillAbility

        public string SkillConfigGroupBegin = "SKILL_CONFIG_INFO_LIST_BEG";
        public string SkillConfigGroupEnd = "SKILL_CONFIG_INFO_LIST_END";

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
        public int B_InspiritEvasionIndex = 1; // BaseInspiritEvasionRate: higher = harder to dodge Inspirit
        public int B_MoneyIndex = 3;
        public int B_ExpIndex = 4;
        public int B_Drop1Index = 5;
        public int B_Drop1RateIndex = 6;
        public int B_Drop2Index = 7;
        public int B_Drop2RateIndex = 8;

        // --- Boss editor / YW2 port ---
        // BOSS_PARTS_INFO lives in battle_chara_param (YW3). [5..12]=skill ids, [21]=phase config id.
        public string BossPartsRecord = "BOSS_PARTS_INFO";
        public string BossPartsGroupBegin = "BOSS_PARTS_INFO_LIST_BEG";
        public string BossPartsGroupEnd = "BOSS_PARTS_INFO_LIST_END";
        public int BP_ParamIndex = 0;
        public int BP_Cmd0Index = 5;      // 8 attack (skill) ids at [5]..[12]
        public int BP_CmdCount = 8;
        public int BP_PhaseIndex = 21;    // BOSS_PHASE_INFO id (battle_boss_config)
        // battle_boss_config: BOSS_PHASE_INFO (keyed by the id in BP_PhaseIndex) with per-phase children.
        public string BossConfigFilePrefix = "battle_boss_config";
        public string BossPhaseRecord = "BOSS_PHASE_INFO";
        public string BossPhaseChildRecord = "BOSS_PHASE_INFO_PAHSE";
        // battle_command (YW3: 12 fields — [3]=anim clip id, [5]=SkillConfigID it plays, [1]=type).
        public string BattleCommandFilePrefix = "battle_command";
        public string BattleCommandRecord = "BATTLE_COMMAND_INFO";
        public string BattleCommandGroupBegin = "BATTLE_COMMAND_INFO_BEGIN";
        public string BattleCommandGroupEndMarker = "BATTLE_COMMAND_STEFF_INFO_BEGIN"; // insert before this
        public int Cmd_IdIndex = 0;
        public int Cmd_TypeIndex = 1;
        public int Cmd_AnimIndex = 3;
        public int Cmd_SkillIndex = 5;
        // YW2 battle_command (19 fields): TextID at [1], SkillConfigID at [10].
        public int Yw2Cmd_TextIndex = 1;
        public int Yw2Cmd_SkillIndex = 10;
        // YW2 chara_param (53 fields): stats baseA at [2..6]; BOSS_PARTS commands are battle_command ids.
        public int Yw2P_HpIndex = 2, Yw2P_StrIndex = 3, Yw2P_SprIndex = 4, Yw2P_DefIndex = 5, Yw2P_SpdIndex = 6;
        public int Yw2P_MoneyIndex = 33, Yw2P_ExpIndex = 34;
        // YW2 skill_config (9 fields): TextID at [2], BasePower at [5], Element at [6].
        public int Yw2Skill_TextIndex = 2, Yw2Skill_PowerIndex = 5, Yw2Skill_ElementIndex = 6;
        // common_enc (encounter): a boss battle event "edy_<model>_NN" → CRC32 → ENCOUNT_TABLE id.
        public string EncountFilePrefix = "common_enc";
        public string EncountTableRecord = "ENCOUNT_TABLE";
        public string EncountTableGroupBegin = "ENCOUNT_TABLE_BEGIN";
        public string EncountTableGroupEnd = "ENCOUNT_TABLE_END";
        public string EncountCharaRecord = "ENCOUNT_CHARA";
        public string EncountCharaGroupBegin = "ENCOUNT_CHARA_BEGIN";
        public string EncountCharaGroupEnd = "ENCOUNT_CHARA_END";
        public int EncTable_IdIndex = 0;
        public int EncTable_Off1Index = 1;   // first ENCOUNT_CHARA index (slots [1]..[6])
        public int Enc_ParamIndex = 0;
        public int Enc_LevelIndex = 1;

        // --- Fusion / evolution recipes (combine_config — COMBINE_INFO): Base + Material → Result ---
        public string CombineConfigFilePrefix = "combine_config";
        public string CombineRecord = "COMBINE_INFO";
        public string CombineGroupBegin = "COMBINE_INFO_LIST_BEG";
        public string CombineGroupEnd = "COMBINE_INFO_LIST_END";
        public int Cmb_BaseIsItemIndex = 0;     // 0 = base is a yo-kai (ParamID), 1 = an item
        public int Cmb_BaseIdIndex = 1;         // yo-kai ParamHash or item ID
        public int Cmb_MaterialIsItemIndex = 2;
        public int Cmb_MaterialIdIndex = 3;
        public int Cmb_ResultIsItemIndex = 4;
        public int Cmb_ResultIdIndex = 5;
        public int Cmb_FlagIdIndex = 6;         // GlobalBitFlagID: story flag that must be set to unlock
        public int Cmb_TypeIndex = 7;           // FusionType (observed: 0,1,3,6)

        // --- Shops (per-shop shop_shp*.cfg.bin): item list + per-item valid conditions ---
        public string ShopFilePrefix = "shop_shp";
        public string ShopConfigRecord = "SHOP_CONFIG_INFO";
        public string ShopConfigBegin = "SHOP_CONFIG_INFO_BEGIN";
        public string ShopConfigEnd = "SHOP_CONFIG_INFO_END";
        public int ShopConfigCountIndex = 1;    // BEGIN[1] = item count ([0] = the shop's hash)
        public string ShopCondRecord = "SHOP_VALID_CONDITION";
        public string ShopCondBegin = "SHOP_VALID_CONDITION_BEGIN";
        public string ShopCondEnd = "SHOP_VALID_CONDITION_END";
        public int ShopCondCountIndex = 0;      // BEGIN[0] = condition count
        public int Shop_SlotIdIndex = 0;        // ShopSlotID (hash, unique per row)
        public int Shop_ItemIdIndex = 1;        // ItemID sold
        public int Shop_MaxStockIndex = 2;      // MaxLimitedStockCount
        public int Shop_HasStockIndex = 3;      // HasLimitedStock (0/1)
        public int Shop_CondStartIndex = 9;     // ShopValidConditionStartPos (index into SHOP_VALID_CONDITION)
        public int Shop_CondLenIndex = 10;      // ShopValidConditionLength
        public int Cond_PriceIndex = 0;         // ExplicitPrice (-1 = use the item's default price)
        public int Cond_CondIndex = 1;          // Cond (availability condition)
        // def_shoplist — master registry of shop IDs. Count is at BEGIN[0] (generic insert works).
        public string DefShoplistFilePrefix = "def_shoplist";
        public string ShopListRecord = "SHOP_LIST_INFO";
        public string ShopListBegin = "SHOP_LIST_BEGIN";
        public string ShopListEnd = "SHOP_LIST_END";
        public int ShopList_IdIndex = 0;        // ShopID (= Crc32.Standard(code))
        public int ShopList_FlagIndex = 1;

        // --- Items (drop names + the standalone item editor) ---
        public string ItemConfigFilePrefix = "item_config";
        public string ItemTextFilePrefix = "item_text";
        public string ItemIconFilePrefix = "item_icon"; // folder containing item_icon.xi atlas

        // item_config record types that share the common item layout (all editable).
        public string[] ItemRecords =
        {
            "ITEM_CONSUME", "ITEM_CREATURE", "ITEM_IMPORTANT", "ITEM_EQUIPMENT",
            "ITEM_HACKSLASH_BATTLE", "ITEM_HACKSLASH_EQUIPMENT", "ITEM_SOUL",
        };
        // Common field indices (identical across the record types above; validated against real data).
        public int Item_IdIndex = 0;
        public int Item_NameHashIndex = 1;       // NounTextID -> item_text NOUN
        public int Item_GlobalIconIndex = 5;     // the individual 64x64 icon file number -> item_<NNNN>.xi
        public int Item_InventorySortIndex = 2;
        public int Item_TypeIndex = 3;
        public int Item_CarryCapIndex = 6;
        public int Item_SellPriceIndex = 10;
        public int Item_ShopPriceIndex = 11;
        public int Item_IconPosXIndex = 12;
        public int Item_IconPosYIndex = 13;
        public int Item_DescHashIndex = 14;      // DescTextID -> item_text TEXT_INFO
        // item_text: names are NOUN_INFO (text at 5), descriptions are TEXT_INFO (text at 2).
        public int ItemNoun_TextIndex = 5;
        public int ItemText_TextIndex = 2;

        // --- Maps (map_config + system_text names) ---
        public string MapConfigFilePrefix = "map_config";
        public string SystemTextFilePrefix = "system_text";  // holds map names as TEXT_INFO (key -> text[2])
        public string MapRecord = "MAP_INFO";
        public string MapGroupBegin = "MAP_BEGIN";
        public string MapGroupEnd = "MAP_END";
        // MAP_INFO layout (12 fields, validated): [0]MapID=CRC32(folder), [1..8]Unk, [9]MapFolderName(String),
        // [10]ShowMapCard, [11]NounID=CRC32(folder) -> system_text TEXT_INFO.
        public int Map_IdIndex = 0;
        public int Map_FolderIndex = 9;
        public int Map_ShowCardIndex = 10;
        public int Map_NounIdIndex = 11;

        // chara_scale: CHARA_SCALE_INFO keyed by BaseHash at [0].
        public string ScaleRecord = "CHARA_SCALE_INFO";
        public string ScaleGroupBegin = "CHARA_SCALE_INFO_LIST_BEG";
        public string ScaleGroupEnd = "CHARA_SCALE_INFO_LIST_END";
        public int Scale_BaseHashIndex = 0;

        // Group markers (value[0] = child count) for delete + enable-BT/drops paths.
        public string HackslashGroupBegin = "HACKSLASH_CHARA_PARAM_INFO_LIST_BEG";
        public string HackslashGroupEnd = "HACKSLASH_CHARA_PARAM_INFO_LIST_END";
        public string BattleGroupBegin = "BATTLE_CHARA_PARAM_INFO_LIST_BEG";
        public string BattleGroupEnd = "BATTLE_CHARA_PARAM_INFO_LIST_END";

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
        public int AbilityHashIndex = 28;         // SkillID
        public int AttitudeIndex = 18;            // CharaRandomActType (Attitude)
        public int BefriendTextIndex = 36;        // Addmember&TradeTextID (befriend + trade dialogue text id)
        public int ItemSlotsIndex = 39;
        public int MoveCooldownIndex = 40;        // "Wait Time" between attacks

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
