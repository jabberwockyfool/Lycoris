using System;
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
        private readonly Button _yokaiBtn;
        private readonly Button _itemBtn;
        private readonly Button _skillBtn;
        private readonly Button _npcBtn;
        private readonly Button _mapBtn;
        private readonly Button _warpBtn;
        private readonly Button _eventBtn;
        private readonly Button _dialogueBtn;
        private readonly Button _battleBtn;
        private readonly Button _saveBtn;
        private readonly Button _checkBtn;

        private MainWindow _yokaiWindow;
        private ItemEditorWindow _itemWindow;
        private SkillEditorWindow _skillWindow;
        private NpcEditorWindow _npcWindow;
        private MapEditorWindow _mapWindow;
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

            var open = new Button
            {
                Content = "Open a folder…",
                Padding = new Thickness(12, 9, 12, 9), FontSize = 14, FontWeight = FontWeights.SemiBold
            };
            open.Click += (s, e) => OpenFolder();
            root.Children.Add(open);

            _yokaiBtn = EditorButton("Yo-kai Editor", OpenYokaiEditor);
            _itemBtn = EditorButton("Item Editor", OpenItemEditor);
            _skillBtn = EditorButton("Skill Editor", OpenSkillEditor);
            _npcBtn = EditorButton("NPC Editor", OpenNpcEditor);
            _mapBtn = EditorButton("Map Editor", OpenMapEditor);
            _warpBtn = EditorButton("Warp Editor", OpenWarpEditor);
            _eventBtn = EditorButton("Event Editor", OpenEventEditor);
            _dialogueBtn = EditorButton("Dialogue Editor", OpenDialogueEditor);
            _battleBtn = EditorButton("Battle Editor", OpenBattleEditor);
            _saveBtn = EditorButton("Save Editor", OpenSaveEditor);
            _checkBtn = EditorButton("Check integrity", OpenIntegrity);

            AddSection(root, "Characters", _yokaiBtn, _itemBtn, _skillBtn);
            AddSection(root, "World", _npcBtn, _mapBtn, _warpBtn, _eventBtn, _dialogueBtn, _battleBtn);
            AddSection(root, "Save & Tools", _saveBtn, _checkBtn);

            foreach (var b in new[] { _yokaiBtn, _itemBtn, _skillBtn, _npcBtn, _mapBtn, _warpBtn,
                                      _eventBtn, _dialogueBtn, _battleBtn, _saveBtn, _checkBtn })
                b.IsEnabled = false;

            _status.Margin = new Thickness(1, 20, 0, 0);
            _status.Text = _referenceFolder != null
                ? "Open your extracted mod folder (YWML). The “cfg” folder is used as a reference for missing names."
                : "Open your extracted mod folder (YWML).";
            root.Children.Add(_status);
        }

        /// <summary>A titled group: a small caption + a hairline rule, then its buttons in a 3-column grid.</summary>
        private static void AddSection(Panel root, string title, params Button[] buttons)
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
            root.Children.Add(caption);

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

        private void OpenFolder()
        {
            string folder = FolderPicker.Pick("Extracted folder (YWML) containing chara_param / chara_base / chara_text…",
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if (folder == null) return;
            try
            {
                _db.LoadFolder(folder, _referenceFolder);

                // A freshly loaded db invalidates any editor windows still bound to the previous state.
                _yokaiWindow?.Close(); _yokaiWindow = null;
                _itemWindow?.Close(); _itemWindow = null;
                _skillWindow?.Close(); _skillWindow = null;
                _npcWindow?.Close(); _npcWindow = null;
                _mapWindow?.Close(); _mapWindow = null;
                _saveWindow?.Close(); _saveWindow = null;
                _eventWindow?.Close(); _eventWindow = null;

                _yokaiBtn.IsEnabled = _db.ParamFile != null;
                _itemBtn.IsEnabled = _db.Items.Count > 0;
                _skillBtn.IsEnabled = _db.Skills.Count > 0;
                _npcBtn.IsEnabled = _db.ParamFile != null;
                _mapBtn.IsEnabled = _db.Maps.Count > 0;
                _warpBtn.IsEnabled = _referenceFolder != null || _db.ModFolder != null;
                _eventBtn.IsEnabled = _referenceFolder != null || _db.ModFolder != null;
                _dialogueBtn.IsEnabled = _referenceFolder != null || _db.ModFolder != null;
                _battleBtn.IsEnabled = _db.Yokai.Count > 0;
                _saveBtn.IsEnabled = _db.Yokai.Count > 0;
                _checkBtn.IsEnabled = _db.ParamFile != null;

                string moves = _db.MoveOptions.Count > 0 ? $"named moves {_db.MoveNameCount}" : "unnamed moves";
                _status.Text = $"Loaded — {_db.Yokai.Count} yo-kai, {_db.Items.Count} items, {_db.Skills.Count} skills, {_db.Maps.Count} maps  ({moves}).";
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _status.Text = "Error: " + ex.Message;
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
}
