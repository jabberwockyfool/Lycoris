using System;
using System.Collections.Generic;
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

namespace Lycoris
{
    /// <summary>
    /// Translation Helper — diffs a mod's text against a vanilla YW3 dump and surfaces every string the mod
    /// ADDED or CHANGED (per .cfg.bin, by relative path), so you translate only the new content. Optional
    /// Arabic shaping (logical → presentation forms + RTL, via <see cref="ArabicShaper"/>) is applied on write.
    /// Standalone (no loaded project). Font-glyph injection (the .xf side) is a separate step.
    /// </summary>
    public sealed class TranslationHelperWindow : Window
    {
        private readonly TextBox _vanilla = Field();
        private readonly TextBox _mod = Field();
        private readonly ObservableCollection<TransRow> _rows = new ObservableCollection<TransRow>();
        private readonly Dictionary<string, T2bFile> _modFiles = new Dictionary<string, T2bFile>();  // path -> parsed
        private readonly CheckBox _shape;
        private readonly DataGrid _grid;
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

        public TranslationHelperWindow(Window owner)
        {
            Owner = owner;
            Title = "Lycoris — Translation Helper";
            Width = 940; Height = 660;
            Background = Theme.WindowBg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { Margin = new Thickness(14) };

            var top = new StackPanel();
            top.Children.Add(new TextBlock
            {
                Text = "Compare a mod against a vanilla YW3 dump — every string the mod added/changed shows up to translate.",
                Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });
            top.Children.Add(FolderRow("Vanilla YW3 dump", _vanilla));
            top.Children.Add(FolderRow("Mod folder", _mod));

            _shape = new CheckBox { Content = "Shape Arabic on Apply (logical → presentation forms + RTL)", Foreground = Theme.Fg, IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
            top.Children.Add(_shape);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 6) };
            bar.Children.Add(Btn("Compare", Compare));
            bar.Children.Add(Btn("Apply to mod", Apply, 8));
            bar.Children.Add(Btn("Install Arabic font", InstallFont, 8));
            bar.Children.Add(Btn("Export TSV…", ExportTsv, 20));
            bar.Children.Add(Btn("Import TSV…", ImportTsv, 8));
            top.Children.Add(bar);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_status);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Background = Theme.FieldBg, Foreground = Theme.Fg, RowBackground = Theme.FieldBg,
                ItemsSource = _rows,
            };
            _grid.Columns.Add(new DataGridTextColumn { Header = "File", Binding = Bind(nameof(TransRow.File)), Width = 190, IsReadOnly = true });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Original (mod)", Binding = Bind(nameof(TransRow.OriginalDisplay)), Width = 330, IsReadOnly = true });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Translation", Binding = Bind(nameof(TransRow.Translation)), Width = 330 });
            root.Children.Add(_grid);

            Content = root;
            _status.Text = "Pick a vanilla dump + a mod folder, then Compare.";
        }

        private void Compare()
        {
            _rows.Clear(); _modFiles.Clear();
            string modRoot = _mod.Text.Trim(), vanRoot = _vanilla.Text.Trim();
            if (!Directory.Exists(modRoot) || !Directory.Exists(vanRoot)) { _status.Text = "Set both folders (they must exist)."; return; }

            int scanned = 0, added = 0;
            foreach (var mf in Directory.EnumerateFiles(modRoot, "*.cfg.bin", SearchOption.AllDirectories))
            {
                T2bFile mod;
                try { mod = T2bReader.ReadFile(mf); } catch { continue; }
                scanned++;
                string rel = mf.Substring(modRoot.Length).TrimStart('\\', '/');
                string vf = Path.Combine(vanRoot, rel);
                var vanStrings = new HashSet<string>(StringComparer.Ordinal);
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
                bool cached = false;
                foreach (var e in mod.Entries)
                    foreach (var v in e.Values)
                    {
                        if (v.Type != Lycoris.Formats.ValueType.String || !(v.Value is string s) || s.Length == 0) continue;
                        if (vanStrings.Contains(s) || !HasLetter(s)) continue;   // unchanged, or a symbol/hash string
                        if (!cached) { _modFiles[mf] = mod; cached = true; }
                        _rows.Add(new TransRow { File = rel, FilePath = mf, Value = v, Original = s });
                        added++;
                    }
            }
            _status.Text = $"Scanned {scanned} .cfg.bin — {added} added/changed string(s). Fill Translation, then Apply.";
        }

        private void Apply()
        {
            if (_rows.Count == 0) return;
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            bool shape = _shape.IsChecked == true;
            int n = 0; var touched = new HashSet<string>();
            foreach (var r in _rows)
            {
                if (string.IsNullOrWhiteSpace(r.Translation)) continue;
                string t = r.Translation;
                if (shape) t = ShapeMultiline(t);
                r.Value.Value = t;
                touched.Add(r.FilePath);
                n++;
            }
            try
            {
                foreach (var f in touched) if (_modFiles.TryGetValue(f, out var tf)) T2bWriter.WriteFile(tf, f);
                _status.Text = $"Applied {n} translation(s) to {touched.Count} mod file(s).";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Translation Helper — apply error", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        /// <summary>Shape Arabic line-by-line, preserving literal "\n" line-break tokens the game uses.</summary>
        private static string ShapeMultiline(string s)
        {
            if (!ArabicShaper.NeedsShaping(s)) return s;
            var parts = s.Split(new[] { "\\n" }, StringSplitOptions.None);
            for (int i = 0; i < parts.Length; i++) parts[i] = ArabicShaper.Shape(parts[i]);
            return string.Join("\\n", parts);
        }

        private void ExportTsv()
        {
            if (_rows.Count == 0) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "TSV|*.tsv|All files|*.*", FileName = "translation.tsv" };
            if (dlg.ShowDialog() != true) return;
            var sb = new StringBuilder("File\tOriginal\tTranslation\n");
            foreach (var r in _rows)
                sb.Append(r.File).Append('\t').Append(Esc(r.Original)).Append('\t').Append(Esc(r.Translation ?? "")).Append('\n');
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
            _status.Text = "Exported: " + Path.GetFileName(dlg.FileName);
        }

        private void ImportTsv()
        {
            if (_rows.Count == 0) { _status.Text = "Compare first, then import a TSV of translations."; return; }
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "TSV|*.tsv|All files|*.*" };
            if (dlg.ShowDialog() != true) return;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);   // File\tOriginal -> Translation
            foreach (var raw in File.ReadAllLines(dlg.FileName, Encoding.UTF8))
            {
                var p = raw.Split('\t');
                if (p.Length < 3 || p[0] == "File") continue;
                map[p[0] + "\t" + Unesc(p[1])] = Unesc(p[2]);
            }
            int n = 0;
            foreach (var r in _rows)
                if (map.TryGetValue(r.File + "\t" + r.Original, out var t) && t.Length > 0) { r.Translation = t; n++; }
            _status.Text = $"Imported {n} translation(s). Review, then Apply.";
        }

        /// <summary>Copy the bundled preset Arabic-injected fonts (ft_nrm.xf / ft_sml.xf) into the mod's
        /// include/fnt so the shaped Arabic renders in-game — the "just use a preset .xf" path.</summary>
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
        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\t", " ").Replace("\r", "").Replace("\n", "\\n");
        private static string Unesc(string s) => (s ?? "").Replace("\\n", "\n").Replace("\\\\", "\\");

        // --- UI helpers ---
        private static TextBox Field() => new TextBox { Margin = new Thickness(0, 2, 6, 2) };
        private static Binding Bind(string p) => new Binding(p) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus };
        private Button Btn(string text, Action onClick, double left = 0)
        {
            var b = new Button { Content = text, MinWidth = 110, MinHeight = 30, Margin = new Thickness(left, 0, 0, 0) };
            b.Click += (s, e) => onClick();
            return b;
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

    /// <summary>One added/changed string to translate (references the live T2bValue so Apply writes in place).</summary>
    public sealed class TransRow : INotifyPropertyChanged
    {
        private string _translation;
        public string File { get; set; }         // relative path (display)
        public string FilePath { get; set; }     // absolute path (write target)
        public T2bValue Value { get; set; }       // the live value in the parsed mod T2bFile
        public string Original { get; set; }
        public string OriginalDisplay => (Original ?? "").Replace("\n", "\\n");
        public string Translation { get => _translation; set { _translation = value; OnChanged(nameof(Translation)); } }
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
