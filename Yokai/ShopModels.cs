using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Lycoris.Formats;

namespace Lycoris.Yokai
{
    /// <summary>Shared lookup for shop items: the item dropdown list + a fast id→name map.</summary>
    public sealed class ShopContext
    {
        public IList<EnumEntry> ItemOptions { get; }
        private readonly Dictionary<int, string> _item = new Dictionary<int, string>();

        public ShopContext(IList<EnumEntry> items)
        {
            ItemOptions = items;
            if (items != null) foreach (var e in items) if (!_item.ContainsKey(e.Key)) _item[e.Key] = e.Name;
        }

        public string Name(int id) => _item.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : $"0x{(uint)id:X8}";
    }

    /// <summary>
    /// One line sold in a shop: a SHOP_CONFIG_INFO record (item + stock) plus its linked
    /// SHOP_VALID_CONDITION record (price + availability). Price -1 means "use the item's default price".
    /// </summary>
    public sealed class ShopItem : INotifyPropertyChanged
    {
        private readonly ShopContext _ctx;
        internal T2bEntry Config;
        internal T2bEntry Condition;   // may be null when the row has no linked condition
        internal Action MarkDirty;

        public ShopItem(ShopContext ctx) { _ctx = ctx; }

        private int _itemId, _price = -1, _maxStock;
        private bool _hasStock;
        private int _cond;

        public int ItemId { get => _itemId; set { if (SetField(ref _itemId, value)) OnChanged(nameof(DisplayName)); } }
        public int Price { get => _price; set { if (SetField(ref _price, value)) { OnChanged(nameof(PriceText)); OnChanged(nameof(DisplayName)); } } }
        public int MaxStock { get => _maxStock; set => SetField(ref _maxStock, value); }
        public bool HasLimitedStock { get => _hasStock; set => SetField(ref _hasStock, value); }
        public int Cond { get => _cond; set { if (SetField(ref _cond, value)) OnChanged(nameof(CondHex)); } }

        /// <summary>Empty = default price (stored as -1); otherwise the explicit price.</summary>
        public string PriceText
        {
            get => _price < 0 ? "" : _price.ToString();
            set
            {
                if (string.IsNullOrWhiteSpace(value)) { Price = -1; return; }
                if (int.TryParse(value.Trim(), out int v)) Price = v;
            }
        }

        public string CondHex
        {
            get => $"0x{(uint)_cond:X8}";
            set
            {
                var s = (value ?? "").Trim();
                if (s.StartsWith("0x") || s.StartsWith("0X")) s = s.Substring(2);
                if (uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint u)) Cond = unchecked((int)u);
                else if (int.TryParse(s, out int i)) Cond = i;
            }
        }

        public IEnumerable ItemOptions => _ctx.ItemOptions;

        public string DisplayName => $"{_ctx.Name(_itemId)}  —  {(_price < 0 ? "default price" : _price + "G")}";

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string prop = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            MarkDirty?.Invoke();
            OnChanged(prop);
            return true;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    /// <summary>One shop file (shop_shp&lt;code&gt;.cfg.bin): its item lines, kept in load order.</summary>
    public sealed class ShopFile
    {
        public string FilePath { get; set; }
        public string Code { get; set; }        // friendly code from the filename, e.g. "shpN001"
        public int ShopHash { get; set; }
        internal T2bFile Data;
        public ObservableCollection<ShopItem> Items { get; } = new ObservableCollection<ShopItem>();
        public bool Dirty { get; internal set; }

        public string DisplayName => $"{Code}   ({Items.Count})";
        public override string ToString() => DisplayName;
    }
}
