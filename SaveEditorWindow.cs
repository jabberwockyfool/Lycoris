using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lycoris.Formats;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Save editor (Yo-kai Watch 3 game{N}.yw). Opens a decrypted save, lists the owned-yo-kai box, and
    /// adds a yo-kai — including custom ones from the loaded mod — into the first empty box slot. Re-saving
    /// re-derives the section order and checksums so the game accepts the file. Always backs up the original.
    /// </summary>
    public sealed class SaveEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private readonly Dictionary<int, string> _nameByParam = new Dictionary<int, string>();

        private YwSave _save;
        private string _gamePath, _headPath;
        private bool _dirty;

        private readonly ListBox _box = new ListBox();
        private readonly TextBlock _info = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };
        private readonly TextBlock _pickedLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        private readonly TextBox _level = new TextBox { Width = 70, Text = "50" };
        private readonly TextBox _nick = new TextBox { Width = 180 };
        private YokaiInfo _picked;

        private IntPtr Handle => new System.Windows.Interop.WindowInteropHelper(this).Handle;

        public SaveEditorWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            foreach (var y in db.Yokai) if (y.ParamHash != 0 && !_nameByParam.ContainsKey(y.ParamHash)) _nameByParam[y.ParamHash] = y.DisplayName;

            Owner = owner;
            Title = "Lycoris — Save Editor (game.yw)";
            Width = 820; Height = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(ToolButton("Open save…", OpenSave, 0));
            toolbar.Children.Add(ToolButton("Save…", SaveFile));
            DockPanel.SetDock(toolbar, Dock.Top);

            // left: current box
            var left = new DockPanel { Width = 340, Margin = new Thickness(6) };
            var boxHeader = new TextBlock { Text = "Owned yo-kai (box)", Foreground = Theme.FgMuted, Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(boxHeader, Dock.Top);
            _box.DisplayMemberPath = "Label";
            left.Children.Add(boxHeader);
            left.Children.Add(_box);
            DockPanel.SetDock(left, Dock.Left);

            // right: add panel
            var right = new StackPanel { Margin = new Thickness(10) };
            right.Children.Add(_info);
            right.Children.Add(new TextBlock { Text = "Choose the yo-kai to place", FontSize = 14, Margin = new Thickness(0, 4, 0, 8) });

            var pickRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            var pickBtn = new Button { Content = "Choose yo-kai…", Padding = new Thickness(10, 4, 10, 4) };
            pickBtn.Click += (s, e) => PickYokai();
            pickRow.Children.Add(pickBtn);
            pickRow.Children.Add(new TextBlock { Text = "  ", VerticalAlignment = VerticalAlignment.Center });
            _pickedLabel.Text = "(none selected)";
            _pickedLabel.Foreground = Theme.FgMuted;
            pickRow.Children.Add(_pickedLabel);
            right.Children.Add(pickRow);

            right.Children.Add(FieldRow("Level (1–99)", _level));
            right.Children.Add(FieldRow("Nickname (optional)", _nick));

            // Replace = the reliable operation (the slot is already tracked by the game).
            var replaceBtn = new Button { Content = "Replace selected box yo-kai", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            replaceBtn.Click += (s, e) => ReplaceYokai();
            right.Children.Add(replaceBtn);
            right.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 4, 0, 0), Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap, FontSize = 11,
                Text = "Reliable: pick an existing yo-kai in the list on the left, then overwrite it with the chosen one. " +
                       "Best for getting a modded yo-kai in-game — replace one you don't need."
            });

            var addBtn = new Button { Content = "Add to empty slot", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            addBtn.Click += (s, e) => AddYokai();
            right.Children.Add(addBtn);
            right.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 4, 0, 0), Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap, FontSize = 11,
                Text = "Experimental: appends to a free slot. The game may not detect yo-kai added this way (it seems to " +
                       "track an owned count), so prefer Replace."
            });

            right.Children.Add(new TextBlock
            {
                Margin = new Thickness(0, 16, 0, 0),
                Foreground = Theme.FgMuted,
                TextWrapping = TextWrapping.Wrap,
                Text = "Stats start from the existing/template record; the game recalculates them from species + level. " +
                       "A .bak backup of the original save is written on the first save. Test on a copy first."
            });

            DockPanel.SetDock(_status, Dock.Bottom);
            var root = new DockPanel();
            root.Children.Add(toolbar);
            root.Children.Add(_status);
            root.Children.Add(left);
            root.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;

            SetEnabled(false);
            _status.Text = _db.Yokai.Count > 0
                ? "Open a game{N}.yw save (head.yw must be in the same folder)."
                : "Open a mod folder first so custom yo-kai appear in the picker, then open a save.";
        }

        private Button ToolButton(string text, Action onClick, double leftMargin = 6)
        {
            var b = new Button { Content = text, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(leftMargin, 0, 0, 0) };
            b.Click += (s, e) => onClick();
            return b;
        }

        private FrameworkElement FieldRow(string label, FrameworkElement field)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            sp.Children.Add(new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            sp.Children.Add(field);
            return sp;
        }

        private void SetEnabled(bool on)
        {
            _box.IsEnabled = on;
        }

        private void OpenSave()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Yo-kai Watch save|*.yw|All files|*.*", Title = "Open a game{N}.yw save" };
            if (dlg.ShowDialog() != true) return;
            string game = dlg.FileName;
            string dir = Path.GetDirectoryName(game);
            string head = Path.Combine(dir, "head.yw");
            if (!File.Exists(head))
            {
                DarkMessage.Show("head.yw was not found next to this save. It is required to decrypt YW3 saves.\n" +
                    "Select it now.", "head.yw required", MessageBoxButton.OK, MessageBoxImage.Information);
                var hd = new Microsoft.Win32.OpenFileDialog { Filter = "head.yw|head.yw|All files|*.*", Title = "Select head.yw" };
                if (hd.ShowDialog() != true) return;
                head = hd.FileName;
            }
            try
            {
                _save = YwSave.Load(game, head);
                _gamePath = game; _headPath = head; _dirty = false;
                RefreshBox();
                SetEnabled(true);
                _status.Text = $"Loaded {Path.GetFileName(game)} — {_save.OccupiedCount()} yo-kai, {_save.FreeSlots()} free slots.";
            }
            catch (Exception ex)
            {
                _save = null; SetEnabled(false);
                DarkMessage.Show(ex.Message, "Could not open save", MessageBoxButton.OK, MessageBoxImage.Error);
                _status.Text = "Failed to open save: " + ex.Message;
            }
        }

        private void RefreshBox()
        {
            var items = _save.ReadBox().Select(e => new BoxRow(e, _nameByParam)).ToList();
            _box.ItemsSource = items;
            _info.Text = $"Save: {Path.GetFileName(_gamePath ?? "")}   •   {_save.OccupiedCount()} owned   •   {_save.FreeSlots()} free slots (capacity {_save.BoxCapacity})";
        }

        private void PickYokai()
        {
            if (_db == null || _db.Yokai.Count == 0)
            {
                DarkMessage.Show("No yo-kai are loaded. Open a mod folder first.", "Choose yo-kai");
                return;
            }
            var dlg = new PickYokaiDialog(this, _db) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Picked == null) return;
            _picked = dlg.Picked;
            _pickedLabel.Foreground = Theme.Fg;
            _pickedLabel.Text = $"{_picked.DisplayName}  (ParamID 0x{unchecked((uint)_picked.ParamHash):X8})";
        }

        private void AddYokai()
        {
            if (_save == null) { DarkMessage.Show("Open a save first.", "Add yo-kai"); return; }
            if (_picked == null) { DarkMessage.Show("Choose a yo-kai first.", "Add yo-kai"); return; }
            if (!int.TryParse(_level.Text.Trim(), out int lvl)) { DarkMessage.Show("Level must be a number (1–99).", "Add yo-kai"); return; }

            if (!_save.TryAddYokai(_picked.ParamHash, lvl, _nick.Text?.Trim(), out string err))
            {
                DarkMessage.Show(err, "Add yo-kai", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _dirty = true;
            RefreshBox();
            _status.Text = $"Added {_picked.DisplayName} (Lv {Math.Max(1, Math.Min(99, lvl))}). Click « Save… » to write it to the file.";
            _box.SelectedIndex = _box.Items.Count - 1;
        }

        private void ReplaceYokai()
        {
            if (_save == null) { DarkMessage.Show("Open a save first.", "Replace yo-kai"); return; }
            if (_picked == null) { DarkMessage.Show("Choose the replacement yo-kai first.", "Replace yo-kai"); return; }
            var row = _box.SelectedItem as BoxRow;
            if (row == null) { DarkMessage.Show("Select an existing yo-kai in the box list (left) to replace.", "Replace yo-kai"); return; }
            if (!int.TryParse(_level.Text.Trim(), out int lvl)) { DarkMessage.Show("Level must be a number (1–99).", "Replace yo-kai"); return; }

            string oldName = _nameByParam.TryGetValue(row.ParamHash, out var on) ? on : $"0x{unchecked((uint)row.ParamHash):X8}";
            if (DarkMessage.Show($"Replace « {oldName} » (slot {row.Slot}) with « {_picked.DisplayName} » (Lv {lvl})?",
                "Replace yo-kai", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            if (!_save.TryReplaceYokai(row.Slot, _picked.ParamHash, lvl, _nick.Text?.Trim(), out string err))
            {
                DarkMessage.Show(err, "Replace yo-kai", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _dirty = true;
            int keepSlot = row.Slot;
            RefreshBox();
            var again = (_box.ItemsSource as System.Collections.Generic.IEnumerable<BoxRow>)?.FirstOrDefault(r => r.Slot == keepSlot);
            if (again != null) _box.SelectedItem = again;
            _status.Text = $"Replaced slot {keepSlot} with {_picked.DisplayName} (Lv {Math.Max(1, Math.Min(99, lvl))}). Click « Save… ».";
        }

        private void SaveFile()
        {
            if (_save == null) { DarkMessage.Show("Open a save first.", "Save"); return; }
            if (!_dirty)
            {
                if (DarkMessage.Show("No changes were made. Save anyway?", "Save", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            }
            if (DarkMessage.Show($"Write changes back to:\n{_gamePath}\n\nA backup (.bak) of the original is kept. Continue?",
                "Save", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            try
            {
                byte[] outBytes = _save.Encrypt();
                string bak = _gamePath + ".bak";
                if (!File.Exists(bak)) File.Copy(_gamePath, bak);
                File.WriteAllBytes(_gamePath, outBytes);
                _dirty = false;
                _status.Text = $"Saved. Backup: {Path.GetFileName(bak)}";
                DarkMessage.Show($"Save written:\n{_gamePath}\n\nOriginal backed up to:\n{bak}\n\n" +
                    "Copy it back to your console's save (with head.yw) and test in-game.",
                    "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
                _status.Text = "Save failed: " + ex.Message;
            }
        }

        private sealed class BoxRow
        {
            public int Slot { get; }
            public int ParamHash { get; }
            public string Label { get; }
            public BoxRow(YwSave.BoxEntry e, Dictionary<int, string> names)
            {
                Slot = e.Slot;
                ParamHash = e.ParamHash;
                string name = names.TryGetValue(e.ParamHash, out var n) ? n : $"0x{unchecked((uint)e.ParamHash):X8}";
                string nick = string.IsNullOrEmpty(e.Nickname) ? "" : $" « {e.Nickname} »";
                Label = $"Lv {e.Level,2}  {name}{nick}";
            }
        }
    }
}
