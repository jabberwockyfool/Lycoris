using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Standalone map editor (map_config / MAP_INFO): a searchable list of maps with their folder name,
    /// display name (system_text), MapID/NounID (CRC32 of the folder), ShowMapCard and the Unk fields.
    /// A custom map needs a MAP_INFO entry here or it bugs in-game. Saved into map_config (+ system_text).
    /// </summary>
    public sealed class MapEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private readonly ListBox _list = new ListBox();
        private readonly TextBox _search = new TextBox();
        private readonly StackPanel _fields = new StackPanel();
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };
        private readonly TextBlock _countText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted, Margin = new Thickness(10, 0, 0, 0) };
        private ICollectionView _view;

        public MapEditorWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            Owner = owner;
            Title = "Lycoris — Éditeur de maps";
            Width = 720; Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var add = ToolBtn("+ Ajouter", 0, AddMap);
            var dup = ToolBtn("Dupliquer", 6, DuplicateMap);
            var del = ToolBtn("Supprimer", 6, DeleteMap);
            var enc = ToolBtn("Rencontres…", 6, OpenEncounters);
            var save = ToolBtn("Sauver le mod", 6, Save);
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(add); toolbar.Children.Add(dup); toolbar.Children.Add(del); toolbar.Children.Add(enc); toolbar.Children.Add(save);
            UpdateCount(); toolbar.Children.Add(_countText);
            DockPanel.SetDock(toolbar, Dock.Top);

            var left = new DockPanel { Width = 260, Margin = new Thickness(6) };
            _search.Margin = new Thickness(0, 0, 0, 4);
            _search.TextChanged += (s, e) => _view?.Refresh();
            DockPanel.SetDock(_search, Dock.Top);
            _list.DisplayMemberPath = "DisplayName";
            _list.SelectionChanged += (s, e) => { _fields.DataContext = _list.SelectedItem; _fields.IsEnabled = _list.SelectedItem != null; };
            left.Children.Add(_search);
            left.Children.Add(_list);
            DockPanel.SetDock(left, Dock.Left);

            _fields.Margin = new Thickness(6);
            BuildFields();
            var right = new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            DockPanel.SetDock(_status, Dock.Bottom);
            var root = new DockPanel();
            root.Children.Add(toolbar); root.Children.Add(_status); root.Children.Add(left); root.Children.Add(right);
            Content = root;

            _view = CollectionViewSource.GetDefaultView(_db.Maps);
            _view.Filter = Filter;
            _list.ItemsSource = _view;
            if (_db.Maps.Count > 0) _list.SelectedIndex = 0;
        }

        private Button ToolBtn(string text, double leftMargin, Action onClick)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(leftMargin, 0, 0, 0) };
            b.Click += (s, e) => onClick();
            return b;
        }

        private bool Filter(object o)
        {
            string q = _search.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            var m = (MapInfo)o;
            return (m.Name != null && m.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                   || (m.MapFolderName != null && m.MapFolderName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                   || m.MapIdHex.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void BuildFields()
        {
            _fields.Children.Add(FolderRow());
            _fields.Children.Add(TextRow("Nom (system_text)", "Name", 260));
            _fields.Children.Add(ReadOnlyRow("MapID", "MapIdHex"));
            _fields.Children.Add(ReadOnlyRow("NounID", "NounIdHex"));
            _fields.Children.Add(NumRow("ShowMapCard", "ShowMapCard"));
            for (int i = 1; i <= 8; i++) _fields.Children.Add(NumRow("Unk" + i, "Unk" + i));
        }

        private static UIElement Label(string text) =>
            new TextBlock { Text = text, Width = 150, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted };

        private static FrameworkElement TextRow(string label, string path, double width)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label(label));
            var tb = new TextBox { Width = width };
            tb.SetBinding(TextBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            return sp;
        }

        private static FrameworkElement NumRow(string label, string path) => TextRow(label, path, 100);

        private static FrameworkElement ReadOnlyRow(string label, string path)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label(label));
            var tb = new TextBlock { FontFamily = new System.Windows.Media.FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
            tb.SetBinding(TextBlock.TextProperty, new Binding(path));
            sp.Children.Add(tb);
            return sp;
        }

        private FrameworkElement FolderRow()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label("Dossier map (id)"));
            var tb = new TextBox { Width = 130 };
            tb.SetBinding(TextBox.TextProperty, new Binding("MapFolderName") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            var recalc = new Button { Content = "↻ Recalculer les ID", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "MapID et NounID = CRC32 du nom de dossier" };
            recalc.Click += (s, e) =>
            {
                var m = _list.SelectedItem as MapInfo;
                if (m == null) return;
                CommitEdits();
                m.RecomputeIds();
                _status.Text = $"{m.MapFolderName} → MapID/NounID = {m.MapIdHex}";
            };
            sp.Children.Add(recalc);
            return sp;
        }

        private void CommitEdits()
        {
            var f = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
        }

        private void UpdateCount() => _countText.Text = $"{_db.Maps.Count} maps";

        private void AddMap()
        {
            var dlg = new AddMapDialog(this) { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Folder)) return;
            try
            {
                var m = _db.AddMap(dlg.Folder.Trim(), dlg.MapName?.Trim());
                _view.Refresh(); UpdateCount();
                _list.SelectedItem = m; _list.ScrollIntoView(m);
                _status.Text = $"Map ajoutée: {m.DisplayName} — MapID {m.MapIdHex}. Édite puis « Sauver le mod ».";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Ajout de map", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void DuplicateMap()
        {
            var src = _list.SelectedItem as MapInfo;
            if (src == null) return;
            CommitEdits();
            var dlg = new AddMapDialog(this, "Dupliquer — nouveau dossier map", src.MapFolderName + "x", src.Name) { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Folder)) return;
            try
            {
                var m = _db.DuplicateMap(src, dlg.Folder.Trim());
                _view.Refresh(); UpdateCount();
                _list.SelectedItem = m; _list.ScrollIntoView(m);
                _status.Text = $"Dupliqué: {m.DisplayName} — MapID {m.MapIdHex}.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Duplication de map", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void DeleteMap()
        {
            var m = _list.SelectedItem as MapInfo;
            if (m == null) return;
            if (DarkMessage.Show($"Supprimer la map « {m.DisplayName} » ({m.MapIdHex}) du config ?\n\nÀ confirmer avec « Sauver le mod ».",
                "Supprimer une map", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            int idx = _list.SelectedIndex;
            _db.RemoveMap(m);
            _view.Refresh(); UpdateCount();
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(idx, _list.Items.Count - 1);
            _status.Text = $"Map supprimée — {_db.Maps.Count} restantes. Sauver pour appliquer.";
        }

        private void OpenEncounters()
        {
            var m = _list.SelectedItem as MapInfo;
            if (m == null || string.IsNullOrWhiteSpace(m.MapFolderName)) return;
            string src = FindMapPck(m.MapFolderName);
            if (src == null)
            {
                DarkMessage.Show($"Fichier {m.MapFolderName}.pck introuvable (ni dans le mod, ni dans la référence).\n" +
                    "Les rencontres sont dans data/res/map/" + m.MapFolderName + "/" + m.MapFolderName + ".pck.",
                    "Rencontres", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            EncounterSet set;
            try { set = Encounters.Load(src, _db); }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Rencontres", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            if (set == null || set.Tables.Count == 0)
            {
                DarkMessage.Show("Cette map n'a pas de fichier de rencontres (_enc_) dans son .pck.", "Rencontres", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            string outPck = _db.ModFolder != null
                ? System.IO.Path.Combine(_db.ModFolder, "res", "map", m.MapFolderName, m.MapFolderName + ".pck")
                : src;
            new EncounterEditorWindow(this, _db, set, outPck, m.DisplayName) { Owner = this }.Show();
        }

        /// <summary>Find the map's .pck — the mod first, then the reference extract.</summary>
        private string FindMapPck(string mapId)
        {
            foreach (var root in new[] { _db?.ModFolder, _db?.ReferenceFolder })
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var cand in new[] {
                    System.IO.Path.Combine(root, "res", "map", mapId, mapId + ".pck"),
                    System.IO.Path.Combine(root, "data", "res", "map", mapId, mapId + ".pck"),
                    System.IO.Path.Combine(root, mapId, mapId + ".pck"),
                })
                    if (System.IO.File.Exists(cand)) return cand;
            }
            return null;
        }

        private void Save()
        {
            CommitEdits();
            try
            {
                int n = _db.SaveMaps();
                _status.Text = n > 0 ? $"Sauvé — {n} valeur(s) de map écrites." : "Aucune modification de map à sauver.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Sauvegarde maps", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }

    /// <summary>Small modal asking for a map folder id + display name.</summary>
    internal sealed class AddMapDialog : Window
    {
        private readonly TextBox _folder = new TextBox();
        private readonly TextBox _name = new TextBox();
        public string Folder => _folder.Text;
        public string MapName => _name.Text;

        public AddMapDialog(Window owner, string title = "Ajouter une map", string folder = "", string name = "")
        {
            Owner = owner;
            Title = title;
            Width = 400; Height = 210;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            _folder.Text = folder; _name.Text = name;

            var grid = new StackPanel { Margin = new Thickness(12) };
            grid.Children.Add(new TextBlock { Text = "Dossier map (ex. t101g00) — MapID = CRC32 de ce nom", Foreground = Theme.FgMuted });
            grid.Children.Add(_folder);
            grid.Children.Add(new TextBlock { Text = "Nom affiché (system_text)", Foreground = Theme.FgMuted, Margin = new Thickness(0, 8, 0, 0) });
            grid.Children.Add(_name);

            var ok = new Button { Content = "OK", IsDefault = true, Width = 90, Margin = new Thickness(0, 12, 6, 0) };
            ok.Click += (s, e) => { DialogResult = !string.IsNullOrWhiteSpace(Folder); };
            var cancel = new Button { Content = "Annuler", IsCancel = true, Width = 90, Margin = new Thickness(0, 12, 0, 0) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            grid.Children.Add(btns);

            Content = grid;
            Loaded += (s, e) => _folder.Focus();
        }
    }
}
