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
    /// Dialogue & text editor: browse the events that have dialogue, edit each line's text and its speaker
    /// (the name-box / washamap), add or remove lines, and save into the mod (data/txt/ev). The speaker field
    /// accepts a model name (converted with CRC32) or a hex TalkerBaseID.
    /// </summary>
    public sealed class DialogueEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private Dictionary<int, string> _talkerNames;

        private DialogueFile _dlg;
        private DialogueLineRow _row;
        private bool _suppress;

        private readonly ListBox _events = new ListBox();
        private readonly TextBox _search = new TextBox();
        private readonly ListBox _lines = new ListBox();
        private readonly TextBox _text = new TextBox { AcceptsReturn = true, Height = 96, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly TextBox _speaker = new TextBox { Width = 200, FontFamily = new FontFamily("Consolas") };
        private readonly TextBlock _speakerHint = new TextBlock { Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center };
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
            _events.SelectionChanged += (s, e) => LoadEvent(_events.SelectedItem as string);
            left.Children.Add(_search);
            left.Children.Add(_events);
            DockPanel.SetDock(left, Dock.Left);

            // middle: line list
            var mid = new DockPanel { Width = 380, Margin = new Thickness(0, 6, 6, 6) };
            var lineBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            lineBtns.Children.Add(Btn("+ Add line", AddLine, 0));
            lineBtns.Children.Add(Btn("Delete line", DeleteLine));
            DockPanel.SetDock(lineBtns, Dock.Bottom);
            _lines.DisplayMemberPath = "Preview";
            _lines.SelectionChanged += (s, e) => ShowLine(_lines.SelectedItem as DialogueLineRow);
            mid.Children.Add(lineBtns);
            mid.Children.Add(_lines);
            DockPanel.SetDock(mid, Dock.Left);

            // right: line detail
            var right = new StackPanel { Margin = new Thickness(6) };
            var spRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            spRow.Children.Add(new TextBlock { Text = "Speaker ", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            _speaker.LostFocus += (s, e) => SpeakerChanged();
            _speaker.TextChanged += (s, e) => { if (!_suppress) _speakerHint.Text = ResolveHint(_speaker.Text); };
            spRow.Children.Add(_speaker);
            spRow.Children.Add(new TextBlock { Text = "  ", VerticalAlignment = VerticalAlignment.Center });
            spRow.Children.Add(_speakerHint);
            right.Children.Add(spRow);
            right.Children.Add(new TextBlock { Text = "Text", Foreground = Theme.FgMuted, Margin = new Thickness(0, 0, 0, 2) });
            _text.LostFocus += (s, e) => TextChanged();
            right.Children.Add(_text);
            right.Children.Add(new TextBlock { Foreground = Theme.FgMuted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Text = "Control codes like <PV#voice_y001000_31>, <A01/06>, <CG>…</C> are kept as-is. Speaker: a model name (c001000, y597000 → CRC32) or a hex TalkerBaseID; 0x00000000 = no name box." });
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
            var all = DialoguePaths.EventNames(_db);
            string q = _search.Text?.Trim();
            if (!string.IsNullOrEmpty(q)) all = all.Where(n => n.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            _events.ItemsSource = all;
            if (_dlg == null) _status.Text = $"{all.Count} events with dialogue.";
        }

        private void LoadEvent(string ev)
        {
            if (ev == null) return;
            string textPath = DialoguePaths.FindText(_db, ev, out bool fromMod);
            if (textPath == null) { _status.Text = $"No dialogue file for {ev}."; return; }
            string washaPath = DialoguePaths.FindWasha(_db, ev);
            try
            {
                _dlg = Dialogue.Load(ev, textPath, washaPath, _db);
                _lines.ItemsSource = _dlg.Rows;
                _lines.SelectedIndex = _dlg.Rows.Count > 0 ? 0 : -1;
                _status.Text = $"{ev}: {_dlg.Rows.Count} lines" + (_dlg.WashaData == null ? " (no name-box file)" : "") + $" — loaded from {(fromMod ? "the mod" : "the reference")}.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Load dialogue", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ShowLine(DialogueLineRow row)
        {
            _row = row;
            _suppress = true;
            if (row == null) { _text.Text = ""; _speaker.Text = ""; _speakerHint.Text = ""; _detail.IsEnabled = false; }
            else
            {
                _text.Text = row.Text;
                _speaker.Text = row.SpeakerName ?? row.TalkerHex;
                _speakerHint.Text = ResolveHint(_speaker.Text);
                _detail.IsEnabled = true;
                _speaker.IsEnabled = row.WashaEntry != null;   // no name box without a washamap entry
            }
            _suppress = false;
        }

        private void TextChanged()
        {
            if (_suppress || _row == null) return;
            _row.Text = _text.Text;
        }

        private void SpeakerChanged()
        {
            if (_suppress || _row == null || _row.WashaEntry == null) return;
            int id = Resolve(_speaker.Text);
            _row.TalkerBaseId = id;
            _row.SpeakerName = _talkerNames.TryGetValue(id, out var nm) ? nm : null;
        }

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
            if (_dlg == null) return;
            if (DialoguePaths.IncBase(_db) == null) { DarkMessage.Show("Open a mod folder first.", "Save"); return; }
            try
            {
                string textOut = DialoguePaths.ModTextPath(_db, _dlg.EventName);
                string washaOut = DialoguePaths.ModWashaPath(_db, _dlg.EventName);
                Dialogue.Save(_dlg, textOut, _dlg.WashaData != null ? washaOut : null);
                _status.Text = $"Saved {_dlg.EventName} dialogue to the mod.";
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
}
