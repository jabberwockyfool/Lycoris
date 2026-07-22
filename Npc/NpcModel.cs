using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Lycoris.Npc
{
    /// <summary>
    /// One NPC = the NPCMake TOML config (v1.2.0 schema). All 11 keys are exposed as bindable properties
    /// with the same defaults as NPCMake's generated template. The NPC's in-game id is not stored here —
    /// NPCMake derives it as CRC32(utf8(NpcName)) at compile time.
    /// </summary>
    public sealed class NpcModel : INotifyPropertyChanged
    {
        private string _npcName = "MyNPC";
        private int _baseId;
        private double _npcX, _npcY, _npcZ;
        private int _npcRotation;                       // NPCMake casts to int — keep integer
        private string _chapterCode = "c11";
        private string _mapId = "t101i01";
        private string _onTalk = "$local1 = log(\"Hello, world!\");";
        private string _appearCond = "0";
        private bool _isYw1;
        private string _npcType = "HUMAN";              // "HUMAN"(2) / "YOKAI"(0) / raw int as string

        public string NpcName { get => _npcName; set { if (Set(ref _npcName, value)) OnPropertyChanged(nameof(DisplayName)); } }
        public int BaseId { get => _baseId; set { if (Set(ref _baseId, value)) OnPropertyChanged(nameof(BaseIdHex)); } }
        public double NpcX { get => _npcX; set => Set(ref _npcX, value); }
        public double NpcY { get => _npcY; set => Set(ref _npcY, value); }
        public double NpcZ { get => _npcZ; set => Set(ref _npcZ, value); }
        public int NpcRotation { get => _npcRotation; set => Set(ref _npcRotation, value); }
        public string ChapterCode { get => _chapterCode; set => Set(ref _chapterCode, value); }
        public string MapID { get => _mapId; set => Set(ref _mapId, value); }
        public string OnTalk { get => _onTalk; set => Set(ref _onTalk, value); }
        public string AppearCond { get => _appearCond; set => Set(ref _appearCond, value); }
        public bool IsYw1 { get => _isYw1; set => Set(ref _isYw1, value); }
        public string NpcType { get => _npcType; set => Set(ref _npcType, value); }

        /// <summary>BaseId edited as hex ("0x…"); blank clears to 0.</summary>
        public string BaseIdHex
        {
            get => $"0x{unchecked((uint)_baseId):X8}";
            set => BaseId = ParseHex(value);
        }

        public string DisplayName => string.IsNullOrEmpty(NpcName) ? "(sans nom)" : NpcName;

        private static int ParseHex(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            if (s.StartsWith("0x") || s.StartsWith("0X")) s = s.Substring(2);
            if (uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint u))
                return unchecked((int)u);
            if (int.TryParse(s, out int i)) return i;
            return 0;
        }

        public NpcModel Clone() => new NpcModel
        {
            _npcName = _npcName, _baseId = _baseId, _npcX = _npcX, _npcY = _npcY, _npcZ = _npcZ,
            _npcRotation = _npcRotation, _chapterCode = _chapterCode, _mapId = _mapId, _onTalk = _onTalk,
            _appearCond = _appearCond, _isYw1 = _isYw1, _npcType = _npcType,
        };

        private bool Set<T>(ref T field, T value, [CallerMemberName] string prop = null)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(prop);
            return true;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
