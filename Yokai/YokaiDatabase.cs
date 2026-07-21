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
        public T2bFile AbilityData { get; private set; }
        public T2bFile AbilityTextData { get; private set; }
        public T2bFile SkillConfigData { get; private set; }
        public T2bFile HackslashData { get; private set; }           // editable
        public T2bFile BattleData { get; private set; }              // editable
        private T2bFile _hsTechnicData, _hsTechnicTextData, _hsAbilityData, _hsAbilityTextData, _itemConfigData, _itemTextData;
        public string HackslashFile { get; private set; }
        public string BattleFile { get; private set; }
        private string _hsTechnicFile, _hsTechnicTextFile, _hsAbilityFile, _hsAbilityTextFile, _itemConfigFile, _itemTextFile;
        private string _modFolder;

        public List<EnumEntry> TechnicOptions { get; private set; } = new List<EnumEntry>();
        public List<EnumEntry> BtAbilityOptions { get; private set; } = new List<EnumEntry>();
        public List<EnumEntry> ItemOptions { get; private set; } = new List<EnumEntry>();

        public string ParamFile { get; private set; }
        public string BaseFile { get; private set; }
        public string TextFile { get; private set; }
        public string DescFile { get; private set; }
        public string ScaleFile { get; private set; }
        public string SkillTextFile { get; private set; }
        public string AbilityFile { get; private set; }
        public string AbilityTextFile { get; private set; }
        public string SkillConfigFile { get; private set; }
        public int NameTableCount { get; private set; }
        public int DescTableCount { get; private set; }
        public int SkillTableCount { get; private set; }

        /// <summary>skill_text key -&gt; name, for the move-name resolver.</summary>
        public Dictionary<int, string> SkillNames { get; private set; } = new Dictionary<int, string>();

        /// <summary>face_icon folders searched for .xi icons (mod first, then reference).</summary>
        private readonly List<string> _faceIconDirs = new List<string>();
        public int IconCount { get; private set; }

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

            // Editable files: mod folder only (SaveAll writes these back).
            ParamFile = FindNewest(folder, Schema.ParamFilePrefix);
            BaseFile = FindNewest(folder, Schema.BaseFilePrefix);
            TextFile = FindNewest(folder, Schema.TextFilePrefix);
            DescFile = FindNewest(folder, Schema.DescFilePrefix);
            ScaleFile = FindNewest(folder, Schema.ScaleFilePrefix);

            // Read-only resolvers: mod folder, else reference folder.
            SkillTextFile = FindResolver(folder, referenceFolder, Schema.SkillTextFilePrefix, null);
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
            AddFaceIconDirs(folder);
            if (referenceFolder != null) AddFaceIconDirs(referenceFolder);

            if (ParamFile == null)
                throw new FileNotFoundException(
                    $"No '{Schema.ParamFilePrefix}*.cfg.bin' found under {folder}.");

            LoadAll();
        }

        private void AddFaceIconDirs(string root)
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(root, "face_icon", SearchOption.AllDirectories))
                    if (!_faceIconDirs.Contains(d)) _faceIconDirs.Add(d);
            }
            catch { /* ignore */ }
        }

        /// <summary>Compute the icon base name and locate its .xi across the face_icon folders.</summary>
        private void ResolveIcon(YokaiInfo y)
        {
            if (!y.FileNamePrefix.HasValue || !y.FileNameNumber.HasValue || !y.FileNameVariant.HasValue) return;
            y.IconBaseName = IconNaming.GetFileModelText(y.FileNamePrefix.Value, y.FileNameNumber.Value, y.FileNameVariant.Value);
            if (y.IconBaseName == null) return;

            // Primary name, then a variant-0 fallback (base form) if the exact variant is absent.
            string fallback = IconNaming.GetFileModelText(y.FileNamePrefix.Value, y.FileNameNumber.Value, 0);
            foreach (var name in new[] { y.IconBaseName, fallback })
            {
                if (name == null) continue;
                foreach (var dir in _faceIconDirs)
                {
                    string p = Path.Combine(dir, name + ".xi");
                    if (File.Exists(p)) { y.IconFile = p; IconCount++; return; }
                }
            }
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
            BaseFile = TextFile = DescFile = ScaleFile = SkillTextFile = null;
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
            }

            // Base fields (rank/tribe) — base records can be shared, so only write rows that
            // actually changed them, and only their changed field.
            int bv = 0;
            foreach (var y in Yokai.Where(y => y.BaseEntry != null))
            {
                if (y.IsNew || y.RankChanged) bv += SetInt(y.BaseEntry, Schema.Base_RankIndex, y.Rank);
                if (y.IsNew || y.TribeChanged) bv += SetInt(y.BaseEntry, Schema.Base_TribeIndex, y.Tribe);
            }

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
                y.OriginalEvolveTarget = y.EvolveTargetHash;
                y.OriginalEvolveLevel = y.EvolveLevel;
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
            return y;
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

        /// <summary>Write a scale value, preserving the slot's original int/float type.</summary>
        private static int SetScaleValue(T2bEntry e, int index, double? value)
        {
            if (!value.HasValue || index < 0 || index >= e.Values.Count) return 0;
            var slot = e.Values[index];
            if (slot.Type == Formats.ValueType.FloatingPoint)
            {
                float f = (float)value.Value;
                if (slot.Value is float cf && cf == f) return 0;
                slot.Value = f;
                return 1;
            }
            if (slot.Type == Formats.ValueType.Integer)
            {
                int iv = (int)System.Math.Round(value.Value);
                if (slot.Value is int ci && ci == iv) return 0;
                slot.Value = iv;
                return 1;
            }
            return 0;
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
