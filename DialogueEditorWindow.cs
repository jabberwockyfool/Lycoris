using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Lycoris.Formats;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Dialogue & text editor: browse the events that have dialogue AND the maps' NPC texts (data/res/map),
    /// edit each line's text and its speaker (the name-box / washamap), add or remove lines, pick a speaker from
    /// the yo-kai list, add a name box where missing, and save into the mod. The speaker field accepts a model
    /// name (converted with CRC32) or a hex TalkerBaseID.
    /// </summary>
    public sealed class DialogueEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private Dictionary<int, string> _talkerNames;

        private DialogueFile _dlg;
        private DialogueTarget _target;
        private DialogueLineRow _row;
        private bool _suppress;

        private readonly ListBox _events = new ListBox();
        private readonly TextBox _search = new TextBox();
        private readonly ListBox _lines = new ListBox();
        private readonly TextBox _text = new TextBox { AcceptsReturn = true, Height = 96, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly TextBox _speaker = new TextBox { Width = 200, FontFamily = new FontFamily("Consolas") };
        private readonly TextBlock _speakerHint = new TextBlock { Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _nameBoxBtn = new Button { Content = "＋ Name box", Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(6, 0, 0, 0) };
        private readonly ComboBox _block = new ComboBox { Width = 90, IsEditable = true };
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };

        public DialogueEditorWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            _talkerNames = Dialogue.BuildTalkerMap(db);
            Owner = owner;
            Title = "Lycoris — Dialogue & Text Editor";
            Width = 940; Height = 660;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(Btn("Save mod", Save, 0));
            DockPanel.SetDock(toolbar, Dock.Top);

            // left: event list
            var left = new DockPanel { Width = 240, Margin = new Thickness(6) };
            _search.Margin = new Thickness(0, 0, 0, 4);
            _search.TextChanged += (s, e) => RefreshEvents();
            DockPanel.SetDock(_search, Dock.Top);
            _events.SelectionChanged += (s, e) => LoadTarget(_events.SelectedItem as DialogueTarget);
            left.Children.Add(_search);
            left.Children.Add(_events);
            DockPanel.SetDock(left, Dock.Left);

            // middle: line list
            var mid = new DockPanel { Width = 380, Margin = new Thickness(0, 6, 6, 6) };
            var lineBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            lineBtns.Children.Add(Btn("+ Add line", AddLine, 0));
            lineBtns.Children.Add(Btn("Delete line", DeleteLine));
            lineBtns.Children.Add(Btn("Paste block…", PasteBlock));
            DockPanel.SetDock(lineBtns, Dock.Bottom);
            _lines.DisplayMemberPath = "Preview";
            _lines.SelectionChanged += (s, e) => ShowLine(_lines.SelectedItem as DialogueLineRow);
            mid.Children.Add(lineBtns);
            mid.Children.Add(_lines);
            DockPanel.SetDock(mid, Dock.Left);

            // right: line detail
            var right = new StackPanel { Margin = new Thickness(6) };

            var blkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            blkRow.Children.Add(new TextBlock { Text = "Block ", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            foreach (var s in new[] { "_010", "_011", "_020", "_021", "_030", "_031" }) _block.Items.Add(s);
            _block.LostFocus += (s, e) => BlockChanged();
            _block.SelectionChanged += (s, e) => BlockChanged();
            blkRow.Children.Add(_block);
            blkRow.Children.Add(new TextBlock { Text = "  (event block — key = CRC32(event + suffix); e.g. _010 intro, _011 male, _020 accept, _030 decline)", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap });
            right.Children.Add(blkRow);

            var spRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            spRow.Children.Add(new TextBlock { Text = "Speaker ", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            _speaker.LostFocus += (s, e) => SpeakerChanged();
            _speaker.TextChanged += (s, e) => { if (!_suppress) _speakerHint.Text = ResolveHint(_speaker.Text); };
            spRow.Children.Add(_speaker);
            spRow.Children.Add(Btn("yo-kai…", PickSpeaker));
            spRow.Children.Add(_nameBoxBtn);
            spRow.Children.Add(new TextBlock { Text = "  ", VerticalAlignment = VerticalAlignment.Center });
            spRow.Children.Add(_speakerHint);
            right.Children.Add(spRow);
            _nameBoxBtn.Click += (s, e) => AddNameBox();
            right.Children.Add(new TextBlock { Text = "Text", Foreground = Theme.FgMuted, Margin = new Thickness(0, 0, 0, 2) });
            _text.LostFocus += (s, e) => TextChanged();
            right.Children.Add(_text);
            right.Children.Add(new TextBlock { Foreground = Theme.FgMuted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Text = "Control codes like <PV#voice_y001000_31>, <A01/06>, <CG>…</C> are kept as-is. Speaker: a model name (c001000, y597000 → CRC32) or a hex TalkerBaseID; 0x00000000 = no name box. Shortcut: start the text with « model| » (e.g. y152000|Hello) to set the speaker inline." });
            _detail = right;
            _detail.IsEnabled = false;

            DockPanel.SetDock(_status, Dock.Bottom);
            var root = new DockPanel();
            root.Children.Add(toolbar);
            root.Children.Add(_status);
            root.Children.Add(left);
            root.Children.Add(mid);
            root.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;

            RefreshEvents();
            _status.Text = DialoguePaths.IncBase(_db) == null
                ? "Open a mod to save. Browsing the reference dialogue."
                : "Select an event to edit its dialogue.";
        }

        private readonly StackPanel _detail;

        private Button Btn(string text, Action onClick, double leftMargin = 6)
        {
            var b = new Button { Content = text, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(leftMargin, 0, 0, 0) };
            b.Click += (s, e) => onClick();
            return b;
        }

        private void RefreshEvents()
        {
            var all = DialoguePaths.AllTargets(_db);
            string q = _search.Text?.Trim();
            if (!string.IsNullOrEmpty(q)) all = all.Where(t => t.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            _events.ItemsSource = all;
            if (_dlg == null) _status.Text = $"{all.Count} dialogue sources (events + map NPC texts).";
        }

        private void LoadTarget(DialogueTarget t)
        {
            if (t?.TextPath == null) return;
            _target = t;
            try
            {
                _dlg = Dialogue.Load(t.EventName ?? "", t.TextPath, t.WashaPath, _db);
                _lines.ItemsSource = _dlg.Rows;
                _lines.SelectedIndex = _dlg.Rows.Count > 0 ? 0 : -1;
                _status.Text = $"{t.Label}: {_dlg.Rows.Count} lines" + (_dlg.WashaData == null ? " (no name-box file)" : "") + $" — loaded from {(t.FromMod ? "the mod" : "the reference")}.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Load dialogue", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ShowLine(DialogueLineRow row)
        {
            _row = row;
            _suppress = true;
            _block.Text = row?.KeyLabel ?? "";
            _block.IsEnabled = row != null && _target?.EventName != null;   // suffix blocks are an event concept
            if (row == null) { _text.Text = ""; _speaker.Text = ""; _speakerHint.Text = ""; _detail.IsEnabled = false; }
            else
            {
                _text.Text = row.Text;
                bool hasBox = row.WashaEntry != null;
                _speaker.Text = hasBox ? (row.SpeakerName ?? row.TalkerHex) : "";
                _speakerHint.Text = hasBox ? ResolveHint(_speaker.Text) : "(no name box)";
                _detail.IsEnabled = true;
                _speaker.IsEnabled = hasBox;             // no name box without a washamap entry
                _nameBoxBtn.Visibility = hasBox ? Visibility.Collapsed : Visibility.Visible; // offer to add one
            }
            _suppress = false;
        }

        private void BlockChanged()
        {
            if (_suppress || _row == null || _dlg == null || _target?.EventName == null) return;
            string suffix = (_block.Text ?? "").Trim();
            if (suffix.Length == 0 || suffix == _row.KeyLabel) return;
            Dialogue.SetBlock(_row, _target.EventName, suffix);
            _lines.Items.Refresh();
            _status.Text = $"Block set to {suffix}. Save to apply.";
        }

        private void TextChanged()
        {
            if (_suppress || _row == null) return;

            // "model|Dialogue" shortcut: a leading model-id / 0xHEX before a '|' sets the speaker (creating a
            // name box if the line lacks one) and the rest becomes the text.
            if (TrySplitSpeaker(_text.Text, out string spk, out string txt))
            {
                _row.Text = txt;
                int id = Resolve(spk);
                try
                {
                    if (_row.WashaEntry == null) Dialogue.EnsureWasha(_dlg, _row, WashaTemplate(), id);
                    else { _row.TalkerBaseId = id; }
                    _row.SpeakerName = _talkerNames.TryGetValue(id, out var nm) ? nm : spk;
                }
                catch (Exception ex) { DarkMessage.Show(ex.Message, "Name box", MessageBoxButton.OK, MessageBoxImage.Error); }
                ShowLine(_row);          // reflect the stripped text + new speaker
                _lines.Items.Refresh();
                _status.Text = $"Speaker set to {spk} from the \"model|text\" shortcut.";
                return;
            }
            _row.Text = _text.Text;
        }

        /// <summary>Recognise a leading "model|" / "0xHEX|" speaker prefix (e.g. "y152000|Hello"). Only splits
        /// when the prefix clearly looks like an id, so ordinary text containing '|' is left alone.</summary>
        private static bool TrySplitSpeaker(string raw, out string speaker, out string text)
        {
            speaker = null; text = raw;
            if (string.IsNullOrEmpty(raw)) return false;
            int bar = raw.IndexOf('|');
            if (bar <= 0) return false;
            string pre = raw.Substring(0, bar).Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(pre, @"^(0x[0-9A-Fa-f]{1,8}|[A-Za-z]\d{6}(_\d{1,2})?)$"))
                return false;
            speaker = pre;
            text = raw.Substring(bar + 1);
            return true;
        }

        private void SpeakerChanged()
        {
            if (_suppress || _row == null || _row.WashaEntry == null) return;
            int id = Resolve(_speaker.Text);
            _row.TalkerBaseId = id;
            _row.SpeakerName = _talkerNames.TryGetValue(id, out var nm) ? nm : null;
        }

        /// <summary>Pick a yo-kai from the loaded list and set it as this line's speaker (talker = CRC32(model)).
        /// If the line has no name box yet, one is created first so the speaker takes effect.</summary>
        private void PickSpeaker()
        {
            if (_dlg == null || _row == null) { DarkMessage.Show("Select a line first.", "Pick yo-kai"); return; }
            if (_db == null || _db.Yokai.Count == 0) { DarkMessage.Show("No yo-kai loaded.", "Pick yo-kai"); return; }
            var dlg = new PickYokaiDialog(this, _db) { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Picked == null || string.IsNullOrEmpty(dlg.Picked.ModelName)) return;
            int talker = Resolve(dlg.Picked.ModelName);
            try
            {
                if (_row.WashaEntry == null) Dialogue.EnsureWasha(_dlg, _row, WashaTemplate(), talker);
                else { _row.TalkerBaseId = talker; _row.SpeakerName = dlg.Picked.ModelName; }
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Name box", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            _row.SpeakerName = _talkerNames.TryGetValue(talker, out var nm) ? nm : dlg.Picked.ModelName;
            ShowLine(_row);
            _lines.Items.Refresh();
            _status.Text = $"Speaker set to {dlg.Picked.ModelName}. Save to apply.";
        }

        /// <summary>Add a name box (washamap entry) to the current line — creating the washamap file if the
        /// event has none — so a speaker can be shown.</summary>
        private void AddNameBox()
        {
            if (_dlg == null || _row == null) { DarkMessage.Show("Select a line first.", "Name box"); return; }
            if (_row.WashaEntry != null) return;
            try { Dialogue.EnsureWasha(_dlg, _row, WashaTemplate(), Resolve(_speaker.Text)); }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Name box", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            ShowLine(_row);
            _lines.Items.Refresh();
            _status.Text = _dlg.WashaData != null ? "Name box added — set the speaker and Save." : "Name box added.";
        }

        /// <summary>A vanilla *_map.cfg.bin to clone when the event has no washamap yet (null if the event already
        /// has one — EnsureWasha won't need it).</summary>
        private string WashaTemplate() =>
            _dlg?.WashaData == null ? EventGen.FindTemplate(_db, DialoguePaths.IncBase(_db), null, "_map.cfg.bin") : null;

        private void AddLine()
        {
            if (_dlg == null) { DarkMessage.Show("Select an event first.", "Add line"); return; }
            var sel = _lines.SelectedItem as DialogueLineRow;
            if (sel == null) { DarkMessage.Show("Select an existing line — the new line is added to its block.", "Add line"); return; }
            try
            {
                var row = Dialogue.AddRow(_dlg, sel.KeyId, sel.KeyLabel, "", sel.TalkerBaseId);
                _lines.Items.Refresh();
                _lines.SelectedItem = row;
                _status.Text = $"Added a line to block {sel.KeyLabel}. Edit its text, then Save.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Add line", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        /// <summary>Fill the selected line's block from a pasted "model|text" list — one line per page — exactly
        /// like the daily-fight dialogue fields. Replaces every page of that block.</summary>
        private void PasteBlock()
        {
            if (_dlg == null || _row == null) { DarkMessage.Show("Select a line in the block you want to fill.", "Paste block"); return; }
            int keyId = _row.KeyId; string keyLabel = _row.KeyLabel;

            // Prefill with the block's current lines as "model|text".
            var existing = _dlg.Rows.Where(r => r.KeyId == keyId).OrderBy(r => r.Page).Select(r =>
            {
                string spk = r.SpeakerName ?? (r.TalkerBaseId != 0 ? r.TalkerHex : "");
                return string.IsNullOrEmpty(spk) ? r.Text : spk + "|" + r.Text;
            });
            string input = MultilinePrompt.Ask(this, $"Paste block {keyLabel}",
                "One line per page. Prefix a line with « model| » (e.g. y768000|Hello) to set that page's speaker. " +
                "This REPLACES every page of this block.", string.Join("\n", existing));
            if (input == null) return;

            try
            {
                var lines = EventGen.ParseBlock(input, null);
                var added = Dialogue.ReplaceBlock(_dlg, keyId, keyLabel, lines, WashaTemplate(), _db);
                _lines.ItemsSource = null; _lines.ItemsSource = _dlg.Rows;
                _lines.SelectedItem = added.FirstOrDefault() ?? _dlg.Rows.FirstOrDefault();
                _status.Text = $"Block {keyLabel} filled with {added.Count} line(s). Save to apply.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Paste block", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void DeleteLine()
        {
            if (_dlg == null) return;
            var sel = _lines.SelectedItem as DialogueLineRow;
            if (sel == null) { DarkMessage.Show("Select a line to delete.", "Delete line"); return; }
            if (DarkMessage.Show($"Delete this line?\n\n[{sel.SpeakerLabel}] {sel.Text}", "Delete line", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            _row = null;
            Dialogue.RemoveRow(_dlg, sel);
            _lines.Items.Refresh();
            if (_dlg.Rows.Count > 0) _lines.SelectedIndex = 0; else ShowLine(null);
            _status.Text = "Line removed. Save to apply.";
        }

        private void Save()
        {
            var focused = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            focused?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            if (_dlg == null || _target == null) return;
            if (DialoguePaths.IncBase(_db) == null || _target.ModTextPath == null) { DarkMessage.Show("Open a mod folder first.", "Save"); return; }
            try
            {
                string textOut = _target.ModTextPath;
                string washaOut = _target.ModWashaPath;
                Dialogue.Save(_dlg, textOut, _dlg.WashaData != null ? washaOut : null);
                _status.Text = $"Saved {_target.Label} to the mod.";
                DarkMessage.Show($"Dialogue saved:\n{textOut}" + (_dlg.WashaData != null ? $"\n{washaOut}" : ""), "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Save dialogue", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private string ResolveHint(string s)
        {
            int id = Resolve(s);
            string nm = _talkerNames.TryGetValue(id, out var n) ? " (" + n + ")" : "";
            return $"= 0x{unchecked((uint)id):X8}{nm}";
        }

        private int Resolve(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("0x") || s.StartsWith("0X"))
            {
                if (uint.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint u)) return unchecked((int)u);
            }
            if (s.Length == 0) return 0;
            return unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(s)));
        }
    }

    /// <summary>A modal multiline text prompt (for pasting a whole dialogue block).</summary>
    internal static class MultilinePrompt
    {
        public static string Ask(Window owner, string title, string message, string initial)
        {
            var win = new Window { Owner = owner, Title = title, Width = 580, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var tb = new TextBox { Text = initial ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            string result = null;
            var ok = new Button { Content = "OK", Width = 90, Margin = new Thickness(0, 0, 6, 0) };
            ok.Click += (s, e) => { result = tb.Text; win.DialogResult = true; };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90 };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Theme.FgMuted, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(msg, Dock.Top);
            DockPanel.SetDock(btns, Dock.Bottom);
            var root = new DockPanel { Margin = new Thickness(12) };
            root.Children.Add(msg); root.Children.Add(btns); root.Children.Add(tb);
            win.Content = root;
            tb.Focus();
            return win.ShowDialog() == true ? result : null;
        }
    }
}
