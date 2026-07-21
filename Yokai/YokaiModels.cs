using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Lycoris.Formats;

namespace Lycoris.Yokai
{
    /// <summary>
    /// One yo-kai, resolved across chara_param, chara_base, chara_text, chara_desc_text and
    /// chara_scale. Properties (not fields) so WPF binds two-way. Param fields are unique per
    /// yo-kai (safe to write directly); base/name/desc/scale records can be SHARED, so those
    /// are written only when this row changed them (Original* snapshots), to avoid clobbering.
    /// </summary>
    public sealed class YokaiInfo : INotifyPropertyChanged
    {
        // --- Identity (read-only) ---
        public int ParamHash { get; set; }
        public int BaseHash { get; set; }
        public int NameHash { get; set; }
        public int? DescriptionHash { get; set; }

        // Face-icon model fields (from chara_base) and the resolved .xi path (null if none).
        public int? FileNamePrefix { get; set; }
        public int? FileNameNumber { get; set; }
        public int? FileNameVariant { get; set; }
        public string IconBaseName { get; set; }   // e.g. "y105010"
        public string IconFile { get; set; }        // full path to the .xi, or null

        // --- chara_scale: Scale1..Scale7 (may be shared by BaseHash) ---
        private readonly double?[] _scale = new double?[8];      // indices 1..7 used
        private readonly double?[] _scaleOrig = new double?[8];
        public double? Scale1 { get => _scale[1]; set => SetScale(1, value); }
        public double? Scale2 { get => _scale[2]; set => SetScale(2, value); }
        public double? Scale3 { get => _scale[3]; set => SetScale(3, value); }
        public double? Scale4 { get => _scale[4]; set => SetScale(4, value); }
        public double? Scale5 { get => _scale[5]; set => SetScale(5, value); }
        public double? Scale6 { get => _scale[6]; set => SetScale(6, value); }
        public double? Scale7 { get => _scale[7]; set => SetScale(7, value); }
        /// <summary>Main size multiplier (Scale2) — used in the list view.</summary>
        public double? Scale => _scale[2];

        internal double? GetScale(int i) => _scale[i];
        internal void InitScale(int i, double? v) { _scale[i] = v; _scaleOrig[i] = v; }
        internal void SnapshotScale() { for (int i = 1; i <= 7; i++) _scaleOrig[i] = _scale[i]; }
        internal bool ScaleChanged(int i) => _scale[i] != _scaleOrig[i];

        private void SetScale(int i, double? v)
        {
            if (_scale[i] == v) return;
            _scale[i] = v;
            IsDirty = true;
            OnPropertyChanged("Scale" + i);
            if (i == 2) OnPropertyChanged(nameof(Scale));
        }

        // --- Editable text (chara_text / chara_desc_text) ---
        private string _name, _description;
        public string Name { get => _name; set => SetField(ref _name, value); }
        public string Description { get => _description; set => SetField(ref _description, value); }

        // --- Editable base fields (chara_base — may be shared) ---
        private int? _rank, _tribe;
        public int? Rank { get => _rank; set => SetField(ref _rank, value); }
        public int? Tribe { get => _tribe; set => SetField(ref _tribe, value); }
        public string RankName => YokaiEnums.Rank(_rank);
        public string TribeName => YokaiEnums.Tribe(_tribe);

        // --- Editable param fields (unique per yo-kai) ---
        private bool _show;
        private int? _medal, _resistance, _weakness;
        public bool Show { get => _show; set => SetField(ref _show, value); }
        public int? Medal { get => _medal; set => SetField(ref _medal, value); }
        public int? Resistance { get => _resistance; set => SetField(ref _resistance, value); }
        public int? Weakness { get => _weakness; set => SetField(ref _weakness, value); }
        public string ResistanceName => YokaiEnums.Attribute(_resistance);
        public string WeaknessName => YokaiEnums.Attribute(_weakness);

        private int? _minHp, _maxHp, _minStrength, _maxStrength, _minSpirit, _maxSpirit,
                     _minDefense, _maxDefense, _minSpeed, _maxSpeed;
        public int? MinHp { get => _minHp; set => SetField(ref _minHp, value); }
        public int? MaxHp { get => _maxHp; set => SetField(ref _maxHp, value); }
        public int? MinStrength { get => _minStrength; set => SetField(ref _minStrength, value); }
        public int? MaxStrength { get => _maxStrength; set => SetField(ref _maxStrength, value); }
        public int? MinSpirit { get => _minSpirit; set => SetField(ref _minSpirit, value); }
        public int? MaxSpirit { get => _maxSpirit; set => SetField(ref _maxSpirit, value); }
        public int? MinDefense { get => _minDefense; set => SetField(ref _minDefense, value); }
        public int? MaxDefense { get => _maxDefense; set => SetField(ref _maxDefense, value); }
        public int? MinSpeed { get => _minSpeed; set => SetField(ref _minSpeed, value); }
        public int? MaxSpeed { get => _maxSpeed; set => SetField(ref _maxSpeed, value); }

        // --- Moves: hash + percentage, plus a resolved display name (set by the resolver) ---
        private int? _attackHash, _techniqueHash, _inspiritHash, _guardHash, _soultimateHash, _abilityHash;
        private int? _attackPct, _techniquePct, _inspiritPct, _guardPct;
        public int? AttackHash { get => _attackHash; set => SetField(ref _attackHash, value); }
        public int? TechniqueHash { get => _techniqueHash; set => SetField(ref _techniqueHash, value); }
        public int? InspiritHash { get => _inspiritHash; set => SetField(ref _inspiritHash, value); }
        public int? GuardHash { get => _guardHash; set => SetField(ref _guardHash, value); }
        public int? SoultimateHash { get => _soultimateHash; set => SetField(ref _soultimateHash, value); }
        public int? AbilityHash { get => _abilityHash; set => SetField(ref _abilityHash, value); }
        public int? AttackPct { get => _attackPct; set => SetField(ref _attackPct, value); }
        public int? TechniquePct { get => _techniquePct; set => SetField(ref _techniquePct, value); }
        public int? InspiritPct { get => _inspiritPct; set => SetField(ref _inspiritPct, value); }
        public int? GuardPct { get => _guardPct; set => SetField(ref _guardPct, value); }

        public string AttackName { get; set; }      // filled by MoveResolver when tables are present
        public string TechniqueName { get; set; }
        public string InspiritName { get; set; }
        public string GuardName { get; set; }
        public string SoultimateName { get; set; }
        public string AbilityName { get; set; }

        // Hex mirrors for editing the raw move hashes.
        public string AttackHex { get => Hex(_attackHash); set => AttackHash = ParseHex(value, _attackHash); }
        public string TechniqueHex { get => Hex(_techniqueHash); set => TechniqueHash = ParseHex(value, _techniqueHash); }
        public string InspiritHex { get => Hex(_inspiritHash); set => InspiritHash = ParseHex(value, _inspiritHash); }
        public string GuardHex { get => Hex(_guardHash); set => GuardHash = ParseHex(value, _guardHash); }
        public string SoultimateHex { get => Hex(_soultimateHash); set => SoultimateHash = ParseHex(value, _soultimateHash); }
        public string AbilityHex { get => Hex(_abilityHash); set => AbilityHash = ParseHex(value, _abilityHash); }

        // --- Evolution (CHARA_EVOLVE_INFO in chara_param, indexed by EvolveOffset) ---
        private int? _evolveOffset;                    // -1 / null = none
        public int? EvolveOffset
        {
            get => _evolveOffset;
            set { _evolveOffset = value; OnPropertyChanged(nameof(EvolveOffset)); OnPropertyChanged(nameof(CanEvolve)); }
        }
        internal int? OriginalEvolveOffset { get; set; }
        public bool EvolveOffsetChanged => EvolveOffset != OriginalEvolveOffset;
        private int? _evolveTarget, _evolveLevel;
        public int? EvolveTargetHash { get => _evolveTarget; set => SetField(ref _evolveTarget, value); }
        public int? EvolveLevel { get => _evolveLevel; set => SetField(ref _evolveLevel, value); }
        public string EvolvesToName { get; set; }      // resolved display name of the target
        public bool CanEvolve => EvolveOffset.HasValue && EvolveOffset.Value >= 0;
        internal int? OriginalEvolveTarget { get; set; }
        internal int? OriginalEvolveLevel { get; set; }
        public bool EvolveChanged => EvolveTargetHash != OriginalEvolveTarget || EvolveLevel != OriginalEvolveLevel;
        internal Formats.T2bEntry EvolveEntry { get; set; }

        // --- Blaster T (hackslash_chara_param, keyed by ParamHash) ---
        private int? _btAbility, _btSoul, _btAtkA, _btAtkY, _btAtkX;
        public int? BtAbilityHash { get => _btAbility; set => SetField(ref _btAbility, value); }
        public int? BtSoultimateHash { get => _btSoul; set => SetField(ref _btSoul, value); }
        public int? BtAttackAHash { get => _btAtkA; set => SetField(ref _btAtkA, value); }
        public int? BtAttackYHash { get => _btAtkY; set => SetField(ref _btAtkY, value); }
        public int? BtAttackXHash { get => _btAtkX; set => SetField(ref _btAtkX, value); }
        public bool HasBlasterT => HackslashEntry != null;
        internal Formats.T2bEntry HackslashEntry { get; set; }

        // --- Drops / rewards (battle_chara_param, keyed by ParamHash) ---
        private int? _money, _exp, _drop1, _drop1Rate, _drop2, _drop2Rate;
        public int? Money { get => _money; set => SetField(ref _money, value); }
        public int? Experience { get => _exp; set => SetField(ref _exp, value); }
        public int? Drop1Hash { get => _drop1; set => SetField(ref _drop1, value); }
        public int? Drop1Rate { get => _drop1Rate; set => SetField(ref _drop1Rate, value); }
        public int? Drop2Hash { get => _drop2; set => SetField(ref _drop2, value); }
        public int? Drop2Rate { get => _drop2Rate; set => SetField(ref _drop2Rate, value); }
        public bool HasDrops => BattleEntry != null;
        internal Formats.T2bEntry BattleEntry { get; set; }

        public bool IsDirty { get; set; }
        public bool IsNew { get; set; }

        // Snapshots for shared records (only write when THIS row changed them).
        internal string OriginalName { get; set; }
        internal string OriginalDescription { get; set; }
        internal int? OriginalRank { get; set; }
        internal int? OriginalTribe { get; set; }
        public bool NameChanged => !System.Collections.Generic.EqualityComparer<string>.Default.Equals(Name, OriginalName);
        public bool DescriptionChanged => !System.Collections.Generic.EqualityComparer<string>.Default.Equals(Description, OriginalDescription);
        public bool RankChanged => Rank != OriginalRank;
        public bool TribeChanged => Tribe != OriginalTribe;

        // Links back to source records (null when unresolved).
        internal T2bEntry SourceEntry { get; set; }   // CHARA_PARAM_INFO
        internal T2bEntry BaseEntry { get; set; }      // CHARA_BASE_YOKAI_INFO
        internal T2bEntry NameEntry { get; set; }      // NOUN_INFO (chara_text)
        internal T2bEntry DescEntry { get; set; }      // TEXT_INFO (chara_desc_text)
        internal T2bEntry ScaleEntry { get; set; }     // CHARA_SCALE_INFO

        public string ParamIdHex => $"0x{unchecked((uint)ParamHash):X8}";
        public string BaseHex => $"0x{unchecked((uint)BaseHash):X8}";
        public string DisplayName => string.IsNullOrEmpty(Name) ? ParamIdHex : Name;

        private static string Hex(int? v) => v.HasValue ? $"0x{unchecked((uint)v.Value):X8}" : "";

        private static int? ParseHex(string s, int? fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("0x") || s.StartsWith("0X")) s = s.Substring(2);
            if (uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint u))
                return unchecked((int)u);
            return fallback;
        }

        private void SetField<T>(ref T field, T value, [CallerMemberName] string prop = null)
        {
            if (Equals(field, value)) return;
            field = value;
            IsDirty = true;
            OnPropertyChanged(prop);
            switch (prop)
            {
                case nameof(Rank): OnPropertyChanged(nameof(RankName)); break;
                case nameof(Tribe): OnPropertyChanged(nameof(TribeName)); break;
                case nameof(Resistance): OnPropertyChanged(nameof(ResistanceName)); break;
                case nameof(Weakness): OnPropertyChanged(nameof(WeaknessName)); break;
                case nameof(AttackHash): OnPropertyChanged(nameof(AttackHex)); break;
                case nameof(TechniqueHash): OnPropertyChanged(nameof(TechniqueHex)); break;
                case nameof(InspiritHash): OnPropertyChanged(nameof(InspiritHex)); break;
                case nameof(GuardHash): OnPropertyChanged(nameof(GuardHex)); break;
                case nameof(SoultimateHash): OnPropertyChanged(nameof(SoultimateHex)); break;
                case nameof(AbilityHash): OnPropertyChanged(nameof(AbilityHex)); break;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string prop) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
