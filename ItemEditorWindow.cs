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
            Title = "Lycoris — Éditeur d'items";
            Width = 760; Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _moddedAtlas = _db.ModItemAtlasFile;

            // Toolbar
            var add = new Button { Content = "+ Ajouter", Padding = new Thickness(10, 4, 10, 4) };
            add.Click += (s, e) => AddItem();
            var dup = new Button { Content = "Dupliquer", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            dup.Click += (s, e) => DuplicateItem();
            var del = new Button { Content = "Supprimer", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            del.Click += (s, e) => DeleteItem();
            var save = new Button { Content = "Sauver le mod", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            save.Click += (s, e) => Save();
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(add);
            toolbar.Children.Add(dup);
            toolbar.Children.Add(del);
            toolbar.Children.Add(save);
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
            var pick = new Button { Content = "Choisir position dans l'atlas…", Padding = new Thickness(8, 2, 8, 2) };
            pick.Click += (s, e) => PickPos();
            var repl = new Button { Content = "Remplacer l'icône (PNG)", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            repl.Click += (s, e) => ReplaceIcon();
            iconBtns.Children.Add(pick);
            iconBtns.Children.Add(repl);
            _fields.Children.Add(iconBtns);

            _fields.Children.Add(TextRow("Nom", "Name", 220));
            _fields.Children.Add(DescRow());
            _fields.Children.Add(NumRow("Ordre inventaire", "InventorySort"));
            _fields.Children.Add(NumRow("Type d'item", "ItemType"));
            _fields.Children.Add(NumRow("Capacité (carry)", "CarryCap"));
            _fields.Children.Add(NumRow("Prix de vente", "SellPrice"));
            _fields.Children.Add(NumRow("Prix en shop", "ShopPrice"));
            _fields.Children.Add(NumRow("Icon X", "IconPosX"));
            _fields.Children.Add(NumRow("Icon Y", "IconPosY"));
        }

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var it = _list.SelectedItem as ItemInfo;
            _fields.DataContext = it;
            _fields.IsEnabled = it != null;
            _iconImg.Source = CropIcon(it);
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
                    _status.Text = $"{it.DisplayName} — icône à ({picker.PickedX}, {picker.PickedY}).";
                }
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Atlas item"); }
        }

        private void ReplaceIcon()
        {
            var it = _list.SelectedItem as ItemInfo;
            if (it?.IconPosX == null || it.IconPosY == null) return;
            string atlas = AtlasPath();
            if (atlas == null) { DarkMessage.Show("Atlas item_icon.xi introuvable.", "Item"); return; }
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images PNG|*.png", Title = "Icône item — PNG 32×32" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var img = Imgc.Decode(System.IO.File.ReadAllBytes(atlas));
                byte[] cell = PngToBgra(dlg.FileName, Cell, Cell);
                int x = it.IconPosX.Value * Cell, y = it.IconPosY.Value * Cell;
                if (x + Cell > img.Width || y + Cell > img.Height) { DarkMessage.Show("Position hors de l'atlas.", "Item"); return; }
                for (int ry = 0; ry < Cell; ry++)
                    Array.Copy(cell, ry * Cell * 4, img.Bgra, ((y + ry) * img.Width + x) * 4, Cell * 4);

                string target = _db.MirrorToMod(atlas);
                System.IO.File.WriteAllBytes(target, Imgc.EncodeXi(img.Bgra, img.Width, img.Height));
                _moddedAtlas = target;
                _iconImg.Source = CropIcon(it);
                _status.Text = $"Icône item remplacée à ({it.IconPosX},{it.IconPosY}).";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Erreur icône item", MessageBoxButton.OK, MessageBoxImage.Error); }
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
                _status.Text = $"Item ajouté: {it.DisplayName} ({it.ItemIdHex}). Édite puis « Sauver le mod ».";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Ajout d'item", MessageBoxButton.OK, MessageBoxImage.Warning); }
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
                _status.Text = $"Dupliqué: {it.DisplayName} ({it.ItemIdHex}). Édite puis « Sauver le mod ».";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Duplication d'item", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void DeleteItem()
        {
            var it = _list.SelectedItem as ItemInfo;
            if (it == null) return;
            var confirm = DarkMessage.Show(
                $"Supprimer l'item « {it.DisplayName} » ({it.ItemIdHex}) ?\n\n" +
                "Son nom/description ne sont retirés que si aucun autre item ne les partage. " +
                "À confirmer avec « Sauver le mod ».",
                "Supprimer un item", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            int idx = _list.SelectedIndex;
            _db.RemoveItem(it);
            _view.Refresh();
            UpdateCount();
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(idx, _list.Items.Count - 1);
            _status.Text = $"Item supprimé — {_db.Items.Count} restants. Sauver pour appliquer.";
        }

        private void Save()
        {
            var f = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            try
            {
                int n = _db.SaveItems();
                _status.Text = n > 0 ? $"Sauvé — {n} valeur(s) d'items écrites." : "Aucune modification d'item à sauver.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Sauvegarde items", MessageBoxButton.OK, MessageBoxImage.Error); }
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
                case "ITEM_CONSUME": return "Consommable";
                case "ITEM_CREATURE": return "Créature / appât";
                case "ITEM_IMPORTANT": return "Objet important";
                case "ITEM_EQUIPMENT": return "Équipement";
                case "ITEM_HACKSLASH_BATTLE": return "Blaster T — combat";
                case "ITEM_HACKSLASH_EQUIPMENT": return "Blaster T — équipement";
                case "ITEM_SOUL": return "Âme (soul)";
                default: return rec;
            }
        }

        public AddItemDialog(Window owner, string[] recordTypes)
        {
            Owner = owner;
            Title = "Ajouter un item";
            Width = 380; Height = 190;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            foreach (var rt in recordTypes)
                _type.Items.Add(new { Label = Friendly(rt), Value = rt });
            _type.SelectedIndex = 0;

            var grid = new StackPanel { Margin = new Thickness(12) };
            grid.Children.Add(new TextBlock { Text = "Nom de l'item", Foreground = Theme.FgMuted });
            grid.Children.Add(_name);
            grid.Children.Add(new TextBlock { Text = "Catégorie", Foreground = Theme.FgMuted, Margin = new Thickness(0, 8, 0, 0) });
            grid.Children.Add(_type);

            var ok = new Button { Content = "Ajouter", IsDefault = true, Width = 90, Margin = new Thickness(0, 12, 6, 0) };
            ok.Click += (s, e) => { DialogResult = !string.IsNullOrWhiteSpace(ItemName); };
            var cancel = new Button { Content = "Annuler", IsCancel = true, Width = 90, Margin = new Thickness(0, 12, 0, 0) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            grid.Children.Add(btns);

            Content = grid;
            Loaded += (s, e) => _name.Focus();
        }
    }
}
