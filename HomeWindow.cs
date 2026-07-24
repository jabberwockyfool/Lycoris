using System;
using System.Windows;
using System.Windows.Controls;
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
        private readonly Button _saveBtn;
        private readonly Button _checkBtn;

        private MainWindow _yokaiWindow;
        private ItemEditorWindow _itemWindow;
        private SkillEditorWindow _skillWindow;
        private NpcEditorWindow _npcWindow;
        private MapEditorWindow _mapWindow;
        private SaveEditorWindow _saveWindow;

        public HomeWindow()
        {
            Title = "Lycoris — Yo-kai Watch 3 Editor";
            Width = 460; Height = 810;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Theme.WindowBg;

            var root = new StackPanel { Margin = new Thickness(24) };

            root.Children.Add(new TextBlock
            {
                Text = "Lycoris",
                FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Theme.Accent
            });
            root.Children.Add(new TextBlock
            {
                Text = "Yo-kai Watch 3 mod editor",
                FontSize = 13, Foreground = Theme.FgMuted, Margin = new Thickness(0, 0, 0, 18)
            });

            var open = new Button { Content = "📂  Open a folder (extracted mod)…", Padding = new Thickness(12, 8, 12, 8), FontSize = 14 };
            open.Click += (s, e) => OpenFolder();
            root.Children.Add(open);

            _yokaiBtn = BigButton("👹  Yo-kai Editor", "Stats, moves, evolutions, Blaster T, drops, charabase, portraits…", OpenYokaiEditor);
            _itemBtn = BigButton("🎁  Item Editor", "Name, description, price, inventory order, atlas icon…", OpenItemEditor);
            _skillBtn = BigButton("⚔  Skill Editor", "Type, element, power, hits, Soultimate range… (add/delete)", OpenSkillEditor);
            _npcBtn = BigButton("🧍  NPC Editor", "NPCMake config (TOML) editable in the GUI, import/export .toml.", OpenNpcEditor);
            _mapBtn = BigButton("🗺  Map Editor", "map_config: add/edit map entries (MapID, name, ShowMapCard…).", OpenMapEditor);
            _saveBtn = BigButton("💾  Save Editor", "Add yo-kai (incl. your modded ones) into a game{N}.yw save file.", OpenSaveEditor);
            _checkBtn = BigButton("🩺  Check integrity", "Detects broken references (missing move/drop/evolution) and duplicates.", OpenIntegrity);
            _yokaiBtn.IsEnabled = false;
            _itemBtn.IsEnabled = false;
            _skillBtn.IsEnabled = false;
            _npcBtn.IsEnabled = false;
            _mapBtn.IsEnabled = false;
            _saveBtn.IsEnabled = false;
            _checkBtn.IsEnabled = false;
            root.Children.Add(_yokaiBtn);
            root.Children.Add(_itemBtn);
            root.Children.Add(_skillBtn);
            root.Children.Add(_npcBtn);
            root.Children.Add(_mapBtn);
            root.Children.Add(_saveBtn);
            root.Children.Add(_checkBtn);

            root.Children.Add(_status);
            _status.Text = _referenceFolder != null
                ? "Tip: open your extracted mod folder (YWML). The “cfg” folder serves as a reference for missing names."
                : "Open your extracted mod folder (YWML).";

            Content = root;
        }

        private Button BigButton(string title, string subtitle, Action onClick)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap });
            var b = new Button
            {
                Content = sp,
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left
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

                _yokaiBtn.IsEnabled = _db.ParamFile != null;
                _itemBtn.IsEnabled = _db.Items.Count > 0;
                _skillBtn.IsEnabled = _db.Skills.Count > 0;
                _npcBtn.IsEnabled = _db.ParamFile != null;
                _mapBtn.IsEnabled = _db.Maps.Count > 0;
                _saveBtn.IsEnabled = _db.Yokai.Count > 0;
                _checkBtn.IsEnabled = _db.ParamFile != null;

                string moves = _db.MoveOptions.Count > 0 ? $"named moves {_db.MoveNameCount}" : "unnamed moves";
                _status.Text = $"Loaded — {_db.Yokai.Count} yo-kai, {_db.Items.Count} items, {_db.Skills.Count} skills, {_db.Maps.Count} maps  ({moves}).\n" +
                               "Choose an editor above.";
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
