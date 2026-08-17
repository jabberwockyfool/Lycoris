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
    /// NPC editor with two tabs:
    ///  • "Custom NPC (mod)" — manage a list of NPCMake NPC configs (the "TOML"), edit every field in
    ///    the GUI, import/export .toml, and compile. The list is persisted inside the mod (lycoris_npc/*.toml)
    ///    so custom NPCs can be re-opened, edited or removed later in case of a mistake.
    ///  • "Existing NPC (maps)" — browse/edit the NPCs already placed in the game's maps (BaseID, NpcType,
    ///    AppearCond, chapters, OnTalk XQ), saving into the mod.
    /// "From a yo-kai" computes BaseId = CRC32(model name) from the loaded mod.
    /// </summary>
    public sealed class NpcEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private readonly System.Collections.Generic.List<MapEntry> _maps;
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };

        // --- custom NPC tab ---
        private readonly ObservableCollection<NpcModel> _npcs = new ObservableCollection<NpcModel>();
        private readonly ListBox _list = new ListBox();
        private readonly TextBox _search = new TextBox();
        private readonly StackPanel _fields = new StackPanel();
        private ICollectionView _view;

        // --- existing NPC tab ---
        private readonly ComboBox _exMapCombo = new ComboBox { Width = 300, IsEditable = false, DisplayMemberPath = "DisplayName", SelectedValuePath = "MapFolderName" };
        private readonly ListBox _exList = new ListBox();
        private readonly StackPanel _exFields = new StackPanel();
        private readonly CheckBox[] _exChap = new CheckBox[12];
        private readonly TextBox _exOnTalk = new TextBox { Width = 380, Height = 90, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly TextBlock _exTalkInfo = new TextBlock { Foreground = Theme.FgMuted };
        private readonly Button _exLoadScript = new Button { Content = "Load XQ script", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 0, 4) };
        private MapNpcs _exMap;
        private ExistingNpc _exNpc;
        private bool _exSuppress;

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
            Title = "Lycoris — NPC Editor (NPCMake)";
            Width = 860; Height = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var tabs = new TabControl { Margin = new Thickness(4) };
            tabs.Items.Add(new TabItem { Header = "Custom NPC (mod)", Content = BuildCustomTab() });
            tabs.Items.Add(new TabItem { Header = "Existing NPC (maps)", Content = BuildExistingTab() });

            DockPanel.SetDock(_status, Dock.Bottom);
            var root = new DockPanel();
            root.Children.Add(_status);
            root.Children.Add(tabs);
            Content = root;

            _view = CollectionViewSource.GetDefaultView(_npcs);
            _view.Filter = Filter;
            _list.ItemsSource = _view;

            LoadPersistedNpcs();
            if (_npcs.Count == 0) NewNpc(); else _list.SelectedIndex = 0;

            _exFields.IsEnabled = false;

            // Persist edits (and any pending field edit) when the window closes.
            Closing += (s, e) => PersistNpcs();

            _status.Text = PersistDir != null
                ? "Custom NPCs stored in the mod (lycoris_npc). \"From a yo-kai\" computes the BaseId."
                : "No mod loaded: custom NPCs will not be kept. \"From a yo-kai\" computes the BaseId.";
        }

        // ============================ custom NPC tab ============================

        private FrameworkElement BuildCustomTab()
        {
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(ToolButton("+ New", NewNpc, 0));
            toolbar.Children.Add(ToolButton("Duplicate", DuplicateNpc));
            toolbar.Children.Add(ToolButton("Delete", DeleteNpc));
            toolbar.Children.Add(ToolButton("Import TOML…", ImportToml));
            toolbar.Children.Add(ToolButton("Export TOML…", ExportToml));
            toolbar.Children.Add(ToolButton("Compile…", CompileNpc));
            toolbar.Children.Add(ToolButton("Get save's location", GetSaveLocation));
            DockPanel.SetDock(toolbar, Dock.Top);

            var left = new DockPanel { Width = 220, Margin = new Thickness(6) };
            _search.Margin = new Thickness(0, 0, 0, 4);
            _search.TextChanged += (s, e) => _view?.Refresh();
            DockPanel.SetDock(_search, Dock.Top);
            _list.DisplayMemberPath = "DisplayName";
            _list.SelectionChanged += (s, e) => { _fields.DataContext = _list.SelectedItem; _fields.IsEnabled = _list.SelectedItem != null; UpdateDailyGreying(); };
            left.Children.Add(_search);
            left.Children.Add(_list);
            DockPanel.SetDock(left, Dock.Left);

            _fields.Margin = new Thickness(6);
            BuildFields();
            var right = new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var root = new DockPanel();
            root.Children.Add(toolbar);
            root.Children.Add(left);
            root.Children.Add(right);
            return root;
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
            _fields.Children.Add(TextRow("NPC name", "NpcName", 260));
            _fields.Children.Add(BaseIdRow());
            _fields.Children.Add(NumRow("NpcX", "NpcX"));
            _fields.Children.Add(NumRow("NpcY (2D)", "NpcY"));
            _fields.Children.Add(NumRow("NpcZ (height)", "NpcZ"));
            _fields.Children.Add(NumRow("Rotation (°)", "NpcRotation"));
            _fields.Children.Add(ComboRow("Chapter", "ChapterCode",
                new[] { "c01", "c02", "c03", "c04", "c05", "c06", "c07", "c08", "c09", "c10", "c11", "C99" }));
            _fields.Children.Add(MapRow());
            _fields.Children.Add(DescRow("OnTalk (XQ code)", "OnTalk"));
            _fields.Children.Add(TextRow("AppearCond", "AppearCond", 200));
            _fields.Children.Add(CheckRow("IsYw1 (leave unchecked for YW3)", "IsYw1"));
            _fields.Children.Add(ComboRow("NpcType", "NpcType", new[] { "HUMAN", "YOKAI" }));

            _fields.Children.Add(new TextBlock
            {
                Text = "Daily-fight NPC (a once-a-day battle: 4 talk events). Create the battle events first in " +
                       "the Event editor (Daily Fight), then give the base event name here. OnTalk is ignored in this mode.",
                TextWrapping = TextWrapping.Wrap, Foreground = Theme.FgMuted, Margin = new Thickness(0, 10, 0, 2)
            });
            _fields.Children.Add(CheckRow("Daily-fight NPC", "IsDailyFight"));
            _fields.Children.Add(ReuseEventsRow());
            _fields.Children.Add(TextRow("Base event (evXX_YYY0)", "DailyFightEvent", 160));
            _fields.Children.Add(BattleRow());
            _fields.Children.Add(PickModelRow("NPC model (yo-kai)", "DailyModel", "The yo-kai you fight — auto turn-target, auto BaseID, always a bustup."));
            // Rows below only feed EVENT GENERATION — greyed out when "Reuse existing events" is on.
            AddGen(BustupsRow());
            AddGen(CheckRow("Differentiate Katie/Nate (gender)", "DifferentiateGender"));
            AddGen(PickModelRow("Girl bustup (Katie/Hailey)", "GirlBustup", null));
            AddGen(PickModelRow("Boy bustup (Nate)", "BoyBustup", null));
            AddGen(DescRow("Intro — first time (♀)", "IntroText"));
            AddGen(DescRow("Intro (♂ — if gender on)", "IntroTextMale"));
            AddGen(DescRow("Accept — before battle", "AcceptText"));
            AddGen(DescRow("Decline", "DeclineText"));
            AddGen(DescRow("Repeat fight (♀)", "RepeatText"));
            AddGen(DescRow("Repeat fight (♂)", "RepeatTextMale"));
            AddGen(DescRow("Victory (♀)", "VictoryText"));
            AddGen(DescRow("Victory (♂)", "VictoryTextMale"));
            AddGen(DescRow("Loss (♀)", "LossText"));
            AddGen(DescRow("Loss (♂)", "LossTextMale"));
            _fields.Children.Add(DescRow("\"Come back tomorrow\" (npc_talk)", "TomorrowText"));
        }

        // Rows that only matter when GENERATING the daily events (greyed when reusing existing ones).
        private readonly System.Collections.Generic.List<FrameworkElement> _genOnlyRows = new System.Collections.Generic.List<FrameworkElement>();

        private void AddGen(FrameworkElement row) { _genOnlyRows.Add(row); _fields.Children.Add(row); }

        /// <summary>The "Reuse existing events" checkbox — bound + a hook to grey the generation-only rows.</summary>
        private FrameworkElement ReuseEventsRow()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label("Reuse existing events"));
            var chk = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            chk.SetBinding(CheckBox.IsCheckedProperty, new Binding("ReuseExistingEvents") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            chk.Checked += (s, e) => UpdateDailyGreying();
            chk.Unchecked += (s, e) => UpdateDailyGreying();
            sp.Children.Add(chk);
            sp.Children.Add(new TextBlock { Text = "  wire only, don't regenerate the 4 events", Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center });
            return sp;
        }

        /// <summary>Grey the event-generation-only fields when the selected NPC reuses existing events.</summary>
        private void UpdateDailyGreying()
        {
            bool reuse = Selected?.ReuseExistingEvents ?? false;
            foreach (var r in _genOnlyRows) r.IsEnabled = !reuse;
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
            var pick = new Button { Content = "From a yo-kai…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            pick.Click += (s, e) => BaseIdFromYokai();
            sp.Children.Add(pick);
            return sp;
        }

        // ---------- daily-fight rows ----------

        /// <summary>Battle picker: the common_enc battle list (as in the Event editor) + free-text id.</summary>
        private FrameworkElement BattleRow()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label("Battle"));
            var cb = new ComboBox { Width = 300, IsEditable = true };
            try { cb.ItemsSource = Yokai.CommonEnc.AllBattles(_db); cb.DisplayMemberPath = "Label"; } catch { /* no battles loaded */ }
            cb.SetBinding(ComboBox.TextProperty, new Binding("DailyBattle") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(cb);
            return sp;
        }

        /// <summary>A model text box + "yo-kai…" picker that sets the bound property to the chosen model name.</summary>
        private FrameworkElement PickModelRow(string label, string path, string hint)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label(label));
            var tb = new TextBox { Width = 150 };
            tb.SetBinding(TextBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            var btn = new Button { Content = "yo-kai…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            btn.Click += (s, e) => { var m = PickModel(); if (m != null) SetProp(path, m); };
            sp.Children.Add(btn);
            if (hint != null)
                sp.Children.Add(new TextBlock { Text = "  " + hint, Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Width = 240 });
            return sp;
        }

        /// <summary>Multiline bustups + a "+ yo-kai" button that appends the chosen model to the list.</summary>
        private FrameworkElement BustupsRow()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label("Bustups (one/line)"));
            var tb = new TextBox { Width = 300, Height = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            tb.SetBinding(TextBox.TextProperty, new Binding("DailyBustups") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            var btn = new Button { Content = "+ yo-kai", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            btn.Click += (s, e) => AppendBustup();
            sp.Children.Add(btn);
            return sp;
        }

        /// <summary>Open the yo-kai picker and return the chosen model name (or a typed id), or null.</summary>
        private string PickModel()
        {
            if (_db == null || _db.Yokai.Count == 0) { DarkMessage.Show("No yo-kai loaded — you can still type an id (e.g. y152000).", "Pick yo-kai"); return null; }
            var dlg = new PickYokaiDialog(this, _db) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Picked == null) return null;
            return string.IsNullOrEmpty(dlg.Picked.ModelName) ? null : dlg.Picked.ModelName;
        }

        private void SetProp(string path, string value) => typeof(NpcModel).GetProperty(path)?.SetValue(Selected, value);

        private void AppendBustup()
        {
            var m = PickModel(); var n = Selected;
            if (m == null || n == null) return;
            n.DailyBustups = string.IsNullOrWhiteSpace(n.DailyBustups) ? m : n.DailyBustups.TrimEnd('\r', '\n') + "\n" + m;
        }

        // ---------- actions ----------

        private NpcModel Selected => _list.SelectedItem as NpcModel;

        /// <summary>Read the player's position from a save and drop it on the selected NPC. Asks whether to
        /// use Nate's (male) or Hailey's (female) coordinates; remembers the save used (YwSave.Last*Path).</summary>
        private void GetSaveLocation()
        {
            var npc = Selected;
            if (npc == null) { DarkMessage.Show("Select an NPC first.", "Get save's location"); return; }

            var who = DarkMessage.Show("Whose location from the save?\n\nYes = Nate (male)\nNo = Hailey (female)",
                "Get save's location", MessageBoxButton.YesNoCancel);
            if (who == MessageBoxResult.Cancel || who == MessageBoxResult.None) return;
            bool female = who == MessageBoxResult.No;

            string game = Formats.YwSave.LastGamePath;
            if (string.IsNullOrEmpty(game) || !System.IO.File.Exists(game))
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Yo-kai Watch save|*.yw|All files|*.*", Title = "Open a game{N}.yw save" };
                if (dlg.ShowDialog() != true) return;
                game = dlg.FileName;
            }
            string head = Formats.YwSave.LastHeadPath;
            if (string.IsNullOrEmpty(head) || !System.IO.File.Exists(head))
            {
                string sib = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(game) ?? "", "head.yw");
                if (System.IO.File.Exists(sib)) head = sib;
                else
                {
                    var hd = new Microsoft.Win32.OpenFileDialog { Filter = "head.yw|head.yw|All files|*.*", Title = "Select head.yw" };
                    if (hd.ShowDialog() != true) return;
                    head = hd.FileName;
                }
            }
            try
            {
                var save = Formats.YwSave.Load(game, head);
                if (!save.TryReadPlayerLocation(female, out float c0, out float c1, out float c2))
                { DarkMessage.Show("Could not read the player position (section 242) from this save.", "Get save's location"); return; }
                npc.NpcX = c0; npc.NpcZ = c1; npc.NpcY = c2;   // POINT (X, height, horizontalZ) -> NpcX/NpcZ/NpcY
                _status.Text = $"{(female ? "Hailey" : "Nate")}'s location → X={c0:0.##}  Z(height)={c1:0.##}  Y={c2:0.##}  (from {System.IO.Path.GetFileName(game)})";
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Get save's location", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CommitEdits()
        {
            var f = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            // Belt-and-suspenders: flush every bound TextBox/ComboBox in the fields panel so a pending edit
            // (e.g. a dialogue field the user just typed in) is written to the model before compiling.
            foreach (var el in Descendants(_fields))
            {
                (el as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                (el as ComboBox)?.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
            }
        }

        private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            if (root == null) yield break;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var d in Descendants(child)) yield return d;
            }
        }

        private void NewNpc()
        {
            var n = new NpcModel();
            _npcs.Add(n);
            _list.SelectedItem = n;
            PersistNpcs();
        }

        private void DuplicateNpc()
        {
            var src = Selected;
            if (src == null) return;
            CommitEdits();
            var n = src.Clone();
            n.NpcName = src.NpcName + " (copy)";
            _npcs.Add(n);
            _list.SelectedItem = n;
            _status.Text = $"Duplicated: {n.DisplayName}.";
            PersistNpcs();
        }

        private void DeleteNpc()
        {
            var n = Selected;
            if (n == null) return;
            int idx = _list.SelectedIndex;
            _npcs.Remove(n);
            if (_npcs.Count > 0) _list.SelectedIndex = Math.Min(idx, _npcs.Count - 1);
            _status.Text = "NPC removed from the list.";
            PersistNpcs();
        }

        private void ImportToml()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "TOML files|*.toml|All|*.*", Multiselect = true, Title = "Import one or more NPCMake .toml" };
            if (dlg.ShowDialog() != true) return;
            int added = 0;
            foreach (var path in dlg.FileNames)
            {
                try { _npcs.Add(NpcToml.Parse(File.ReadAllText(path))); added++; }
                catch (Exception ex) { DarkMessage.Show($"{Path.GetFileName(path)}: {ex.Message}", "Import TOML"); }
            }
            if (added > 0) { _list.SelectedIndex = _npcs.Count - 1; _status.Text = $"{added} NPC imported."; PersistNpcs(); }
        }

        private void ExportToml()
        {
            var n = Selected;
            if (n == null) return;
            CommitEdits();
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "TOML files|*.toml", FileName = SafeFileName(n.NpcName) + ".toml", Title = "Export the NPC as .toml" };
            if (dlg.ShowDialog() != true) return;
            try { File.WriteAllText(dlg.FileName, NpcToml.Write(n), new UTF8Encoding(false)); _status.Text = $"Exported: {Path.GetFileName(dlg.FileName)}"; }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Export TOML", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void CompileNpc()
        {
            var n = Selected;
            if (n == null) return;
            CommitEdits();
            if (string.IsNullOrWhiteSpace(n.NpcName)) { DarkMessage.Show("Give the NPC a name.", "Compile"); return; }
            if (n.IsDailyFight && string.IsNullOrWhiteSpace(n.DailyFightEvent))
            {
                DarkMessage.Show("A daily-fight NPC needs a base event name (evXX_YYY0).", "Compile");
                return;
            }
            // A daily-fight NPC IS the yo-kai model you fight: derive the BaseID from it if not set explicitly.
            if (n.IsDailyFight && n.BaseId == 0 && !string.IsNullOrWhiteSpace(n.DailyModel))
                n.BaseId = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(n.DailyModel.Trim())));

            if ((!string.IsNullOrWhiteSpace(n.OnTalk) || n.IsDailyFight) && !NpcXq.IsAvailable())
            {
                DarkMessage.Show("xtractquery was not found in PATH — required to compile the talk XQ.\n" +
                    "Install it globally (see yo-docs), or clear the OnTalk field (non-daily NPC) to compile without dialogue.",
                    "xtractquery missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Auto-merge target inside the loaded mod (mirrors res/map/<MapID>). Null if no mod loaded.
            // Mod res lives at include/data/res (include = <mod>/include, or the mod folder if it is already it).
            string incBase = string.IsNullOrEmpty(_db?.ModFolder) ? null
                : (System.IO.Directory.Exists(System.IO.Path.Combine(_db.ModFolder, "include")) ? System.IO.Path.Combine(_db.ModFolder, "include") : _db.ModFolder);
            string mergeMapDir = incBase == null ? null : System.IO.Path.Combine(incBase, "data", "res", "map", n.MapID);

            // Base/complete map = the vanilla reference (fallback for anything not yet in the mod). The compiler
            // reads each file from the mod copy (mergeMapDir) FIRST, so repeated compiles keep earlier NPCs.
            string mapFolder = FindMapDir(n.MapID);
            if (mapFolder == null && mergeMapDir != null && System.IO.File.Exists(System.IO.Path.Combine(mergeMapDir, "npc.pck")))
                mapFolder = mergeMapDir;
            if (mapFolder == null)
            {
                mapFolder = FolderPicker.Pick($"Folder for map \"{n.MapID}\" (containing npc.pck, {n.MapID}.pck…)", Handle);
                if (mapFolder == null) return;
            }
            string outRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lycoris_npc_build");

            string plan = mergeMapDir != null
                ? $"The files will be ADDED to your mod:\n{mergeMapDir}"
                : "No mod loaded: choose an output folder (manual merge).";
            if (DarkMessage.Show($"Compile \"{n.NpcName}\" for map {n.MapID}?\n\n{plan}",
                "Compile the NPC", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            if (mergeMapDir == null)
            {
                string picked = FolderPicker.Pick("Output folder (a <Name>_output folder will be created there)", Handle);
                if (picked == null) return;
                outRoot = picked;
            }

            NpcCompiler.DailyPaths daily = n.IsDailyFight ? ResolveDailyPaths() : null;

            try
            {
                var r = NpcCompiler.Compile(n, mapFolder, outRoot, mergeMapDir, daily);
                _status.Text = $"NPC compiled — ID {r.NpcIdHex}." + (r.MergedDir != null ? " Merged into the mod." : "");

                // Daily-fight: also generate the 4 events the NPC's triggers call (xq + config + dialogue) —
                // unless "Reuse existing events" is on, in which case the 4 events already exist and we only wired
                // the NPC's talk/triggers to them (by name, so nothing else to do).
                string eventMsg = "";
                if (n.IsDailyFight && n.ReuseExistingEvents)
                {
                    string ev0 = n.DailyFightEvent.Trim();
                    eventMsg = $"\n\n♻ Reused existing events: {ev0}, {NpcDailyFight.StepEvent(ev0, 10)}, " +
                               $"{NpcDailyFight.StepEvent(ev0, 20)}, {NpcDailyFight.StepEvent(ev0, 30)} (not regenerated).";
                }
                else if (n.IsDailyFight)
                {
                    try { eventMsg = "\n\n✔ " + GenerateDailyEvents(n); }
                    catch (Exception ex) { eventMsg = "\n\n⚠ The NPC wiring was written, but the events were NOT generated:\n" + ex.Message; }
                }

                string msg = $"NPC \"{n.NpcName}\" compiled.\n\nNPC ID (hex): {r.NpcIdHex}\n" +
                             (r.FuncId >= 0 ? $"Talk function(s) start at RunCmd_Map{r.FuncId}\n" : "OnTalk: (none)\n") +
                             (r.MergedDir != null
                                ? $"\n✔ Automatically merged into the mod:\n{r.MergedDir}\n\n(backup copy: {r.OutputDir})"
                                : $"\nFiles written to:\n{r.OutputDir}\nMerge this folder into your mod (res/map/…).") +
                             eventMsg;
                DarkMessage.Show(msg, "Compilation succeeded", MessageBoxButton.OK, MessageBoxImage.Information);
                PersistNpcs();
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Compilation failed", MessageBoxButton.OK, MessageBoxImage.Error);
                _status.Text = "Compilation failed: " + ex.Message;
            }
        }

        /// <summary>Find the complete base map (with npc.pck) — the reference first (it is complete), then the
        /// mod. The mod's partial copy is used as the accumulation source separately (mergeMapDir).</summary>
        private string FindMapDir(string mapId)
        {
            foreach (var root in new[] { _db?.ReferenceFolder, _db?.ModFolder })
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var cand in new[] {
                    System.IO.Path.Combine(root, "res", "map", mapId),
                    System.IO.Path.Combine(root, "data", "res", "map", mapId),
                    System.IO.Path.Combine(root, "include", "data", "res", "map", mapId),
                    System.IO.Path.Combine(root, mapId),
                })
                    if (System.IO.File.Exists(System.IO.Path.Combine(cand, "npc.pck"))) return cand;
            }
            return null;
        }

        /// <summary>Locate the global flag_config for a daily-fight NPC: read the mod's copy if it exists (so
        /// repeated compiles accumulate), else the reference; write the edit to the mod (mirrored path).</summary>
        private NpcCompiler.DailyPaths ResolveDailyPaths()
        {
            // flag_config belongs at (mod)/include/data/res/sys — same place as event_set_config (the reference
            // keeps it at its cfg root, so MirrorToMod would put it in the wrong spot). Read the mod copy first
            // (so repeated compiles accumulate), else the reference.
            const string flagName = "flag_config_0.01r.cfg.bin";
            string incBase = IncBase();
            string modCfg = incBase != null ? Path.Combine(incBase, "data", "res", "sys", flagName) : null;
            string refCfg = FindFlagConfigUnder(_db?.ReferenceFolder);
            string src = (modCfg != null && File.Exists(modCfg)) ? modCfg : refCfg;
            return new NpcCompiler.DailyPaths { FlagConfigSrc = src, FlagConfigDst = modCfg };
        }

        private static string FindFlagConfigUnder(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            string direct = Path.Combine(root, "flag_config_0.01r.cfg.bin");
            if (File.Exists(direct)) return direct;
            try { return Directory.EnumerateFiles(root, "flag_config_0.01r.cfg.bin", SearchOption.AllDirectories).FirstOrDefault(); }
            catch { return null; }
        }

        /// <summary>
        /// Generate the 4 daily-fight events the NPC's talk triggers call (ev+0/+10/+20/+30): first-time and
        /// repeat are battle events (BuildDailyFightSource); win and lose are dialogue-only (BuildDailyDialogue-
        /// Source). Each gets its .xq (compiled), an event_set_config entry, and its dialogue text. The daily
        /// (sub14025) flag = CRC32(base event) — the same flag the npc_talk GetOneDayBitFlag checks.
        /// </summary>
        private string GenerateDailyEvents(NpcModel n)
        {
            string incBase = IncBase();
            if (incBase == null) throw new InvalidOperationException("No mod loaded — cannot write the events.");

            const string cfgName = "event_set_config_0.01.cfg.bin";
            string modCfg = Path.Combine(incBase, "data", "res", "sys", cfgName);
            string refCfg = null;
            foreach (var cand in new[] { Path.Combine(_db?.ReferenceFolder ?? "", cfgName),
                Path.Combine(_db?.ReferenceFolder ?? "", "data", "res", "sys", cfgName) })
                if (File.Exists(cand)) { refCfg = cand; break; }
            string loadPath = File.Exists(modCfg) ? modCfg : refCfg;
            if (loadPath == null) throw new InvalidOperationException($"Could not find {cfgName} in the mod or reference.");

            var eventDirs = new System.Collections.Generic.List<string>();
            if (_db?.ReferenceFolder != null)
            {
                eventDirs.Add(Path.Combine(_db.ReferenceFolder, "seq", "event"));
                eventDirs.Add(Path.Combine(_db.ReferenceFolder, "include", "seq", "event"));   // reorg: cfg/include/…
            }
            eventDirs.Add(Path.Combine(incBase, "seq", "event"));
            var es = EventSet.Load(loadPath, eventDirs);

            string ev0 = n.DailyFightEvent.Trim();
            string[] names = { ev0, NpcDailyFight.StepEvent(ev0, 10), NpcDailyFight.StepEvent(ev0, 20), NpcDailyFight.StepEvent(ev0, 30) };
            var bustups = (n.DailyBustups ?? "").Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            string model = n.DailyModel?.Trim();
            // The NPC's model is the turn target and always shown — add it to the bustups if the user didn't.
            if (!string.IsNullOrWhiteSpace(model) && !bustups.Any(b => b.Equals(model, StringComparison.OrdinalIgnoreCase)))
                bustups.Add(model);
            bool genderDlg = n.DifferentiateGender;
            string girl = genderDlg ? n.GirlBustup : null;
            string boy = genderDlg ? n.BoyBustup : null;
            uint flag = unchecked((uint)Crc32.Standard(Encoding.UTF8.GetBytes(ev0)));
            string battleExpr = EventGen.BattleExpr(n.DailyBattle);
            string speaker = string.IsNullOrWhiteSpace(model) ? bustups.LastOrDefault() : model;

            var sources = new[]
            {
                EventSet.BuildDailyFightSource(names[0], bustups, battleExpr, flag, girl, boy, genderDlg),
                EventSet.BuildDailyFightSource(names[1], bustups, battleExpr, flag, girl, boy, genderDlg),
                EventSet.BuildDailyDialogueSource(names[2], bustups, model, girl, boy, genderDlg),
                EventSet.BuildDailyDialogueSource(names[3], bustups, model, girl, boy, genderDlg),
            };
            // Per-event dialogue: female + optional male _010 block, and (battle events only) accept/decline.
            var dlg = new[]
            {
                (female: n.IntroText, male: n.IntroTextMale, accept: n.AcceptText, decline: n.DeclineText),
                (female: n.RepeatText, male: n.RepeatTextMale, accept: n.AcceptText, decline: n.DeclineText),
                (female: n.VictoryText, male: n.VictoryTextMale, accept: (string)null, decline: (string)null),
                (female: n.LossText, male: n.LossTextMale, accept: (string)null, decline: (string)null),
            };

            for (int i = 0; i < 4; i++)
            {
                byte[] xq = NpcXq.CompileScript(sources[i], out _);
                string xqPath = Path.Combine(incBase, "seq", "event", names[i] + ".xq");
                Directory.CreateDirectory(Path.GetDirectoryName(xqPath));
                File.WriteAllBytes(xqPath, xq);

                int hash = EventSet.NameHash(names[i]);
                if (!es.Contains(hash)) es.AddEvent(hash, names[i]);

                var blocks = new System.Collections.Generic.List<(string, System.Collections.Generic.List<Lycoris.Yokai.DialogueLine>)>
                {
                    ("_010", EventGen.ParseBlock(dlg[i].female, speaker)),
                };
                if (genderDlg)
                {
                    string male = string.IsNullOrWhiteSpace(dlg[i].male) ? dlg[i].female : dlg[i].male;
                    blocks.Add((EventSet.MaleBlock("_010"), EventGen.ParseBlock(male, speaker)));
                }
                if (dlg[i].accept != null) blocks.Add(("_020", EventGen.ParseBlock(dlg[i].accept, speaker)));
                if (dlg[i].decline != null) blocks.Add(("_030", EventGen.ParseBlock(dlg[i].decline, speaker)));
                EventGen.WriteDialogueBlocks(_db, incBase, names[i], blocks);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(modCfg));
            T2bWriter.WriteFile(es.File, modCfg);
            return $"4 events generated: {string.Join(", ", names)} (+ event_set_config, dialogue{(genderDlg ? " ♀/♂" : "")}, daily flag 0x{flag:X8}).";
        }

        private void BaseIdFromYokai()
        {
            var n = Selected;
            if (n == null) return;
            if (_db == null || _db.Yokai.Count == 0)
            {
                DarkMessage.Show("No yo-kai loaded (open a mod folder first).", "From a yo-kai");
                return;
            }
            var dlg = new PickYokaiDialog(this, _db) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Picked == null) return;
            string model = dlg.Picked.ModelName;
            if (string.IsNullOrEmpty(model))
            {
                DarkMessage.Show("This yo-kai has no model name.", "From a yo-kai");
                return;
            }
            n.BaseId = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(model)));
            n.NpcType = "YOKAI";
            _status.Text = $"BaseId = {n.BaseIdHex} (CRC32 of \"{model}\"), NpcType = YOKAI.";
        }

        private static string SafeFileName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "npc";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        // ---------- persistence (custom NPCs stored inside the mod) ----------

        /// <summary>Where the custom NPC list is stored (mod/lycoris_npc), or null if no mod is loaded.</summary>
        private string PersistDir => string.IsNullOrEmpty(_db?.ModFolder) ? null : Path.Combine(_db.ModFolder, "lycoris_npc");

        private void LoadPersistedNpcs()
        {
            string dir = PersistDir;
            if (dir == null || !Directory.Exists(dir)) return;
            foreach (var path in Directory.GetFiles(dir, "*.toml").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try { _npcs.Add(NpcToml.Parse(File.ReadAllText(path))); }
                catch { /* skip a malformed file rather than fail to open the editor */ }
            }
        }

        /// <summary>Rewrite the whole lycoris_npc folder from the current list (captures pending field edits first).</summary>
        private void PersistNpcs()
        {
            string dir = PersistDir;
            if (dir == null) return;
            CommitEdits();
            try
            {
                Directory.CreateDirectory(dir);
                foreach (var old in Directory.GetFiles(dir, "*.toml")) File.Delete(old);
                int i = 0;
                foreach (var n in _npcs)
                    File.WriteAllText(Path.Combine(dir, $"{i++:000}_{SafeFileName(n.NpcName)}.toml"),
                        NpcToml.Write(n), new UTF8Encoding(false));
            }
            catch { /* best-effort: never block the UI on a persistence failure */ }
        }

        // ============================ existing NPC tab ============================

        private FrameworkElement BuildExistingTab()
        {
            var save = new Button { Content = "Save the mod", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(10, 0, 0, 0) };
            save.Click += (s, e) => SaveExisting();
            var del = new Button { Content = "Delete NPC", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            del.Click += (s, e) => DeleteExNpc();
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(new TextBlock { Text = "Map ", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            _exMapCombo.ItemsSource = _db != null ? _db.Maps.OrderBy(m => m.MapFolderName, StringComparer.OrdinalIgnoreCase).ToList() : null;
            _exMapCombo.SelectionChanged += (s, e) => LoadExMap();
            toolbar.Children.Add(_exMapCombo);
            toolbar.Children.Add(save);
            toolbar.Children.Add(del);
            DockPanel.SetDock(toolbar, Dock.Top);

            var left = new DockPanel { Width = 230, Margin = new Thickness(6) };
            _exList.DisplayMemberPath = "DisplayName";
            _exList.SelectionChanged += (s, e) => ShowExNpc(_exList.SelectedItem as ExistingNpc);
            left.Children.Add(_exList);
            DockPanel.SetDock(left, Dock.Left);

            _exFields.Margin = new Thickness(8);
            BuildExistingFields();

            var root = new DockPanel();
            root.Children.Add(toolbar);
            root.Children.Add(left);
            root.Children.Add(new ScrollViewer { Content = _exFields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            return root;
        }

        private void BuildExistingFields()
        {
            _exFields.Children.Add(ReadOnlyRow("Model (npcbin)", "ModelName"));
            _exFields.Children.Add(ReadOnlyRow("NpcID", "NpcIdHex"));
            _exFields.Children.Add(ExBaseIdRow());
            _exFields.Children.Add(ExNpcTypeRow());
            _exFields.Children.Add(TextRow("AppearCond", "AppearCond", 300));

            var chGrid = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 2) };
            chGrid.Children.Add(Label("Chapters"));
            var wrap = new WrapPanel { Width = 400 };
            for (int ch = 1; ch <= 11; ch++)
            {
                int c = ch;
                _exChap[ch] = new CheckBox { Content = "c" + ch.ToString("00"), Width = 60, Margin = new Thickness(0, 2, 0, 2) };
                _exChap[ch].Checked += (s, e) => ExChapterToggled(c, true);
                _exChap[ch].Unchecked += (s, e) => ExChapterToggled(c, false);
                wrap.Children.Add(_exChap[ch]);
            }
            chGrid.Children.Add(wrap);
            _exFields.Children.Add(chGrid);

            var talk = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 2) };
            talk.Children.Add(Label("OnTalk (XQ)"));
            var talkCol = new StackPanel();
            talkCol.Children.Add(_exTalkInfo);
            _exLoadScript.Click += (s, e) => LoadExScript();
            talkCol.Children.Add(_exLoadScript);
            _exOnTalk.SetBinding(TextBox.TextProperty, new Binding("OnTalk") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            talkCol.Children.Add(_exOnTalk);
            talk.Children.Add(talkCol);
            _exFields.Children.Add(talk);
        }

        // NpcType as a small combo (Human=2 / Yokai=0)
        private FrameworkElement ExNpcTypeRow()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label("NpcType"));
            var cb = new ComboBox { Width = 140 };
            cb.Items.Add(new EnumEntry(2, "Human"));
            cb.Items.Add(new EnumEntry(0, "Yokai"));
            cb.DisplayMemberPath = "Name"; cb.SelectedValuePath = "Key";
            cb.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new Binding("NpcType") { Mode = BindingMode.TwoWay });
            sp.Children.Add(cb);
            return sp;
        }

        private static FrameworkElement ReadOnlyRow(string label, string path)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label(label));
            var tb = new TextBlock { FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
            tb.SetBinding(TextBlock.TextProperty, new Binding(path));
            sp.Children.Add(tb);
            return sp;
        }

        private FrameworkElement ExBaseIdRow()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label("BaseID (hex)"));
            var tb = new TextBox { Width = 130 };
            tb.SetBinding(TextBox.TextProperty, new Binding("BaseIdHex") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            var pick = new Button { Content = "From a yo-kai…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
            pick.Click += (s, e) => ExBaseIdFromYokai();
            sp.Children.Add(pick);
            return sp;
        }

        private void LoadExMap()
        {
            string mapId = _exMapCombo.SelectedValue as string;
            if (string.IsNullOrEmpty(mapId)) return;
            string dir = FindMapDirWithSet(mapId);
            if (dir == null) { _exList.ItemsSource = null; _exFields.IsEnabled = false; _status.Text = $"No res/map/{mapId} folder (neither mod nor reference)."; return; }
            try
            {
                _exMap = ExistingNpcs.Load(dir, mapId);
            }
            catch (Exception ex)
            {
                _exMap = null; _exList.ItemsSource = null; _exFields.IsEnabled = false;
                _status.Text = $"Could not read {mapId}: {ex.Message}";
                DarkMessage.Show($"The NPC data for « {mapId} » could not be read — the map's files in your mod may be " +
                    $"edited or corrupt.\n\n{ex.Message}", "Cannot open map", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_exMap == null || _exMap.Npcs.Count == 0) { _exList.ItemsSource = null; _exFields.IsEnabled = false; _status.Text = "This map has no NPC (npc_set)."; return; }
            _exList.ItemsSource = _exMap.Npcs;
            _exList.SelectedIndex = 0;
            _status.Text = $"{_exMap.Npcs.Count} NPC in {mapId}.";
        }

        private void ShowExNpc(ExistingNpc n)
        {
            _exNpc = n;
            _exFields.DataContext = n;
            _exFields.IsEnabled = n != null;
            _exSuppress = true;
            for (int ch = 1; ch <= 11; ch++) _exChap[ch].IsChecked = n != null && n.Chapters[ch];
            _exSuppress = false;
            bool xq = n != null && n.HasXqTalk;
            _exTalkInfo.Text = n == null ? "" : (xq ? $"XQ script: RunCmd_Map{n.FuncId}" : "Vanilla talk (data, no XQ script).");
            _exLoadScript.IsEnabled = xq && _exMap?.PckPath != null;
            _exOnTalk.IsEnabled = xq;
            _exOnTalk.Text = ""; // loaded on demand
        }

        private void ExChapterToggled(int ch, bool on)
        {
            if (_exSuppress || _exNpc == null) return;
            _exNpc.Chapters[ch] = on;
            _exNpc.ChaptersDirty = true;
            _status.Text = "Chapters modified — remember to \"Save the mod\".";
        }

        private void LoadExScript()
        {
            if (_exNpc == null || !_exNpc.HasXqTalk || _exMap?.PckPath == null) return;
            if (!NpcXq.IsAvailable()) { DarkMessage.Show("xtractquery not found in PATH — required to read/edit the XQ script.", "OnTalk", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            try
            {
                var pck = Xpck.Read(File.ReadAllBytes(_exMap.PckPath));
                var xqFile = pck.FirstOrDefault(x => x.Name.EndsWith(".xq", StringComparison.OrdinalIgnoreCase) && x.Name.IndexOf("quest", StringComparison.OrdinalIgnoreCase) < 0);
                if (xqFile == null) { DarkMessage.Show("No .xq in the .pck.", "OnTalk"); return; }
                string body = NpcXq.ExtractFunction(xqFile.Data, _exNpc.FuncId);
                _exNpc.OnTalk = body;
                _exNpc.OnTalkDirty = false;
                _exOnTalk.Text = body;
                _status.Text = $"Script RunCmd_Map{_exNpc.FuncId} loaded.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "OnTalk", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ExBaseIdFromYokai()
        {
            if (_exNpc == null || _db.Yokai.Count == 0) return;
            var dlg = new PickYokaiDialog(this, _db) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Picked == null) return;
            string model = dlg.Picked.ModelName;
            if (string.IsNullOrEmpty(model)) { DarkMessage.Show("This yo-kai has no model.", "BaseID"); return; }
            _exNpc.BaseId = unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(model)));
            _exNpc.NpcType = 0; // yokai
            _status.Text = $"BaseID = {_exNpc.BaseIdHex} (CRC32 of \"{model}\").";
        }

        private void SaveExisting()
        {
            var f = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            if (_exMap == null) return;
            try
            {
                var written = ExistingNpcs.Save(_exMap, ExMirror);

                // OnTalk (XQ) edits -> repack the map .pck
                var onTalkEdited = _exMap.Npcs.Where(n => n.OnTalkDirty && n.HasXqTalk).ToList();
                if (onTalkEdited.Count > 0 && _exMap.PckPath != null)
                {
                    if (!NpcXq.IsAvailable()) { DarkMessage.Show("xtractquery not found — the OnTalk scripts were not recompiled (the rest is saved).", "OnTalk", MessageBoxButton.OK, MessageBoxImage.Warning); }
                    else
                    {
                        var pck = Xpck.Read(File.ReadAllBytes(_exMap.PckPath));
                        var xqFile = pck.FirstOrDefault(x => x.Name.EndsWith(".xq", StringComparison.OrdinalIgnoreCase) && x.Name.IndexOf("quest", StringComparison.OrdinalIgnoreCase) < 0);
                        if (xqFile != null)
                        {
                            byte[] xqData = xqFile.Data;
                            foreach (var n in onTalkEdited) { xqData = NpcXq.ReplaceFunction(xqData, n.FuncId, n.OnTalk ?? "", out _); n.OnTalkDirty = false; }
                            Xpck.AddOrReplace(pck, xqFile.Name, xqData);
                            string outPck = ExMirror(_exMap.PckPath);
                            Directory.CreateDirectory(Path.GetDirectoryName(outPck));
                            File.WriteAllBytes(outPck, Xpck.Write(pck));
                            written.Add(outPck);
                        }
                    }
                }
                foreach (var n in _exMap.Npcs) { n.IsDirty = false; n.ChaptersDirty = false; }
                _status.Text = written.Count > 0 ? $"Saved — {written.Count} file(s) written to the mod." : "No changes to save.";
                if (written.Count > 0) DarkMessage.Show("NPCs saved to the mod:\n" + string.Join("\n", written.Select(Path.GetFileName)), "Save", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Save NPC", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void DeleteExNpc()
        {
            if (_exMap == null) { DarkMessage.Show("Open a map first.", "Delete NPC"); return; }
            var npc = _exList.SelectedItem as ExistingNpc;
            if (npc == null) { DarkMessage.Show("Select an NPC to delete.", "Delete NPC"); return; }
            if (DarkMessage.Show(
                    $"Delete « {npc.DisplayName} » from {_exMap.MapId}?\n\n" +
                    "This removes its placement, base, talk and trigger from your mod.\n" +
                    "⚠ Deleting a story/event NPC can break events that spawn it — prefer deleting NPCs you added.",
                    "Delete NPC", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            try
            {
                var written = ExistingNpcs.Delete(_exMap, npc, ExMirror);
                _exList.ItemsSource = null;
                _exList.ItemsSource = _exMap.Npcs;
                _exFields.IsEnabled = false;
                _status.Text = $"Deleted {npc.ModelName} — {written.Count} file(s) written to the mod.";
                DarkMessage.Show("NPC deleted. Files written:\n" + string.Join("\n", written.Select(Path.GetFileName)), "Delete NPC", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Delete NPC", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        /// <summary>Mirror a map file into the mod at include/data/res/map/&lt;MapID&gt; (where NPC edits belong).</summary>
        private string ExMirror(string srcPath)
        {
            string incBase = IncBase();
            if (incBase == null || _exMap == null) return _db?.MirrorToMod(srcPath) ?? srcPath;
            string dir = System.IO.Path.Combine(incBase, "data", "res", "map", _exMap.MapId);
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, System.IO.Path.GetFileName(srcPath));
        }

        private string IncBase()
        {
            if (string.IsNullOrEmpty(_db?.ModFolder)) return null;
            string inc = System.IO.Path.Combine(_db.ModFolder, "include");
            return System.IO.Directory.Exists(inc) ? inc : _db.ModFolder;
        }

        private string FindMapDirWithSet(string mapId)
        {
            foreach (var root in new[] { _db?.ModFolder, _db?.ReferenceFolder })
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var cand in new[] {
                    Path.Combine(root, "res", "map", mapId),
                    Path.Combine(root, "data", "res", "map", mapId),
                    Path.Combine(root, "include", "data", "res", "map", mapId),
                    Path.Combine(root, mapId),
                })
                    if (Directory.Exists(cand) && Directory.EnumerateFiles(cand, mapId + "_npc_set*").Any()) return cand;
            }
            return null;
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
            Title = "Choose a yo-kai";
            Width = 320; Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _search.Margin = new Thickness(0, 0, 0, 4);
            _search.TextChanged += (s, e) => _view?.Refresh();
            DockPanel.SetDock(_search, Dock.Top);
            _list.DisplayMemberPath = "DisplayName";
            _list.MouseDoubleClick += (s, e) => Accept();

            var ok = new Button { Content = "Choose", IsDefault = true, Width = 90, Margin = new Thickness(0, 6, 6, 0) };
            ok.Click += (s, e) => Accept();
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Margin = new Thickness(0, 6, 0, 0) };
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
