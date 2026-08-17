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

        // --- daily-fight mode (an NPC that triggers a once-a-day battle: 4 talk events) ---
        private bool _isDailyFight;
        private bool _reuseExistingEvents;               // wire the NPC to 4 events that ALREADY exist (don't regenerate)
        private string _dailyFightEvent = "";           // base event name (evXX_YYY0); +10/+20/+30 derived
        private string _tomorrowText = "I'll play with you next time,\nso just be patient until then!";
        private string _dailyBattle = "";               // battle id for load_battle_ev (first + repeat events)
        private string _dailyBustups = "";              // always-shown bustup models, one per line
        private bool _differentiateGender;              // insert a get_player_type() branch for a girl/boy bustup
        private string _girlBustup = "";                // bustup when get_player_type()==2 (Katie/Hailey)
        private string _boyBustup = "";                 // bustup otherwise (Nate)
        private string _dailyModel = "";                // the NPC yokai model (PlayerTurnTargetStart target)
        private string _introText = "A challenger has appeared!";
        private string _acceptText = "Then let's battle!";
        private string _declineText = "Come back anytime.";
        private string _repeatText = "Back for another round? Let's go!";
        private string _victoryText = "Grrr… you got lucky this time!";
        private string _lossText = "Ha! Better luck next time!";
        // Male variants (used when DifferentiateGender is on; the primary fields are the female/Katie version).
        private string _introTextMale = "";
        private string _repeatTextMale = "";
        private string _victoryTextMale = "";
        private string _lossTextMale = "";

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

        /// <summary>When true, the NPC is compiled as a daily-fight trigger (4 talk events + battle), cloned
        /// from a vanilla daily NPC in the map. Requires <see cref="DailyFightEvent"/>.</summary>
        public bool IsDailyFight { get => _isDailyFight; set => Set(ref _isDailyFight, value); }

        /// <summary>Base event name (evXX_YYY0) created in the Event editor; +10/+20/+30 give the repeat/win/lose events.</summary>
        public string DailyFightEvent { get => _dailyFightEvent; set => Set(ref _dailyFightEvent, value); }

        /// <summary>When true, the 4 events (base/+10/+20/+30) ALREADY exist — only wire the NPC's talk/triggers to
        /// them; don't regenerate the .xq / event_set_config / dialogue. The base event name must match the existing
        /// events (the daily flag = CRC32(base name)).</summary>
        public bool ReuseExistingEvents { get => _reuseExistingEvents; set => Set(ref _reuseExistingEvents, value); }

        /// <summary>The overworld "come back tomorrow" line the NPC says once the daily fight is done.</summary>
        public string TomorrowText { get => _tomorrowText; set => Set(ref _tomorrowText, value); }

        /// <summary>Battle id loaded by load_battle_ev in the first/repeat events.</summary>
        public string DailyBattle { get => _dailyBattle; set => Set(ref _dailyBattle, value); }
        /// <summary>Always-shown bustup models (one per line).</summary>
        public string DailyBustups { get => _dailyBustups; set => Set(ref _dailyBustups, value); }
        /// <summary>Insert a get_player_type() branch so Katie/Hailey and Nate see a different bustup.</summary>
        public bool DifferentiateGender { get => _differentiateGender; set => Set(ref _differentiateGender, value); }
        public string GirlBustup { get => _girlBustup; set => Set(ref _girlBustup, value); }
        public string BoyBustup { get => _boyBustup; set => Set(ref _boyBustup, value); }
        /// <summary>NPC yokai model (e.g. y456000_01) — the PlayerTurnTargetStart target in win/lose events.</summary>
        public string DailyModel { get => _dailyModel; set => Set(ref _dailyModel, value); }
        public string IntroText { get => _introText; set => Set(ref _introText, value); }
        public string AcceptText { get => _acceptText; set => Set(ref _acceptText, value); }
        public string DeclineText { get => _declineText; set => Set(ref _declineText, value); }
        public string RepeatText { get => _repeatText; set => Set(ref _repeatText, value); }
        public string VictoryText { get => _victoryText; set => Set(ref _victoryText, value); }
        public string LossText { get => _lossText; set => Set(ref _lossText, value); }
        public string IntroTextMale { get => _introTextMale; set => Set(ref _introTextMale, value); }
        public string RepeatTextMale { get => _repeatTextMale; set => Set(ref _repeatTextMale, value); }
        public string VictoryTextMale { get => _victoryTextMale; set => Set(ref _victoryTextMale, value); }
        public string LossTextMale { get => _lossTextMale; set => Set(ref _lossTextMale, value); }

        /// <summary>BaseId edited as hex ("0x…"); blank clears to 0.</summary>
        public string BaseIdHex
        {
            get => $"0x{unchecked((uint)_baseId):X8}";
            set => BaseId = ParseHex(value);
        }

        public string DisplayName => string.IsNullOrEmpty(NpcName) ? "(no name)" : NpcName;

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
            _isDailyFight = _isDailyFight, _reuseExistingEvents = _reuseExistingEvents,
            _dailyFightEvent = _dailyFightEvent, _tomorrowText = _tomorrowText,
            _dailyBattle = _dailyBattle, _dailyBustups = _dailyBustups, _differentiateGender = _differentiateGender,
            _girlBustup = _girlBustup, _boyBustup = _boyBustup, _dailyModel = _dailyModel,
            _introText = _introText, _acceptText = _acceptText, _declineText = _declineText,
            _repeatText = _repeatText, _victoryText = _victoryText, _lossText = _lossText,
            _introTextMale = _introTextMale, _repeatTextMale = _repeatTextMale,
            _victoryTextMale = _victoryTextMale, _lossTextMale = _lossTextMale,
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
