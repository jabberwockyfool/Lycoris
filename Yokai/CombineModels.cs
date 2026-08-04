using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Lycoris.Formats;

namespace Lycoris.Yokai
{
    /// <summary>
    /// Shared lookup context for combine recipes: the yo-kai / item dropdown option lists and fast
    /// id→name maps so a recipe can render "Base + Material → Result" and offer the right dropdown.
    /// </summary>
    public sealed class CombineContext
    {
        public IList<EnumEntry> YokaiOptions { get; }
        public IList<EnumEntry> ItemOptions { get; }
        private readonly Dictionary<int, string> _yokai;
        private readonly Dictionary<int, string> _item;

        public CombineContext(IList<EnumEntry> yokai, IList<EnumEntry> item)
        {
            YokaiOptions = yokai; ItemOptions = item;
            _yokai = ToMap(yokai); _item = ToMap(item);
        }

        private static Dictionary<int, string> ToMap(IList<EnumEntry> list)
        {
            var d = new Dictionary<int, string>();
            if (list != null) foreach (var e in list) if (!d.ContainsKey(e.Key)) d[e.Key] = e.Name;
            return d;
        }

        public string Name(bool isItem, int id)
        {
            var d = isItem ? _item : _yokai;
            return d.TryGetValue(id, out var n) && !string.IsNullOrEmpty(n) ? n : $"0x{(uint)id:X8}";
        }
    }

    /// <summary>
    /// One COMBINE_INFO recipe: a Base + a Material produce a Result. Each of the three can be either a
    /// yo-kai (a chara_param ParamID) or an item (an item ID), flagged by the paired *IsItem field. A
    /// GlobalBitFlagID gates the recipe behind a story flag; FusionType categorises it.
    /// </summary>
    public sealed class CombineRecipe : INotifyPropertyChanged
    {
        private readonly CombineContext _ctx;
        internal T2bEntry Source;
        internal bool IsNew;

        public CombineRecipe(CombineContext ctx) { _ctx = ctx; }

        private bool _baseIsItem, _matIsItem, _resIsItem;
        private int _baseId, _matId, _resId, _flag, _type;

        public bool BaseIsItem { get => _baseIsItem; set { if (SetField(ref _baseIsItem, value)) { OnChanged(nameof(BaseOptions)); OnChanged(nameof(DisplayName)); } } }
        public int BaseId { get => _baseId; set { if (SetField(ref _baseId, value)) OnChanged(nameof(DisplayName)); } }
        public bool MaterialIsItem { get => _matIsItem; set { if (SetField(ref _matIsItem, value)) { OnChanged(nameof(MaterialOptions)); OnChanged(nameof(DisplayName)); } } }
        public int MaterialId { get => _matId; set { if (SetField(ref _matId, value)) OnChanged(nameof(DisplayName)); } }
        public bool ResultIsItem { get => _resIsItem; set { if (SetField(ref _resIsItem, value)) { OnChanged(nameof(ResultOptions)); OnChanged(nameof(DisplayName)); } } }
        public int ResultId { get => _resId; set { if (SetField(ref _resId, value)) OnChanged(nameof(DisplayName)); } }
        public int FlagId { get => _flag; set { if (SetField(ref _flag, value)) OnChanged(nameof(FlagHex)); } }
        public int FusionType { get => _type; set => SetField(ref _type, value); }

        public string FlagHex
        {
            get => $"0x{(uint)_flag:X8}";
            set { if (TryParseHex(value, out int v)) FlagId = v; }
        }

        public IEnumerable BaseOptions => BaseIsItem ? (IEnumerable)_ctx.ItemOptions : _ctx.YokaiOptions;
        public IEnumerable MaterialOptions => MaterialIsItem ? (IEnumerable)_ctx.ItemOptions : _ctx.YokaiOptions;
        public IEnumerable ResultOptions => ResultIsItem ? (IEnumerable)_ctx.ItemOptions : _ctx.YokaiOptions;

        public string DisplayName =>
            $"{_ctx.Name(BaseIsItem, BaseId)}  +  {_ctx.Name(MaterialIsItem, MaterialId)}   →   {_ctx.Name(ResultIsItem, ResultId)}";

        public bool IsDirty { get; internal set; }

        private static bool TryParseHex(string s, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x") || s.StartsWith("0X")) s = s.Substring(2);
            if (uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint u)) { value = unchecked((int)u); return true; }
            if (int.TryParse(s, out int i)) { value = i; return true; }
            return false;
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string prop = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            IsDirty = true;
            OnChanged(prop);
            return true;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
