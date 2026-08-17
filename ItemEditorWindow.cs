using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lycoris.Formats;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Standalone item editor (separate from the yo-kai editor): a searchable list of items with their
    /// name/description, inventory sort, type, carry cap, sell/shop prices, atlas icon position, and the
    /// item_icon.xi icon (view + replace by PNG). Edits are saved into item_config / item_text / item_icon
    /// inside the mod.
    /// </summary>
    public sealed class ItemEditorWindow : Window
    {
        private const int Cell = 32;
        private readonly YokaiDatabase _db;
        private readonly ListBox _list = new ListBox();
        private readonly TextBox _search = new TextBox();
        private readonly Image _iconImg = new Image { Stretch = Stretch.Uniform };
        private readonly StackPanel _fields = new StackPanel();
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };
        private ICollectionView _view;
        private string _moddedAtlas;
        private readonly TextBlock _countText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted };

        public ItemEditorWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            Owner = owner;
            Title = "Lycoris — Item Editor";
            Width = 760; Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _moddedAtlas = _db.ModItemAtlasFile;

            // Toolbar
            var add = new Button { Content = "+ Add", Padding = new Thickness(10, 4, 10, 4) };
            add.Click += (s, e) => AddItem();
            var dup = new Button { Content = "Duplicate", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            dup.Click += (s, e) => DuplicateItem();
            var del = new Button { Content = "Delete", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            del.Click += (s, e) => DeleteItem();
            var save = new Button { Content = "Save the mod", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            save.Click += (s, e) => Save();
            var switchBtn = new Button { Content = "⇄ Equip transform…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            switchBtn.Click += (s, e) => new CharaSwitchWindow(this, _db).ShowDialog();
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(add);
            toolbar.Children.Add(dup);
            toolbar.Children.Add(del);
            toolbar.Children.Add(save);
            toolbar.Children.Add(switchBtn);
            _countText.Margin = new Thickness(10, 0, 0, 0);
            UpdateCount();
            toolbar.Children.Add(_countText);
            DockPanel.SetDock(toolbar, Dock.Top);

            // Left: search + list
            var left = new DockPanel { Width = 240, Margin = new Thickness(6) };
            _search.Margin = new Thickness(0, 0, 0, 4);
            _search.TextChanged += (s, e) => _view?.Refresh();
            DockPanel.SetDock(_search, Dock.Top);
            _list.DisplayMemberPath = "DisplayName";
            _list.SelectionChanged += List_SelectionChanged;
            left.Children.Add(_search);
            left.Children.Add(_list);
            DockPanel.SetDock(left, Dock.Left);

            // Right: fields + icon
            _fields.Margin = new Thickness(6);
            BuildFields();
            var right = new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            DockPanel.SetDock(_status, Dock.Bottom);

            var root = new DockPanel();
            root.Children.Add(toolbar);
            root.Children.Add(_status);
            root.Children.Add(left);
            root.Children.Add(right);
            Content = root;

            _view = CollectionViewSource.GetDefaultView(_db.Items);
            _view.Filter = Filter;
            _list.ItemsSource = _view;
            if (_db.Items.Count > 0) _list.SelectedIndex = 0;
        }

        private bool Filter(object o)
        {
            string q = _search.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            var it = (ItemInfo)o;
            return (it.Name != null && it.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                   || it.ItemIdHex.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void BuildFields()
        {
            // Header: icon + id/type
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var iconBorder = new Border
            {
                Width = 56, Height = 56, BorderBrush = Theme.Border, BorderThickness = new Thickness(1),
                Background = Theme.FieldBg, Margin = new Thickness(0, 0, 10, 0)
            };
            RenderOptions.SetBitmapScalingMode(_iconImg, BitmapScalingMode.NearestNeighbor);
            iconBorder.Child = _iconImg;
            header.Children.Add(iconBorder);

            var idPanel = new StackPanel();
            idPanel.Children.Add(ReadOnlyRow("ItemID", "ItemIdHex"));
            idPanel.Children.Add(ReadOnlyRow("Type", "RecordType"));
            header.Children.Add(idPanel);
            _fields.Children.Add(header);

            var iconBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var pick = new Button { Content = "Choose position in the atlas…", Padding = new Thickness(8, 2, 8, 2) };
            pick.Click += (s, e) => PickPos();
            var repl = new Button { Content = "Replace the icon (PNG)", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            repl.Click += (s, e) => ReplaceIcon();
            iconBtns.Children.Add(pick);
            iconBtns.Children.Add(repl);
            _fields.Children.Add(iconBtns);

            _fields.Children.Add(TextRow("Name", "Name", 220));
            _fields.Children.Add(DescRow());
            _fields.Children.Add(NumRow("Inventory order", "InventorySort"));
            _fields.Children.Add(NumRow("Item type", "ItemType"));
            _fields.Children.Add(NumRow("Carry cap", "CarryCap"));
            _fields.Children.Add(NumRow("Sell price", "SellPrice"));
            _fields.Children.Add(NumRow("Shop price", "ShopPrice"));
            _fields.Children.Add(NumRow("Icon X", "IconPosX"));
            _fields.Children.Add(NumRow("Icon Y", "IconPosY"));
        }

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var it = _list.SelectedItem as ItemInfo;
            _fields.DataContext = it;
            _fields.IsEnabled = it != null;
            _iconImg.Source = IconImage(it);
        }

        /// <summary>The item's real 64×64 icon (its individual item_&lt;NNNN&gt;.xi from GlobalIconIndex), falling
        /// back to the atlas thumbnail cell when there's no individual file.</summary>
        private BitmapSource IconImage(ItemInfo it)
        {
            if (it?.GlobalIconIndex != null)
            {
                string f = _db.ItemIconFile(it.GlobalIconIndex.Value);
                if (f != null) { try { var img = Imgc.Decode(System.IO.File.ReadAllBytes(f)); return ToBitmap(img.Bgra, img.Width, img.Height); } catch { } }
            }
            return CropIcon(it);
        }

        // ---------- field builders ----------

        private static UIElement Label(string text) =>
            new TextBlock { Text = text, Width = 130, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted };

        private FrameworkElement ReadOnlyRow(string label, string path)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            sp.Children.Add(new TextBlock { Text = label + ": ", Foreground = Theme.FgMuted });
            var tb = new TextBlock { FontFamily = new FontFamily("Consolas") };
            tb.SetBinding(TextBlock.TextProperty, new Binding(path));
            sp.Children.Add(tb);
            return sp;
        }

        private FrameworkElement TextRow(string label, string path, double width)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label(label));
            var tb = new TextBox { Width = width };
            tb.SetBinding(TextBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            return sp;
        }

        private FrameworkElement NumRow(string label, string path) => TextRow(label, path, 90);

        private FrameworkElement DescRow()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label("Description"));
            var tb = new TextBox { Width = 300, Height = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            tb.SetBinding(TextBox.TextProperty, new Binding("Description") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            return sp;
        }

        // ---------- icon ----------

        private string AtlasPath() =>
            _moddedAtlas != null && System.IO.File.Exists(_moddedAtlas) ? _moddedAtlas : _db.ItemAtlasFile;

        private BitmapSource CropIcon(ItemInfo it)
        {
            string atlas = AtlasPath();
            if (it?.IconPosX == null || it.IconPosY == null || atlas == null) return null;
            try
            {
                var img = Imgc.Decode(System.IO.File.ReadAllBytes(atlas));
                int x = it.IconPosX.Value * Cell, y = it.IconPosY.Value * Cell;
                if (x + Cell > img.Width || y + Cell > img.Height) return null;
                var cell = new byte[Cell * Cell * 4];
                for (int ry = 0; ry < Cell; ry++)
                    Array.Copy(img.Bgra, ((y + ry) * img.Width + x) * 4, cell, ry * Cell * 4, Cell * 4);
                return ToBitmap(cell, Cell, Cell);
            }
            catch { return null; }
        }

        private void PickPos()
        {
            var it = _list.SelectedItem as ItemInfo;
            string atlas = AtlasPath();
            if (it == null || atlas == null) return;
            try
            {
                var img = Imgc.Decode(System.IO.File.ReadAllBytes(atlas));
                var picker = new AtlasPickerWindow(this, ToBitmap(img.Bgra, img.Width, img.Height), Cell, it.IconPosX ?? 0, it.IconPosY ?? 0);
                if (picker.ShowDialog() == true)
                {
                    it.IconPosX = picker.PickedX;
                    it.IconPosY = picker.PickedY;
                    _iconImg.Source = CropIcon(it);
                    _status.Text = $"{it.DisplayName} — icon at ({picker.PickedX}, {picker.PickedY}).";
                }
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Item atlas"); }
        }

        private void ReplaceIcon()
        {
            var it = _list.SelectedItem as ItemInfo;
            if (it == null) return;
            if (it.GlobalIconIndex == null)
            { DarkMessage.Show("This item has no icon number (GlobalIconIndex / field 5).", "Item icon"); return; }
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG images|*.png", Title = "Item icon — PNG 64×64" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                int num = it.GlobalIconIndex.Value;
                byte[] bgra64 = PngToBgra(dlg.FileName, 64, 64);

                // The REAL in-game icon is the individual 64×64 item_<NNNN>.xi (ETC1A4). Write that — preserving
                // its format via an existing item_*.xi as the template.
                string dir = _db.ItemIconWriteDir;
                if (dir == null) { DarkMessage.Show("Open a mod folder first — the icon is written into it.", "Item icon"); return; }
                string tplPath = _db.ItemIconFile(num) ?? _db.ItemIconFile(1);
                byte[] xi = tplPath != null
                    ? Imgc.EncodeXiPreserve(System.IO.File.ReadAllBytes(tplPath), bgra64, 64, 64)
                    : Imgc.EncodeXi(bgra64, 64, 64);
                System.IO.Directory.CreateDirectory(dir);
                string target = System.IO.Path.Combine(dir, "item_" + num.ToString("0000") + ".xi");
                System.IO.File.WriteAllBytes(target, xi);

                // Also refresh the atlas thumbnail cell (32×32 downscale) so the item grid matches, best-effort.
                string atlasMsg = UpdateAtlasThumb(it, bgra64);

                _iconImg.Source = IconImage(it);
                _status.Text = $"Icon replaced: item_{num:0000}.xi (64×64).{atlasMsg}";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Item icon error", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        /// <summary>Update the 32×32 atlas thumbnail (downscaled) for the grid view; preserves the atlas format.</summary>
        private string UpdateAtlasThumb(ItemInfo it, byte[] bgra64)
        {
            if (it.IconPosX == null || it.IconPosY == null) return "";
            string atlas = AtlasPath();
            if (atlas == null) return "";
            try
            {
                byte[] rawAtlas = System.IO.File.ReadAllBytes(atlas);
                var img = Imgc.Decode(rawAtlas);
                int x = it.IconPosX.Value * Cell, y = it.IconPosY.Value * Cell;
                if (x + Cell > img.Width || y + Cell > img.Height) return "";
                byte[] cell = Downscale2x(bgra64, 64);   // 64 -> 32
                for (int ry = 0; ry < Cell; ry++)
                    Array.Copy(cell, ry * Cell * 4, img.Bgra, ((y + ry) * img.Width + x) * 4, Cell * 4);
                string target = _db.MirrorToMod(atlas);
                System.IO.File.WriteAllBytes(target, Imgc.EncodeXiPreserve(rawAtlas, img.Bgra, img.Width, img.Height));
                _moddedAtlas = target;
                return " (+ atlas thumb)";
            }
            catch { return ""; }
        }

        /// <summary>Average-downscale a square BGRA buffer by 2× (e.g. 64→32).</summary>
        private static byte[] Downscale2x(byte[] src, int srcSize)
        {
            int dst = srcSize / 2;
            var outp = new byte[dst * dst * 4];
            for (int y = 0; y < dst; y++)
                for (int x = 0; x < dst; x++)
                    for (int c = 0; c < 4; c++)
                    {
                        int sx = x * 2, sy = y * 2;
                        int sum = src[((sy) * srcSize + sx) * 4 + c] + src[((sy) * srcSize + sx + 1) * 4 + c]
                                + src[((sy + 1) * srcSize + sx) * 4 + c] + src[((sy + 1) * srcSize + sx + 1) * 4 + c];
                        outp[(y * dst + x) * 4 + c] = (byte)(sum / 4);
                    }
            return outp;
        }

        private void UpdateCount() => _countText.Text = $"{_db.Items.Count} items";

        private void AddItem()
        {
            var dlg = new AddItemDialog(this, _db.Schema.ItemRecords) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            if (string.IsNullOrWhiteSpace(dlg.ItemName)) return;
            try
            {
                var it = _db.AddItem(dlg.ItemName, dlg.RecordType);
                _view.Refresh();
                UpdateCount();
                _list.SelectedItem = it;
                _list.ScrollIntoView(it);
                _status.Text = $"Item added: {it.DisplayName} ({it.ItemIdHex}). Edit then “Save the mod”.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Add item", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void DuplicateItem()
        {
            var src = _list.SelectedItem as ItemInfo;
            if (src == null) return;
            var f = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            try
            {
                var it = _db.DuplicateItem(src);
                _view.Refresh();
                UpdateCount();
                _list.SelectedItem = it;
                _list.ScrollIntoView(it);
                _status.Text = $"Duplicated: {it.DisplayName} ({it.ItemIdHex}). Edit then “Save the mod”.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Duplicate item", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void DeleteItem()
        {
            var it = _list.SelectedItem as ItemInfo;
            if (it == null) return;
            var confirm = DarkMessage.Show(
                $"Delete the item “{it.DisplayName}” ({it.ItemIdHex})?\n\n" +
                "Its name/description are removed only if no other item shares them. " +
                "Confirm with “Save the mod”.",
                "Delete an item", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            int idx = _list.SelectedIndex;
            _db.RemoveItem(it);
            _view.Refresh();
            UpdateCount();
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(idx, _list.Items.Count - 1);
            _status.Text = $"Item deleted — {_db.Items.Count} remaining. Save to apply.";
        }

        private void Save()
        {
            var f = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            try
            {
                int n = _db.SaveItems();
                _status.Text = n > 0 ? $"Saved — {n} item value(s) written." : "No item changes to save.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Save items", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        // ---------- image helpers ----------

        private static BitmapSource ToBitmap(byte[] bgra, int w, int h)
        {
            var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, w, h), bgra, w * 4, 0);
            bmp.Freeze();
            return bmp;
        }

        private static byte[] PngToBgra(string path, int w, int h)
        {
            var png = new BitmapImage();
            png.BeginInit(); png.CacheOption = BitmapCacheOption.OnLoad; png.UriSource = new Uri(path); png.EndInit();
            var conv = new FormatConvertedBitmap(png, PixelFormats.Bgra32, null, 0);
            BitmapSource src = conv.PixelWidth == w && conv.PixelHeight == h
                ? (BitmapSource)conv
                : new TransformedBitmap(conv, new ScaleTransform((double)w / conv.PixelWidth, (double)h / conv.PixelHeight));
            var bgra = new byte[w * h * 4];
            src.CopyPixels(bgra, w * 4, 0);
            return bgra;
        }
    }

    /// <summary>Small modal asking for a new item's name and record type (category).</summary>
    internal sealed class AddItemDialog : Window
    {
        private readonly TextBox _name = new TextBox();
        private readonly ComboBox _type = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Value" };

        public string ItemName => _name.Text?.Trim();
        public string RecordType => _type.SelectedValue as string ?? "ITEM_CONSUME";

        // Friendly labels for the item_config record types.
        private static string Friendly(string rec)
        {
            switch (rec)
            {
                case "ITEM_CONSUME": return "Consumable";
                case "ITEM_CREATURE": return "Creature / bait";
                case "ITEM_IMPORTANT": return "Important item";
                case "ITEM_EQUIPMENT": return "Equipment";
                case "ITEM_HACKSLASH_BATTLE": return "Blaster T — battle";
                case "ITEM_HACKSLASH_EQUIPMENT": return "Blaster T — equipment";
                case "ITEM_SOUL": return "Soul";
                default: return rec;
            }
        }

        public AddItemDialog(Window owner, string[] recordTypes)
        {
            Owner = owner;
            Title = "Add an item";
            Width = 380; Height = 190;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            foreach (var rt in recordTypes)
                _type.Items.Add(new { Label = Friendly(rt), Value = rt });
            _type.SelectedIndex = 0;

            var grid = new StackPanel { Margin = new Thickness(12) };
            grid.Children.Add(new TextBlock { Text = "Item name", Foreground = Theme.FgMuted });
            grid.Children.Add(_name);
            grid.Children.Add(new TextBlock { Text = "Category", Foreground = Theme.FgMuted, Margin = new Thickness(0, 8, 0, 0) });
            grid.Children.Add(_type);

            var ok = new Button { Content = "Add", IsDefault = true, Width = 90, Margin = new Thickness(0, 12, 6, 0) };
            ok.Click += (s, e) => { DialogResult = !string.IsNullOrWhiteSpace(ItemName); };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Margin = new Thickness(0, 12, 0, 0) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            grid.Children.Add(btns);

            Content = grid;
            Loaded += (s, e) => _name.Focus();
        }
    }
}
