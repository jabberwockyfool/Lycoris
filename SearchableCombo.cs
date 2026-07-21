using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Turns a ComboBox into a type-to-filter search over a large option list. Typing filters the
    /// dropdown by name; picking an item writes its Key through the setter. The bound int? value is
    /// managed via get/set delegates (not a two-way SelectedValue binding) so that filtering — which
    /// transiently removes the selected item from the view — never nulls the model value.
    /// </summary>
    internal sealed class SearchableCombo
    {
        private readonly ComboBox _c;
        private readonly List<EnumEntry> _items = new List<EnumEntry>();
        private readonly ListCollectionView _view;
        private readonly Func<YokaiInfo, int?> _get;
        private readonly Action<YokaiInfo, int?> _set;
        private YokaiInfo _y;
        private bool _sync;

        public SearchableCombo(ComboBox c, IEnumerable<EnumEntry> source,
            Func<YokaiInfo, int?> get, Action<YokaiInfo, int?> set)
        {
            _c = c; _get = get; _set = set;
            _items.AddRange(source);
            _view = new ListCollectionView(_items);
            _c.ItemsSource = _view;
            _c.IsEditable = true;
            _c.IsTextSearchEnabled = false;
            _c.StaysOpenOnEdit = true;
            _c.SelectedValuePath = "Key";
            _c.SelectionChanged += OnSelectionChanged;
            _c.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnTextChanged));
            _c.DropDownClosed += (s, e) => ResetFilter();
        }

        /// <summary>Replace the option list (e.g. after loading a different folder).</summary>
        public void SetSource(IEnumerable<EnumEntry> source)
        {
            _sync = true;
            _view.Filter = null;
            _items.Clear();
            _items.AddRange(source);
            _view.Refresh();
            _sync = false;
        }

        /// <summary>Show <paramref name="y"/>'s current value (clears any active search filter).</summary>
        public void Bind(YokaiInfo y)
        {
            _y = y;
            _sync = true;
            _view.Filter = null;
            _c.SelectedValue = y != null ? _get(y) : null;
            _sync = false;
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_sync || _y == null) return;
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is EnumEntry en) _set(_y, en.Key);
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_sync) return;
            string t = _c.Text ?? "";
            _view.Filter = string.IsNullOrEmpty(t)
                ? (Predicate<object>)null
                : o => ((EnumEntry)o).Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!_c.IsDropDownOpen && t.Length > 0) _c.IsDropDownOpen = true;
        }

        /// <summary>Clear the filter but keep the current selection/text.</summary>
        private void ResetFilter()
        {
            _sync = true;
            object val = _c.SelectedValue;
            _view.Filter = null;
            _c.SelectedValue = val;
            _sync = false;
        }
    }
}
