using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lycoris.Formats;

namespace Lycoris.Yokai
{
    /// <summary>
    /// Loads the yo-kai parameter set from a folder of extracted .cfg.bin files (a YWML mod
    /// tree or ARC0 unpack) and resolves ParamID -&gt; name by chaining chara_param -&gt;
    /// chara_base -&gt; chara_text/chara_desc_text, like Albatross. Retains all four files so
    /// edits (stats, attack, name, description) and new entries can be written back.
    /// </summary>
    public sealed class YokaiDatabase
    {
        public YokaiSchema Schema { get; }
        public List<YokaiInfo> Yokai { get; } = new List<YokaiInfo>();

        public T2bFile ParamData { get; private set; }
        public T2bFile BaseData { get; private set; }
        public T2bFile TextData { get; private set; }
        public T2bFile DescData { get; private set; }
        public T2bFile ScaleData { get; private set; }
        public T2bFile SkillTextData { get; private set; }
        public T2bFile SkillDescTextData { get; private set; }
        public T2bFile AbilityData { get; private set; }
        public T2bFile AbilityTextData { get; private set; }
        public T2bFile SkillConfigData { get; private set; }
        public T2bFile HackslashData { get; private set; }           // editable
        public T2bFile BattleData { get; private set; }              // editable
        private T2bFile _hsTechnicData, _hsTechnicTextData, _hsAbilityData, _hsAbilityTextData, _itemConfigData, _itemTextData;
        public string HackslashFile { get; private set; }
        public string BattleFile { get; private set; }
        private string _hsTechnicFile, _hsTechnicTextFile, _hsAbilityFile, _hsAbilityTextFile, _itemConfigFile, _itemTextFile;
        private string _modFolder, _referenceFolder;

        public List<EnumEntry> TechnicOptions { get; private set; } = new List<EnumEntry>();
        public List<EnumEntry> BtAbilityOptions { get; private set; } = new List<EnumEntry>();
        public List<EnumEntry> ItemOptions { get; private set; } = new List<EnumEntry>();

        public struct SkillMove
        {
            public int Hash; public string Name; public int Power;
            public SkillMove(int h, string n, int p) { Hash = h; Name = n; Power = p; }
        }
        /// <summary>skill_config moves grouped by element (Attributes 0-9), sorted by power. For attack profiles.</summary>
        public Dictionary<int, List<SkillMove>> SkillsByElement { get; private set; } = new Dictionary<int, List<SkillMove>>();
        /// <summary>Blaster-T technic name -&gt; hash, for name-matched Blaster-T profile assignment.</summary>
        public Dictionary<string, int> TechnicByName { get; private set; } = new Dictionary<string, int>();

        public string ParamFile { get; private set; }
        public string BaseFile { get; private set; }
        public string TextFile { get; private set; }
        public string DescFile { get; private set; }
        public string ScaleFile { get; private set; }
        public string SkillTextFile { get; private set; }
        public string SkillDescTextFile { get; private set; }
        public string AbilityFile { get; private set; }
        public string AbilityTextFile { get; private set; }
        public string SkillConfigFile { get; private set; }
        public int NameTableCount { get; private set; }
        public int DescTableCount { get; private set; }
        public int SkillTableCount { get; private set; }

        /// <summary>skill_text key -&gt; name, for the move-name resolver.</summary>
        public Dictionary<int, string> SkillNames { get; private set; } = new Dictionary<int, string>();

        /// <summary>face_icon / medal_icon / item_icon folders searched for .xi icons (mod first, then reference).</summary>
        private readonly List<string> _faceIconDirs = new List<string>();
        private readonly List<string> _medalIconDirs = new List<string>();
        private readonly List<string> _itemIconDirs = new List<string>();
        public int IconCount { get; private set; }

        /// <summary>All editable items (for the standalone item editor).</summary>
        public List<ItemInfo> Items { get; } = new List<ItemInfo>();

        /// <summary>Set when an item is added or removed, so SaveItems writes even with no field edits.</summary>
        private bool _itemStructureDirty;

        /// <summary>All editable skills (for the standalone skill editor).</summary>
        public List<SkillInfo> Skills { get; } = new List<SkillInfo>();

        /// <summary>Set when a skill is added or removed, so SaveSkills writes even with no field edits.</summary>
        private bool _skillStructureDirty;

        /// <summary>Set when a skill_desc_text entry is added/removed/edited, so that file is (re)written.</summary>
        private bool _skillDescDirty;

        public YokaiDatabase(YokaiSchema schema = null)
        {
            Schema = schema ?? YokaiSchema.Yw3;
        }

        // ============================ Loading ============================

        /// <summary>Set of resolver files taken from the reference folder (mod lacked them). For the UI.</summary>
        public System.Collections.Generic.List<string> ResolverFromReference { get; } = new List<string>();

        /// <summary>
        /// Load a mod folder. Editable files (param/base/text/desc/scale) come only from the mod.
        /// Read-only name-resolver files (skill_config, skill_text, chara_ability[_text]) fall back to
        /// <paramref name="referenceFolder"/> (a full game extract) when the mod doesn't include them —
        /// this is the normal case, since a YWML mod only ships the files it edits.
        /// </summary>
        public void LoadFolder(string folder, string referenceFolder = null)
        {
            if (!Directory.Exists(folder))
                throw new DirectoryNotFoundException(folder);
            ResolverFromReference.Clear();
            _modFolder = folder;
            _referenceFolder = referenceFolder;

            // Editable files: mod folder only (SaveAll writes these back).
            ParamFile = FindNewest(folder, Schema.ParamFilePrefix);
            BaseFile = FindNewest(folder, Schema.BaseFilePrefix);
            TextFile = FindNewest(folder, Schema.TextFilePrefix);
            DescFile = FindNewest(folder, Schema.DescFilePrefix);
            ScaleFile = FindNewest(folder, Schema.ScaleFilePrefix);

            // Read-only resolvers: mod folder, else reference folder.
            SkillTextFile = FindResolver(folder, referenceFolder, Schema.SkillTextFilePrefix, "desc");
            SkillDescTextFile = FindResolver(folder, referenceFolder, Schema.SkillDescTextFilePrefix, null);
            AbilityTextFile = FindResolver(folder, referenceFolder, Schema.AbilityTextFilePrefix, null);
            AbilityFile = FindResolver(folder, referenceFolder, Schema.AbilityFilePrefix, "text");
            SkillConfigFile = FindResolver(folder, referenceFolder, Schema.SkillConfigFilePrefix, null);
            // Editable Blaster-T / drops files (mod preferred; reference = read-only display).
            HackslashFile = FindResolver(folder, referenceFolder, Schema.HackslashParamFilePrefix, null);
            BattleFile = FindResolver(folder, referenceFolder, Schema.BattleParamFilePrefix, null);
            // Blaster-T / item name resolvers (read-only).
            _hsTechnicFile = FindResolver(folder, referenceFolder, Schema.HackslashTechnicFilePrefix, "text");
            _hsTechnicTextFile = FindResolver(folder, referenceFolder, Schema.HackslashTechnicTextFilePrefix, null);
            _hsAbilityFile = FindResolver(folder, referenceFolder, Schema.HackslashAbilityFilePrefix, "text");
            _hsAbilityTextFile = FindResolver(folder, referenceFolder, Schema.HackslashAbilityTextFilePrefix, null);
            _itemConfigFile = FindResolver(folder, referenceFolder, Schema.ItemConfigFilePrefix, null);
            _itemTextFile = FindResolver(folder, referenceFolder, Schema.ItemTextFilePrefix, null);

            _faceIconDirs.Clear();
            _medalIconDirs.Clear();
            _itemIconDirs.Clear();
            AddIconDirs(folder);
            if (referenceFolder != null) AddIconDirs(referenceFolder);

            if (ParamFile == null)
                throw new FileNotFoundException(
                    $"No '{Schema.ParamFilePrefix}*.cfg.bin' found under {folder}.");

            LoadAll();
        }

        private void AddIconDirs(string root)
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(root, "face_icon", SearchOption.AllDirectories))
                    if (!_faceIconDirs.Contains(d)) _faceIconDirs.Add(d);
                foreach (var d in Directory.EnumerateDirectories(root, "medal_icon", SearchOption.AllDirectories))
                    if (!_medalIconDirs.Contains(d)) _medalIconDirs.Add(d);
                foreach (var d in Directory.EnumerateDirectories(root, "item_icon", SearchOption.AllDirectories))
                    if (!_itemIconDirs.Contains(d)) _itemIconDirs.Add(d);
            }
            catch { /* ignore */ }
        }

        /// <summary>Compute the icon base name and locate its .xi across the face_icon / medal_icon folders.</summary>
        private void ResolveIcon(YokaiInfo y)
        {
            if (!y.FileNamePrefix.HasValue || !y.FileNameNumber.HasValue || !y.FileNameVariant.HasValue) return;
            y.IconBaseName = IconNaming.GetFileModelText(y.FileNamePrefix.Value, y.FileNameNumber.Value, y.FileNameVariant.Value);
            if (y.IconBaseName == null) return;

            string fallback = IconNaming.GetFileModelText(y.FileNamePrefix.Value, y.FileNameNumber.Value, 0);
            y.IconFile = FindIcon(_faceIconDirs, y.IconBaseName, fallback, countIt: true);
            y.MedalIconFile = FindIcon(_medalIconDirs, y.IconBaseName, fallback, countIt: false);
        }

        private string FindIcon(List<string> dirs, string name, string fallback, bool countIt)
        {
            foreach (var n in new[] { name, fallback })
            {
                if (n == null) continue;
                foreach (var dir in dirs)
                {
                    string p = Path.Combine(dir, n + ".xi");
                    if (File.Exists(p)) { if (countIt) IconCount++; return p; }
                }
            }
            return null;
        }

        /// <summary>The first medal_icon folder inside the mod (write target), or null.</summary>
        public string ModMedalIconDir => _medalIconDirs.Count > 0 ? _medalIconDirs[0] : null;

        /// <summary>The medal atlas (face_icon/face_icon.xi) — mod preferred, else reference — or null.</summary>
        public string FaceAtlasFile =>
            _faceIconDirs.Select(d => Path.Combine(d, "face_icon.xi")).FirstOrDefault(File.Exists);

        /// <summary>The mod's own face_icon.xi atlas, if it ships one (it already contains the vanilla
        /// medals). Preferred as the working atlas so the mod's medals are shown/edited, not the reference.</summary>
        public string ModFaceAtlasFile =>
            _faceIconDirs.Where(IsUnderMod).Select(d => Path.Combine(d, "face_icon.xi")).FirstOrDefault(File.Exists);

        /// <summary>The item icon atlas (item_icon/item_icon.xi) — mod preferred, else reference — or null.</summary>
        public string ItemAtlasFile =>
            _itemIconDirs.Select(d => Path.Combine(d, "item_icon.xi")).FirstOrDefault(File.Exists);
        public string ModItemAtlasFile =>
            _itemIconDirs.Where(IsUnderMod).Select(d => Path.Combine(d, "item_icon.xi")).FirstOrDefault(File.Exists);

        /// <summary>
        /// Where a modified copy of <paramref name="resolvedPath"/> should be written inside the mod.
        /// If the file already lives in the mod, it is overwritten; if it was resolved from the reference
        /// game folder, its relative path is mirrored under the mod (creating folders as needed) — the
        /// correct YWML behaviour, and reference files are never touched.
        /// </summary>
        public string MirrorToMod(string resolvedPath)
        {
            if (resolvedPath == null || _modFolder == null) return null;
            string target;
            if (IsUnderMod(resolvedPath))
                target = resolvedPath;
            else if (_referenceFolder != null &&
                     resolvedPath.StartsWith(_referenceFolder, StringComparison.OrdinalIgnoreCase))
                target = Path.Combine(_modFolder, resolvedPath.Substring(_referenceFolder.Length).TrimStart('\\', '/'));
            else
                target = Path.Combine(_modFolder, Path.GetFileName(resolvedPath));
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            return target;
        }

        /// <summary>The first face_icon folder inside the mod (write target for replaced icons), or null.</summary>
        public string ModFaceIconDir => _faceIconDirs.Count > 0 ? _faceIconDirs[0] : null;

        /// <summary>True if a resolved file lives inside the opened mod folder (so it may be written).</summary>
        private bool IsUnderMod(string path) =>
            path != null && _modFolder != null &&
            path.StartsWith(_modFolder, StringComparison.OrdinalIgnoreCase);

        private string FindResolver(string modFolder, string referenceFolder, string prefix, string exclude)
        {
            string inMod = FindNewest(modFolder, prefix, exclude);
            if (inMod != null) return inMod;
            if (referenceFolder != null && Directory.Exists(referenceFolder))
            {
                string inRef = FindNewest(referenceFolder, prefix, exclude);
                if (inRef != null) ResolverFromReference.Add(Path.GetFileName(inRef));
                return inRef;
            }
            return null;
        }

        /// <summary>Load only chara_param (ParamIDs only, no name resolution).</summary>
        public void LoadParamFile(string paramFilePath)
        {
            ParamFile = paramFilePath;
            BaseFile = TextFile = DescFile = ScaleFile = SkillTextFile = SkillDescTextFile = null;
            AbilityFile = AbilityTextFile = SkillConfigFile = null;
            HackslashFile = BattleFile = _hsTechnicFile = _hsTechnicTextFile = null;
            _hsAbilityFile = _hsAbilityTextFile = _itemConfigFile = _itemTextFile = null;
            _modFolder = System.IO.Path.GetDirectoryName(paramFilePath);
            LoadAll();
        }

        private void LoadAll()
        {
            Yokai.Clear();
            IconCount = 0;
            ParamData = T2bReader.ReadFile(ParamFile);
            BaseData = BaseFile != null ? T2bReader.ReadFile(BaseFile) : null;
            TextData = TextFile != null ? T2bReader.ReadFile(TextFile) : null;
            DescData = DescFile != null ? T2bReader.ReadFile(DescFile) : null;
            ScaleData = ScaleFile != null ? T2bReader.ReadFile(ScaleFile) : null;
            SkillTextData = SkillTextFile != null ? T2bReader.ReadFile(SkillTextFile) : null;
            SkillDescTextData = SkillDescTextFile != null ? T2bReader.ReadFile(SkillDescTextFile) : null;
            AbilityData = AbilityFile != null ? T2bReader.ReadFile(AbilityFile) : null;
            AbilityTextData = AbilityTextFile != null ? T2bReader.ReadFile(AbilityTextFile) : null;
            SkillConfigData = SkillConfigFile != null ? T2bReader.ReadFile(SkillConfigFile) : null;
            HackslashData = HackslashFile != null ? T2bReader.ReadFile(HackslashFile) : null;
            BattleData = BattleFile != null ? T2bReader.ReadFile(BattleFile) : null;
            _hsTechnicData = _hsTechnicFile != null ? T2bReader.ReadFile(_hsTechnicFile) : null;
            _hsTechnicTextData = _hsTechnicTextFile != null ? T2bReader.ReadFile(_hsTechnicTextFile) : null;
            _hsAbilityData = _hsAbilityFile != null ? T2bReader.ReadFile(_hsAbilityFile) : null;
            _hsAbilityTextData = _hsAbilityTextFile != null ? T2bReader.ReadFile(_hsAbilityTextFile) : null;
            _itemConfigData = _itemConfigFile != null ? T2bReader.ReadFile(_itemConfigFile) : null;
            _itemTextData = _itemTextFile != null ? T2bReader.ReadFile(_itemTextFile) : null;

            // Blaster-T technic/ability name maps + item name map (config[0]=key -> nameHash -> text).
            var technicNames = BuildConfigMap(_hsTechnicData, Schema.HackslashTechnicRecord, BuildNounValueMap(_hsTechnicTextData), out var techOpts);
            var btAbilityNames = BuildConfigMap(_hsAbilityData, Schema.AbilityConfigRecord, BuildNounValueMap(_hsAbilityTextData), out var btAbilOpts);
            var itemNames = BuildConfigMap(_itemConfigData, null, BuildNounValueMap(_itemTextData), out var itemOpts);
            TechnicOptions = techOpts; BtAbilityOptions = btAbilOpts; ItemOptions = itemOpts;

            var hackslashByParam = BuildKeyMap(HackslashData, Schema.HackslashRecord);
            var battleByParam = BuildKeyMap(BattleData, Schema.BattleRecord);

            var baseByHash = BuildBaseMap(BaseData);
            var nounByKey = BuildTextEntryMap(TextData);
            var textByKey = BuildTextEntryMap(DescData);
            var scaleByBase = BuildScaleMap(ScaleData);
            SkillNames = BuildTextValueMap(SkillTextData);
            _moveNames = BuildMoveNames();
            SkillsByElement = BuildSkillPools();
            TechnicByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in TechnicOptions) if (!TechnicByName.ContainsKey(o.Name)) TechnicByName[o.Name] = o.Key;
            NameTableCount = nounByKey.Count;
            DescTableCount = textByKey.Count;
            SkillTableCount = SkillNames.Count;

            var evolveList = ParamData.Records(Schema.EvolveRecord).ToList();

            foreach (var e in ParamData.Records(Schema.ParamRecord))
            {
                var y = new YokaiInfo
                {
                    SourceEntry = e,
                    ParamHash = e.GetInt(Schema.ParamHashIndex) ?? 0,
                    BaseHash = e.GetInt(Schema.Param_BaseHashIndex) ?? 0,
                    Show = (e.GetInt(Schema.ShowInMedaliumIndex) ?? 0) != 0,
                    Medal = e.GetInt(Schema.MedaliumOffsetIndex),
                    Resistance = e.GetInt(Schema.ResistanceIndex),
                    Weakness = e.GetInt(Schema.WeaknessIndex),
                    MinHp = e.GetInt(Schema.MinHpIndex),
                    MaxHp = e.GetInt(Schema.MaxHpIndex),
                    MinStrength = e.GetInt(Schema.MinStrengthIndex),
                    MaxStrength = e.GetInt(Schema.MaxStrengthIndex),
                    MinSpirit = e.GetInt(Schema.MinSpiritIndex),
                    MaxSpirit = e.GetInt(Schema.MaxSpiritIndex),
                    MinDefense = e.GetInt(Schema.MinDefenseIndex),
                    MaxDefense = e.GetInt(Schema.MaxDefenseIndex),
                    MinSpeed = e.GetInt(Schema.MinSpeedIndex),
                    MaxSpeed = e.GetInt(Schema.MaxSpeedIndex),
                    AttackHash = e.GetInt(Schema.AttackHashIndex),
                    AttackPct = e.GetInt(Schema.AttackPctIndex),
                    TechniqueHash = e.GetInt(Schema.TechniqueHashIndex),
                    TechniquePct = e.GetInt(Schema.TechniquePctIndex),
                    InspiritHash = e.GetInt(Schema.InspiritHashIndex),
                    InspiritPct = e.GetInt(Schema.InspiritPctIndex),
                    GuardHash = e.GetInt(Schema.GuardHashIndex),
                    GuardPct = e.GetInt(Schema.GuardPctIndex),
                    SoultimateHash = e.GetInt(Schema.SoultimateHashIndex),
                    AbilityHash = e.GetInt(Schema.AbilityHashIndex),
                    EvolveOffset = e.GetInt(Schema.EvolveOffsetIndex),
                };

                if (y.EvolveOffset.HasValue && y.EvolveOffset.Value >= 0 && y.EvolveOffset.Value < evolveList.Count)
                {
                    var ev = evolveList[y.EvolveOffset.Value];
                    y.EvolveEntry = ev;
                    y.EvolveTargetHash = ev.GetInt(Schema.Evolve_TargetIndex);
                    y.EvolveLevel = ev.GetInt(Schema.Evolve_LevelIndex);
                    y.OriginalEvolveTarget = y.EvolveTargetHash;
                    y.OriginalEvolveLevel = y.EvolveLevel;
                }

                if (baseByHash.TryGetValue(y.BaseHash, out T2bEntry baseEntry))
                {
                    y.BaseEntry = baseEntry;
                    y.NameHash = baseEntry.GetInt(Schema.Base_NameHashIndex) ?? 0;
                    y.Rank = baseEntry.GetInt(Schema.Base_RankIndex);
                    y.Tribe = baseEntry.GetInt(Schema.Base_TribeIndex);
                    y.DescriptionHash = baseEntry.GetInt(Schema.Base_DescriptionHashIndex);
                    y.FileNamePrefix = baseEntry.GetInt(Schema.Base_FileNamePrefixIndex);
                    y.FileNameNumber = baseEntry.GetInt(Schema.Base_FileNameNumberIndex);
                    y.FileNameVariant = baseEntry.GetInt(Schema.Base_FileNameVariantIndex);
                    y.MedalPosX = baseEntry.GetInt(Schema.Base_MedalPosXIndex);
                    y.MedalPosY = baseEntry.GetInt(Schema.Base_MedalPosYIndex);
                    y.FavoriteFood = baseEntry.GetInt(Schema.Base_FavoriteFoodIndex);
                    y.HatedFood = baseEntry.GetInt(Schema.Base_HatedFoodIndex);
                    y.Role = baseEntry.GetInt(Schema.Base_RoleIndex);
                    y.IsRare = (baseEntry.GetInt(Schema.Base_IsRareIndex) ?? 0) != 0;
                    y.IsLegend = (baseEntry.GetInt(Schema.Base_IsLegendIndex) ?? 0) != 0;
                    y.IsPionner = (baseEntry.GetInt(Schema.Base_IsPionnerIndex) ?? 0) != 0;
                    y.IsCommandant = (baseEntry.GetInt(Schema.Base_IsCommandantIndex) ?? 0) != 0;
                    y.IsClassic = (baseEntry.GetInt(Schema.Base_IsClassicIndex) ?? 0) != 0;
                    y.IsMerican = (baseEntry.GetInt(Schema.Base_IsMericanIndex) ?? 0) != 0;
                    y.IsDeva = (baseEntry.GetInt(Schema.Base_IsDevaIndex) ?? 0) != 0;
                    y.IsMystery = (baseEntry.GetInt(Schema.Base_IsMysteryIndex) ?? 0) != 0;
                    y.IsTreasure = (baseEntry.GetInt(Schema.Base_IsTreasureIndex) ?? 0) != 0;
                    foreach (var kv in y.BaseFieldValues(Schema)) y.BaseOriginal[kv.Key] = baseEntry.GetInt(kv.Key);
                    ResolveIcon(y);

                    if (nounByKey.TryGetValue(y.NameHash, out T2bEntry ne))
                    {
                        y.NameEntry = ne;
                        y.Name = ne.FirstText();
                    }
                    if (y.DescriptionHash.HasValue && textByKey.TryGetValue(y.DescriptionHash.Value, out T2bEntry de))
                    {
                        y.DescEntry = de;
                        y.Description = de.FirstText();
                    }
                    if (scaleByBase.TryGetValue(y.BaseHash, out T2bEntry se))
                    {
                        y.ScaleEntry = se;
                        for (int i = 1; i <= 7; i++)
                        {
                            float? v = se.GetFloat(i);
                            y.InitScale(i, v.HasValue ? (double?)v.Value : null);
                        }
                    }
                }

                ResolveMoves(y);

                if (hackslashByParam.TryGetValue(y.ParamHash, out T2bEntry hsE))
                {
                    y.HackslashEntry = hsE;
                    y.BtAbilityHash = hsE.GetInt(Schema.Hs_AbilityIndex);
                    y.BtSoultimateHash = hsE.GetInt(Schema.Hs_SoultimateIndex);
                    y.BtAttackAHash = hsE.GetInt(Schema.Hs_AttackAIndex);
                    y.BtAttackYHash = hsE.GetInt(Schema.Hs_AttackYIndex);
                    y.BtAttackXHash = hsE.GetInt(Schema.Hs_AttackXIndex);
                }
                if (battleByParam.TryGetValue(y.ParamHash, out T2bEntry bE))
                {
                    y.BattleEntry = bE;
                    y.Money = bE.GetInt(Schema.B_MoneyIndex);
                    y.Experience = bE.GetInt(Schema.B_ExpIndex);
                    y.Drop1Hash = bE.GetInt(Schema.B_Drop1Index);
                    y.Drop1Rate = bE.GetInt(Schema.B_Drop1RateIndex);
                    y.Drop2Hash = bE.GetInt(Schema.B_Drop2Index);
                    y.Drop2Rate = bE.GetInt(Schema.B_Drop2RateIndex);
                }

                y.OriginalName = y.Name;
                y.OriginalDescription = y.Description;
                y.OriginalRank = y.Rank;
                y.OriginalTribe = y.Tribe;
                y.OriginalEvolveOffset = y.EvolveOffset;
                y.IsDirty = false; // setters above flipped it; a freshly loaded row is clean
                Yokai.Add(y);
            }

            // Resolve evolution target names + build the yo-kai option list (for dropdowns).
            var nameByParam = new Dictionary<int, string>();
            foreach (var y in Yokai) if (!nameByParam.ContainsKey(y.ParamHash)) nameByParam[y.ParamHash] = y.DisplayName;
            foreach (var y in Yokai)
                if (y.EvolveTargetHash.HasValue && nameByParam.TryGetValue(y.EvolveTargetHash.Value, out string tn))
                    y.EvolvesToName = tn;
            YokaiOptions = Yokai.Select(y => new EnumEntry(y.ParamHash, y.DisplayName))
                                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();

            LoadItems();
            LoadSkills();
            BuildSkillSlotOptions();
        }

        /// <summary>Named skill options bucketed by SkillType, for the per-slot move dropdowns in the yo-kai editor.</summary>
        public List<EnumEntry> AttackOptions { get; private set; } = new List<EnumEntry>();
        public List<EnumEntry> TechniqueOptions { get; private set; } = new List<EnumEntry>();
        public List<EnumEntry> InspiritOptions { get; private set; } = new List<EnumEntry>();
        public List<EnumEntry> GuardOptions { get; private set; } = new List<EnumEntry>();
        public List<EnumEntry> SoultimateSkillOptions { get; private set; } = new List<EnumEntry>();

        /// <summary>Bucket the named skills by type (1=Attack, 3=Technique, 5=Inspirit, 4=Soultimate, 0=Guard).</summary>
        private void BuildSkillSlotOptions()
        {
            List<EnumEntry> ForType(int t) => Skills
                .Where(s => s.SkillType == t && !string.IsNullOrWhiteSpace(s.Name))
                .GroupBy(s => s.SkillConfigID).Select(g => g.First())
                .Select(s => new EnumEntry(s.SkillConfigID, s.Name))
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
            AttackOptions = ForType(1);
            TechniqueOptions = ForType(3);
            InspiritOptions = ForType(5);
            SoultimateSkillOptions = ForType(4);
            GuardOptions = ForType(0);
        }

        private void LoadSkills()
        {
            Skills.Clear();
            _skillStructureDirty = false;
            _skillDescDirty = false;
            if (SkillConfigData == null) return;

            var nounByKey = new Dictionary<int, T2bEntry>();
            var textByKey = new Dictionary<int, T2bEntry>();
            if (SkillTextData != null)
                foreach (var e in SkillTextData.Records(Schema.NounRecord))
                { int? k = e.FirstIntKey(); if (k.HasValue && !nounByKey.ContainsKey(k.Value)) nounByKey[k.Value] = e; }
            if (SkillDescTextData != null)
                foreach (var e in SkillDescTextData.Records(Schema.DescRecord)) // descriptions live in skill_desc_text
                { int? k = e.FirstIntKey(); if (k.HasValue && !textByKey.ContainsKey(k.Value)) textByKey[k.Value] = e; }

            foreach (var e in SkillConfigData.Records(Schema.SkillConfigRecord))
            {
                if (e.Values.Count <= Schema.Skill_AbilityIndex) continue;
                var s = new SkillInfo
                {
                    Entry = e,
                    SkillConfigID = e.GetInt(Schema.Skill_IdIndex) ?? 0,
                    SkillType = e.GetInt(Schema.Skill_TypeIndex),
                    EffectID = e.GetInt(Schema.Skill_EffectIdIndex),
                    NameID = e.GetInt(Schema.Skill_NameHashIndex) ?? 0,
                    DescID = e.GetInt(Schema.Skill_DescIdIndex) ?? 0,
                    SkillGrowth = e.GetInt(Schema.Skill_GrowthIndex),
                    Power = e.GetInt(Schema.Skill_PowerIndex),
                    Hits = e.GetInt(Schema.Skill_HitsIndex),
                    Element = e.GetInt(Schema.Skill_ElementIndex),
                    SoultChargeSpeed = e.GetInt(Schema.Skill_SoulChargeIndex),
                    BattleAnimation = e.GetInt(Schema.Skill_BattleAnimIndex),
                    SoultimateRange = e.GetInt(Schema.Skill_SoulRangeIndex),
                    SkillAbility = e.GetInt(Schema.Skill_AbilityIndex),
                };
                if (nounByKey.TryGetValue(s.NameID, out T2bEntry ne)) { s.NameEntry = ne; s.Name = ne.FirstText(); }
                if (textByKey.TryGetValue(s.DescID, out T2bEntry de)) { s.DescEntry = de; s.Description = de.FirstText(); }
                s.OriginalName = s.Name;
                s.OriginalDescription = s.Description;
                s.IsDirty = false;
                Skills.Add(s);
            }
        }

        /// <summary>Apply skill edits and write skill_config (+ skill_text names + skill_desc_text) into the mod.</summary>
        public int SaveSkills()
        {
            if (SkillConfigData == null ||
                (!_skillStructureDirty && !_skillDescDirty && !Skills.Any(s => s.IsDirty || s.NameChanged || s.DescriptionChanged)))
                return 0;
            int n = 0;
            foreach (var s in Skills.Where(x => x.Entry != null))
            {
                n += SetInt(s.Entry, Schema.Skill_TypeIndex, s.SkillType);
                n += SetInt(s.Entry, Schema.Skill_EffectIdIndex, s.EffectID);
                n += SetInt(s.Entry, Schema.Skill_DescIdIndex, s.DescID);
                n += SetInt(s.Entry, Schema.Skill_GrowthIndex, s.SkillGrowth);
                n += SetInt(s.Entry, Schema.Skill_PowerIndex, s.Power);
                n += SetInt(s.Entry, Schema.Skill_HitsIndex, s.Hits);
                n += SetInt(s.Entry, Schema.Skill_ElementIndex, s.Element);
                n += SetInt(s.Entry, Schema.Skill_SoulChargeIndex, s.SoultChargeSpeed);
                n += SetInt(s.Entry, Schema.Skill_BattleAnimIndex, s.BattleAnimation);
                n += SetInt(s.Entry, Schema.Skill_SoulRangeIndex, s.SoultimateRange);
                n += SetInt(s.Entry, Schema.Skill_AbilityIndex, s.SkillAbility);
            }
            bool textDirty = _skillStructureDirty;
            foreach (var s in Skills.Where(x => x.NameEntry != null && x.NameChanged))
            { n += SetText(s.NameEntry, Schema.NounTextIndex, s.Name); textDirty = true; }

            // Descriptions live in skill_desc_text (TEXT_INFO). Edit the existing entry, or create one
            // (with a fresh DescID) for a skill that had none.
            bool descDirty = _skillDescDirty;
            foreach (var s in Skills.Where(x => x.DescriptionChanged))
            {
                if (s.DescEntry != null)
                { n += SetText(s.DescEntry, Schema.DescTextIndex, s.Description); descDirty = true; }
                else if (!string.IsNullOrEmpty(s.Description) && SkillDescTextData != null)
                {
                    var de = MakeDescEntry(s.Name, s.Description, out int descId);
                    if (de != null)
                    {
                        s.DescEntry = de; s.DescID = descId;
                        n += SetInt(s.Entry, Schema.Skill_DescIdIndex, descId);
                        descDirty = true;
                    }
                }
            }

            if (!IsUnderMod(SkillConfigFile)) SkillConfigFile = MirrorToMod(SkillConfigFile);
            T2bWriter.WriteFile(SkillConfigData, SkillConfigFile);
            if (SkillTextData != null && textDirty)
            {
                if (!IsUnderMod(SkillTextFile)) SkillTextFile = MirrorToMod(SkillTextFile);
                T2bWriter.WriteFile(SkillTextData, SkillTextFile);
            }
            if (SkillDescTextData != null && descDirty)
            {
                if (!IsUnderMod(SkillDescTextFile)) SkillDescTextFile = MirrorToMod(SkillDescTextFile);
                T2bWriter.WriteFile(SkillDescTextData, SkillDescTextFile);
            }
            foreach (var s in Skills) { s.IsDirty = false; s.OriginalName = s.Name; s.OriginalDescription = s.Description; }
            if ((_skillStructureDirty || _skillDescDirty) && n == 0) n = 1;
            _skillStructureDirty = false;
            _skillDescDirty = false;
            return n;
        }

        /// <summary>Clone a TEXT_INFO template into skill_desc_text with a fresh key + the given text.</summary>
        private T2bEntry MakeDescEntry(string forName, string text, out int descId)
        {
            descId = 0;
            var tpl = SkillDescTextData?.Records(Schema.DescRecord).FirstOrDefault();
            if (tpl == null) return null;
            descId = UniqueHash("lycoris_skill_" + Sanitize(forName) + "_desc",
                ExistingFirstKeys(SkillDescTextData.Records(Schema.DescRecord)));
            var de = tpl.Clone();
            SetIntForce(de, Schema.DescKeyIndex, descId);
            SetText(de, Schema.DescTextIndex, text ?? "");
            InsertIntoGroup(SkillDescTextData, Schema.DescGroupBegin, Schema.DescGroupEnd, de);
            _skillDescDirty = true;
            return de;
        }

        /// <summary>
        /// Create a new skill in skill_config (plus its name in skill_text NOUN, and a description in
        /// skill_desc_text if given). Clones a template record and generates collision-free IDs. Persisted
        /// on the next <see cref="SaveSkills"/>. Returns the created <see cref="SkillInfo"/>.
        /// </summary>
        public SkillInfo AddSkill(string name, int skillType)
        {
            var tpl = SkillConfigData?.Records(Schema.SkillConfigRecord).FirstOrDefault();
            if (tpl == null) throw new InvalidOperationException("Aucun skill modèle dans skill_config.");
            return CreateSkill(tpl, name, skillType, null, applyDefaults: true);
        }

        /// <summary>Duplicate an existing skill: same fields (incl. description), a new name/ID pair.</summary>
        public SkillInfo DuplicateSkill(SkillInfo src)
        {
            if (src?.Entry == null) throw new InvalidOperationException("Aucun skill à dupliquer.");
            string name = (string.IsNullOrEmpty(src.Name) ? src.SkillIdHex : src.Name) + " (copie)";
            return CreateSkill(src.Entry, name, src.SkillType, src.Description, applyDefaults: false);
        }

        /// <summary>Shared record-builder for AddSkill/DuplicateSkill. Clones <paramref name="template"/>.</summary>
        private SkillInfo CreateSkill(T2bEntry template, string name, int? skillType, string description, bool applyDefaults)
        {
            if (SkillConfigData == null) throw new InvalidOperationException("skill_config non chargé.");

            string code = "lycoris_skill_" + Sanitize(name);
            int id = UniqueHash(code, ExistingKeys(SkillConfigData.Records(Schema.SkillConfigRecord), Schema.Skill_IdIndex));
            int nameHash = UniqueHash(code + "_name",
                SkillTextData != null ? ExistingFirstKeys(SkillTextData.Records(Schema.NounRecord)) : new HashSet<int>());

            var rec = template.Clone();
            SetIntForce(rec, Schema.Skill_IdIndex, id);
            SetIntForce(rec, Schema.Skill_NameHashIndex, nameHash);
            if (skillType.HasValue) SetIntForce(rec, Schema.Skill_TypeIndex, skillType.Value);
            if (applyDefaults)
            {
                SetIntForce(rec, Schema.Skill_PowerIndex, 30);
                SetIntForce(rec, Schema.Skill_HitsIndex, 1);
            }

            // Description entry (skill_desc_text), only when there is text to store.
            int descId = 0;
            T2bEntry descEntry = null;
            if (!string.IsNullOrEmpty(description))
                descEntry = MakeDescEntry(name, description, out descId);
            SetIntForce(rec, Schema.Skill_DescIdIndex, descId);

            InsertIntoGroup(SkillConfigData, Schema.SkillConfigGroupBegin, Schema.SkillConfigGroupEnd, rec);

            // Name entry (skill_text NOUN).
            T2bEntry nounEntry = null;
            var nounTpl = SkillTextData?.Records(Schema.NounRecord).FirstOrDefault();
            if (nounTpl != null)
            {
                nounEntry = nounTpl.Clone();
                SetIntForce(nounEntry, Schema.NounKeyIndex, nameHash);
                SetText(nounEntry, Schema.NounTextIndex, name ?? "");
                InsertIntoGroup(SkillTextData, Schema.NounGroupBegin, Schema.NounGroupEnd, nounEntry);
            }

            var s = new SkillInfo
            {
                Entry = rec,
                SkillConfigID = id,
                NameID = nameHash,
                DescID = descId,
                SkillType = rec.GetInt(Schema.Skill_TypeIndex),
                EffectID = rec.GetInt(Schema.Skill_EffectIdIndex),
                SkillGrowth = rec.GetInt(Schema.Skill_GrowthIndex),
                Power = rec.GetInt(Schema.Skill_PowerIndex),
                Hits = rec.GetInt(Schema.Skill_HitsIndex),
                Element = rec.GetInt(Schema.Skill_ElementIndex),
                SoultChargeSpeed = rec.GetInt(Schema.Skill_SoulChargeIndex),
                BattleAnimation = rec.GetInt(Schema.Skill_BattleAnimIndex),
                SoultimateRange = rec.GetInt(Schema.Skill_SoulRangeIndex),
                SkillAbility = rec.GetInt(Schema.Skill_AbilityIndex),
                NameEntry = nounEntry,
                DescEntry = descEntry,
                Name = name,
                Description = description ?? "",
                OriginalName = null,          // force the name/description to be written on save
                OriginalDescription = null,
                IsDirty = true,
            };
            Skills.Add(s);
            _skillStructureDirty = true;
            return s;
        }

        /// <summary>
        /// Delete a skill: removes its skill_config record, and its skill_text name / skill_desc_text
        /// description entries only when no remaining skill still references them. Persisted on next SaveSkills.
        /// </summary>
        public void RemoveSkill(SkillInfo s)
        {
            if (s?.Entry == null || SkillConfigData == null) return;

            RemoveEntry(SkillConfigData, s.Entry, Schema.SkillConfigGroupBegin);
            Skills.Remove(s);

            if (SkillTextData != null && s.NameEntry != null && Skills.All(o => o.NameID != s.NameID))
                RemoveEntry(SkillTextData, s.NameEntry, Schema.NounGroupBegin);
            if (SkillDescTextData != null && s.DescEntry != null && Skills.All(o => o.DescID != s.DescID))
            { RemoveEntry(SkillDescTextData, s.DescEntry, Schema.DescGroupBegin); _skillDescDirty = true; }
            _skillStructureDirty = true;
        }

        private void LoadItems()
        {
            Items.Clear();
            _itemStructureDirty = false;
            if (_itemConfigData == null) return;

            var nounByKey = new Dictionary<int, T2bEntry>();
            var textByKey = new Dictionary<int, T2bEntry>();
            if (_itemTextData != null)
            {
                foreach (var e in _itemTextData.Records(Schema.NounRecord))
                { int? k = e.FirstIntKey(); if (k.HasValue && !nounByKey.ContainsKey(k.Value)) nounByKey[k.Value] = e; }
                foreach (var e in _itemTextData.Records(Schema.DescRecord))
                { int? k = e.FirstIntKey(); if (k.HasValue && !textByKey.ContainsKey(k.Value)) textByKey[k.Value] = e; }
            }

            foreach (var recName in Schema.ItemRecords)
                foreach (var e in _itemConfigData.Records(recName))
                {
                    if (e.Values.Count <= Schema.Item_DescHashIndex) continue;
                    var it = new ItemInfo
                    {
                        Entry = e,
                        RecordType = recName,
                        ItemId = e.GetInt(Schema.Item_IdIndex) ?? 0,
                        NounTextID = e.GetInt(Schema.Item_NameHashIndex) ?? 0,
                        DescTextID = e.GetInt(Schema.Item_DescHashIndex) ?? 0,
                        InventorySort = e.GetInt(Schema.Item_InventorySortIndex),
                        ItemType = e.GetInt(Schema.Item_TypeIndex),
                        CarryCap = e.GetInt(Schema.Item_CarryCapIndex),
                        SellPrice = e.GetInt(Schema.Item_SellPriceIndex),
                        ShopPrice = e.GetInt(Schema.Item_ShopPriceIndex),
                        IconPosX = e.GetInt(Schema.Item_IconPosXIndex),
                        IconPosY = e.GetInt(Schema.Item_IconPosYIndex),
                    };
                    if (nounByKey.TryGetValue(it.NounTextID, out T2bEntry ne)) { it.NameEntry = ne; it.Name = ne.FirstText(); }
                    if (textByKey.TryGetValue(it.DescTextID, out T2bEntry de)) { it.DescEntry = de; it.Description = de.FirstText(); }
                    it.OriginalName = it.Name;
                    it.OriginalDescription = it.Description;
                    it.IsDirty = false;
                    Items.Add(it);
                }

            Items.Sort((a, b) => (a.InventorySort ?? 0).CompareTo(b.InventorySort ?? 0));
        }

        /// <summary>Apply item edits and write item_config + item_text into the mod (only if any changed).</summary>
        public int SaveItems()
        {
            if (_itemConfigData == null ||
                (!_itemStructureDirty && !Items.Any(i => i.IsDirty || i.NameChanged || i.DescriptionChanged)))
                return 0;
            int n = 0;
            foreach (var it in Items.Where(i => i.Entry != null))
            {
                n += SetInt(it.Entry, Schema.Item_InventorySortIndex, it.InventorySort);
                n += SetInt(it.Entry, Schema.Item_TypeIndex, it.ItemType);
                n += SetInt(it.Entry, Schema.Item_CarryCapIndex, it.CarryCap);
                n += SetInt(it.Entry, Schema.Item_SellPriceIndex, it.SellPrice);
                n += SetInt(it.Entry, Schema.Item_ShopPriceIndex, it.ShopPrice);
                n += SetInt(it.Entry, Schema.Item_IconPosXIndex, it.IconPosX);
                n += SetInt(it.Entry, Schema.Item_IconPosYIndex, it.IconPosY);
            }
            foreach (var it in Items.Where(i => i.NameEntry != null && i.NameChanged))
                n += SetText(it.NameEntry, Schema.ItemNoun_TextIndex, it.Name);
            foreach (var it in Items.Where(i => i.DescEntry != null && i.DescriptionChanged))
                n += SetText(it.DescEntry, Schema.ItemText_TextIndex, it.Description);

            if (!IsUnderMod(_itemConfigFile)) _itemConfigFile = MirrorToMod(_itemConfigFile);
            T2bWriter.WriteFile(_itemConfigData, _itemConfigFile);
            if (_itemTextData != null)
            {
                if (!IsUnderMod(_itemTextFile)) _itemTextFile = MirrorToMod(_itemTextFile);
                T2bWriter.WriteFile(_itemTextData, _itemTextFile);
            }
            foreach (var it in Items) { it.IsDirty = false; it.OriginalName = it.Name; it.OriginalDescription = it.Description; }
            if (_itemStructureDirty && n == 0) n = 1; // report the add/remove even when no field changed
            _itemStructureDirty = false;
            return n;
        }

        /// <summary>
        /// Create a new item in item_config (plus its name/description in item_text) under the given
        /// record type. Clones a real record of that type as a valid template and generates collision-free
        /// IDs derived from <paramref name="name"/>. Persisted on the next <see cref="SaveItems"/>.
        /// Returns the created <see cref="ItemInfo"/> (already added to <see cref="Items"/>).
        /// </summary>
        public ItemInfo AddItem(string name, string recordType)
        {
            if (_itemConfigData == null)
                throw new InvalidOperationException("item_config non chargé.");
            if (string.IsNullOrEmpty(recordType) || !Schema.ItemRecords.Contains(recordType))
                recordType = Schema.ItemRecords[0];

            // Template: a real record of the requested type, else any item record.
            string useType = recordType;
            var tpl = _itemConfigData.Records(recordType).FirstOrDefault();
            if (tpl == null)
                foreach (var rt in Schema.ItemRecords)
                {
                    tpl = _itemConfigData.Records(rt).FirstOrDefault();
                    if (tpl != null) { useType = rt; break; }
                }
            if (tpl == null) throw new InvalidOperationException("Aucun item modèle dans item_config.");
            return CreateItem(tpl, useType, name, "");
        }

        /// <summary>Duplicate an existing item: same fields (incl. description + icon position), a new ID/name.</summary>
        public ItemInfo DuplicateItem(ItemInfo src)
        {
            if (src?.Entry == null || _itemConfigData == null)
                throw new InvalidOperationException("Aucun item à dupliquer.");
            string name = (string.IsNullOrEmpty(src.Name) ? src.ItemIdHex : src.Name) + " (copie)";
            return CreateItem(src.Entry, src.RecordType, name, src.Description);
        }

        /// <summary>Shared record-builder for AddItem/DuplicateItem. Clones <paramref name="template"/>.</summary>
        private ItemInfo CreateItem(T2bEntry template, string useType, string name, string description)
        {
            // Collision-free IDs, checked against every item id / text key already present.
            string code = "lycoris_item_" + Sanitize(name);
            var allIds = new HashSet<int>();
            foreach (var rt in Schema.ItemRecords)
                foreach (var e in _itemConfigData.Records(rt))
                { int? id = e.GetInt(Schema.Item_IdIndex); if (id.HasValue) allIds.Add(id.Value); }
            int itemId = UniqueHash(code, allIds);
            int nounHash = UniqueHash(code + "_noun",
                _itemTextData != null ? ExistingFirstKeys(_itemTextData.Records(Schema.NounRecord)) : new HashSet<int>());
            int descHash = UniqueHash(code + "_desc",
                _itemTextData != null ? ExistingFirstKeys(_itemTextData.Records(Schema.DescRecord)) : new HashSet<int>());
            int nextSort = Items.Count > 0 ? Items.Max(i => i.InventorySort ?? 0) + 1 : 0;

            var rec = template.Clone();
            SetIntForce(rec, Schema.Item_IdIndex, itemId);
            SetIntForce(rec, Schema.Item_NameHashIndex, nounHash);
            SetIntForce(rec, Schema.Item_DescHashIndex, descHash);
            SetIntForce(rec, Schema.Item_InventorySortIndex, nextSort);
            InsertIntoGroup(_itemConfigData, useType + "_LIST_BEG", useType + "_LIST_END", rec);

            T2bEntry nounEntry = null, descEntry = null;
            if (_itemTextData != null)
            {
                var nounTpl = _itemTextData.Records(Schema.NounRecord).FirstOrDefault();
                if (nounTpl != null)
                {
                    nounEntry = nounTpl.Clone();
                    SetIntForce(nounEntry, 0, nounHash);
                    SetText(nounEntry, Schema.ItemNoun_TextIndex, name ?? "");
                    InsertIntoGroup(_itemTextData, Schema.NounGroupBegin, Schema.NounGroupEnd, nounEntry);
                }
                var textTpl = _itemTextData.Records(Schema.DescRecord).FirstOrDefault();
                if (textTpl != null)
                {
                    descEntry = textTpl.Clone();
                    SetIntForce(descEntry, 0, descHash);
                    SetText(descEntry, Schema.ItemText_TextIndex, description ?? "");
                    InsertIntoGroup(_itemTextData, Schema.DescGroupBegin, Schema.DescGroupEnd, descEntry);
                }
            }

            var it = new ItemInfo
            {
                Entry = rec,
                RecordType = useType,
                ItemId = itemId,
                NounTextID = nounHash,
                DescTextID = descHash,
                Name = name,
                Description = description ?? "",
                InventorySort = nextSort,
                ItemType = rec.GetInt(Schema.Item_TypeIndex),
                CarryCap = rec.GetInt(Schema.Item_CarryCapIndex),
                SellPrice = rec.GetInt(Schema.Item_SellPriceIndex),
                ShopPrice = rec.GetInt(Schema.Item_ShopPriceIndex),
                IconPosX = rec.GetInt(Schema.Item_IconPosXIndex),
                IconPosY = rec.GetInt(Schema.Item_IconPosYIndex),
                NameEntry = nounEntry,
                DescEntry = descEntry,
                OriginalName = null,        // force the name/desc to be written on save
                OriginalDescription = null,
                IsDirty = true,
            };
            Items.Add(it);
            _itemStructureDirty = true;
            return it;
        }

        /// <summary>
        /// Delete an item: removes its item_config record, and its item_text name/description entries only
        /// when no remaining item still references them (they can be shared). Persisted on the next SaveItems.
        /// </summary>
        public void RemoveItem(ItemInfo it)
        {
            if (it?.Entry == null || _itemConfigData == null) return;

            RemoveEntry(_itemConfigData, it.Entry, it.RecordType + "_LIST_BEG");
            Items.Remove(it);

            if (_itemTextData != null)
            {
                if (it.NameEntry != null && Items.All(o => o.NounTextID != it.NounTextID))
                    RemoveEntry(_itemTextData, it.NameEntry, Schema.NounGroupBegin);
                if (it.DescEntry != null && Items.All(o => o.DescTextID != it.DescTextID))
                    RemoveEntry(_itemTextData, it.DescEntry, Schema.DescGroupBegin);
            }
            _itemStructureDirty = true;
        }

        /// <summary>All yo-kai as (ParamHash, name) — for the evolution-target dropdown.</summary>
        public List<EnumEntry> YokaiOptions { get; private set; } = new List<EnumEntry>();

        // ============================ Saving ============================

        /// <summary>
        /// Apply every edit back into its source record and re-serialise all loaded files.
        /// Only touched slots change, so unedited files stay byte-identical. Returns a short
        /// summary of what was written.
        /// </summary>
        public string SaveAll()
        {
            if (ParamData == null) throw new InvalidOperationException("Nothing loaded.");

            int pv = 0;
            foreach (var y in Yokai.Where(y => y.SourceEntry != null))
            {
                pv += SetInt(y.SourceEntry, Schema.ShowInMedaliumIndex, y.Show ? 1 : 0);
                pv += SetInt(y.SourceEntry, Schema.MedaliumOffsetIndex, y.Medal);
                pv += SetInt(y.SourceEntry, Schema.ResistanceIndex, y.Resistance);
                pv += SetInt(y.SourceEntry, Schema.WeaknessIndex, y.Weakness);
                pv += SetInt(y.SourceEntry, Schema.MinHpIndex, y.MinHp);
                pv += SetInt(y.SourceEntry, Schema.MaxHpIndex, y.MaxHp);
                pv += SetInt(y.SourceEntry, Schema.MinStrengthIndex, y.MinStrength);
                pv += SetInt(y.SourceEntry, Schema.MaxStrengthIndex, y.MaxStrength);
                pv += SetInt(y.SourceEntry, Schema.MinSpiritIndex, y.MinSpirit);
                pv += SetInt(y.SourceEntry, Schema.MaxSpiritIndex, y.MaxSpirit);
                pv += SetInt(y.SourceEntry, Schema.MinDefenseIndex, y.MinDefense);
                pv += SetInt(y.SourceEntry, Schema.MaxDefenseIndex, y.MaxDefense);
                pv += SetInt(y.SourceEntry, Schema.MinSpeedIndex, y.MinSpeed);
                pv += SetInt(y.SourceEntry, Schema.MaxSpeedIndex, y.MaxSpeed);
                pv += SetInt(y.SourceEntry, Schema.AttackHashIndex, y.AttackHash);
                pv += SetInt(y.SourceEntry, Schema.AttackPctIndex, y.AttackPct);
                pv += SetInt(y.SourceEntry, Schema.TechniqueHashIndex, y.TechniqueHash);
                pv += SetInt(y.SourceEntry, Schema.TechniquePctIndex, y.TechniquePct);
                pv += SetInt(y.SourceEntry, Schema.InspiritHashIndex, y.InspiritHash);
                pv += SetInt(y.SourceEntry, Schema.InspiritPctIndex, y.InspiritPct);
                pv += SetInt(y.SourceEntry, Schema.GuardHashIndex, y.GuardHash);
                pv += SetInt(y.SourceEntry, Schema.GuardPctIndex, y.GuardPct);
                pv += SetInt(y.SourceEntry, Schema.SoultimateHashIndex, y.SoultimateHash);
                pv += SetInt(y.SourceEntry, Schema.AbilityHashIndex, y.AbilityHash);
                pv += SetInt(y.SourceEntry, Schema.EvolveOffsetIndex, y.EvolveOffset);
            }

            // Charabase fields (rank, tribe, model, medal pos, food, role, status flags) — base records
            // can be shared, so write each field only for the row that actually changed it.
            int bv = 0;
            foreach (var y in Yokai.Where(y => y.BaseEntry != null))
                foreach (var kv in y.BaseFieldValues(Schema))
                    if (!y.BaseOriginal.TryGetValue(kv.Key, out int? orig) || kv.Value != orig)
                        bv += SetInt(y.BaseEntry, kv.Key, kv.Value);

            // Scale records are keyed by BaseHash and can be shared — write only changed values,
            // preserving each slot's original int/float type so unedited files stay byte-identical.
            int sv = 0;
            foreach (var y in Yokai.Where(y => y.ScaleEntry != null))
                for (int i = 1; i <= 7; i++)
                    if (y.IsNew || y.ScaleChanged(i)) sv += SetScaleValue(y.ScaleEntry, i, y.GetScale(i));

            // Evolution records live in chara_param; write only rows that changed theirs.
            int ev = 0;
            foreach (var y in Yokai.Where(y => y.EvolveEntry != null && (y.IsNew || y.EvolveChanged)))
            {
                ev += SetInt(y.EvolveEntry, Schema.Evolve_TargetIndex, y.EvolveTargetHash);
                ev += SetInt(y.EvolveEntry, Schema.Evolve_LevelIndex, y.EvolveLevel);
            }

            // Blaster-T (hackslash_chara_param) and drops (battle_chara_param) — per-ParamHash unique,
            // but only writable when the file is part of the mod (never the read-only reference copy).
            int hs = 0;
            bool hsSave = HackslashData != null && IsUnderMod(HackslashFile);
            if (hsSave)
                foreach (var y in Yokai.Where(y => y.HackslashEntry != null))
                {
                    hs += SetInt(y.HackslashEntry, Schema.Hs_AbilityIndex, y.BtAbilityHash);
                    hs += SetInt(y.HackslashEntry, Schema.Hs_SoultimateIndex, y.BtSoultimateHash);
                    hs += SetInt(y.HackslashEntry, Schema.Hs_AttackAIndex, y.BtAttackAHash);
                    hs += SetInt(y.HackslashEntry, Schema.Hs_AttackYIndex, y.BtAttackYHash);
                    hs += SetInt(y.HackslashEntry, Schema.Hs_AttackXIndex, y.BtAttackXHash);
                }

            int dr = 0;
            bool btSave = BattleData != null && IsUnderMod(BattleFile);
            if (btSave)
                foreach (var y in Yokai.Where(y => y.BattleEntry != null))
                {
                    dr += SetInt(y.BattleEntry, Schema.B_MoneyIndex, y.Money);
                    dr += SetInt(y.BattleEntry, Schema.B_ExpIndex, y.Experience);
                    dr += SetInt(y.BattleEntry, Schema.B_Drop1Index, y.Drop1Hash);
                    dr += SetInt(y.BattleEntry, Schema.B_Drop1RateIndex, y.Drop1Rate);
                    dr += SetInt(y.BattleEntry, Schema.B_Drop2Index, y.Drop2Hash);
                    dr += SetInt(y.BattleEntry, Schema.B_Drop2RateIndex, y.Drop2Rate);
                }

            // Name/description records are shared too — same rule.
            int nv = 0;
            foreach (var y in Yokai.Where(y => y.NameEntry != null && (y.IsNew || y.NameChanged)))
                nv += SetText(y.NameEntry, Schema.NounTextIndex, y.Name);

            int dv = 0;
            foreach (var y in Yokai.Where(y => y.DescEntry != null && (y.IsNew || y.DescriptionChanged)))
                dv += SetText(y.DescEntry, Schema.DescTextIndex, y.Description);

            T2bWriter.WriteFile(ParamData, ParamFile);
            if (BaseData != null) T2bWriter.WriteFile(BaseData, BaseFile);
            if (TextData != null) T2bWriter.WriteFile(TextData, TextFile);
            if (DescData != null) T2bWriter.WriteFile(DescData, DescFile);
            if (ScaleData != null) T2bWriter.WriteFile(ScaleData, ScaleFile);
            if (hsSave) T2bWriter.WriteFile(HackslashData, HackslashFile);
            if (btSave) T2bWriter.WriteFile(BattleData, BattleFile);

            foreach (var y in Yokai)
            {
                y.IsDirty = false;
                y.IsNew = false;
                y.OriginalName = y.Name;
                y.OriginalDescription = y.Description;
                y.OriginalRank = y.Rank;
                y.OriginalTribe = y.Tribe;
                foreach (var kv in y.BaseFieldValues(Schema)) y.BaseOriginal[kv.Key] = kv.Value;
                y.OriginalEvolveTarget = y.EvolveTargetHash;
                y.OriginalEvolveLevel = y.EvolveLevel;
                y.OriginalEvolveOffset = y.EvolveOffset;
                y.SnapshotScale();
            }
            return $"param:{pv}, base:{bv}, scale:{sv}, evo:{ev}, blasterT:{hs}, drops:{dr}, noms:{nv}, desc:{dv}";
        }

        /// <summary>Back-compat: save only the param file (stats/attack).</summary>
        public int SaveParams(string path)
        {
            int changed = 0;
            foreach (var y in Yokai.Where(y => y.SourceEntry != null))
            {
                changed += SetInt(y.SourceEntry, Schema.MinHpIndex, y.MinHp);
                changed += SetInt(y.SourceEntry, Schema.MaxHpIndex, y.MaxHp);
                changed += SetInt(y.SourceEntry, Schema.MinStrengthIndex, y.MinStrength);
                changed += SetInt(y.SourceEntry, Schema.MaxStrengthIndex, y.MaxStrength);
                changed += SetInt(y.SourceEntry, Schema.MinSpiritIndex, y.MinSpirit);
                changed += SetInt(y.SourceEntry, Schema.MaxSpiritIndex, y.MaxSpirit);
                changed += SetInt(y.SourceEntry, Schema.MinDefenseIndex, y.MinDefense);
                changed += SetInt(y.SourceEntry, Schema.MaxDefenseIndex, y.MaxDefense);
                changed += SetInt(y.SourceEntry, Schema.MinSpeedIndex, y.MinSpeed);
                changed += SetInt(y.SourceEntry, Schema.MaxSpeedIndex, y.MaxSpeed);
                changed += SetInt(y.SourceEntry, Schema.AttackHashIndex, y.AttackHash);
                changed += SetInt(y.SourceEntry, Schema.SoultimateHashIndex, y.SoultimateHash);
                changed += SetInt(y.SourceEntry, Schema.AbilityHashIndex, y.AbilityHash);
            }
            T2bWriter.WriteFile(ParamData, path);
            foreach (var y in Yokai) y.IsDirty = false;
            return changed;
        }

        // ============================ Adding ============================

        /// <summary>
        /// Create a brand-new yo-kai across all four files. Clones existing records as valid
        /// templates and generates collision-free CRC32 hashes derived from <paramref name="name"/>.
        /// Requires chara_base/text/desc to be loaded (full mod). The new row is appended and
        /// persisted on the next <see cref="SaveAll"/>. Returns the created <see cref="YokaiInfo"/>.
        /// </summary>
        public YokaiInfo AddYokai(string name, string description, int tribe = 0, int rank = 0,
            YokaiInfo statsTemplate = null)
        {
            if (BaseData == null || TextData == null || DescData == null)
                throw new InvalidOperationException(
                    "Ajouter un yo-kai nécessite chara_base + chara_text + chara_desc_text chargés (mod complet).");

            string code = "lycoris_" + Sanitize(name);
            int baseHash = UniqueHash(code + "_base", ExistingKeys(BaseData.Records(Schema.BaseYokaiRecord), Schema.Base_BaseHashIndex));
            int paramHash = UniqueHash(code + "_param", ExistingKeys(ParamData.Records(Schema.ParamRecord), Schema.ParamHashIndex));
            int nameHash = UniqueHash(code + "_name", ExistingFirstKeys(TextData.Records(Schema.NounRecord)));
            int descHash = UniqueHash(code + "_desc", ExistingFirstKeys(DescData.Records(Schema.DescRecord)));

            // --- param record (clone a real one so all 41 fields are valid) ---
            var paramTpl = (statsTemplate?.SourceEntry ?? ParamData.Records(Schema.ParamRecord).First()).Clone();
            SetIntForce(paramTpl, Schema.ParamHashIndex, paramHash);
            SetIntForce(paramTpl, Schema.Param_BaseHashIndex, baseHash);
            InsertIntoGroup(ParamData, Schema.ParamGroupBegin, Schema.ParamGroupEnd, paramTpl);

            // --- base record ---
            var baseTpl = BaseData.Records(Schema.BaseYokaiRecord).First().Clone();
            SetIntForce(baseTpl, Schema.Base_BaseHashIndex, baseHash);
            SetIntForce(baseTpl, Schema.Base_NameHashIndex, nameHash);
            SetIntForce(baseTpl, Schema.Base_DescriptionHashIndex, descHash);
            SetIntForce(baseTpl, Schema.Base_RankIndex, rank);
            SetIntForce(baseTpl, Schema.Base_TribeIndex, tribe);
            InsertIntoGroup(BaseData, Schema.BaseGroupBegin, Schema.BaseGroupEnd, baseTpl);

            // --- name record (NOUN_INFO) ---
            var nounTpl = TextData.Records(Schema.NounRecord).First().Clone();
            SetIntForce(nounTpl, Schema.NounKeyIndex, nameHash);
            SetText(nounTpl, Schema.NounTextIndex, name);
            InsertIntoGroup(TextData, Schema.NounGroupBegin, Schema.NounGroupEnd, nounTpl);

            // --- description record (TEXT_INFO) ---
            var textTpl = DescData.Records(Schema.DescRecord).First().Clone();
            SetIntForce(textTpl, Schema.DescKeyIndex, descHash);
            SetText(textTpl, Schema.DescTextIndex, description ?? "");
            InsertIntoGroup(DescData, Schema.DescGroupBegin, Schema.DescGroupEnd, textTpl);

            var y = new YokaiInfo
            {
                IsNew = true,
                SourceEntry = paramTpl,
                BaseEntry = baseTpl,
                NameEntry = nounTpl,
                DescEntry = textTpl,
                ParamHash = paramHash,
                BaseHash = baseHash,
                NameHash = nameHash,
                DescriptionHash = descHash,
                Name = name,
                Description = description,
                Rank = rank,
                Tribe = tribe,
                MinHp = paramTpl.GetInt(Schema.MinHpIndex),
                MaxHp = paramTpl.GetInt(Schema.MaxHpIndex),
                MinStrength = paramTpl.GetInt(Schema.MinStrengthIndex),
                MaxStrength = paramTpl.GetInt(Schema.MaxStrengthIndex),
                MinSpirit = paramTpl.GetInt(Schema.MinSpiritIndex),
                MaxSpirit = paramTpl.GetInt(Schema.MaxSpiritIndex),
                MinDefense = paramTpl.GetInt(Schema.MinDefenseIndex),
                MaxDefense = paramTpl.GetInt(Schema.MaxDefenseIndex),
                MinSpeed = paramTpl.GetInt(Schema.MinSpeedIndex),
                MaxSpeed = paramTpl.GetInt(Schema.MaxSpeedIndex),
                AttackHash = paramTpl.GetInt(Schema.AttackHashIndex),
                SoultimateHash = paramTpl.GetInt(Schema.SoultimateHashIndex),
                AbilityHash = paramTpl.GetInt(Schema.AbilityHashIndex),
            };
            Yokai.Add(y);
            EnableBlasterT(y);   // new yo-kai get an editable Blaster-T + Drops entry
            EnableDrops(y);
            return y;
        }

        /// <summary>
        /// Duplicate a yo-kai: clones its param / base / name / description / scale records (and Blaster-T /
        /// drops if it has them) with fresh collision-free hashes, so the copy is fully independent. All stats,
        /// moves, tribe/rank, model, charabase flags, scale and evolution target are copied. Persisted on the
        /// next <see cref="SaveAll"/>. Returns the created <see cref="YokaiInfo"/>.
        /// </summary>
        public YokaiInfo DuplicateYokai(YokaiInfo src)
        {
            if (src?.SourceEntry == null) throw new InvalidOperationException("Aucun yo-kai à dupliquer.");
            if (BaseData == null || TextData == null || DescData == null)
                throw new InvalidOperationException(
                    "Dupliquer nécessite chara_base + chara_text + chara_desc_text chargés (mod complet).");

            string name = (string.IsNullOrEmpty(src.Name) ? src.ParamIdHex : src.Name) + " (copie)";
            string code = "lycoris_" + Sanitize(name);
            int baseHash = UniqueHash(code + "_base", ExistingKeys(BaseData.Records(Schema.BaseYokaiRecord), Schema.Base_BaseHashIndex));
            int paramHash = UniqueHash(code + "_param", ExistingKeys(ParamData.Records(Schema.ParamRecord), Schema.ParamHashIndex));
            int nameHash = UniqueHash(code + "_name", ExistingFirstKeys(TextData.Records(Schema.NounRecord)));
            int descHash = UniqueHash(code + "_desc", ExistingFirstKeys(DescData.Records(Schema.DescRecord)));

            // --- param (clone the source, re-key to the new param/base hashes) ---
            var paramTpl = src.SourceEntry.Clone();
            SetIntForce(paramTpl, Schema.ParamHashIndex, paramHash);
            SetIntForce(paramTpl, Schema.Param_BaseHashIndex, baseHash);
            InsertIntoGroup(ParamData, Schema.ParamGroupBegin, Schema.ParamGroupEnd, paramTpl);

            // --- base (clone the source's base so rank/tribe/model/charabase all carry over) ---
            var baseTpl = (src.BaseEntry ?? BaseData.Records(Schema.BaseYokaiRecord).First()).Clone();
            SetIntForce(baseTpl, Schema.Base_BaseHashIndex, baseHash);
            SetIntForce(baseTpl, Schema.Base_NameHashIndex, nameHash);
            SetIntForce(baseTpl, Schema.Base_DescriptionHashIndex, descHash);
            InsertIntoGroup(BaseData, Schema.BaseGroupBegin, Schema.BaseGroupEnd, baseTpl);

            // --- name + description (new independent entries) ---
            var nounTpl = (src.NameEntry ?? TextData.Records(Schema.NounRecord).First()).Clone();
            SetIntForce(nounTpl, Schema.NounKeyIndex, nameHash);
            SetText(nounTpl, Schema.NounTextIndex, name);
            InsertIntoGroup(TextData, Schema.NounGroupBegin, Schema.NounGroupEnd, nounTpl);

            var descTpl = (src.DescEntry ?? DescData.Records(Schema.DescRecord).First()).Clone();
            SetIntForce(descTpl, Schema.DescKeyIndex, descHash);
            SetText(descTpl, Schema.DescTextIndex, src.Description ?? "");
            InsertIntoGroup(DescData, Schema.DescGroupBegin, Schema.DescGroupEnd, descTpl);

            // --- scale (keyed by BaseHash → clone with the new base hash) ---
            T2bEntry scaleTpl = null;
            if (ScaleData != null && src.ScaleEntry != null)
            {
                scaleTpl = src.ScaleEntry.Clone();
                SetIntForce(scaleTpl, Schema.Scale_BaseHashIndex, baseHash);
                InsertIntoGroup(ScaleData, Schema.ScaleGroupBegin, Schema.ScaleGroupEnd, scaleTpl);
            }

            // --- Blaster-T / drops (keyed by ParamHash → clone with the new param hash) ---
            T2bEntry hsTpl = null;
            if (HackslashData != null && src.HackslashEntry != null)
            {
                hsTpl = src.HackslashEntry.Clone();
                SetIntForce(hsTpl, 0, paramHash);
                InsertIntoGroup(HackslashData, Schema.HackslashGroupBegin, Schema.HackslashGroupEnd, hsTpl);
                if (!IsUnderMod(HackslashFile)) HackslashFile = MirrorToMod(HackslashFile);
            }
            T2bEntry btTpl = null;
            if (BattleData != null && src.BattleEntry != null)
            {
                btTpl = src.BattleEntry.Clone();
                SetIntForce(btTpl, 0, paramHash);
                InsertIntoGroup(BattleData, Schema.BattleGroupBegin, Schema.BattleGroupEnd, btTpl);
                if (!IsUnderMod(BattleFile)) BattleFile = MirrorToMod(BattleFile);
            }

            // Build the model by copying every editable/display field from the source.
            var y = new YokaiInfo
            {
                IsNew = true,
                SourceEntry = paramTpl, BaseEntry = baseTpl, NameEntry = nounTpl, DescEntry = descTpl,
                ScaleEntry = scaleTpl, HackslashEntry = hsTpl, BattleEntry = btTpl,
                ParamHash = paramHash, BaseHash = baseHash, NameHash = nameHash, DescriptionHash = descHash,
                Name = name, Description = src.Description,
                // model / icons
                FileNamePrefix = src.FileNamePrefix, FileNameNumber = src.FileNameNumber, FileNameVariant = src.FileNameVariant,
                IconBaseName = src.IconBaseName, IconFile = src.IconFile, MedalIconFile = src.MedalIconFile,
                // param stats
                Show = src.Show, Medal = src.Medal, Resistance = src.Resistance, Weakness = src.Weakness,
                MinHp = src.MinHp, MaxHp = src.MaxHp, MinStrength = src.MinStrength, MaxStrength = src.MaxStrength,
                MinSpirit = src.MinSpirit, MaxSpirit = src.MaxSpirit, MinDefense = src.MinDefense, MaxDefense = src.MaxDefense,
                MinSpeed = src.MinSpeed, MaxSpeed = src.MaxSpeed,
                // moves
                AttackHash = src.AttackHash, AttackPct = src.AttackPct, TechniqueHash = src.TechniqueHash, TechniquePct = src.TechniquePct,
                InspiritHash = src.InspiritHash, InspiritPct = src.InspiritPct, GuardHash = src.GuardHash, GuardPct = src.GuardPct,
                SoultimateHash = src.SoultimateHash, AbilityHash = src.AbilityHash,
                AttackName = src.AttackName, TechniqueName = src.TechniqueName, InspiritName = src.InspiritName,
                GuardName = src.GuardName, SoultimateName = src.SoultimateName, AbilityName = src.AbilityName,
                // base / charabase
                Rank = src.Rank, Tribe = src.Tribe,
                MedalPosX = src.MedalPosX, MedalPosY = src.MedalPosY, FavoriteFood = src.FavoriteFood, HatedFood = src.HatedFood,
                Role = src.Role, IsRare = src.IsRare, IsLegend = src.IsLegend, IsPionner = src.IsPionner,
                IsCommandant = src.IsCommandant, IsClassic = src.IsClassic, IsMerican = src.IsMerican,
                IsDeva = src.IsDeva, IsMystery = src.IsMystery, IsTreasure = src.IsTreasure,
                // evolution: copy the offset (shared record referenced by index; not re-written)
                EvolveOffset = src.EvolveOffset, EvolveTargetHash = src.EvolveTargetHash, EvolveLevel = src.EvolveLevel,
                EvolvesToName = src.EvolvesToName,
                // Blaster-T / drops
                BtAbilityHash = src.BtAbilityHash, BtSoultimateHash = src.BtSoultimateHash,
                BtAttackAHash = src.BtAttackAHash, BtAttackYHash = src.BtAttackYHash, BtAttackXHash = src.BtAttackXHash,
                Money = src.Money, Experience = src.Experience,
                Drop1Hash = src.Drop1Hash, Drop1Rate = src.Drop1Rate, Drop2Hash = src.Drop2Hash, Drop2Rate = src.Drop2Rate,
            };
            for (int i = 1; i <= 7; i++) y.InitScale(i, src.GetScale(i));

            // Snapshots so SaveAll doesn't treat the (already-correct) shared records as edited.
            y.OriginalRank = y.Rank; y.OriginalTribe = y.Tribe;
            y.OriginalEvolveOffset = y.EvolveOffset;
            y.OriginalEvolveTarget = y.EvolveTargetHash; y.OriginalEvolveLevel = y.EvolveLevel;
            foreach (var kv in y.BaseFieldValues(Schema)) y.BaseOriginal[kv.Key] = kv.Value;

            Yokai.Add(y);
            return y;
        }

        /// <summary>
        /// Toggle whether a yo-kai can evolve. Turning it ON creates a fresh CHARA_EVOLVE_INFO record
        /// (appended to the group so existing EvolveOffsets stay valid) and points the yo-kai at it;
        /// turning it OFF just sets EvolveOffset to -1 (the record is left orphaned to avoid reindexing).
        /// </summary>
        public void SetEvolvable(YokaiInfo y, bool on)
        {
            if (on)
            {
                if (y.CanEvolve) return;
                var list = ParamData.Records(Schema.EvolveRecord).ToList();
                if (list.Count == 0) return; // need a template record (always present in YW3)
                var entry = list[0].Clone();
                SetIntForce(entry, Schema.Evolve_TargetIndex, y.ParamHash); // default: evolves into itself
                SetIntForce(entry, Schema.Evolve_LevelIndex, 30);
                InsertIntoGroup(ParamData, Schema.EvolveGroupBegin, Schema.EvolveGroupEnd, entry);

                y.EvolveEntry = entry;
                y.EvolveOffset = list.Count;          // index of the newly appended record
                y.EvolveTargetHash = y.ParamHash;
                y.EvolveLevel = 30;
                y.OriginalEvolveTarget = null;        // force the target/level to be written on save
                y.OriginalEvolveLevel = null;
            }
            else
            {
                if (!y.CanEvolve) return;
                y.EvolveOffset = -1;                  // orphan the record; SaveAll writes EvolveOffset back
            }
        }

        /// <summary>
        /// Delete a yo-kai from the registry. Removes its param record (the core registry entry) and its
        /// per-ParamHash records (hackslash, battle); removes SHARED records (base, scale, name, description)
        /// only when no remaining yo-kai still references them. Evolution records are left untouched (removing
        /// them would reindex every other yo-kai's EvolveOffset). Persisted on the next SaveAll.
        /// </summary>
        public void RemoveYokai(YokaiInfo y)
        {
            if (y?.SourceEntry == null) return;

            RemoveEntry(ParamData, y.SourceEntry, Schema.ParamGroupBegin);
            if (HackslashData != null && y.HackslashEntry != null)
                RemoveEntry(HackslashData, y.HackslashEntry, Schema.HackslashGroupBegin);
            if (BattleData != null && y.BattleEntry != null)
                RemoveEntry(BattleData, y.BattleEntry, Schema.BattleGroupBegin);

            Yokai.Remove(y);

            // Shared records — remove only if orphaned (no other yo-kai points at them anymore).
            if (BaseData != null && y.BaseEntry != null && Yokai.All(o => o.BaseHash != y.BaseHash))
                RemoveEntry(BaseData, y.BaseEntry, Schema.BaseGroupBegin);
            if (ScaleData != null && y.ScaleEntry != null && Yokai.All(o => o.BaseHash != y.BaseHash))
                RemoveEntry(ScaleData, y.ScaleEntry, Schema.ScaleGroupBegin);
            if (TextData != null && y.NameEntry != null && Yokai.All(o => o.NameHash != y.NameHash))
                RemoveEntry(TextData, y.NameEntry, Schema.NounGroupBegin);
            if (DescData != null && y.DescEntry != null && y.DescriptionHash.HasValue
                && Yokai.All(o => o.DescriptionHash != y.DescriptionHash))
                RemoveEntry(DescData, y.DescEntry, Schema.DescGroupBegin);
        }

        /// <summary>
        /// Create a Blaster-T (hackslash_chara_param) record for a yo-kai that lacks one, so its Blaster-T
        /// tab becomes editable. Clones a valid template, keys it to the ParamHash, and redirects the file
        /// to the mod (mirroring the reference) so SaveAll persists it there. Returns false if impossible.
        /// </summary>
        public bool EnableBlasterT(YokaiInfo y)
        {
            if (y.HasBlasterT) return true;
            if (HackslashData == null) return false;
            var tpl = HackslashData.Records(Schema.HackslashRecord).FirstOrDefault();
            if (tpl == null) return false;

            var entry = tpl.Clone();
            SetIntForce(entry, 0, y.ParamHash);
            InsertIntoGroup(HackslashData, Schema.HackslashGroupBegin, Schema.HackslashGroupEnd, entry);
            y.HackslashEntry = entry;
            y.BtAbilityHash = entry.GetInt(Schema.Hs_AbilityIndex);
            y.BtSoultimateHash = entry.GetInt(Schema.Hs_SoultimateIndex);
            y.BtAttackAHash = entry.GetInt(Schema.Hs_AttackAIndex);
            y.BtAttackYHash = entry.GetInt(Schema.Hs_AttackYIndex);
            y.BtAttackXHash = entry.GetInt(Schema.Hs_AttackXIndex);
            if (!IsUnderMod(HackslashFile)) HackslashFile = MirrorToMod(HackslashFile);
            return true;
        }

        /// <summary>Create a Drops (battle_chara_param) record for a yo-kai that lacks one.</summary>
        public bool EnableDrops(YokaiInfo y)
        {
            if (y.HasDrops) return true;
            if (BattleData == null) return false;
            var tpl = BattleData.Records(Schema.BattleRecord).FirstOrDefault();
            if (tpl == null) return false;

            var entry = tpl.Clone();
            SetIntForce(entry, 0, y.ParamHash);
            InsertIntoGroup(BattleData, Schema.BattleGroupBegin, Schema.BattleGroupEnd, entry);
            y.BattleEntry = entry;
            y.Money = entry.GetInt(Schema.B_MoneyIndex);
            y.Experience = entry.GetInt(Schema.B_ExpIndex);
            y.Drop1Hash = entry.GetInt(Schema.B_Drop1Index);
            y.Drop1Rate = entry.GetInt(Schema.B_Drop1RateIndex);
            y.Drop2Hash = entry.GetInt(Schema.B_Drop2Index);
            y.Drop2Rate = entry.GetInt(Schema.B_Drop2RateIndex);
            if (!IsUnderMod(BattleFile)) BattleFile = MirrorToMod(BattleFile);
            return true;
        }

        /// <summary>Remove an entry from a file and decrement its group's _BEG child count.</summary>
        private static void RemoveEntry(T2bFile file, T2bEntry entry, string beginName)
        {
            if (file == null || !file.Entries.Remove(entry)) return;
            var begin = file.Entries.FirstOrDefault(e => e.Name == beginName);
            if (begin != null && begin.Values.Count > 0 && begin.Values[0].Value is int c)
                begin.Values[0].Value = c - 1;
        }

        // ============================ Helpers ============================

        /// <summary>Resolve each move hash to a display name via the move-name map (set in LoadAll).</summary>
        private void ResolveMoves(YokaiInfo y)
        {
            y.AttackName = ResolveMove(y.AttackHash);
            y.TechniqueName = ResolveMove(y.TechniqueHash);
            y.InspiritName = ResolveMove(y.InspiritHash);
            y.GuardName = ResolveMove(y.GuardHash);
            y.SoultimateName = ResolveMove(y.SoultimateHash);
            y.AbilityName = ResolveMove(y.AbilityHash);
        }

        private string ResolveMove(int? hash)
        {
            if (!hash.HasValue) return null;
            if (_moveNames.TryGetValue(hash.Value, out string n)) return n;
            return null;
        }

        /// <summary>Move hash -&gt; name, assembled from ability config + skill/ability text tables.</summary>
        private Dictionary<int, string> _moveNames = new Dictionary<int, string>();

        private Dictionary<int, List<SkillMove>> BuildSkillPools()
        {
            var pools = new Dictionary<int, List<SkillMove>>();
            if (SkillConfigData == null) return pools;
            var seen = new HashSet<int>();
            foreach (var e in SkillConfigData.Records(Schema.SkillConfigRecord))
            {
                int? hash = e.GetInt(0);
                int? elem = e.GetInt(Schema.Skill_ElementIndex);
                int? power = e.GetInt(Schema.Skill_PowerIndex);
                int? nameHash = e.GetInt(Schema.Skill_NameHashIndex);
                if (!hash.HasValue || !elem.HasValue || !nameHash.HasValue) continue;
                if (!SkillNames.TryGetValue(nameHash.Value, out string name) || string.IsNullOrEmpty(name)) continue;
                if (!seen.Add(hash.Value)) continue;
                if (!pools.TryGetValue(elem.Value, out var list)) pools[elem.Value] = list = new List<SkillMove>();
                list.Add(new SkillMove(hash.Value, name, power ?? 0));
            }
            foreach (var list in pools.Values) list.Sort((a, b) => a.Power.CompareTo(b.Power));
            return pools;
        }

        /// <summary>How many yo-kai got a resolved ability name (diagnostic for the UI).</summary>
        public int AbilityTableCount { get; private set; }

        /// <summary>How many move hashes got a resolved skill/ability name (diagnostic).</summary>
        public int MoveNameCount { get; private set; }

        /// <summary>Assignable move options for dropdowns: (param move hash, name), sorted by name.</summary>
        public List<EnumEntry> MoveOptions { get; private set; } = new List<EnumEntry>();
        public List<EnumEntry> AbilityOptions { get; private set; } = new List<EnumEntry>();

        private Dictionary<int, string> BuildMoveNames()
        {
            var map = new Dictionary<int, string>();

            // Direct skill_text hits (covers any move hash that is itself a skill_text key).
            foreach (var kv in SkillNames) map[kv.Key] = kv.Value;

            // Skills: <move>Hash -> SKILL_CONFIG_INFO[0] -> NameHash field -> skill_text.
            MoveOptions = AddConfigNames(map, SkillConfigData, Schema.SkillConfigRecord, SkillNames);

            // Abilities: abilityHash -> CHARA_ABILITY_CONFIG_INFO[0] -> NameHash field -> ability_text.
            // ability_text holds BOTH names (NOUN_INFO) and descriptions (TEXT_INFO); restrict to
            // NOUN_INFO so auto-detection can't latch onto the (more numerous) description field.
            AbilityOptions = AddConfigNames(map, AbilityData, Schema.AbilityConfigRecord, BuildNounValueMap(AbilityTextData));

            MoveNameCount = map.Count;
            return map;
        }

        /// <summary>
        /// Generic "config -> name" resolver: config records are keyed by the move hash at [0]
        /// and carry a NameHash field into <paramref name="textMap"/>. The NameHash field index
        /// is auto-detected as the field (other than [0]) whose values resolve most often into
        /// the text table — so it works for skill_config ([3]) and chara_ability ([1]) alike.
        /// </summary>
        private List<EnumEntry> AddConfigNames(Dictionary<int, string> map, T2bFile config, string record,
            Dictionary<int, string> textMap)
        {
            var options = new List<EnumEntry>();
            if (config == null || textMap.Count == 0) return options;
            var records = config.Records(record).ToList();
            if (records.Count == 0) return options;

            int width = records.Max(e => e.Values.Count);
            int bestIndex = -1, bestHits = 0;
            for (int i = 1; i < width; i++)
            {
                int hits = records.Count(e => e.GetInt(i) is int v && textMap.ContainsKey(v));
                if (hits > bestHits) { bestHits = hits; bestIndex = i; }
            }
            if (bestIndex < 0) return options;

            var seenKeys = new HashSet<int>();
            foreach (var e in records)
            {
                int? key = e.GetInt(0);
                int? nameHash = e.GetInt(bestIndex);
                if (key.HasValue && nameHash.HasValue && textMap.TryGetValue(nameHash.Value, out string nm))
                {
                    if (!map.ContainsKey(key.Value)) map[key.Value] = nm;
                    if (seenKeys.Add(key.Value)) options.Add(new EnumEntry(key.Value, nm));
                }
            }
            return options.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private Dictionary<int, T2bEntry> BuildScaleMap(T2bFile file)
        {
            var map = new Dictionary<int, T2bEntry>();
            if (file == null) return map;
            foreach (var e in file.Records(Schema.ScaleRecord))
            {
                int? bh = e.GetInt(Schema.Scale_BaseHashIndex);
                if (bh.HasValue && !map.ContainsKey(bh.Value)) map[bh.Value] = e;
            }
            return map;
        }

        /// <summary>key (first Integer) -&gt; text (first non-empty String) over a text container.</summary>
        private static Dictionary<int, string> BuildTextValueMap(T2bFile file)
        {
            var map = new Dictionary<int, string>();
            if (file == null) return map;
            foreach (var e in file.Entries)
            {
                if (T2bTree.IsGroupOpen(e.Name) || T2bTree.IsGroupClose(e.Name)) continue;
                int? key = e.FirstIntKey();
                string text = e.FirstText();
                if (key.HasValue && text != null && !map.ContainsKey(key.Value)) map[key.Value] = text;
            }
            return map;
        }

        /// <summary>Like BuildTextValueMap but only NOUN_INFO records — the NAME table (not descriptions).</summary>
        private Dictionary<int, string> BuildNounValueMap(T2bFile file)
        {
            var map = new Dictionary<int, string>();
            if (file == null) return map;
            foreach (var e in file.Records(Schema.NounRecord))
            {
                int? key = e.FirstIntKey();
                string text = e.FirstText();
                if (key.HasValue && text != null && !map.ContainsKey(key.Value)) map[key.Value] = text;
            }
            return map;
        }

        /// <summary>key (value[0]) -&gt; entry, for a specific record type.</summary>
        private Dictionary<int, T2bEntry> BuildKeyMap(T2bFile file, string record)
        {
            var map = new Dictionary<int, T2bEntry>();
            if (file == null) return map;
            foreach (var e in file.Records(record))
            {
                int? k = e.GetInt(0);
                if (k.HasValue && !map.ContainsKey(k.Value)) map[k.Value] = e;
            }
            return map;
        }

        /// <summary>
        /// Config[0]=key -&gt; NameHash (auto-detected field) -&gt; name text. record==null scans every
        /// non-marker record (used for item_config, which has many record types). Also yields a
        /// (key,name) option list for dropdowns.
        /// </summary>
        private Dictionary<int, string> BuildConfigMap(T2bFile config, string record,
            Dictionary<int, string> textMap, out List<EnumEntry> options)
        {
            options = new List<EnumEntry>();
            var map = new Dictionary<int, string>();
            if (config == null || textMap.Count == 0) return map;
            var records = (record != null
                ? config.Records(record)
                : config.Entries.Where(e => !T2bTree.IsGroupOpen(e.Name) && !T2bTree.IsGroupClose(e.Name))).ToList();
            if (records.Count == 0) return map;

            int width = records.Max(e => e.Values.Count);
            int best = -1, bestHits = 0;
            for (int i = 1; i < width; i++)
            {
                int h = records.Count(e => e.GetInt(i) is int v && textMap.ContainsKey(v));
                if (h > bestHits) { bestHits = h; best = i; }
            }
            if (best < 0) return map;

            var seen = new HashSet<int>();
            foreach (var e in records)
            {
                int? k = e.GetInt(0);
                int? n = e.GetInt(best);
                if (k.HasValue && n.HasValue && textMap.TryGetValue(n.Value, out string s))
                {
                    if (!map.ContainsKey(k.Value)) map[k.Value] = s;
                    if (seen.Add(k.Value)) options.Add(new EnumEntry(k.Value, s));
                }
            }
            options = options.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return map;
        }

        private Dictionary<int, T2bEntry> BuildBaseMap(T2bFile file)
        {
            var map = new Dictionary<int, T2bEntry>();
            if (file == null) return map;
            foreach (var e in file.Records(Schema.BaseYokaiRecord))
            {
                int? bh = e.GetInt(Schema.Base_BaseHashIndex);
                if (bh.HasValue && !map.ContainsKey(bh.Value)) map[bh.Value] = e;
            }
            return map;
        }

        /// <summary>Map key (first Integer value) -&gt; entry for a text container's records.</summary>
        private static Dictionary<int, T2bEntry> BuildTextEntryMap(T2bFile file)
        {
            var map = new Dictionary<int, T2bEntry>();
            if (file == null) return map;
            foreach (var e in file.Entries)
            {
                if (T2bTree.IsGroupOpen(e.Name) || T2bTree.IsGroupClose(e.Name)) continue;
                int? key = e.FirstIntKey();
                if (key.HasValue && !map.ContainsKey(key.Value)) map[key.Value] = e;
            }
            return map;
        }

        /// <summary>Insert a record just before its group's _END marker and bump the _BEG count.</summary>
        private static void InsertIntoGroup(T2bFile file, string beginName, string endName, T2bEntry entry)
        {
            int endIdx = file.Entries.FindIndex(e => e.Name == endName);
            if (endIdx < 0) throw new InvalidDataException($"Group end '{endName}' not found.");
            file.Entries.Insert(endIdx, entry);

            var begin = file.Entries.FirstOrDefault(e => e.Name == beginName);
            if (begin != null && begin.Values.Count > 0 && begin.Values[0].Value is int count)
                begin.Values[0].Value = count + 1;
        }

        private static int SetInt(T2bEntry e, int index, int? value)
        {
            if (!value.HasValue || index < 0 || index >= e.Values.Count) return 0;
            var slot = e.Values[index];
            if (slot.Type != Formats.ValueType.Integer) return 0;
            if (slot.Value is int cur && cur == value.Value) return 0;
            slot.Value = value.Value;
            return 1;
        }

        /// <summary>
        /// Write a scale value. A whole number on an Integer slot stays an int (preserving the
        /// original layout); a fractional value forces the slot to Float (e.g. applying a 0.85
        /// humanoid scale onto a slot that shipped as int 1).
        /// </summary>
        private static int SetScaleValue(T2bEntry e, int index, double? value)
        {
            if (!value.HasValue || index < 0 || index >= e.Values.Count) return 0;
            var slot = e.Values[index];
            bool whole = value.Value == System.Math.Floor(value.Value);
            if (slot.Type == Formats.ValueType.Integer && whole)
            {
                int iv = (int)value.Value;
                if (slot.Value is int ci && ci == iv) return 0;
                slot.Value = iv;
                return 1;
            }
            float f = (float)value.Value;
            if (slot.Type == Formats.ValueType.FloatingPoint && slot.Value is float cf && cf == f) return 0;
            slot.Type = Formats.ValueType.FloatingPoint;
            slot.Value = f;
            return 1;
        }

        /// <summary>Force-set an Integer slot (used when building a fresh record from a template).</summary>
        private static void SetIntForce(T2bEntry e, int index, int value)
        {
            if (index < 0 || index >= e.Values.Count) return;
            e.Values[index].Type = Formats.ValueType.Integer;
            e.Values[index].Value = value;
        }

        /// <summary>Set a String slot; returns 1 if it changed. Marks the slot as String type.</summary>
        private static int SetText(T2bEntry e, int index, string value)
        {
            if (index < 0 || index >= e.Values.Count) return 0;
            var slot = e.Values[index];
            if (slot.Type == Formats.ValueType.String && (string)slot.Value == value) return 0;
            slot.Type = Formats.ValueType.String;
            slot.Value = value;
            return 1;
        }

        private static HashSet<int> ExistingKeys(IEnumerable<T2bEntry> records, int index) =>
            new HashSet<int>(records.Select(e => e.GetInt(index)).Where(v => v.HasValue).Select(v => v.Value));

        private static HashSet<int> ExistingFirstKeys(IEnumerable<T2bEntry> records) =>
            new HashSet<int>(records.Select(e => e.FirstIntKey()).Where(v => v.HasValue).Select(v => v.Value));

        private static int UniqueHash(string seed, HashSet<int> taken)
        {
            for (int i = 0; ; i++)
            {
                string s = i == 0 ? seed : $"{seed}#{i}";
                int h = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(s)));
                if (!taken.Contains(h)) { taken.Add(h); return h; }
            }
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "yokai";
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
            return sb.ToString();
        }

        private static string FindNewest(string folder, string prefix, string exclude = null) =>
            Directory.EnumerateFiles(folder, prefix + "*.cfg.bin", SearchOption.AllDirectories)
                .Where(p => exclude == null || Path.GetFileName(p).IndexOf(exclude, StringComparison.OrdinalIgnoreCase) < 0)
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
    }
}
