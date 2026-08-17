using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Shop editor: pick a shop (one shop_shp*.cfg.bin per shop), see the items it sells, and edit each
    /// line's item, price (empty = the item's default price), stock and availability. Add / remove lines
    /// and save every changed shop back into the mod.
    /// </summary>
    public sealed class ShopEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private readonly ListBox _shopList = new ListBox { Width = 200 };
        private readonly ListBox _itemList = new ListBox { Width = 260 };
        private readonly StackPanel _detail = new StackPanel();
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };
        private readonly TextBlock _count = new TextBlock();

        // Type-to-filter "Item" picker (managed by delegate, not a two-way SelectedValue binding, so filtering
        // — which transiently drops the selected item from the view — never nulls the line's ItemId).
        private ComboBox _itemCombo;
        private System.Windows.Data.ListCollectionView _itemView;
        private bool _itemSync;

        public ShopEditorWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            Owner = owner;
            Title = "Lycoris — Shop Editor";
            Width = 820; Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var addShop = new Button { Content = "+ Add shop", Padding = new Thickness(10, 4, 10, 4) };
            addShop.Click += (s, e) => AddShop();
            var save = new Button { Content = "Save the mod", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            save.Click += (s, e) => Save();
            _count.VerticalAlignment = VerticalAlignment.Center; _count.Foreground = Theme.FgMuted; _count.Margin = new Thickness(10, 0, 0, 0);
            UpdateCount();
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(addShop);
            toolbar.Children.Add(save);
            toolbar.Children.Add(_count);
            DockPanel.SetDock(toolbar, Dock.Top);

            // Left: shops
            _shopList.DisplayMemberPath = "DisplayName";
            _shopList.Margin = new Thickness(6);
            _shopList.SelectionChanged += (s, e) => ShopSelected();
            DockPanel.SetDock(_shopList, Dock.Left);

            // Middle: item list + add/remove
            var addItem = new Button { Content = "+ Add item", Padding = new Thickness(8, 3, 8, 3) };
            addItem.Click += (s, e) => AddItem();
            var delItem = new Button { Content = "Remove", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(6, 0, 0, 0) };
            delItem.Click += (s, e) => RemoveItem();
            var itemBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 6, 4) };
            itemBar.Children.Add(addItem);
            itemBar.Children.Add(delItem);
            DockPanel.SetDock(itemBar, Dock.Top);
            _itemList.DisplayMemberPath = "DisplayName";
            _itemList.Margin = new Thickness(6, 0, 6, 6);
            _itemList.SelectionChanged += (s, e) => { _detail.DataContext = _itemList.SelectedItem; _detail.IsEnabled = _itemList.SelectedItem != null; BindItemCombo(); };
            var middle = new DockPanel { Width = 272 };
            middle.Children.Add(itemBar);
            middle.Children.Add(_itemList);
            DockPanel.SetDock(middle, Dock.Left);

            // Right: item detail
            _detail.Margin = new Thickness(8);
            BuildDetail();
            var right = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            DockPanel.SetDock(_status, Dock.Bottom);

            var root = new DockPanel();
            root.Children.Add(toolbar);
            root.Children.Add(_status);
            root.Children.Add(_shopList);
            root.Children.Add(middle);
            root.Children.Add(right);
            Content = root;

            _shopList.ItemsSource = _db.Shops;
            if (_db.Shops.Count > 0) _shopList.SelectedIndex = 0;
            _status.Text = "Price empty = the item's default price. Removing a line is confirmed on “Save the mod”.";
        }

        private void ShopSelected()
        {
            var shop = _shopList.SelectedItem as ShopFile;
            _itemList.ItemsSource = shop?.Items;
            if (shop != null && shop.Items.Count > 0) _itemList.SelectedIndex = 0;
            else { _detail.DataContext = null; _detail.IsEnabled = false; }
        }

        private void BuildDetail()
        {
            _detail.Children.Add(ItemComboRow());
            _detail.Children.Add(TextRow("Price (empty = default)", "PriceText", 120));
            _detail.Children.Add(NumRow("Max limited stock", "MaxStock"));
            _detail.Children.Add(CheckRow("Has limited stock", "HasLimitedStock"));
            _detail.Children.Add(TextRow("Condition (hex)", "CondHex", 130));
        }

        // ---------- field builders ----------

        private static UIElement Label(string text) =>
            new TextBlock { Text = text, Width = 150, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted };

        /// <summary>The "Item" picker as a type-to-filter combo over the full item catalog (search by typing).</summary>
        private FrameworkElement ItemComboRow()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            sp.Children.Add(Label("Item"));
            _itemCombo = new ComboBox
            {
                Width = 300, IsEditable = true, IsTextSearchEnabled = false, StaysOpenOnEdit = true,
                SelectedValuePath = "Key",   // no DisplayMemberPath: EnumEntry.ToString() == Name (matches SearchableCombo)
            };
            TextSearch.SetTextPath(_itemCombo, "Name");
            _itemView = new System.Windows.Data.ListCollectionView(_db.ItemOptions);
            _itemCombo.ItemsSource = _itemView;
            _itemCombo.SelectionChanged += ItemCombo_SelectionChanged;
            _itemCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(ItemCombo_TextChanged));
            _itemCombo.DropDownClosed += (s, e) => ItemComboResetFilter();
            sp.Children.Add(_itemCombo);
            return sp;
        }

        private void BindItemCombo()
        {
            if (_itemCombo == null) return;
            _itemSync = true;
            _itemView.Filter = null;
            _itemCombo.SelectedValue = (_detail.DataContext as ShopItem)?.ItemId;
            _itemSync = false;
        }

        private void ItemCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_itemSync) return;
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is EnumEntry en && _detail.DataContext is ShopItem si)
                si.ItemId = en.Key;
        }

        private void ItemCombo_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_itemSync) return;
            string t = _itemCombo.Text ?? "";
            _itemView.Filter = string.IsNullOrEmpty(t)
                ? (Predicate<object>)null
                : o => ((EnumEntry)o).Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!_itemCombo.IsDropDownOpen && t.Length > 0) _itemCombo.IsDropDownOpen = true;
        }

        private void ItemComboResetFilter()
        {
            _itemSync = true;
            object val = _itemCombo.SelectedValue;
            _itemView.Filter = null;
            _itemCombo.SelectedValue = val;
            _itemSync = false;
        }

        private static FrameworkElement ComboRow(string label, string valuePath, string sourcePath)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            sp.Children.Add(Label(label));
            var cb = new ComboBox { Width = 300, IsTextSearchEnabled = true, DisplayMemberPath = "Name", SelectedValuePath = "Key" };
            TextSearch.SetTextPath(cb, "Name");
            cb.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(sourcePath));
            cb.SetBinding(Selector.SelectedValueProperty, new Binding(valuePath) { Mode = BindingMode.TwoWay });
            sp.Children.Add(cb);
            return sp;
        }

        private static FrameworkElement TextRow(string label, string path, double width)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            sp.Children.Add(Label(label));
            var tb = new TextBox { Width = width };
            tb.SetBinding(TextBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            return sp;
        }

        private static FrameworkElement NumRow(string label, string path) => TextRow(label, path, 90);

        private static FrameworkElement CheckRow(string label, string path)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            sp.Children.Add(Label(label));
            var chk = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            chk.SetBinding(CheckBox.IsCheckedProperty, new Binding(path) { Mode = BindingMode.TwoWay });
            sp.Children.Add(chk);
            return sp;
        }

        // ---------- add / remove / save ----------

        private void UpdateCount() => _count.Text = $"{_db.Shops.Count} shops";

        private void AddShop()
        {
            var dlg = new AddShopDialog(this) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var shop = _db.AddShop(dlg.ShopCode);
                _shopList.Items.Refresh();
                UpdateCount();
                _shopList.SelectedItem = shop;
                _shopList.ScrollIntoView(shop);
                _status.Text = $"Shop “{shop.Code}” created (hash 0x{(uint)shop.ShopHash:X8}) + registered. Add items, then “Save the mod”.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Add shop", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void AddItem()
        {
            var shop = _shopList.SelectedItem as ShopFile;
            if (shop == null) return;
            try
            {
                var it = _db.AddShopItem(shop);
                _shopList.Items.Refresh();
                _itemList.SelectedItem = it;
                _itemList.ScrollIntoView(it);
                _status.Text = "Line added. Pick an item + price, then “Save the mod”.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Add item", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void RemoveItem()
        {
            var shop = _shopList.SelectedItem as ShopFile;
            var it = _itemList.SelectedItem as ShopItem;
            if (shop == null || it == null) return;
            int idx = _itemList.SelectedIndex;
            _db.RemoveShopItem(shop, it);
            _shopList.Items.Refresh();
            if (_itemList.Items.Count > 0) _itemList.SelectedIndex = Math.Min(idx, _itemList.Items.Count - 1);
            _status.Text = $"Line removed from {shop.Code}. Save to apply.";
        }

        private void Save()
        {
            var f = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            try
            {
                int n = _db.SaveShops();
                _shopList.Items.Refresh();
                _status.Text = n > 0 ? $"Saved — {n} shop file(s) written." : "No shop changes to save.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Save shops", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }

    /// <summary>Small modal asking for a new shop's code (the filename becomes shop_&lt;code&gt;.cfg.bin).</summary>
    internal sealed class AddShopDialog : Window
    {
        private readonly TextBox _code = new TextBox { Text = "shpMOD001" };
        public string ShopCode => _code.Text?.Trim();

        public AddShopDialog(Window owner)
        {
            Owner = owner;
            Title = "Add a shop";
            Width = 400; Height = 200;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var grid = new StackPanel { Margin = new Thickness(12) };
            grid.Children.Add(new TextBlock { Text = "Shop code (letters/digits)", Foreground = Theme.FgMuted });
            grid.Children.Add(_code);
            grid.Children.Add(new TextBlock
            {
                Text = "The file will be shop_<code>.cfg.bin and the shop hash = CRC32(code).\nIt's registered in def_shoplist automatically.",
                Foreground = Theme.FgMuted, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
            });

            var ok = new Button { Content = "Create", IsDefault = true, Width = 90, Margin = new Thickness(0, 12, 6, 0) };
            ok.Click += (s, e) => { DialogResult = !string.IsNullOrWhiteSpace(ShopCode); };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Margin = new Thickness(0, 12, 0, 0) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            grid.Children.Add(btns);

            Content = grid;
            Loaded += (s, e) => { _code.Focus(); _code.SelectAll(); };
        }
    }
}
