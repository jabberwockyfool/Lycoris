using System.ComponentModel;
using System.Runtime.CompilerServices;
using Lycoris.Formats;

namespace Lycoris.Yokai
{
    /// <summary>
    /// One item from item_config, with its name/description resolved from item_text. The item_config
    /// record is unique per item (safe to write directly); the name/description text entries may be
    /// shared, so those are written only when this item changed them (Original* snapshots).
    /// </summary>
    public sealed class ItemInfo : INotifyPropertyChanged
    {
        public int ItemId { get; set; }
        public int NounTextID { get; set; }
        public int DescTextID { get; set; }
        public string RecordType { get; set; }

        private string _name, _description;
        public string Name { get => _name; set => Set(ref _name, value); }
        public string Description { get => _description; set => Set(ref _description, value); }

        private int? _sort, _type, _carry, _sell, _shop, _iconX, _iconY;
        public int? InventorySort { get => _sort; set => Set(ref _sort, value); }
        public int? ItemType { get => _type; set => Set(ref _type, value); }
        public int? CarryCap { get => _carry; set => Set(ref _carry, value); }
        public int? SellPrice { get => _sell; set => Set(ref _sell, value); }
        public int? ShopPrice { get => _shop; set => Set(ref _shop, value); }
        public int? IconPosX { get => _iconX; set => Set(ref _iconX, value); }
        public int? IconPosY { get => _iconY; set => Set(ref _iconY, value); }

        public bool IsDirty { get; set; }

        internal T2bEntry Entry { get; set; }       // item_config record
        internal T2bEntry NameEntry { get; set; }   // item_text NOUN_INFO
        internal T2bEntry DescEntry { get; set; }   // item_text TEXT_INFO
        internal string OriginalName { get; set; }
        internal string OriginalDescription { get; set; }
        public bool NameChanged => !System.Collections.Generic.EqualityComparer<string>.Default.Equals(Name, OriginalName);
        public bool DescriptionChanged => !System.Collections.Generic.EqualityComparer<string>.Default.Equals(Description, OriginalDescription);

        public string ItemIdHex => $"0x{unchecked((uint)ItemId):X8}";
        public string DisplayName => string.IsNullOrEmpty(Name) ? ItemIdHex : Name;

        private void Set<T>(ref T field, T value, [CallerMemberName] string prop = null)
        {
            if (Equals(field, value)) return;
            field = value;
            IsDirty = true;
            OnPropertyChanged(prop);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
