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
        private TranslationJob _job;      // non-null → "translate this cfg.bin" mode (from the Translation Helper)
        private DockPanel _leftPanel;     // event browser; hidden in translation mode

        private readonly ListBox _events = new ListBox();
        private readonly TextBox _search = new TextBox();
        private readonly ListBox _lines = new ListBox();
        private readonly TextBox _text = new TextBox { AcceptsReturn = true, Height = 96, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly TextBox _speaker = new TextBox { Width = 200, FontFamily = new FontFamily("Consolas") };
        private readonly TextBlock _speakerHint = new TextBlock { Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center };
        private readonly Button _nameBoxBtn = new Button { Content = "＋ Name box", Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(6, 0, 0, 0) };
        private readonly ComboBox _block = new ComboBox { Width = 90, IsEditable = true };
        // voice + bustup pickers (preview a voice, insert a <PV#voice_..> / <A01/NN> tag for the current speaker)
        private readonly ComboBox _voiceCombo = new ComboBox { Width = 64 };
        private readonly ComboBox _exprCombo = new ComboBox { Width = 64 };
        private string _speakerModel;
        // --- live text-bubble preview (control codes interpreted, à la Kuriimu) ---
        private readonly Border _nameBox = new Border { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2), CornerRadius = new CornerRadius(8), Margin = new Thickness(6, 10, 0, -8), Visibility = Visibility.Collapsed };
        private readonly TextBlock _nameText = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 13 };
        private readonly Border _bubble = new Border { CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(3), Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(0, 0, 0, 0) };
        private readonly TextBlock _bubbleText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 16, LineHeight = 22 };
        private readonly Image _bubbleImg = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
        private readonly Image _bustupImg = new Image { Stretch = Stretch.Uniform, MaxHeight = 180, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
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
            var left = _leftPanel = new DockPanel { Width = 240, Margin = new Thickness(6) };
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
            blkRow.Children.Add(new TextBlock { Text = "(E.g. _010 intro, _011 male, _020 accept, _030 decline)", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap });
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
            _text.TextChanged += (s, e) => { if (!_suppress) UpdatePreview(); };   // live bubble + bustup (expression <A..>)
            right.Children.Add(_text);

            // Voice + bustup pickers: preview the speaker's voice, insert its <PV#voice_..> / <A01/NN> tag.
            var mediaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            mediaRow.Children.Add(new TextBlock { Text = "Voice ", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            mediaRow.Children.Add(_voiceCombo);
            mediaRow.Children.Add(Btn("▶ Play", PlayVoice));
            mediaRow.Children.Add(Btn("Insert", InsertVoice));
            mediaRow.Children.Add(new TextBlock { Text = "    Bustup ", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            mediaRow.Children.Add(_exprCombo);
            mediaRow.Children.Add(Btn("Insert", InsertExpr));
            right.Children.Add(mediaRow);
            right.Children.Add(new TextBlock { Foreground = Theme.FgMuted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Text = "Voice/bustup lists follow the speaker (name box). Play previews the voice; Insert adds the tag at the caret." });

            // Live text-bubble preview (control codes interpreted, à la Kuriimu).
            var bubbleBorder = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6));
            _bubble.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF1, 0xFB));
            _bubble.BorderBrush = bubbleBorder;
            _bubbleText.Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x20, 0x3A));
            _bubble.Child = _bubbleText;
            _nameBox.Background = bubbleBorder;
            _nameBox.Child = _nameText;
            right.Children.Add(new TextBlock { Text = "Preview", Foreground = Theme.FgMuted, Margin = new Thickness(0, 12, 0, 2) });
            right.Children.Add(_bustupImg);   // speaker portrait (bustup), when available
            right.Children.Add(_nameBox);
            // Faithful bubble (game font + dialog box) when the embedded resources loaded; else the styled fallback.
            if (DialogueBubble.Available)
            {
                _bubble.Visibility = Visibility.Collapsed;
                right.Children.Add(new ScrollViewer { Content = _bubbleImg, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
            }
            else right.Children.Add(_bubble);

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

        /// <summary>Translation mode: opened from the Translation Helper for a single cfg.bin, showing ONLY the
        /// added/changed strings as editable lines (with the live bubble preview). Save writes the shaped text
        /// into the translation-language file. No event browsing, no speaker/name-box.</summary>
        public DialogueEditorWindow(Window owner, TranslationJob job) : this(owner, (YokaiDatabase)null)
        {
            _job = job;
            Title = "Lycoris — Translate  " + job.Label + "   (" + job.TransLabel + ")";
            _leftPanel.Visibility = Visibility.Collapsed;      // no event browser
            _nameBoxBtn.Visibility = Visibility.Collapsed;     // no speakers in generic text

            var rows = new List<DialogueLineRow>();
            foreach (var it in job.Items)
                rows.Add(new DialogueLineRow { TransValue = it.Value, Original = it.Original, Text = it.Original, KeyLabel = "" });
            _lines.DisplayMemberPath = "TransPreview";
            _lines.ItemsSource = rows;
            _lines.SelectedIndex = rows.Count > 0 ? 0 : -1;
            _status.Text = rows.Count + " added line(s) to translate — edit the text, then Save mod → " + job.TransPath;
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
            RefreshMedia();
            UpdatePreview();
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
            if (_job == null && TrySplitSpeaker(_text.Text, out string spk, out string txt))
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
            RefreshMedia();
            UpdatePreview();
        }

        /// <summary>Refresh the text-bubble preview (game font + dialog box, or the styled fallback) + name box.</summary>
        private void UpdatePreview()
        {
            if (DialogueBubble.Available) _bubbleImg.Source = DialogueBubble.Render(_text.Text);
            else _bubbleText.Text = RenderText(_text.Text);
            bool named = _row != null && _row.WashaEntry != null && _row.TalkerBaseId != 0;
            if (named) { _nameText.Text = _row.SpeakerName ?? _row.TalkerHex; _nameBox.Visibility = Visibility.Visible; }
            else _nameBox.Visibility = Visibility.Collapsed;

            var portrait = named ? Bustup.Get(_db, _row.TalkerBaseId, _text.Text) : null;
            _bustupImg.Source = portrait;
            _bustupImg.Visibility = portrait != null ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Repopulate the voice + expression lists for the current speaker's model (called on line/speaker
        /// change, not per keystroke).</summary>
        private void RefreshMedia()
        {
            _speakerModel = _row != null && _row.TalkerBaseId != 0 ? Bustup.ModelFor(_db, _row.TalkerBaseId) : null;
            _voiceCombo.ItemsSource = _speakerModel != null ? VoiceAudio.List(_db, _speakerModel) : null;
            _exprCombo.ItemsSource = _row != null && _row.TalkerBaseId != 0 ? Bustup.Expressions(_db, _row.TalkerBaseId) : null;
        }

        private void PlayVoice()
        {
            if (_speakerModel == null || _voiceCombo.SelectedItem == null) { DarkMessage.Show("Pick a speaker (name box) and a voice number first.", "Voice"); return; }
            try { VoiceAudio.Play(_db, _speakerModel, (string)_voiceCombo.SelectedItem); }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Voice", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void InsertVoice()
        {
            if (_speakerModel == null || _voiceCombo.SelectedItem == null) { DarkMessage.Show("Pick a speaker (name box) and a voice number first.", "Voice"); return; }
            InsertAtCaret("<PV#voice_" + _speakerModel + "_" + _voiceCombo.SelectedItem + ">");
        }

        private void InsertExpr()
        {
            if (_exprCombo.SelectedItem == null) { DarkMessage.Show("Pick a speaker with a bustup and an expression first.", "Bustup"); return; }
            InsertAtCaret("<A01/" + _exprCombo.SelectedItem + ">");
        }

        /// <summary>Insert a tag at the caret (or end) of the text box and commit it to the line.</summary>
        private void InsertAtCaret(string tag)
        {
            if (_row == null) return;
            int i = Math.Min(_text.CaretIndex, _text.Text.Length);
            _text.Text = _text.Text.Insert(i, tag);
            _text.CaretIndex = i + tag.Length;
            _row.Text = _text.Text;
            _text.Focus();
        }

        /// <summary>Interpret the game's text control codes into a readable preview (à la Kuriimu): strip tag
        /// codes (&lt;PV#…&gt;, &lt;A../..&gt;, colour tags), keep furigana base text, expand \n and &lt;PNAME&gt;.</summary>
        private static string RenderText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Replace("<PNAME>", "Nate");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^/\]]*)/[^\]]*\]", "$1"); // furigana [base/ruby] → base
            s = System.Text.RegularExpressions.Regex.Replace(s, @"<[^>]*>", "");                 // strip control tags
            return s.Replace("\\n", "\n").Replace("\r", "");
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
            if (_job != null) { SaveTranslation(); return; }
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

        /// <summary>Translation mode: write each (shaped) line into its live string value, then save the whole
        /// cfg.bin to the translation-language path.</summary>
        private void SaveTranslation()
        {
            try
            {
                foreach (var r in (_lines.ItemsSource as IEnumerable<DialogueLineRow>) ?? Enumerable.Empty<DialogueLineRow>())
                {
                    if (r.TransValue == null) continue;
                    string t = r.Text ?? "";
                    if (_job.ShapeArabic) t = ShapeMultiline(t);
                    r.TransValue.Type = Lycoris.Formats.ValueType.String;
                    r.TransValue.Value = t;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(_job.TransPath));
                T2bWriter.WriteFile(_job.File, _job.TransPath);
                _lines.Items.Refresh();
                _status.Text = "Saved → " + _job.TransPath;
                DarkMessage.Show("Saved:\n" + _job.TransPath, "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Save translation", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        /// <summary>Shape Arabic line-by-line, preserving the literal "\n" line-break tokens the game uses.</summary>
        private static string ShapeMultiline(string s)
        {
            if (!ArabicShaper.NeedsShaping(s)) return s;
            var parts = s.Split(new[] { "\\n" }, StringSplitOptions.None);
            for (int i = 0; i < parts.Length; i++) parts[i] = ArabicShaper.Shape(parts[i]);
            return string.Join("\\n", parts);
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

    /// <summary>One string to translate: the live cfg.bin value + its original text.</summary>
    public sealed class TransItem
    {
        public T2bValue Value;
        public string Original;
    }

    /// <summary>A "translate one cfg.bin" job handed from the Translation Helper to the Dialogue Editor:
    /// the parsed original file, the added/changed strings, and where/how to write the translation.</summary>
    public sealed class TranslationJob
    {
        public string Label;          // relative path (window title)
        public string TransLabel;     // e.g. "→ _fr" / "→ _en (Arabic)"
        public string OrigPath;       // source cfg.bin (original language)
        public string TransPath;      // where the translated cfg.bin is written
        public T2bFile File;          // parsed original file (its values are edited in place, then written to TransPath)
        public bool ShapeArabic;      // shape Arabic on save (translation language is the custom Arabic locale)
        public List<TransItem> Items = new List<TransItem>();
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
