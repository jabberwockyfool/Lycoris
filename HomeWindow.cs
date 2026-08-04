using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Home launcher: open a mod folder once, then choose which editor to use (yo-kai or items).
    /// Both editors share the same in-memory database, so edits made in one are visible in the other
    /// and a single "Save the mod" in each writes the corresponding files.
    /// </summary>
    public sealed class HomeWindow : Window
    {
        private readonly YokaiDatabase _db = new YokaiDatabase(YokaiSchema.Yw3);
        private readonly string _referenceFolder = MainWindow.FindDefaultReference();

        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Theme.FgMuted, Margin = new Thickness(0, 12, 0, 0) };
        private readonly StackPanel _recentPanel = new StackPanel();
        private FrameworkElement _recentCaption;
        private string _currentModFolder;
        private readonly Button _yokaiBtn;
        private readonly Button _itemBtn;
        private readonly Button _skillBtn;
        private readonly Button _combineBtn;
        private readonly Button _npcBtn;
        private readonly Button _mapBtn;
        private readonly Button _shopBtn;
        private readonly Button _warpBtn;
        private readonly Button _eventBtn;
        private readonly Button _dialogueBtn;
        private readonly Button _battleBtn;
        private readonly Button _saveBtn;
        private readonly Button _checkBtn;

        private MainWindow _yokaiWindow;
        private ItemEditorWindow _itemWindow;
        private SkillEditorWindow _skillWindow;
        private CombineEditorWindow _combineWindow;
        private NpcEditorWindow _npcWindow;
        private MapEditorWindow _mapWindow;
        private ShopEditorWindow _shopWindow;
        private SaveEditorWindow _saveWindow;
        private EventEditorWindow _eventWindow;

        public HomeWindow()
        {
            Title = "Lycoris — Yo-kai Watch 3 Editor";
            Width = 600;
            SizeToContent = SizeToContent.Height;   // fit the grouped content height exactly
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.WindowBg;

            // A raised "card" over the darker window background gives the launcher some depth.
            var card = new Border
            {
                Background = Theme.PanelBg,
                BorderBrush = Theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(16),
                Padding = new Thickness(24, 20, 24, 20)
            };
            var root = new StackPanel();
            card.Child = root;
            Content = card;

            root.Children.Add(new TextBlock
            {
                Text = "Lycoris",
                FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Theme.Accent
            });
            root.Children.Add(new TextBlock
            {
                Text = "Yo-kai Watch 3 mod editor",
                FontSize = 12, Foreground = Theme.FgMuted, Margin = new Thickness(1, 1, 0, 16)
            });

            var newMod = new Button { Content = "New mod…", Padding = new Thickness(12, 9, 12, 9), FontSize = 14, FontWeight = FontWeights.SemiBold };
            newMod.Click += (s, e) => NewMod();
            var import = new Button { Content = "Import YWML mod…", Padding = new Thickness(12, 9, 12, 9), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0) };
            import.Click += (s, e) => ImportMod();
            var projBar = new StackPanel { Orientation = Orientation.Horizontal };
            projBar.Children.Add(newMod);
            projBar.Children.Add(import);
            root.Children.Add(projBar);

            // Recent projects (populated at load; hidden when empty).
            _recentCaption = SectionCaption("Recent projects");
            _recentCaption.Visibility = Visibility.Collapsed;
            root.Children.Add(_recentCaption);
            root.Children.Add(_recentPanel);

            _yokaiBtn = EditorButton("Yo-kai Editor", OpenYokaiEditor);
            _itemBtn = EditorButton("Item Editor", OpenItemEditor);
            _skillBtn = EditorButton("Skill Editor", OpenSkillEditor);
            _combineBtn = EditorButton("Fusion Editor", OpenCombineEditor);
            _npcBtn = EditorButton("NPC Editor", OpenNpcEditor);
            _mapBtn = EditorButton("Map Editor", OpenMapEditor);
            _shopBtn = EditorButton("Shop Editor", OpenShopEditor);
            _warpBtn = EditorButton("Warp Editor", OpenWarpEditor);
            _eventBtn = EditorButton("Event Editor", OpenEventEditor);
            _dialogueBtn = EditorButton("Dialogue Editor", OpenDialogueEditor);
            _battleBtn = EditorButton("Battle Editor", OpenBattleEditor);
            _saveBtn = EditorButton("Save Editor", OpenSaveEditor);
            _checkBtn = EditorButton("Check integrity", OpenIntegrity);

            AddSection(root, "Characters", _yokaiBtn, _itemBtn, _skillBtn, _combineBtn);
            AddSection(root, "World", _npcBtn, _mapBtn, _shopBtn, _warpBtn, _eventBtn, _dialogueBtn, _battleBtn);
            AddSection(root, "Save & Tools", _saveBtn, _checkBtn);

            foreach (var b in new[] { _yokaiBtn, _itemBtn, _skillBtn, _combineBtn, _npcBtn, _mapBtn, _shopBtn, _warpBtn,
                                      _eventBtn, _dialogueBtn, _battleBtn, _saveBtn, _checkBtn })
                b.IsEnabled = false;

            _status.Margin = new Thickness(1, 20, 0, 0);
            _status.Text = _referenceFolder != null
                ? "Open your extracted mod folder (YWML). The “cfg” folder is used as a reference for missing names."
                : "Open your extracted mod folder (YWML).";
            root.Children.Add(_status);
        }

        /// <summary>A small section caption: an uppercase label + a hairline rule filling the width.</summary>
        private static FrameworkElement SectionCaption(string title)
        {
            var caption = new DockPanel { Margin = new Thickness(1, 18, 1, 8) };
            var label = new TextBlock
            {
                Text = title.ToUpperInvariant(),
                FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Theme.FgMuted,
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(label, Dock.Left);
            caption.Children.Add(label);
            caption.Children.Add(new Border { Height = 1, Background = Theme.Border, VerticalAlignment = VerticalAlignment.Center });
            return caption;
        }

        /// <summary>A titled group: a caption + a hairline rule, then its buttons in a 3-column grid.</summary>
        private static void AddSection(Panel root, string title, params Button[] buttons)
        {
            root.Children.Add(SectionCaption(title));
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(-5, 0, -5, 0) };
            foreach (var b in buttons) grid.Children.Add(b);
            root.Children.Add(grid);
        }

        private Button EditorButton(string text, Action onClick)
        {
            var b = new Button
            {
                Content = text,
                FontSize = 14,
                Margin = new Thickness(5),
                MinHeight = 50,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        // ============================ Projects ============================

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            if (_openedOnce) return;
            _openedOnce = true;
            RefreshRecents();
            // Restore + re-save the most recent still-existing project on each entry into the app.
            var last = ProjectStore.Load().FirstOrDefault(p => p.Exists);
            if (last != null) LoadProject(last, quiet: true);
        }
        private bool _openedOnce;

        /// <summary>Import an existing extracted YWML mod folder as a project.</summary>
        private void ImportMod()
        {
            string folder = FolderPicker.Pick("Extracted folder (YWML) containing chara_param / chara_base / chara_text…",
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if (folder == null) return;
            LoadModFolder(ProjectStore.DefaultName(folder), folder, _referenceFolder);
        }

        /// <summary>Create a new, empty mod folder as a project (vanilla data comes from the reference).</summary>
        private void NewMod()
        {
            if (_referenceFolder == null)
            {
                DarkMessage.Show(
                    "Creating a new mod needs a reference game extract (the “cfg” folder) to read the vanilla data.\n" +
                    "None was found. Import an existing YWML mod instead, or place a cfg reference next to Lycoris.",
                    "New mod", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var dlg = new NewModDialog(this) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            string parent = dlg.Location, name = dlg.ModName;
            try
            {
                string folder = System.IO.Path.Combine(parent, name);
                if (System.IO.Directory.Exists(folder) &&
                    System.IO.Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    DarkMessage.Show($"“{folder}” already exists and isn't empty. Pick another name or location.",
                        "New mod", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                System.IO.Directory.CreateDirectory(folder);
                Ywml.Write(folder, name, "", "v0.0.0");   // create the YWML manifest so the name persists
                LoadModFolder(name, folder, _referenceFolder);
                _status.Text = $"New mod “{name}” created. Edits are written into {folder}. Choose an editor.";
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "New mod", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadProject(Project p, bool quiet = false)
        {
            LoadModFolder(p.Name, p.ModFolder, p.ReferenceFolder ?? _referenceFolder, quiet);
        }

        /// <summary>Load a mod folder into the shared db, wire the launcher, and record it as the current project.</summary>
        private void LoadModFolder(string name, string folder, string reference, bool quiet = false)
        {
            // Prefer the mod's own name from its ywml.json manifest over the folder-derived fallback.
            name = Ywml.FindName(folder) ?? name;
            try
            {
                _db.LoadFolder(folder, reference);

                // A freshly loaded db invalidates any editor windows still bound to the previous state.
                _yokaiWindow?.Close(); _yokaiWindow = null;
                _itemWindow?.Close(); _itemWindow = null;
                _skillWindow?.Close(); _skillWindow = null;
                _combineWindow?.Close(); _combineWindow = null;
                _npcWindow?.Close(); _npcWindow = null;
                _mapWindow?.Close(); _mapWindow = null;
                _shopWindow?.Close(); _shopWindow = null;
                _saveWindow?.Close(); _saveWindow = null;
                _eventWindow?.Close(); _eventWindow = null;

                _yokaiBtn.IsEnabled = _db.ParamFile != null;
                _itemBtn.IsEnabled = _db.Items.Count > 0;
                _skillBtn.IsEnabled = _db.Skills.Count > 0;
                _combineBtn.IsEnabled = _db.Combines.Count > 0;
                _npcBtn.IsEnabled = _db.ParamFile != null;
                _mapBtn.IsEnabled = _db.Maps.Count > 0;
                _shopBtn.IsEnabled = _db.Shops.Count > 0;
                _warpBtn.IsEnabled = _referenceFolder != null || _db.ModFolder != null;
                _eventBtn.IsEnabled = _referenceFolder != null || _db.ModFolder != null;
                _dialogueBtn.IsEnabled = _referenceFolder != null || _db.ModFolder != null;
                _battleBtn.IsEnabled = _db.Yokai.Count > 0;
                _saveBtn.IsEnabled = _db.Yokai.Count > 0;
                _checkBtn.IsEnabled = _db.ParamFile != null;

                _currentModFolder = folder;
                Title = $"Lycoris — {name}";
                // Persist the project (and stamp it as just-opened) on every load, incl. auto-restore.
                ProjectStore.Touch(name, folder, reference);
                RefreshRecents();

                string moves = _db.MoveOptions.Count > 0 ? $"named moves {_db.MoveNameCount}" : "unnamed moves";
                _status.Text = $"{name} — {_db.Yokai.Count} yo-kai, {_db.Items.Count} items, {_db.Skills.Count} skills, {_db.Maps.Count} maps  ({moves}).";
            }
            catch (Exception ex)
            {
                if (!quiet) DarkMessage.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _status.Text = "Error: " + ex.Message;
            }
        }

        private void RefreshRecents()
        {
            _recentPanel.Children.Clear();
            var projects = ProjectStore.Load().Where(p => p.Exists).Take(6).ToList();
            _recentCaption.Visibility = projects.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var p in projects)
            {
                bool current = string.Equals(p.ModFolder, _currentModFolder, StringComparison.OrdinalIgnoreCase);
                var text = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
                text.Inlines.Add(new System.Windows.Documents.Run(p.Name) { FontWeight = FontWeights.SemiBold });
                text.Inlines.Add(new System.Windows.Documents.Run("   " + p.ModFolder) { Foreground = Theme.FgMuted });
                var btn = new Button
                {
                    Content = text,
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 4),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    ToolTip = current ? "Current project" : "Open this project",
                    FontWeight = current ? FontWeights.Bold : FontWeights.Normal,
                };
                var proj = p;
                btn.Click += (s, e) => LoadProject(proj);
                _recentPanel.Children.Add(btn);
            }
        }

        private void OpenYokaiEditor()
        {
            if (_db.ParamFile == null) return;
            if (_yokaiWindow != null && _yokaiWindow.IsLoaded) { _yokaiWindow.Activate(); return; }
            _yokaiWindow = new MainWindow(_db, _referenceFolder) { Owner = this };
            _yokaiWindow.Closed += (s, e) => _yokaiWindow = null;
            _yokaiWindow.Show();
        }

        private void OpenItemEditor()
        {
            if (_db.Items.Count == 0) return;
            if (_itemWindow != null && _itemWindow.IsLoaded) { _itemWindow.Activate(); return; }
            _itemWindow = new ItemEditorWindow(this, _db) { Owner = this };
            _itemWindow.Closed += (s, e) => _itemWindow = null;
            _itemWindow.Show();
        }

        private void OpenSkillEditor()
        {
            if (_db.Skills.Count == 0) return;
            if (_skillWindow != null && _skillWindow.IsLoaded) { _skillWindow.Activate(); return; }
            _skillWindow = new SkillEditorWindow(this, _db) { Owner = this };
            _skillWindow.Closed += (s, e) => _skillWindow = null;
            _skillWindow.Show();
        }

        private void OpenCombineEditor()
        {
            if (_db.Combines.Count == 0) return;
            if (_combineWindow != null && _combineWindow.IsLoaded) { _combineWindow.Activate(); return; }
            _combineWindow = new CombineEditorWindow(this, _db) { Owner = this };
            _combineWindow.Closed += (s, e) => _combineWindow = null;
            _combineWindow.Show();
        }

        private void OpenNpcEditor()
        {
            if (_db.ParamFile == null) return;
            if (_npcWindow != null && _npcWindow.IsLoaded) { _npcWindow.Activate(); return; }
            _npcWindow = new NpcEditorWindow(this, _db) { Owner = this };
            _npcWindow.Closed += (s, e) => _npcWindow = null;
            _npcWindow.Show();
        }

        private void OpenMapEditor()
        {
            if (_db.Maps.Count == 0) return;
            if (_mapWindow != null && _mapWindow.IsLoaded) { _mapWindow.Activate(); return; }
            _mapWindow = new MapEditorWindow(this, _db) { Owner = this };
            _mapWindow.Closed += (s, e) => _mapWindow = null;
            _mapWindow.Show();
        }

        private void OpenShopEditor()
        {
            if (_db.Shops.Count == 0) return;
            if (_shopWindow != null && _shopWindow.IsLoaded) { _shopWindow.Activate(); return; }
            _shopWindow = new ShopEditorWindow(this, _db) { Owner = this };
            _shopWindow.Closed += (s, e) => _shopWindow = null;
            _shopWindow.Show();
        }

        private void OpenBattleEditor()
        {
            if (_db == null || _db.Yokai.Count == 0) return;
            new BattleEditorWindow(this, _db) { Owner = this }.Show();
        }

        private void OpenWarpEditor()
        {
            if (_db == null) return;
            new WarpEditorWindow(this, _db) { Owner = this }.Show();
        }

        private void OpenDialogueEditor()
        {
            if (_db == null) return;
            new DialogueEditorWindow(this, _db) { Owner = this }.Show();
        }

        private void OpenEventEditor()
        {
            if (_db == null) return;
            if (_eventWindow != null && _eventWindow.IsLoaded) { _eventWindow.Activate(); return; }
            _eventWindow = new EventEditorWindow(this, _db) { Owner = this };
            _eventWindow.Closed += (s, e) => _eventWindow = null;
            _eventWindow.Show();
        }

        private void OpenSaveEditor()
        {
            if (_db.Yokai.Count == 0) return;
            if (_saveWindow != null && _saveWindow.IsLoaded) { _saveWindow.Activate(); return; }
            _saveWindow = new SaveEditorWindow(this, _db) { Owner = this };
            _saveWindow.Closed += (s, e) => _saveWindow = null;
            _saveWindow.Show();
        }

        private void OpenIntegrity()
        {
            if (_db.ParamFile == null) return;
            new IntegrityWindow(this, _db) { Owner = this }.Show();
        }
    }

    /// <summary>Small modal for a new mod: a name + a parent location (the mod folder = location\name).</summary>
    internal sealed class NewModDialog : Window
    {
        private readonly TextBox _name = new TextBox { Text = "MyMod" };
        private readonly TextBox _loc = new TextBox { IsReadOnly = true };

        public string ModName => _name.Text?.Trim();
        public string Location => _loc.Text?.Trim();

        public NewModDialog(Window owner)
        {
            Owner = owner;
            Title = "New mod";
            Width = 460; Height = 230;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            _loc.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            var grid = new StackPanel { Margin = new Thickness(12) };
            grid.Children.Add(new TextBlock { Text = "Mod name", Foreground = Theme.FgMuted });
            grid.Children.Add(_name);
            grid.Children.Add(new TextBlock { Text = "Location", Foreground = Theme.FgMuted, Margin = new Thickness(0, 8, 0, 0) });
            var locRow = new DockPanel();
            var browse = new Button { Content = "Browse…", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            browse.Click += (s, e) =>
            {
                string f = FolderPicker.Pick("Where to create the mod folder",
                    new System.Windows.Interop.WindowInteropHelper(this).Handle);
                if (f != null) _loc.Text = f;
            };
            DockPanel.SetDock(browse, Dock.Right);
            locRow.Children.Add(browse);
            locRow.Children.Add(_loc);
            grid.Children.Add(locRow);
            grid.Children.Add(new TextBlock
            {
                Text = "The folder location\\name is created; vanilla data is read from the reference and your edits are written into it.",
                Foreground = Theme.FgMuted, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
            });

            var ok = new Button { Content = "Create", IsDefault = true, Width = 90, Margin = new Thickness(0, 12, 6, 0) };
            ok.Click += (s, e) =>
            {
                DialogResult = !string.IsNullOrWhiteSpace(ModName) && !string.IsNullOrWhiteSpace(Location)
                               && System.IO.Directory.Exists(Location);
            };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Margin = new Thickness(0, 12, 0, 0) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            grid.Children.Add(btns);

            Content = grid;
            Loaded += (s, e) => { _name.Focus(); _name.SelectAll(); };
        }
    }
}
