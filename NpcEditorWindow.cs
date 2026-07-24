using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Lycoris.Formats;
using Lycoris.Npc;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// NPC editor: manage a list of NPCMake NPC configs (the "TOML"), edit every field in the GUI, and
    /// import/export .toml files. "BaseId depuis un yo-kai" computes BaseId = CRC32(model name) from the
    /// loaded mod. Compilation (Jalon B) is wired via the "Compiler…" button.
    /// </summary>
    public sealed class NpcEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private readonly ObservableCollection<NpcModel> _npcs = new ObservableCollection<NpcModel>();
        private readonly ListBox _list = new ListBox();
        private readonly TextBox _search = new TextBox();
        private readonly StackPanel _fields = new StackPanel();
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };
        private ICollectionView _view;
        private readonly System.Collections.Generic.List<MapEntry> _maps;

        private IntPtr Handle => new System.Windows.Interop.WindowInteropHelper(this).Handle;

        public NpcEditorWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            // Map dropdown = the maps from the map editor (map_config, with their system_text names + any you
            // added). Falls back to scanning res/map folders if map_config isn't loaded.
            _maps = db != null && db.Maps.Count > 0
                ? db.Maps.OrderBy(m => m.MapFolderName, StringComparer.OrdinalIgnoreCase)
                         .Select(m => new MapEntry(m.MapFolderName, m.Name)).ToList()
                : MapList.Available(db?.ReferenceFolder, db?.ModFolder);
            Owner = owner;
            Title = "Lycoris — Éditeur de NPC (NPCMake)";
            Width = 780; Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // Toolbar
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(ToolButton("+ Nouveau", NewNpc, 0));
            toolbar.Children.Add(ToolButton("Dupliquer", DuplicateNpc));
            toolbar.Children.Add(ToolButton("Supprimer", DeleteNpc));
            toolbar.Children.Add(ToolButton("Importer TOML…", ImportToml));
            toolbar.Children.Add(ToolButton("Exporter TOML…", ExportToml));
            toolbar.Children.Add(ToolButton("Exporter tout…", ExportAll));
            toolbar.Children.Add(ToolButton("Compiler…", CompileNpc));
            DockPanel.SetDock(toolbar, Dock.Top);

            // Left: search + list
            var left = new DockPanel { Width = 220, Margin = new Thickness(6) };
            _search.Margin = new Thickness(0, 0, 0, 4);
            _search.TextChanged += (s, e) => _view?.Refresh();
            DockPanel.SetDock(_search, Dock.Top);
            _list.DisplayMemberPath = "DisplayName";
            _list.SelectionChanged += (s, e) => { _fields.DataContext = _list.SelectedItem; _fields.IsEnabled = _list.SelectedItem != null; };
            left.Children.Add(_search);
            left.Children.Add(_list);
            DockPanel.SetDock(left, Dock.Left);

            // Right: fields
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

            _view = CollectionViewSource.GetDefaultView(_npcs);
            _view.Filter = Filter;
            _list.ItemsSource = _view;

            NewNpc();
            _status.Text = "Crée un NPC ou importe un .toml. « Depuis un yo-kai » calcule le BaseId à partir du modèle.";
        }

        private Button ToolButton(string text, Action onClick, double leftMargin = 6)
        {
            var b = new Button { Content = text, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(leftMargin, 0, 0, 0) };
            b.Click += (s, e) => onClick();
            return b;
        }

        private bool Filter(object o)
        {
            string q = _search.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            var n = (NpcModel)o;
            return n.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------- fields ----------

        private void BuildFields()
        {
            _fields.Children.Add(TextRow("Nom du NPC", "NpcName", 260));
            _fields.Children.Add(BaseIdRow());
            _fields.Children.Add(NumRow("NpcX", "NpcX"));
            _fields.Children.Add(NumRow("NpcY (2D)", "NpcY"));
            _fields.Children.Add(NumRow("NpcZ (hauteur)", "NpcZ"));
            _fields.Children.Add(NumRow("Rotation (°)", "NpcRotation"));
            _fields.Children.Add(ComboRow("Chapitre", "ChapterCode",
                new[] { "c01", "c02", "c03", "c04", "c05", "c06", "c07", "c08", "c09", "c10", "c11", "C99" }));
            _fields.Children.Add(MapRow());
            _fields.Children.Add(DescRow("OnTalk (code XQ)", "OnTalk"));
            _fields.Children.Add(TextRow("AppearCond", "AppearCond", 200));
            _fields.Children.Add(CheckRow("IsYw1 (laisser décoché pour YW3)", "IsYw1"));
            _fields.Children.Add(ComboRow("NpcType", "NpcType", new[] { "HUMAN", "YOKAI" }));
        }

        private static UIElement Label(string text) =>
            new TextBlock { Text = text, Width = 160, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted };

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

        private static FrameworkElement DescRow(string label, string path)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label(label));
            var tb = new TextBox { Width = 380, Height = 90, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            tb.SetBinding(TextBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            return sp;
        }

        private static FrameworkElement ComboRow(string label, string path, string[] items)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label(label));
            var cb = new ComboBox { Width = 160, IsEditable = true };
            foreach (var it in items) cb.Items.Add(it);
            cb.SetBinding(ComboBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(cb);
            return sp;
        }

        private static FrameworkElement CheckRow(string label, string path)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label(label));
            var chk = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            chk.SetBinding(CheckBox.IsCheckedProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            sp.Children.Add(chk);
            return sp;
        }

        private FrameworkElement MapRow()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label("Map"));
            if (_maps != null && _maps.Count > 0)
            {
                // Dropdown of the maps actually present in the extract, labelled with their name.
                var cb = new ComboBox { Width = 300, ItemsSource = _maps, DisplayMemberPath = "Label", SelectedValuePath = "Id", IsEditable = false };
                cb.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
                    new Binding("MapID") { Mode = BindingMode.TwoWay });
                sp.Children.Add(cb);
                sp.Children.Add(new TextBlock { Text = $"  ({_maps.Count} maps)", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            }
            else
            {
                // No maps discovered — fall back to a free-text MapID.
                var tb = new TextBox { Width = 160 };
                tb.SetBinding(TextBox.TextProperty, new Binding("MapID") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
                sp.Children.Add(tb);
            }
            return sp;
        }

        private FrameworkElement BaseIdRow()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label("BaseId (hex)"));
            var tb = new TextBox { Width = 130 };
            tb.SetBinding(TextBox.TextProperty, new Binding("BaseIdHex") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            var pick = new Button { Content = "Depuis un yo-kai…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            pick.Click += (s, e) => BaseIdFromYokai();
            sp.Children.Add(pick);
            return sp;
        }

        // ---------- actions ----------

        private NpcModel Selected => _list.SelectedItem as NpcModel;

        private void CommitEdits()
        {
            var f = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
        }

        private void NewNpc()
        {
            var n = new NpcModel();
            _npcs.Add(n);
            _list.SelectedItem = n;
        }

        private void DuplicateNpc()
        {
            var src = Selected;
            if (src == null) return;
            CommitEdits();
            var n = src.Clone();
            n.NpcName = src.NpcName + " (copie)";
            _npcs.Add(n);
            _list.SelectedItem = n;
            _status.Text = $"Dupliqué: {n.DisplayName}.";
        }

        private void DeleteNpc()
        {
            var n = Selected;
            if (n == null) return;
            int idx = _list.SelectedIndex;
            _npcs.Remove(n);
            if (_npcs.Count > 0) _list.SelectedIndex = Math.Min(idx, _npcs.Count - 1);
            _status.Text = "NPC retiré de la liste.";
        }

        private void ImportToml()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Fichiers TOML|*.toml|Tous|*.*", Multiselect = true, Title = "Importer un ou des .toml NPCMake" };
            if (dlg.ShowDialog() != true) return;
            int added = 0;
            foreach (var path in dlg.FileNames)
            {
                try { _npcs.Add(NpcToml.Parse(File.ReadAllText(path))); added++; }
                catch (Exception ex) { DarkMessage.Show($"{Path.GetFileName(path)}: {ex.Message}", "Import TOML"); }
            }
            if (added > 0) { _list.SelectedIndex = _npcs.Count - 1; _status.Text = $"{added} NPC importé(s)."; }
        }

        private void ExportToml()
        {
            var n = Selected;
            if (n == null) return;
            CommitEdits();
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Fichiers TOML|*.toml", FileName = SafeFileName(n.NpcName) + ".toml", Title = "Exporter le NPC en .toml" };
            if (dlg.ShowDialog() != true) return;
            try { File.WriteAllText(dlg.FileName, NpcToml.Write(n), new UTF8Encoding(false)); _status.Text = $"Exporté: {Path.GetFileName(dlg.FileName)}"; }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Export TOML", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ExportAll()
        {
            if (_npcs.Count == 0) return;
            CommitEdits();
            string dir = FolderPicker.Pick("Dossier où écrire un .toml par NPC", Handle);
            if (dir == null) return;
            int n = 0;
            foreach (var npc in _npcs)
            {
                try { File.WriteAllText(Path.Combine(dir, SafeFileName(npc.NpcName) + ".toml"), NpcToml.Write(npc), new UTF8Encoding(false)); n++; }
                catch (Exception ex) { DarkMessage.Show($"{npc.DisplayName}: {ex.Message}", "Export tout"); }
            }
            _status.Text = $"{n} .toml écrit(s) dans {dir}.";
        }

        private void CompileNpc()
        {
            var n = Selected;
            if (n == null) return;
            CommitEdits();
            if (string.IsNullOrWhiteSpace(n.NpcName)) { DarkMessage.Show("Donne un nom au NPC.", "Compiler"); return; }

            if (!string.IsNullOrWhiteSpace(n.OnTalk) && !NpcXq.IsAvailable())
            {
                DarkMessage.Show("xtractquery est introuvable dans le PATH — nécessaire pour compiler le code OnTalk.\n" +
                    "Installe-le globalement (voir yo-docs), ou vide le champ OnTalk pour compiler sans dialogue.",
                    "xtractquery manquant", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Locate the map's source files (mod first, then reference), else ask.
            string mapFolder = FindMapDir(n.MapID);
            if (mapFolder == null)
            {
                mapFolder = FolderPicker.Pick($"Dossier de la map « {n.MapID} » (contenant npc.pck, {n.MapID}.pck…)", Handle);
                if (mapFolder == null) return;
            }

            // Auto-merge target inside the loaded mod (mirrors res/map/<MapID>). Null if no mod loaded.
            string mergeMapDir = string.IsNullOrEmpty(_db?.ModFolder)
                ? null : System.IO.Path.Combine(_db.ModFolder, "res", "map", n.MapID);
            string outRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lycoris_npc_build");

            string plan = mergeMapDir != null
                ? $"Les fichiers seront AJOUTÉS à ton mod :\n{mergeMapDir}"
                : "Aucun mod chargé : choisis un dossier de sortie (fusion manuelle).";
            if (DarkMessage.Show($"Compiler « {n.NpcName} » pour la map {n.MapID} ?\n\n{plan}",
                "Compiler le NPC", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            if (mergeMapDir == null)
            {
                string picked = FolderPicker.Pick("Dossier de sortie (un dossier <Nom>_output y sera créé)", Handle);
                if (picked == null) return;
                outRoot = picked;
            }

            try
            {
                var r = NpcCompiler.Compile(n, mapFolder, outRoot, mergeMapDir);
                _status.Text = $"NPC compilé — ID {r.NpcIdHex}." + (r.MergedDir != null ? " Fusionné dans le mod." : "");
                string msg = $"NPC « {n.NpcName} » compilé.\n\nID NPC (hex) : {r.NpcIdHex}\n" +
                             (r.FuncId >= 0 ? $"Fonction OnTalk : RunCmd_Map{r.FuncId}\n" : "OnTalk : (aucun)\n") +
                             (r.MergedDir != null
                                ? $"\n✔ Fusionné automatiquement dans le mod :\n{r.MergedDir}\n\n(copie de secours : {r.OutputDir})"
                                : $"\nFichiers écrits dans :\n{r.OutputDir}\nFusionne ce dossier dans ton mod (res/map/…).");
                DarkMessage.Show(msg, "Compilation réussie", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Échec de la compilation", MessageBoxButton.OK, MessageBoxImage.Error);
                _status.Text = "Échec de la compilation: " + ex.Message;
            }
        }

        /// <summary>Find the folder containing the map's npc.pck — the mod first, then the reference.</summary>
        private string FindMapDir(string mapId)
        {
            foreach (var root in new[] { _db?.ModFolder, _db?.ReferenceFolder })
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var cand in new[] {
                    System.IO.Path.Combine(root, "res", "map", mapId),
                    System.IO.Path.Combine(root, "data", "res", "map", mapId),
                    System.IO.Path.Combine(root, mapId),
                })
                    if (System.IO.File.Exists(System.IO.Path.Combine(cand, "npc.pck"))) return cand;
            }
            return null;
        }

        private void BaseIdFromYokai()
        {
            var n = Selected;
            if (n == null) return;
            if (_db == null || _db.Yokai.Count == 0)
            {
                DarkMessage.Show("Aucun yo-kai chargé (ouvre un dossier de mod d'abord).", "Depuis un yo-kai");
                return;
            }
            var dlg = new PickYokaiDialog(this, _db) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Picked == null) return;
            string model = dlg.Picked.ModelName;
            if (string.IsNullOrEmpty(model))
            {
                DarkMessage.Show("Ce yo-kai n'a pas de nom de modèle.", "Depuis un yo-kai");
                return;
            }
            n.BaseId = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(model)));
            n.NpcType = "YOKAI";
            _status.Text = $"BaseId = {n.BaseIdHex} (CRC32 de « {model} »), NpcType = YOKAI.";
        }

        private static string SafeFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "npc";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }

    /// <summary>Small searchable picker returning a yo-kai (for the BaseId helper).</summary>
    internal sealed class PickYokaiDialog : Window
    {
        private readonly ListBox _list = new ListBox();
        private readonly TextBox _search = new TextBox();
        private ICollectionView _view;
        public YokaiInfo Picked { get; private set; }

        public PickYokaiDialog(Window owner, YokaiDatabase db)
        {
            Owner = owner;
            Title = "Choisir un yo-kai";
            Width = 320; Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _search.Margin = new Thickness(0, 0, 0, 4);
            _search.TextChanged += (s, e) => _view?.Refresh();
            DockPanel.SetDock(_search, Dock.Top);
            _list.DisplayMemberPath = "DisplayName";
            _list.MouseDoubleClick += (s, e) => Accept();

            var ok = new Button { Content = "Choisir", IsDefault = true, Width = 90, Margin = new Thickness(0, 6, 6, 0) };
            ok.Click += (s, e) => Accept();
            var cancel = new Button { Content = "Annuler", IsCancel = true, Width = 90, Margin = new Thickness(0, 6, 0, 0) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            DockPanel.SetDock(btns, Dock.Bottom);

            var root = new DockPanel { Margin = new Thickness(10) };
            root.Children.Add(_search);
            root.Children.Add(btns);
            root.Children.Add(_list);
            Content = root;

            _view = CollectionViewSource.GetDefaultView(db.Yokai.ToList());
            _view.Filter = o =>
            {
                string q = _search.Text?.Trim();
                if (string.IsNullOrEmpty(q)) return true;
                var y = (YokaiInfo)o;
                return (y.Name != null && y.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                       || (y.ModelName != null && y.ModelName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            };
            _list.ItemsSource = _view;
        }

        private void Accept()
        {
            Picked = _list.SelectedItem as YokaiInfo;
            if (Picked != null) DialogResult = true;
        }
    }
}
