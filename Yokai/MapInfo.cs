using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Lycoris.Formats;

namespace Lycoris.Yokai
{
    /// <summary>
    /// One map from map_config (MAP_INFO), with its display name resolved from system_text (TEXT_INFO via
    /// NounID). MapID and NounID are both CRC32 of the map's folder name (e.g. "t101g00"). A map needs a
    /// MAP_INFO entry or it bugs in-game even if its model files are present.
    /// </summary>
    public sealed class MapInfo : INotifyPropertyChanged
    {
        public int MapId { get; set; }
        public int NounID { get; set; }

        private string _folder, _name;
        public string MapFolderName { get => _folder; set { if (Set(ref _folder, value)) OnPropertyChanged(nameof(DisplayName)); } }
        public string Name { get => _name; set { if (Set(ref _name, value)) OnPropertyChanged(nameof(DisplayName)); } }

        private int? _showCard;
        public int? ShowMapCard { get => _showCard; set => Set(ref _showCard, value); }

        // Unknown/less-documented fields [1..8] of MAP_INFO (kept editable).
        private readonly int?[] _unk = new int?[9];
        public int? Unk1 { get => _unk[1]; set => SetUnk(1, value); }
        public int? Unk2 { get => _unk[2]; set => SetUnk(2, value); }
        public int? Unk3 { get => _unk[3]; set => SetUnk(3, value); }
        public int? Unk4 { get => _unk[4]; set => SetUnk(4, value); }
        public int? Unk5 { get => _unk[5]; set => SetUnk(5, value); }
        public int? Unk6 { get => _unk[6]; set => SetUnk(6, value); }
        public int? Unk7 { get => _unk[7]; set => SetUnk(7, value); }
        public int? Unk8 { get => _unk[8]; set => SetUnk(8, value); }
        internal int? GetUnk(int i) => _unk[i];
        internal void InitUnk(int i, int? v) => _unk[i] = v;

        public bool IsDirty { get; set; }

        internal T2bEntry Entry { get; set; }        // map_config MAP_INFO
        internal T2bEntry NameEntry { get; set; }    // system_text TEXT_INFO
        internal string OriginalName { get; set; }
        public bool NameChanged => !System.Collections.Generic.EqualityComparer<string>.Default.Equals(Name, OriginalName);

        public string MapIdHex => $"0x{unchecked((uint)MapId):X8}";
        public string NounIdHex => $"0x{unchecked((uint)NounID):X8}";
        public string DisplayName => !string.IsNullOrEmpty(Name)
            ? $"{Name} ({MapFolderName})" : (string.IsNullOrEmpty(MapFolderName) ? MapIdHex : MapFolderName);

        /// <summary>Recompute MapID and NounID as CRC32 of the current folder name.</summary>
        public void RecomputeIds()
        {
            int id = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(MapFolderName ?? "")));
            MapId = id; NounID = id;
            IsDirty = true;
            OnPropertyChanged(nameof(MapIdHex));
            OnPropertyChanged(nameof(NounIdHex));
        }

        private void SetUnk(int i, int? v)
        {
            if (_unk[i] == v) return;
            _unk[i] = v; IsDirty = true;
            OnPropertyChanged("Unk" + i);
        }

        private bool Set<T>(ref T field, T value, [CallerMemberName] string prop = null)
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value; IsDirty = true; OnPropertyChanged(prop);
            return true;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
