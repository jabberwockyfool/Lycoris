using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
    /// Event editor (event_set_config_0.01). Browse/add/duplicate/delete registered events and edit an event's
    /// config fields. The "Daily Fight maker" button opens a separate generator that builds a complete daily
    /// fight (compiled .xq + config entry + dialogue text + name box). Mod files go under &lt;mod&gt;/data
    /// (res → data/res, event text/name-box → data/txt/ev, scripts → data/seq/event).
    /// </summary>
    public sealed class EventEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private EventSet _es;
        private string _configSavePath;
        private bool _dirty;

        private readonly ListBox _list = new ListBox();
        private readonly TextBox _search = new TextBox();
        private ICollectionView _view;

        private readonly StackPanel _fields = new StackPanel();
        private readonly TextBox _nameBox = new TextBox { Width = 200 };
        private readonly TextBlock _hashLabel = new TextBlock { Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBox[] _fieldBoxes = new TextBox[17];
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };
        private EventEntry _sel;
        private bool _suppress;

        // Mod romfs base = <mod>/include (or the mod folder itself if it is already the include folder).
        // Under it: seq/ (event scripts) and data/ (res, txt) are siblings.
        internal string IncludeBase
        {
            get
            {
                if (string.IsNullOrEmpty(_db?.ModFolder)) return null;
                string inc = Path.Combine(_db.ModFolder, "include");
                return Directory.Exists(inc) ? inc : _db.ModFolder;
            }
        }
        internal EventSet Events => _es;
        internal string ConfigSavePath => _configSavePath;
        internal YokaiDatabase Db => _db;

        public EventEditorWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            Owner = owner;
            Title = "Lycoris — Event Editor";
            Width = 880; Height = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(ToolButton("＋ Blank event maker…", OpenBlankMaker, 0));
            toolbar.Children.Add(ToolButton("⚔ Daily Fight maker…", OpenDailyMaker));
            toolbar.Children.Add(ToolButton("Save config", SaveConfig));
            DockPanel.SetDock(toolbar, Dock.Top);

            // left: list + add/dup/delete
            var left = new DockPanel { Width = 300, Margin = new Thickness(6) };
            var listBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            listBtns.Children.Add(ToolButton("+ Add", AddEvent, 0));
            listBtns.Children.Add(ToolButton("Duplicate", DuplicateEvent));
            listBtns.Children.Add(ToolButton("Delete", DeleteEvent));
            DockPanel.SetDock(listBtns, Dock.Bottom);
            _search.Margin = new Thickness(0, 0, 0, 4);
            _search.TextChanged += (s, e) => _view?.Refresh();
            DockPanel.SetDock(_search, Dock.Top);
            _list.DisplayMemberPath = "Label";
            _list.SelectionChanged += (s, e) => OnSelChanged();
            left.Children.Add(_search);
            left.Children.Add(listBtns);
            left.Children.Add(_list);
            DockPanel.SetDock(left, Dock.Left);

            // right: field editor
            _fields.Margin = new Thickness(12);
            BuildFieldEditor();

            DockPanel.SetDock(_status, Dock.Bottom);
            var root = new DockPanel();
            root.Children.Add(toolbar);
            root.Children.Add(_status);
            root.Children.Add(left);
            root.Children.Add(new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;

            Closing += (s, e) => { CommitFields(); };
            LoadConfig();
        }

        private Button ToolButton(string text, Action onClick, double leftMargin = 6)
        {
            var b = new Button { Content = text, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(leftMargin, 0, 0, 0) };
            b.Click += (s, e) => onClick();
            return b;
        }

        private void BuildFieldEditor()
        {
            _fields.Children.Add(new TextBlock { Text = "Selected event", FontSize = 14, Margin = new Thickness(0, 0, 0, 8) });

            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            nameRow.Children.Add(Label("Name"));
            _nameBox.TextChanged += (s, e) => { if (!_suppress) _hashLabel.Text = _nameBox.Text.Trim().Length == 0 ? "—" : $"0x{unchecked((uint)EventSet.NameHash(_nameBox.Text.Trim())):X8}"; };
            nameRow.Children.Add(_nameBox);
            nameRow.Children.Add(new TextBlock { Text = "  (sets the event id)", Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center });
            _fields.Children.Add(nameRow);

            var hashRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
            hashRow.Children.Add(Label("Event id (field 0)"));
            hashRow.Children.Add(_hashLabel);
            _fields.Children.Add(hashRow);

            _fields.Children.Add(new TextBlock { Text = "Config flags (fields 1–16)", Foreground = Theme.FgMuted, Margin = new Thickness(0, 0, 0, 4) });
            var wrap = new WrapPanel { Width = 460 };
            for (int i = 1; i <= 16; i++)
            {
                var cell = new StackPanel { Margin = new Thickness(0, 0, 12, 8) };
                cell.Children.Add(new TextBlock { Text = "field " + i, Foreground = Theme.FgMuted, FontSize = 11 });
                _fieldBoxes[i] = new TextBox { Width = 90 };
                cell.Children.Add(_fieldBoxes[i]);
                wrap.Children.Add(cell);
            }
            _fields.Children.Add(wrap);
            _fields.Children.Add(new TextBlock { Foreground = Theme.FgMuted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Text = "Edits are kept in memory as you switch events; click « Save config » to write event_set_config to the mod." });

            _fields.IsEnabled = false;
        }

        private static UIElement Label(string t) => new TextBlock { Text = t, Width = 130, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted };

        private void LoadConfig()
        {
            const string cfgName = "event_set_config_0.01.cfg.bin";
            // In the mod the config lives at data/res/sys; in the reference it sits at the cfg root.
            string modCfg = IncludeBase != null ? Path.Combine(IncludeBase, "data", "res", "sys", cfgName) : null;
            string refCfg = FindReferenceConfig(cfgName);
            string loadPath = (modCfg != null && File.Exists(modCfg)) ? modCfg
                : (refCfg != null && File.Exists(refCfg)) ? refCfg : null;
            if (loadPath == null) { _status.Text = $"Could not find {cfgName} in the mod or reference."; return; }
            _configSavePath = modCfg ?? loadPath;

            var eventDirs = new List<string>();
            if (_db?.ReferenceFolder != null) eventDirs.Add(Path.Combine(_db.ReferenceFolder, "seq", "event"));
            if (IncludeBase != null) eventDirs.Add(Path.Combine(IncludeBase, "seq", "event"));

            try { _es = EventSet.Load(loadPath, eventDirs); }
            catch (Exception ex) { _status.Text = "Could not read event_set_config: " + ex.Message; return; }

            _view = CollectionViewSource.GetDefaultView(_es.Events);
            _view.Filter = o =>
            {
                string q = _search.Text?.Trim();
                if (string.IsNullOrEmpty(q)) return true;
                return ((EventEntry)o).Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            };
            _list.ItemsSource = _view;
            int named = _es.Events.Count(e => e.Name != null);
            _status.Text = $"{_es.Events.Count} events ({named} named). " + (IncludeBase == null ? "Open a mod to save." : "");
        }

        private string FindReferenceConfig(string cfgName)
        {
            string root = _db?.ReferenceFolder;
            if (string.IsNullOrEmpty(root)) return null;
            foreach (var cand in new[] { Path.Combine(root, cfgName), Path.Combine(root, "data", "res", "sys", cfgName), Path.Combine(root, "res", "sys", cfgName) })
                if (File.Exists(cand)) return cand;
            return null;
        }

        // ---------------------------------------------------------------- selection / editing

        private void OnSelChanged()
        {
            CommitFields();
            _sel = _list.SelectedItem as EventEntry;
            ShowEvent(_sel);
        }

        private void ShowEvent(EventEntry ev)
        {
            _suppress = true;
            if (ev == null)
            {
                _nameBox.Text = ""; _hashLabel.Text = "—";
                for (int i = 1; i <= 16; i++) _fieldBoxes[i].Text = "";
                _fields.IsEnabled = false;
            }
            else
            {
                _nameBox.Text = ev.Name ?? "";
                _hashLabel.Text = ev.HashHex;
                for (int i = 1; i <= 16; i++) _fieldBoxes[i].Text = _es.GetField(ev, i).ToString();
                _fields.IsEnabled = true;
            }
            _suppress = false;
        }

        private void CommitFields()
        {
            if (_sel == null || _es == null) return;
            for (int i = 1; i <= 16; i++)
                if (int.TryParse(_fieldBoxes[i].Text?.Trim(), out int v) && v != _es.GetField(_sel, i)) { _es.SetField(_sel, i, v); _dirty = true; }
            string nm = _nameBox.Text?.Trim();
            if (!string.IsNullOrEmpty(nm))
            {
                int h = EventSet.NameHash(nm);
                if (h != _sel.Hash || nm != _sel.Name) { _es.SetEventHash(_sel, h, nm); _dirty = true; }
            }
        }

        private void AddEvent()
        {
            if (_es == null) return;
            string name = TextPrompt.Ask(this, "Add event", "New event name (used as the id via CRC32):", "");
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0) { DarkMessage.Show("Enter a name.", "Add event"); return; }
            int hash = EventSet.NameHash(name);
            if (_es.Contains(hash)) { DarkMessage.Show("An event with that id already exists.", "Add event"); return; }
            var ev = _es.AddBlankEvent(hash, name);
            _dirty = true; _view.Refresh(); _list.SelectedItem = ev;
            _status.Text = $"Added {name} (0x{unchecked((uint)hash):X8}).";
        }

        private void DuplicateEvent()
        {
            CommitFields();
            var src = _list.SelectedItem as EventEntry;
            if (src == null) { DarkMessage.Show("Select an event to duplicate.", "Duplicate"); return; }
            string name = TextPrompt.Ask(this, "Duplicate event", "Name for the copy:", (src.Name ?? "event") + "_copy");
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0) return;
            int hash = EventSet.NameHash(name);
            if (_es.Contains(hash)) { DarkMessage.Show("An event with that id already exists.", "Duplicate"); return; }
            var ev = _es.DuplicateEvent(src, hash, name);
            _dirty = true; _view.Refresh(); _list.SelectedItem = ev;
            _status.Text = $"Duplicated to {name}.";
        }

        private void DeleteEvent()
        {
            var ev = _list.SelectedItem as EventEntry;
            if (ev == null) { DarkMessage.Show("Select an event to delete.", "Delete"); return; }
            if (DarkMessage.Show($"Remove event « {ev.Label} » from event_set_config?\n(Its .xq / text files are not touched.)",
                "Delete event", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            _sel = null;
            _es.RemoveEvent(ev);
            _dirty = true; _view.Refresh();
            _status.Text = "Event removed. Click « Save config » to persist.";
        }

        private void SaveConfig()
        {
            if (_es == null) return;
            CommitFields();
            if (IncludeBase == null) { DarkMessage.Show("Open a mod folder first.", "Save config"); return; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configSavePath));
                T2bWriter.WriteFile(_es.File, _configSavePath);
                _dirty = false;
                _status.Text = $"Saved event_set_config → {_configSavePath}";
                DarkMessage.Show($"event_set_config written:\n{_configSavePath}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void OpenDailyMaker()
        {
            if (_es == null) { DarkMessage.Show("event_set_config is not loaded.", "Daily Fight maker"); return; }
            if (IncludeBase == null) { DarkMessage.Show("Open a mod folder first — events are written into the mod.", "Daily Fight maker"); return; }
            new DailyFightWindow(this) { Owner = this }.ShowDialog();
        }

        private void OpenBlankMaker()
        {
            if (_es == null) { DarkMessage.Show("event_set_config is not loaded.", "Blank event maker"); return; }
            if (IncludeBase == null) { DarkMessage.Show("Open a mod folder first — events are written into the mod.", "Blank event maker"); return; }
            new BlankEventWindow(this) { Owner = this }.ShowDialog();
        }

        /// <summary>Called by the Daily Fight maker after it adds an event, to refresh the list.</summary>
        internal void ReloadAfterGenerate(string statusMsg)
        {
            _view?.Refresh();
            if (statusMsg != null) _status.Text = statusMsg;
        }
    }

    /// <summary>Tiny modal text prompt (Lycoris has no input box otherwise).</summary>
    internal static class TextPrompt
    {
        public static string Ask(Window owner, string title, string message, string initial)
        {
            var win = new Window { Owner = owner, Title = title, Width = 380, Height = 160, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
            var tb = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 6, 0, 6) };
            string result = null;
            var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 6, 0) };
            ok.Click += (s, e) => { result = tb.Text; win.DialogResult = true; };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            var root = new StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            root.Children.Add(tb);
            root.Children.Add(btns);
            win.Content = root;
            tb.Focus(); tb.SelectAll();
            return win.ShowDialog() == true ? result : null;
        }
    }

    /// <summary>
    /// Daily Fight generator: builds and compiles the event .xq, registers it in event_set_config, and
    /// (optionally) generates the dialogue text + name-box washamap. All outputs go under &lt;mod&gt;/data.
    /// </summary>
    internal sealed class DailyFightWindow : Window
    {
        private readonly EventEditorWindow _host;
        private readonly YokaiDatabase _db;

        private readonly TextBox _name = new TextBox { Width = 200 };
        private readonly TextBlock _hashLabel = new TextBlock { Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBox _bustups = NewMulti(96);
        private readonly TextBox _battle = new TextBox { Width = 300, Text = "enc_day_" };
        private readonly TextBox _flag = new TextBox { Width = 130 };
        private readonly CheckBox _genDlg = new CheckBox { Content = "Also generate dialogue (text + name box)", IsChecked = true, Margin = new Thickness(0, 10, 0, 4) };
        private readonly TextBox _speaker = new TextBox { Width = 160, Text = "c001000" };
        private readonly TextBox _intro = NewMulti(54);
        private readonly TextBox _accept = NewMulti(54);
        private readonly TextBox _decline = NewMulti(54);
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };

        private static TextBox NewMulti(double h) => new TextBox { Width = 360, Height = h, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        public DailyFightWindow(EventEditorWindow host)
        {
            _host = host; _db = host.Db;
            Title = "Lycoris — Daily Fight maker";
            Width = 620; Height = 680;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var p = new StackPanel { Margin = new Thickness(14) };
            p.Children.Add(new TextBlock { Text = "New « Daily Fight » event", FontSize = 15, Margin = new Thickness(0, 0, 0, 4) });
            p.Children.Add(new TextBlock { Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
                Text = "Generates include/seq/event/<name>.xq, registers it in event_set_config (data/res/sys), and " +
                       "(optionally) the dialogue under data/txt/ev. Then wire an NPC to run this event. Names must end in 0." });

            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            nameRow.Children.Add(Label("Event name"));
            _name.TextChanged += (s, e) => OnNameChanged();
            nameRow.Children.Add(_name);
            nameRow.Children.Add(new TextBlock { Text = "  (must end in 0)", Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(nameRow);
            p.Children.Add(Row("Event id (hash)", _hashLabel));

            var busRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 2) };
            busRow.Children.Add(TopLabel("Bustups (one/line)"));
            _bustups.Text = "c001000\r\nc003000\r\ny001000\r\ny597000\r\n";
            busRow.Children.Add(_bustups);
            p.Children.Add(busRow);
            p.Children.Add(Hint("Character/yo-kai models shown during the event (add the enemy's model, e.g. y159900_01)."));

            p.Children.Add(Row("Battle encounter id", _battle));
            p.Children.Add(Hint("load_battle_ev target, e.g. enc_day_y159900_01."));
            p.Children.Add(Row("Daily flag (auto)", _flag));

            p.Children.Add(_genDlg);
            p.Children.Add(Row("Default speaker (model)", _speaker));
            p.Children.Add(Hint("One page/line. Start a line with « model| » to change the speaker for that line."));
            _intro.Text = "A challenger has appeared!";
            _accept.Text = "Then let's battle!";
            _decline.Text = "Come back anytime.";
            p.Children.Add(DlgRow("Intro (_010)", _intro));
            p.Children.Add(DlgRow("Accept (_020)", _accept));
            p.Children.Add(DlgRow("Decline (_030)", _decline));

            var gen = new Button { Content = "Generate event", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            gen.Click += (s, e) => Generate();
            p.Children.Add(gen);

            DockPanel.SetDock(_status, Dock.Bottom);
            var root = new DockPanel();
            root.Children.Add(_status);
            root.Children.Add(new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
            OnNameChanged();
        }

        private static UIElement Label(string t) => new TextBlock { Text = t, Width = 150, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted };
        private static UIElement TopLabel(string t) => new TextBlock { Text = t, Width = 150, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0), Foreground = Theme.FgMuted };
        private static FrameworkElement Row(string label, FrameworkElement field) { var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) }; sp.Children.Add(Label(label)); sp.Children.Add(field); return sp; }
        private static FrameworkElement DlgRow(string label, FrameworkElement field) { var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) }; sp.Children.Add(TopLabel(label)); sp.Children.Add(field); return sp; }
        private static UIElement Hint(string t) => new TextBlock { Foreground = Theme.FgMuted, FontSize = 11, Margin = new Thickness(150, 0, 0, 6), TextWrapping = TextWrapping.Wrap, Text = t };

        private void OnNameChanged()
        {
            string n = _name.Text?.Trim() ?? "";
            if (n.Length == 0) { _hashLabel.Text = "—"; return; }
            uint h = unchecked((uint)EventSet.NameHash(n));
            _hashLabel.Text = $"0x{h:X8}" + (_host.Events.Contains(unchecked((int)h)) ? "   ⚠ already registered" : "");
            if (string.IsNullOrWhiteSpace(_flag.Text) || (_flag.Tag as string) == _flag.Text) { _flag.Text = $"0x{h:X8}"; _flag.Tag = _flag.Text; }
        }

        private void Generate()
        {
            var es = _host.Events;
            string incBase = _host.IncludeBase;
            string name = _name.Text?.Trim() ?? "";
            if (name.Length == 0) { DarkMessage.Show("Enter an event name.", "Generate"); return; }
            if (!name.EndsWith("0")) { DarkMessage.Show("Custom event names must end in 0 (a non-0 suffix is treated as a sub-event).", "Invalid name", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (name.Any(c => !(char.IsLetterOrDigit(c) || c == '_'))) { DarkMessage.Show("Use only letters, digits and underscores.", "Invalid name", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            int hash = EventSet.NameHash(name);
            if (!NpcXq.IsAvailable()) { DarkMessage.Show("xtractquery was not found on PATH — required to compile the event script.", "xtractquery missing", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            uint flag = EventGen.ParseHex(_flag.Text, unchecked((uint)hash));
            var bustups = _bustups.Text.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            string battle = _battle.Text?.Trim() ?? "";
            if (bustups.Count == 0) { DarkMessage.Show("Add at least one bustup model.", "Generate"); return; }
            if (battle.Length == 0) { DarkMessage.Show("Enter the battle encounter id.", "Generate"); return; }

            try
            {
                string source = EventSet.BuildDailyFightSource(name, bustups, battle, flag);
                byte[] xq = NpcXq.CompileScript(source, out _);
                string xqPath = Path.Combine(incBase, "seq", "event", name + ".xq");
                Directory.CreateDirectory(Path.GetDirectoryName(xqPath));
                File.WriteAllBytes(xqPath, xq);

                bool addedConfig = false;
                if (!es.Contains(hash)) { es.AddEvent(hash, name); addedConfig = true; }
                string cfgPath = _host.ConfigSavePath;
                Directory.CreateDirectory(Path.GetDirectoryName(cfgPath));
                T2bWriter.WriteFile(es.File, cfgPath);

                string dlgMsg = "";
                if (_genDlg.IsChecked == true)
                {
                    var intro = EventGen.ParseBlock(_intro.Text, _speaker.Text?.Trim());
                    var accept = EventGen.ParseBlock(_accept.Text, _speaker.Text?.Trim());
                    var decline = EventGen.ParseBlock(_decline.Text, _speaker.Text?.Trim());
                    if (intro.Count + accept.Count + decline.Count > 0)
                    {
                        string txtTpl = EventGen.FindTemplate(_db, incBase, "en", "_en.cfg.bin");
                        string wmTpl = EventGen.FindTemplate(_db, incBase, null, "_map.cfg.bin");
                        if (txtTpl == null || wmTpl == null) throw new InvalidOperationException("Could not find a vanilla *_en.cfg.bin / *_map.cfg.bin under ev/ to use as a template.");
                        var tf = T2bReader.ReadFile(txtTpl);
                        var wf = T2bReader.ReadFile(wmTpl);
                        EventDialogue.Build(tf, wf, name, intro, accept, decline);
                        string outTxt = Path.Combine(incBase, "data", "txt", "ev", "en", name + "_en.cfg.bin");
                        string outWm = Path.Combine(incBase, "data", "txt", "ev", name + "_map.cfg.bin");
                        Directory.CreateDirectory(Path.GetDirectoryName(outTxt));
                        Directory.CreateDirectory(Path.GetDirectoryName(outWm));
                        T2bWriter.WriteFile(tf, outTxt);
                        T2bWriter.WriteFile(wf, outWm);
                        dlgMsg = $"\nText:    {outTxt}\nName box: {outWm}";
                    }
                }

                _host.ReloadAfterGenerate($"Generated {name} (xq + config{(dlgMsg.Length > 0 ? " + dialogue" : "")}).");
                _status.Text = $"Generated {name}.";
                DarkMessage.Show(
                    $"Event « {name} » generated.\n\nScript:  {xqPath}\nConfig:  {cfgPath}{(addedConfig ? "  (entry added)" : "  (already registered)")}" +
                    dlgMsg + $"\n\nEvent id: 0x{unchecked((uint)hash):X8}\nDaily flag: 0x{flag:X8}\n\n" +
                    $"To make an NPC run it, set the NPC's OnTalk to:\n    {EventSet.NpcRunSnippet(name)}",
                    "Event generated", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Generation failed", MessageBoxButton.OK, MessageBoxImage.Error);
                _status.Text = "Generation failed: " + ex.Message;
            }
        }

    }

    /// <summary>Shared helpers for the event generators.</summary>
    internal static class EventGen
    {
        public static List<DialogueLine> ParseBlock(string text, string defSpeaker)
        {
            var res = new List<DialogueLine>();
            foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                if (raw.Trim().Length == 0) continue;
                string spk = defSpeaker, txt = raw;
                int bar = raw.IndexOf('|');
                if (bar > 0) { spk = raw.Substring(0, bar).Trim(); txt = raw.Substring(bar + 1); }
                res.Add(new DialogueLine { Speaker = spk, Text = txt });
            }
            return res;
        }

        public static string FindTemplate(YokaiDatabase db, string includeBase, string subDir, string suffix)
        {
            foreach (var root in new[] { db?.ReferenceFolder, includeBase })
            {
                if (string.IsNullOrEmpty(root)) continue;
                // reference keeps text at cfg/ev; the mod keeps it at data/txt/ev.
                foreach (var evRoot in new[] { Path.Combine(root, "ev"), Path.Combine(root, "data", "txt", "ev"), Path.Combine(root, "txt", "ev") })
                {
                    string dir = subDir != null ? Path.Combine(evRoot, subDir) : evRoot;
                    if (!Directory.Exists(dir)) continue;
                    string pref = Path.Combine(dir, "ev75_5100" + suffix);
                    if (File.Exists(pref)) return pref;
                    var any = Directory.EnumerateFiles(dir, "*" + suffix).FirstOrDefault();
                    if (any != null) return any;
                }
            }
            return null;
        }

        public static uint ParseHex(string s, uint fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            s = s.Trim(); if (s.StartsWith("0x") || s.StartsWith("0X")) s = s.Substring(2);
            return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint u) ? u : fallback;
        }

        /// <summary>Write a dialogue text file + washamap for a set of blocks under the mod's data/txt/ev.</summary>
        public static string WriteDialogue(YokaiDatabase db, string includeBase, string name,
            List<DialogueLine> intro, List<DialogueLine> accept, List<DialogueLine> decline)
        {
            if (intro.Count + accept.Count + decline.Count == 0) return "";
            string txtTpl = FindTemplate(db, includeBase, "en", "_en.cfg.bin");
            string wmTpl = FindTemplate(db, includeBase, null, "_map.cfg.bin");
            if (txtTpl == null || wmTpl == null) throw new InvalidOperationException("Could not find a vanilla *_en.cfg.bin / *_map.cfg.bin under ev/ to use as a template.");
            var tf = T2bReader.ReadFile(txtTpl);
            var wf = T2bReader.ReadFile(wmTpl);
            EventDialogue.Build(tf, wf, name, intro, accept, decline);
            string outTxt = Path.Combine(includeBase, "data", "txt", "ev", "en", name + "_en.cfg.bin");
            string outWm = Path.Combine(includeBase, "data", "txt", "ev", name + "_map.cfg.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(outTxt));
            Directory.CreateDirectory(Path.GetDirectoryName(outWm));
            T2bWriter.WriteFile(tf, outTxt);
            T2bWriter.WriteFile(wf, outWm);
            return $"\nText:    {outTxt}\nName box: {outWm}";
        }
    }

    /// <summary>
    /// Blank event generator: a simple single-block talk event (evName_010) with a camera zoom, no battle.
    /// Uses the user's blank template. Writes the .xq, registers the event, and (optionally) the dialogue.
    /// </summary>
    internal sealed class BlankEventWindow : Window
    {
        private readonly EventEditorWindow _host;
        private readonly YokaiDatabase _db;

        private readonly TextBox _name = new TextBox { Width = 200 };
        private readonly TextBlock _hashLabel = new TextBlock { Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _genDlg = new CheckBox { Content = "Also generate dialogue (text + name box)", IsChecked = true, Margin = new Thickness(0, 10, 0, 4) };
        private readonly CheckBox _register = new CheckBox { Content = "Register in event_set_config", IsChecked = true, Margin = new Thickness(0, 2, 0, 4) };
        private readonly TextBox _speaker = new TextBox { Width = 160, Text = "c001000" };
        private readonly TextBox _dialogue = new TextBox { Width = 360, Height = 120, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };

        public BlankEventWindow(EventEditorWindow host)
        {
            _host = host; _db = host.Db;
            Title = "Lycoris — Blank event maker";
            Width = 600; Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var p = new StackPanel { Margin = new Thickness(14) };
            p.Children.Add(new TextBlock { Text = "New blank event", FontSize = 15, Margin = new Thickness(0, 0, 0, 4) });
            p.Children.Add(new TextBlock { Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
                Text = "A simple talk event: a camera zoom and one dialogue block (<name>_010). Writes include/seq/event/<name>.xq " +
                       "and the dialogue under data/txt/ev. Names must end in 0." });

            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            nameRow.Children.Add(Label("Event name"));
            _name.TextChanged += (s, e) => OnNameChanged();
            nameRow.Children.Add(_name);
            nameRow.Children.Add(new TextBlock { Text = "  (must end in 0)", Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(nameRow);
            p.Children.Add(Row("Event id (hash)", _hashLabel));

            p.Children.Add(_register);
            p.Children.Add(_genDlg);
            p.Children.Add(Row("Default speaker (model)", _speaker));
            p.Children.Add(new TextBlock { Foreground = Theme.FgMuted, FontSize = 11, Margin = new Thickness(150, 0, 0, 6), TextWrapping = TextWrapping.Wrap, Text = "One page/line. Start a line with « model| » to change the speaker for that line." });
            var dRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            dRow.Children.Add(new TextBlock { Text = "Dialogue (_010)", Width = 150, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0), Foreground = Theme.FgMuted });
            _dialogue.Text = "Hello there!";
            dRow.Children.Add(_dialogue);
            p.Children.Add(dRow);

            var gen = new Button { Content = "Generate event", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            gen.Click += (s, e) => Generate();
            p.Children.Add(gen);

            DockPanel.SetDock(_status, Dock.Bottom);
            var root = new DockPanel();
            root.Children.Add(_status);
            root.Children.Add(new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;
            OnNameChanged();
        }

        private static UIElement Label(string t) => new TextBlock { Text = t, Width = 150, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted };
        private static FrameworkElement Row(string label, FrameworkElement field) { var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) }; sp.Children.Add(Label(label)); sp.Children.Add(field); return sp; }

        private void OnNameChanged()
        {
            string n = _name.Text?.Trim() ?? "";
            if (n.Length == 0) { _hashLabel.Text = "—"; return; }
            uint h = unchecked((uint)EventSet.NameHash(n));
            _hashLabel.Text = $"0x{h:X8}" + (_host.Events.Contains(unchecked((int)h)) ? "   ⚠ already registered" : "");
        }

        private void Generate()
        {
            var es = _host.Events;
            string incBase = _host.IncludeBase;
            string name = _name.Text?.Trim() ?? "";
            if (name.Length == 0) { DarkMessage.Show("Enter an event name.", "Generate"); return; }
            if (!name.EndsWith("0")) { DarkMessage.Show("Custom event names must end in 0 (a non-0 suffix is treated as a sub-event).", "Invalid name", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (name.Any(c => !(char.IsLetterOrDigit(c) || c == '_'))) { DarkMessage.Show("Use only letters, digits and underscores.", "Invalid name", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (!NpcXq.IsAvailable()) { DarkMessage.Show("xtractquery was not found on PATH — required to compile the event script.", "xtractquery missing", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            int hash = EventSet.NameHash(name);

            try
            {
                byte[] xq = NpcXq.CompileScript(EventSet.BuildBlankEventSource(name), out _);
                string xqPath = Path.Combine(incBase, "seq", "event", name + ".xq");
                Directory.CreateDirectory(Path.GetDirectoryName(xqPath));
                File.WriteAllBytes(xqPath, xq);

                string cfgLine = "";
                if (_register.IsChecked == true && !es.Contains(hash))
                {
                    es.AddBlankEvent(hash, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(_host.ConfigSavePath));
                    T2bWriter.WriteFile(es.File, _host.ConfigSavePath);
                    cfgLine = $"\nConfig:  {_host.ConfigSavePath}  (entry added)";
                }

                string dlgMsg = "";
                if (_genDlg.IsChecked == true)
                {
                    var lines = EventGen.ParseBlock(_dialogue.Text, _speaker.Text?.Trim());
                    dlgMsg = EventGen.WriteDialogue(_db, incBase, name, lines, new List<DialogueLine>(), new List<DialogueLine>());
                }

                _host.ReloadAfterGenerate($"Generated blank event {name}.");
                _status.Text = $"Generated {name}.";
                DarkMessage.Show(
                    $"Blank event « {name} » generated.\n\nScript:  {xqPath}{cfgLine}{dlgMsg}\n\nEvent id: 0x{unchecked((uint)hash):X8}\n\n" +
                    $"To make an NPC run it, set the NPC's OnTalk to:\n    {EventSet.NpcRunSnippet(name)}",
                    "Event generated", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Generation failed", MessageBoxButton.OK, MessageBoxImage.Error);
                _status.Text = "Generation failed: " + ex.Message;
            }
        }
    }
}
