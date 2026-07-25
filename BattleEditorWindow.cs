using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lycoris.Formats;
using Lycoris.Npc;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Battle editor for common_enc_0.01 (the shared event/story battle config). Like the wild-encounter editor
    /// it edits each battle's up to 6 yo-kai slots (icon + level), plus the battle's BattleScript name. A
    /// checkable "Make a battle script" generates a blank battle-event .xq (into include/seq/battle/encount)
    /// named after the BattleScript. Wild encounters keep their own per-map editor.
    /// </summary>
    public sealed class BattleEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private EncounterSet _set;
        private string _savePath, _xqDir;

        private readonly ListBox _list = new ListBox();
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };
        private readonly TextBox _script = new TextBox { Width = 240 };
        private readonly CheckBox _makeScript = new CheckBox { Content = "Make a battle script (generate its .xq on save)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

        private readonly CheckBox[] _tog = new CheckBox[6];
        private readonly Image[] _icon = new Image[6];
        private readonly TextBlock[] _name = new TextBlock[6];
        private readonly TextBox[] _level = new TextBox[6];
        private readonly Button[] _change = new Button[6];
        private EncTable _table;
        private bool _suppress;

        public BattleEditorWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            Owner = owner;
            Title = "Lycoris — Battle Editor (common_enc)";
            Width = 760; Height = 580;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(Btn("Save mod", Save, 0));
            DockPanel.SetDock(toolbar, Dock.Top);

            var left = new DockPanel { Width = 250, Margin = new Thickness(6) };
            var listBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            listBtns.Children.Add(Btn("+ Create", CreateBattle, 0));
            listBtns.Children.Add(Btn("Duplicate", DuplicateBattle));
            listBtns.Children.Add(Btn("Delete", DeleteBattle));
            DockPanel.SetDock(listBtns, Dock.Bottom);
            _list.DisplayMemberPath = "Label";
            _list.SelectionChanged += (s, e) => ShowTable(_list.SelectedItem as EncTable);
            left.Children.Add(listBtns);
            left.Children.Add(_list);
            DockPanel.SetDock(left, Dock.Left);

            var right = new StackPanel { Margin = new Thickness(8) };
            var scriptRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            scriptRow.Children.Add(new TextBlock { Text = "BattleScript ", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            _script.LostFocus += (s, e) => ScriptChanged();
            scriptRow.Children.Add(_script);
            scriptRow.Children.Add(_makeScript);
            right.Children.Add(scriptRow);
            right.Children.Add(new TextBlock { Text = "Yo-kai for this battle (6 slots):", Foreground = Theme.FgMuted, Margin = new Thickness(0, 0, 0, 6) });
            for (int i = 0; i < 6; i++) right.Children.Add(BuildSlot(i));

            DockPanel.SetDock(_status, Dock.Bottom);
            var root = new DockPanel();
            root.Children.Add(toolbar);
            root.Children.Add(_status);
            root.Children.Add(left);
            root.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;

            LoadConfig();
        }

        private Button Btn(string text, Action onClick, double leftMargin = 6)
        {
            var b = new Button { Content = text, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(leftMargin, 0, 0, 0) };
            b.Click += (s, e) => onClick();
            return b;
        }

        private string IncBase()
        {
            if (string.IsNullOrEmpty(_db?.ModFolder)) return null;
            string inc = Path.Combine(_db.ModFolder, "include");
            return Directory.Exists(inc) ? inc : _db.ModFolder;
        }

        // Where common_enc lives in the mod (relative to the include base).
        private const string ModRelDir = "data/res/battle";

        // search a base folder for common_enc across the paths it might live at.
        private static string FindIn(string root, string name)
        {
            if (string.IsNullOrEmpty(root)) return null;
            foreach (var cand in new[] {
                Path.Combine(root, "data", "res", "battle", name),
                Path.Combine(root, "res", "battle", name),
                Path.Combine(root, "data", "res", "sys", name),
                Path.Combine(root, "res", "sys", name),
                Path.Combine(root, "data", "res", name),
                Path.Combine(root, name),
            })
                if (File.Exists(cand)) return cand;
            return null;
        }

        private void LoadConfig()
        {
            const string name = "common_enc_0.01.cfg.bin";
            string incBase = IncBase();
            // prefer the mod's own common_enc (include-aware), else the reference.
            string modCfg = FindIn(incBase, name) ?? FindIn(_db?.ModFolder, name);
            string refCfg = FindIn(_db?.ReferenceFolder, name);
            string loadPath = modCfg ?? refCfg;
            if (loadPath == null) { _status.Text = $"Could not find {name} in the mod or reference."; return; }

            _savePath = incBase != null ? Path.Combine(incBase, "data", "res", "battle", name) : loadPath;
            _xqDir = incBase != null ? Path.Combine(incBase, "seq", "battle", "encount") : null;

            try { _set = Encounters.LoadCfg(loadPath, _db); }
            catch (Exception ex) { _status.Text = "Could not read common_enc: " + ex.Message; return; }

            _list.ItemsSource = _set.Tables;
            if (_set.Tables.Count > 0) _list.SelectedIndex = 0;
            _status.Text = $"{_set.Tables.Count} battles, {_set.Charas.Count} yo-kai — loaded from " +
                           (modCfg != null ? "the mod" : "the reference") + $" ({Path.GetFileName(loadPath)})." +
                           (incBase == null ? "  Open a mod to save." : "");
        }

        private FrameworkElement BuildSlot(int i)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };

            _tog[i] = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            _tog[i].Checked += (s, e) => SlotEnabled(i, true);
            _tog[i].Unchecked += (s, e) => SlotEnabled(i, false);
            row.Children.Add(_tog[i]);

            _icon[i] = new Image { Width = 44, Height = 44, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(_icon[i], BitmapScalingMode.NearestNeighbor);
            row.Children.Add(new Border { Width = 46, Height = 46, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), Background = Theme.FieldBg, Child = _icon[i] });

            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Width = 220 };
            _name[i] = new TextBlock { Foreground = Theme.Fg, FontWeight = FontWeights.SemiBold };
            var lvlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            lvlRow.Children.Add(new TextBlock { Text = "Level ", Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center });
            _level[i] = new TextBox { Width = 60 };
            int idx = i;
            _level[i].LostFocus += (s, e) => LevelChanged(idx);
            lvlRow.Children.Add(_level[i]);
            mid.Children.Add(_name[i]);
            mid.Children.Add(lvlRow);
            row.Children.Add(mid);

            _change[i] = new Button { Content = "Change yo-kai…", Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center };
            _change[i].Click += (s, e) => ChangeYokai(idx);
            row.Children.Add(_change[i]);
            return row;
        }

        private void ShowTable(EncTable t)
        {
            _table = t;
            _suppress = true;
            _script.Text = t?.BattleScript ?? "";
            _script.IsEnabled = t != null;
            for (int i = 0; i < 6; i++)
            {
                int off = t?.Offsets[i] ?? -1;
                bool on = t != null && off >= 0 && off < _set.Charas.Count;
                _tog[i].IsChecked = on;
                if (on)
                {
                    var c = _set.Charas[off];
                    _icon[i].Source = LoadIcon(c.IconFile);
                    _name[i].Text = c.YokaiName;
                    _level[i].Text = c.Level?.ToString();
                }
                else { _icon[i].Source = null; _name[i].Text = "(empty)"; _level[i].Text = ""; }
                _level[i].IsEnabled = on;
                _change[i].IsEnabled = on;
            }
            _suppress = false;
        }

        private void ScriptChanged()
        {
            if (_suppress || _table == null) return;
            _table.BattleScript = _script.Text?.Trim() ?? "";
            _list.Items.Refresh();
        }

        private void SlotEnabled(int i, bool on)
        {
            if (_suppress || _table == null) return;
            if (on)
            {
                if (_table.Offsets[i] < 0)
                {
                    int pid = _db.Yokai.Count > 0 ? _db.Yokai[0].ParamHash : 0;
                    Encounters.AddChara(_set, pid, 1, _db);
                    _table.Offsets[i] = _set.Charas.Count - 1;
                }
            }
            else _table.Offsets[i] = -1;
            ShowTable(_table);
            _status.Text = "Modified — remember to \"Save mod\".";
        }

        private void LevelChanged(int i)
        {
            if (_table == null) return;
            int off = _table.Offsets[i];
            if (off < 0 || off >= _set.Charas.Count) return;
            if (int.TryParse(_level[i].Text?.Trim(), out int lvl)) _set.Charas[off].Level = lvl;
        }

        private void ChangeYokai(int i)
        {
            if (_table == null) return;
            int off = _table.Offsets[i];
            if (off < 0 || off >= _set.Charas.Count) return;
            var dlg = new PickYokaiDialog(this, _db) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Picked == null) return;
            var c = _set.Charas[off];
            c.ParamId = dlg.Picked.ParamHash;
            Encounters.Resolve(c, _db);
            ShowTable(_table);
            _status.Text = $"Slot {i + 1} → {dlg.Picked.DisplayName}. Save to apply.";
        }

        private void CreateBattle()
        {
            if (_set == null) return;
            string name = TextPrompt.Ask(this, "Create battle", "Battle name (sets the id via CRC32; also the BattleScript):", "btl_custom0");
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0) { DarkMessage.Show("Enter a name.", "Create battle"); return; }
            int id = EventSet.NameHash(name);
            if (_set.Tables.Any(t => t.EncountId == id)) { DarkMessage.Show("A battle with that id already exists.", "Create battle"); return; }
            var tbl = Encounters.AddTable(_set, id, name);
            _list.Items.Refresh();
            _list.SelectedItem = tbl;
            _status.Text = $"Created {name} (0x{unchecked((uint)id):X8}). Add yo-kai, then Save.";
        }

        private void DuplicateBattle()
        {
            if (_set == null) return;
            var src = _list.SelectedItem as EncTable;
            if (src == null) { DarkMessage.Show("Select a battle to duplicate.", "Duplicate"); return; }
            string name = TextPrompt.Ask(this, "Duplicate battle", "Name for the copy:", (string.IsNullOrEmpty(src.BattleScript) ? "btl_copy" : src.BattleScript) + "0");
            if (name == null) return;
            name = name.Trim();
            if (name.Length == 0) return;
            int id = EventSet.NameHash(name);
            if (_set.Tables.Any(t => t.EncountId == id)) { DarkMessage.Show("A battle with that id already exists.", "Duplicate"); return; }
            var tbl = Encounters.DuplicateTable(_set, src, id, name, _db);
            _list.Items.Refresh();
            _list.SelectedItem = tbl;
            _status.Text = $"Duplicated to {name} (its yo-kai were copied). Save to apply.";
        }

        private void DeleteBattle()
        {
            if (_set == null) return;
            var t = _list.SelectedItem as EncTable;
            if (t == null) { DarkMessage.Show("Select a battle to delete.", "Delete"); return; }
            if (DarkMessage.Show($"Delete battle « {t.Label} »?\n(Its .xq battle script, if any, is not touched.)",
                "Delete battle", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            _table = null;
            Encounters.RemoveTable(_set, t);
            _list.Items.Refresh();
            if (_set.Tables.Count > 0) _list.SelectedIndex = 0; else ShowTable(null);
            _status.Text = "Battle removed. Save to apply.";
        }

        private void Save()
        {
            var focused = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            focused?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            if (_set == null) return;
            if (_savePath == null || _xqDir == null) { DarkMessage.Show("Open a mod folder first.", "Save"); return; }
            try
            {
                string scriptMsg = "";
                if (_makeScript.IsChecked == true && _table != null && !string.IsNullOrWhiteSpace(_table.BattleScript))
                {
                    if (!NpcXq.IsAvailable()) { DarkMessage.Show("xtractquery not found on PATH — required to compile the battle script.", "xtractquery missing", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                    byte[] xq = NpcXq.CompileScript(EventSet.BuildBlankBattleSource(), out _);
                    string xqPath = Path.Combine(_xqDir, _table.BattleScript + ".xq");
                    Directory.CreateDirectory(_xqDir);
                    File.WriteAllBytes(xqPath, xq);
                    scriptMsg = $"\nBattle script: {xqPath}";
                }

                Encounters.SaveCfg(_set, _savePath);
                _status.Text = $"Saved common_enc → {_savePath}" + (scriptMsg.Length > 0 ? " (+ battle script)" : "");
                DarkMessage.Show($"Battles saved:\n{_savePath}{scriptMsg}", "Save", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Save battles", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private static BitmapSource LoadIcon(string path)
        {
            if (path == null || !File.Exists(path)) return null;
            try
            {
                var img = Imgc.Decode(File.ReadAllBytes(path));
                var bmp = new WriteableBitmap(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null);
                bmp.WritePixels(new Int32Rect(0, 0, img.Width, img.Height), img.Bgra, img.Width * 4, 0);
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }
}
