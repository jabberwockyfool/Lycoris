using System.ComponentModel;
using System.Runtime.CompilerServices;
using Lycoris.Formats;

namespace Lycoris.Yokai
{
    /// <summary>
    /// One skill from skill_config (SKILL_CONFIG_INFO), with its name resolved from skill_text (NOUN, via
    /// NameID) and description (TEXT, via DescID). The config record is unique per skill (safe to write
    /// directly); the name/description text entries may be shared, so those use Original* snapshots.
    /// </summary>
    public sealed class SkillInfo : INotifyPropertyChanged
    {
        public int SkillConfigID { get; set; }   // record key [0]
        public int NameID { get; set; }           // -> skill_text NOUN
        public int DescID { get; set; }           // -> skill_text TEXT

        private string _name, _description;
        public string Name { get => _name; set => Set(ref _name, value); }
        public string Description { get => _description; set => Set(ref _description, value); }

        private int? _type, _effect, _growth, _power, _hits, _element, _soulCharge, _battleAnim, _soulRange, _ability;
        public int? SkillType { get => _type; set { Set(ref _type, value); OnPropertyChanged(nameof(SkillTypeName)); } }
        public int? EffectID { get => _effect; set { Set(ref _effect, value); OnPropertyChanged(nameof(EffectIDHex)); } }
        public int? SkillGrowth { get => _growth; set => Set(ref _growth, value); }
        public int? Power { get => _power; set => Set(ref _power, value); }
        public int? Hits { get => _hits; set => Set(ref _hits, value); }
        public int? Element { get => _element; set => Set(ref _element, value); }
        public int? SoultChargeSpeed { get => _soulCharge; set => Set(ref _soulCharge, value); }
        public int? BattleAnimation { get => _battleAnim; set { Set(ref _battleAnim, value); OnPropertyChanged(nameof(BattleAnimationHex)); } }
        public int? SoultimateRange { get => _soulRange; set => Set(ref _soulRange, value); }
        public int? SkillAbility { get => _ability; set => Set(ref _ability, value); }

        public bool IsDirty { get; set; }

        internal T2bEntry Entry { get; set; }       // skill_config record
        internal T2bEntry NameEntry { get; set; }   // skill_text NOUN_INFO
        internal T2bEntry DescEntry { get; set; }   // skill_text TEXT_INFO
        internal string OriginalName { get; set; }
        internal string OriginalDescription { get; set; }
        public bool NameChanged => !System.Collections.Generic.EqualityComparer<string>.Default.Equals(Name, OriginalName);
        public bool DescriptionChanged => !System.Collections.Generic.EqualityComparer<string>.Default.Equals(Description, OriginalDescription);

        public string SkillIdHex => $"0x{unchecked((uint)SkillConfigID):X8}";
        public string NameIDHex => $"0x{unchecked((uint)NameID):X8}";
        public string DisplayName => string.IsNullOrEmpty(Name) ? SkillIdHex : Name;
        public string SkillTypeName => YokaiEnums.SkillType(SkillType);

        // Hash-like fields are edited as hex; the string wrappers parse "0x...."/decimal, blank -> null.
        public string EffectIDHex { get => Hex(EffectID); set => EffectID = ParseHex(value); }
        public string BattleAnimationHex { get => Hex(BattleAnimation); set => BattleAnimation = ParseHex(value); }

        private static string Hex(int? v) => v.HasValue ? $"0x{unchecked((uint)v.Value):X8}" : "";
        private static int? ParseHex(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (s.StartsWith("0x") || s.StartsWith("0X")) s = s.Substring(2);
            if (uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint u))
                return unchecked((int)u);
            if (int.TryParse(s, out int i)) return i;
            return null;
        }

        private void Set<T>(ref T field, T value, [CallerMemberName] string prop = null)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            IsDirty = true;
            OnPropertyChanged(prop);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
