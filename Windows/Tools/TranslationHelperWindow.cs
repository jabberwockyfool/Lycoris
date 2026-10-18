using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Lycoris.Formats;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Translation Helper — diffs a mod's text against a vanilla YW3 dump and lists every cfg.bin the mod ADDED
    /// or CHANGED strings in (per file, matched by relative path), for the chosen ORIGINAL language. Click a file
    /// to open it in the Dialogue Editor with only those added/changed lines, translate them, and Save — which
    /// writes the TRANSLATION language's cfg.bin (Arabic is a custom locale that ships over _en and is shaped).
    /// Point both fields at the game's "data" folder. Standalone (no loaded project).
    /// </summary>
    public sealed class TranslationHelperWindow : Window
    {
        private readonly TextBox _vanilla = Field();
        private readonly TextBox _mod = Field();
        private readonly TextBox _out = Field();
        private readonly ComboBox _origLang = new ComboBox { Margin = new Thickness(0, 2, 6, 2) };
        private readonly ComboBox _transLang = new ComboBox { Margin = new Thickness(0, 2, 6, 2) };
        private readonly ObservableCollection<FileEntry> _files = new ObservableCollection<FileEntry>();
        private readonly ListBox _list;
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

        /// <summary>YW3 per-language cfg.bin suffixes. Arabic is a custom locale that ships over the English
        /// files, so as a TRANSLATION target it maps to "_en" and turns on shaping.</summary>
        private static readonly (string Label, string Suffix, bool Arabic)[] Langs =
        {
            ("Français  (_fr)", "_fr", false),
            ("English  (_en)", "_en", false),
            ("Deutsch  (_de)", "_de", false),
            ("Español  (_es)", "_es", false),
            ("Italiano  (_it)", "_it", false),
            ("العربية Arabic  (custom → _en)", "_en", true),
        };

        public TranslationHelperWindow(Window owner)
        {
            Owner = owner;
            Title = "Lycoris — Translation Helper";
            Width = 860; Height = 620;
            Background = Theme.WindowBg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { Margin = new Thickness(14) };

            var top = new StackPanel();
            top.Children.Add(new TextBlock
            {
                Text = "Compare a mod against a vanilla YW3 dump — every cfg.bin the mod added/changed text in shows up. " +
                       "Point both at the game's \"data\" folder, pick the Original (what you're translating from) and Translation languages, then Compare. Click a file to translate its added lines in the Dialogue Editor.",
                Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });
            top.Children.Add(FolderRow("Vanilla \"data\" folder", _vanilla));
            top.Children.Add(FolderRow("Mod \"data\" folder", _mod));
            top.Children.Add(FolderRow("Translated mod (output)", _out));

            foreach (var l in Langs) { _origLang.Items.Add(l.Label); _transLang.Items.Add(l.Label); }
            _origLang.SelectedIndex = 1;   // English by default (mods usually add English text)
            _transLang.SelectedIndex = 0;  // French by default
            top.Children.Add(LabeledRow("Original language", _origLang));
            top.Children.Add(LabeledRow("Translation language", _transLang));

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 6) };
            bar.Children.Add(Btn("Compare", Compare));
            bar.Children.Add(Btn("Open in Dialogue Editor", OpenSelected, 8));
            bar.Children.Add(Btn("Install Arabic font", InstallFont, 20));
            top.Children.Add(bar);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_status);

            _list = new ListBox { Background = Theme.FieldBg, Foreground = Theme.Fg };
            _list.MouseDoubleClick += (s, e) => OpenSelected();
            _list.ItemsSource = _files;
            root.Children.Add(_list);

            Content = root;
            _status.Text = "Pick the vanilla + mod \"data\" folders and both languages, then Compare.";
        }

        private const int MaxFiles = 5000;

        private void Compare()
        {
            _files.Clear();
            string modRoot = _mod.Text.Trim(), vanRoot = _vanilla.Text.Trim();
            if (!Directory.Exists(modRoot) || !Directory.Exists(vanRoot)) { _status.Text = "Set both folders (they must exist)."; return; }

            var orig = Langs[_origLang.SelectedIndex < 0 ? 1 : _origLang.SelectedIndex];
            int scanned = 0, strings = 0;
            try
            {
                foreach (var mf in SafeEnumCfgBin(modRoot))
                {
                    if (_files.Count >= MaxFiles) break;
                    string rel = mf.Substring(modRoot.Length).TrimStart('\\', '/');
                    if (!IsUnderData(modRoot, rel)) continue;               // only game text under a "data" folder
                    if (!MatchesLang(mf, orig.Suffix)) continue;            // only the original language's cfg.bin
                    T2bFile mod;
                    try { mod = T2bReader.ReadFile(mf); } catch { continue; }
                    scanned++;

                    var vanStrings = new HashSet<string>(StringComparer.Ordinal);
                    string vf = Path.Combine(vanRoot, rel);
                    if (File.Exists(vf))
                    {
                        try
                        {
                            foreach (var e in T2bReader.ReadFile(vf).Entries)
                                foreach (var v in e.Values)
                                    if (v.Type == Lycoris.Formats.ValueType.String && v.Value is string s && s.Length > 0) vanStrings.Add(s);
                        }
                        catch { }
                    }

                    var items = new List<TransItem>();
                    foreach (var e in mod.Entries)
                        foreach (var v in e.Values)
                        {
                            if (v.Type != Lycoris.Formats.ValueType.String || !(v.Value is string s) || s.Length == 0) continue;
                            if (vanStrings.Contains(s) || !HasLetter(s)) continue;   // unchanged, or a symbol/hash string
                            items.Add(new TransItem { Value = v, Original = s });
                        }
                    if (items.Count > 0)
                    {
                        _files.Add(new FileEntry { Rel = rel, ModPath = mf, ModFile = mod, Items = items });
                        strings += items.Count;
                    }
                }
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.ToString(), "Translation Helper — compare error", MessageBoxButton.OK, MessageBoxImage.Error);
                _status.Text = "Compare failed: " + ex.Message;
                return;
            }

            _status.Text = _files.Count == 0
                ? $"No {orig.Suffix}.cfg.bin with added/changed text found under a \"data\" folder. Check the folders and Original language."
                : $"{_files.Count} file(s) with {strings} added/changed string(s). Click one to translate in the Dialogue Editor.";
        }

        private void OpenSelected()
        {
            var fe = _list.SelectedItem as FileEntry;
            if (fe == null) { _status.Text = "Select a file first."; return; }
            string outRoot = _out.Text.Trim();
            if (string.IsNullOrEmpty(outRoot)) { _status.Text = "Set the \"Translated mod (output)\" folder first."; return; }
            var orig = Langs[_origLang.SelectedIndex < 0 ? 1 : _origLang.SelectedIndex];
            var trans = Langs[_transLang.SelectedIndex < 0 ? 0 : _transLang.SelectedIndex];

            // Base the output on the REAL target-language text: keep the mod file's structure (so it loads and its
            // added entries are present), but swap every UNCHANGED English string for its vanilla target-language
            // equivalent (matched by entry CRC + value index). Only the added/changed lines are left for you to
            // translate. If there's no vanilla target file, unchanged lines stay as the mod's English (fallback).
            OverlayVanillaTarget(fe, orig.Suffix, trans.Suffix);

            var job = new TranslationJob
            {
                Label = fe.Rel,
                TransLabel = "→ " + trans.Suffix + (trans.Arabic ? " (Arabic)" : ""),
                OrigPath = fe.ModPath,
                TransPath = TransPathIn(outRoot, fe.Rel, orig.Suffix, trans.Suffix),
                File = fe.ModFile,
                ShapeArabic = trans.Arabic,
                Items = fe.Items,
            };
            new DialogueEditorWindow(this, job).Show();
        }

        /// <summary>Overlay the vanilla target-language text onto the mod file's unchanged strings (matched by
        /// entry CRC + value index), leaving the added/changed lines (the <see cref="FileEntry.Items"/>) untouched
        /// for translation. Mutates <paramref name="fe"/>.ModFile in place. No-op if no vanilla target file exists.</summary>
        private void OverlayVanillaTarget(FileEntry fe, string origSuffix, string transSuffix)
        {
            string vanRoot = _vanilla.Text.Trim();
            string vanTarget = SwapLangPath(vanRoot, fe.Rel, origSuffix, transSuffix);
            if (!File.Exists(vanTarget)) return;

            // (CRC, valueIndex) -> target-language string, from the vanilla target file.
            var map = new Dictionary<(uint, int), string>();
            try
            {
                foreach (var e in T2bReader.ReadFile(vanTarget).Entries)
                    for (int i = 0; i < e.Values.Count; i++)
                        if (e.Values[i].Type == Lycoris.Formats.ValueType.String && e.Values[i].Value is string s)
                            map[(e.Crc, i)] = s;
            }
            catch { return; }

            var modified = new HashSet<T2bValue>(fe.Items.Select(it => it.Value));
            foreach (var e in fe.ModFile.Entries)
                for (int i = 0; i < e.Values.Count; i++)
                {
                    var v = e.Values[i];
                    if (v.Type != Lycoris.Formats.ValueType.String || modified.Contains(v)) continue;   // leave changed lines to translate
                    if (map.TryGetValue((e.Crc, i), out var fr)) { v.Value = fr; }                       // real target-language text
                }
        }

        /// <summary>A sibling path under a different root with the language folder (…\en\ → …\fr\) and the
        /// filename suffix (_en → _fr) swapped. Used to locate the vanilla target-language file.</summary>
        private static string SwapLangPath(string root, string rel, string origSuffix, string transSuffix)
        {
            string origCode = origSuffix.TrimStart('_'), transCode = transSuffix.TrimStart('_');
            string relDir = Path.GetDirectoryName(rel) ?? "";
            var segs = relDir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segs.Length; i++)
                if (segs[i].Equals(origCode, StringComparison.OrdinalIgnoreCase)) segs[i] = transCode;
            relDir = string.Join("\\", segs);

            string name = Path.GetFileName(rel);
            const string ext = ".cfg.bin";
            string baseName = name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - ext.Length) : name;
            if (baseName.EndsWith(origSuffix, StringComparison.OrdinalIgnoreCase))
                baseName = baseName.Substring(0, baseName.Length - origSuffix.Length) + transSuffix;
            return Path.Combine(root, relDir, baseName + ext);
        }

        /// <summary>Output path inside the "Translated mod" folder, as a proper YW3 mod tree: rebuild the
        /// include\data\… prefix (the Mod field points at the "data" folder, so rel omits both), keep the file's
        /// relative sub-tree — but swap the original-language LANGUAGE FOLDER (e.g. …\en\ → …\fr\) as well as the
        /// original-language suffix in the file name for the translation ones.</summary>
        private static string TransPathIn(string outRoot, string rel, string origSuffix, string transSuffix)
            => SwapLangPath(Path.Combine(outRoot, "include", "data"), rel, origSuffix, transSuffix);

        /// <summary>Walk a tree for *.cfg.bin, swallowing per-folder access errors (game dumps often have
        /// unreadable subfolders that would otherwise abort the whole enumeration and crash the app).</summary>
        private static IEnumerable<string> SafeEnumCfgBin(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files = null, subs = null;
                try { files = Directory.GetFiles(dir, "*.cfg.bin"); } catch { }
                if (files != null) foreach (var f in files) yield return f;
                try { subs = Directory.GetDirectories(dir); } catch { }
                if (subs != null) foreach (var d in subs) stack.Push(d);
            }
        }

        private static bool IsUnderData(string root, string rel)
        {
            if (Path.GetFileName(root.TrimEnd('\\', '/')).Equals("data", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var seg in rel.Split('\\', '/'))
                if (seg.Equals("data", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool MatchesLang(string path, string suffix)
        {
            string name = Path.GetFileName(path);
            if (name.EndsWith(".cfg.bin", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - ".cfg.bin".Length);
            return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Copy the bundled preset Arabic-injected fonts (ft_nrm.xf / ft_sml.xf) into the mod's
        /// include/fnt so shaped Arabic renders in-game — the "just use a preset .xf" path.</summary>
        private void InstallFont()
        {
            string modRoot = _mod.Text.Trim();
            if (!Directory.Exists(modRoot)) { _status.Text = "Set the mod folder first."; return; }
            string preset = FindUp("tools\\arabic_font");
            if (preset == null) { DarkMessage.Show("Preset fonts not found (tools/arabic_font next to Lycoris).", "Install Arabic font"); return; }

            string fntDir = Directory.EnumerateDirectories(modRoot, "fnt", SearchOption.AllDirectories).FirstOrDefault();
            if (fntDir == null)
            {
                var parts = modRoot.Replace('/', '\\').Split('\\');
                int inc = Array.FindLastIndex(parts, p => p.Equals("include", StringComparison.OrdinalIgnoreCase));
                string incBase = inc >= 0 ? string.Join("\\", parts.Take(inc + 1))
                                          : (Directory.Exists(Path.Combine(modRoot, "include")) ? Path.Combine(modRoot, "include") : modRoot);
                fntDir = Path.Combine(incBase, "fnt");
                Directory.CreateDirectory(fntDir);
            }
            int n = 0;
            foreach (var name in new[] { "ft_nrm.xf", "ft_sml.xf" })
            {
                string src = Path.Combine(preset, name);
                if (File.Exists(src)) { File.Copy(src, Path.Combine(fntDir, name), true); n++; }
            }
            _status.Text = n > 0 ? $"Installed {n} Arabic font(s) → {fntDir}" : "No preset fonts found to install.";
        }

        private static string FindUp(string rel)
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string p = Path.Combine(dir, rel);
                if (Directory.Exists(p) || File.Exists(p)) return p;
                dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
            return null;
        }

        private static bool HasLetter(string s)
        {
            foreach (char c in s) if (char.IsLetter(c)) return true;
            return false;
        }

        // --- UI helpers ---
        private static TextBox Field() => new TextBox { Margin = new Thickness(0, 2, 6, 2) };
        private Button Btn(string text, Action onClick, double left = 0)
        {
            var b = new Button { Content = text, MinWidth = 110, MinHeight = 30, Margin = new Thickness(left, 0, 0, 0) };
            b.Click += (s, e) => onClick();
            return b;
        }
        private FrameworkElement LabeledRow(string label, FrameworkElement control)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var lbl = new TextBlock { Text = label, Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lbl, 0); Grid.SetColumn(control, 1);
            g.Children.Add(lbl); g.Children.Add(control);
            return g;
        }
        private FrameworkElement FolderRow(string label, TextBox box)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new TextBlock { Text = label, Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center };
            var browse = new Button { Content = "…", MinWidth = 34, MinHeight = 26 };
            browse.Click += (s, e) =>
            {
                using (var d = new System.Windows.Forms.FolderBrowserDialog())
                    if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) box.Text = d.SelectedPath;
            };
            Grid.SetColumn(lbl, 0); Grid.SetColumn(box, 1); Grid.SetColumn(browse, 2);
            g.Children.Add(lbl); g.Children.Add(box); g.Children.Add(browse);
            return g;
        }
    }

    /// <summary>One mod cfg.bin (original language) that has added/changed strings vs the vanilla dump.</summary>
    public sealed class FileEntry
    {
        public string Rel;              // relative path (display)
        public string ModPath;          // absolute path to the mod's original-language file
        public T2bFile ModFile;         // parsed original file (its values become the translation targets)
        public List<TransItem> Items;   // the added/changed strings
        public override string ToString() => $"{Rel}    ({Items.Count})";
    }
}
