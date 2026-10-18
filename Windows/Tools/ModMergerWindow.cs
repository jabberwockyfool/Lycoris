using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Lycoris.Formats;

namespace Lycoris
{
    /// <summary>
    /// Mod merger tool: combines two extracted YWML mods into a new output mod, unioning the entries of
    /// any .cfg.bin both mods touch instead of one file clobbering the other. Standalone — it doesn't need
    /// a loaded project, only two mod folders (and, optionally, the vanilla reference for a smarter 3-way merge).
    /// </summary>
    public sealed class ModMergerWindow : Window
    {
        private readonly string _reference;
        private readonly TextBox _aBox = Field();
        private readonly TextBox _bBox = Field();
        private readonly TextBox _outBox = Field();
        private readonly ComboBox _policy = new ComboBox { Margin = new Thickness(0, 0, 0, 0), MinWidth = 220 };
        private readonly CheckBox _use3Way;
        private readonly TextBox _log = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Background = Theme.FieldBg, Foreground = Theme.Fg, BorderBrush = Theme.Border,
        };
        private readonly Button _merge;

        public ModMergerWindow(Window owner, string referenceFolder)
        {
            _reference = referenceFolder;
            Owner = owner;
            Title = "Lycoris — Mod merger";
            Width = 820; Height = 620;
            Background = Theme.WindowBg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _policy.Items.Add("Prefer A (base) on conflict");
            _policy.Items.Add("Prefer B (incoming) on conflict");
            _policy.SelectedIndex = 0;

            _use3Way = new CheckBox
            {
                Content = "Use reference as 3-way base (smarter conflict resolution)",
                Foreground = Theme.Fg, VerticalAlignment = VerticalAlignment.Center,
                IsChecked = _reference != null, IsEnabled = _reference != null,
                Margin = new Thickness(0, 6, 0, 0),
            };

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(Intro());
            root.Children.Add(FolderRow("Mod A  (kept on conflict)", _aBox));
            root.Children.Add(FolderRow("Mod B  (merged in)", _bBox));
            root.Children.Add(FolderRow("Output folder (new mod)", _outBox));

            var opts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            opts.Children.Add(new TextBlock { Text = "Conflict policy:", Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            opts.Children.Add(_policy);
            root.Children.Add(opts);
            root.Children.Add(_use3Way);

            _merge = new Button { Content = "Merge", MinWidth = 120, MinHeight = 34, Margin = new Thickness(0, 12, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
            _merge.Click += (s, e) => Run();
            root.Children.Add(_merge);

            root.Children.Add(new TextBlock { Text = "Report", Foreground = Theme.FgMuted, Margin = new Thickness(0, 4, 0, 4) });
            _log.Height = 300;
            root.Children.Add(_log);

            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private void Run()
        {
            string a = _aBox.Text.Trim(), b = _bBox.Text.Trim(), outp = _outBox.Text.Trim();
            if (!Directory.Exists(a)) { DarkMessage.Show("Mod A folder does not exist.", "Mod merger"); return; }
            if (!Directory.Exists(b)) { DarkMessage.Show("Mod B folder does not exist.", "Mod merger"); return; }
            if (string.IsNullOrEmpty(outp)) { DarkMessage.Show("Choose an output folder.", "Mod merger"); return; }
            if (PathsOverlap(a, outp) || PathsOverlap(b, outp))
            {
                DarkMessage.Show("The output folder must be separate from Mod A and Mod B.", "Mod merger");
                return;
            }
            if (Directory.Exists(outp) && Directory.EnumerateFileSystemEntries(outp).Any())
            {
                var r = DarkMessage.Show($"“{outp}” isn't empty. Files with the same name will be overwritten. Continue?",
                    "Mod merger", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (r != MessageBoxResult.OK) return;
            }

            var policy = _policy.SelectedIndex == 1 ? MergePolicy.PreferB : MergePolicy.PreferA;
            string reference = _use3Way.IsChecked == true ? _reference : null;

            _merge.IsEnabled = false;
            try
            {
                var report = ModMerger.Merge(a, b, outp, reference, policy);
                _log.Text = Format(report, outp);
            }
            catch (Exception ex)
            {
                _log.Text = "Merge failed:\r\n" + ex;
            }
            finally { _merge.IsEnabled = true; }
        }

        private static string Format(MergeReport r, string outp)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Done → {outp}\\include");
            sb.AppendLine();
            sb.AppendLine($"Files:  {r.Merged} merged · {r.Identical} identical · {r.CopiedAOnly} A-only · {r.CopiedBOnly} B-only");
            sb.AppendLine($"cfg.bin records:  +{r.RecordsAdded} added · {r.RecordsOverwritten} updated · {r.RecordConflicts} conflict(s)");
            if (r.BinaryConflicts > 0) sb.AppendLine($"Binary conflicts (resolved by policy):  {r.BinaryConflicts}");
            if (r.Errors > 0) sb.AppendLine($"Errors:  {r.Errors}");
            sb.AppendLine();

            var problems = r.Problems.ToList();
            if (problems.Count > 0)
            {
                sb.AppendLine("⚠ Needs attention:");
                foreach (var f in problems) sb.AppendLine($"  {f.RelativePath}  —  {f.Outcome}: {f.Detail}");
                sb.AppendLine();
            }

            sb.AppendLine("All files:");
            foreach (var f in r.Files)
            {
                string line = $"  [{f.Outcome}] {f.RelativePath}";
                if (!string.IsNullOrEmpty(f.Detail)) line += "  —  " + f.Detail;
                sb.AppendLine(line);
            }
            return sb.ToString();
        }

        private static bool PathsOverlap(string x, string y)
        {
            string nx = Norm(x), ny = Norm(y);
            return nx == ny || nx.StartsWith(ny + "\\", StringComparison.OrdinalIgnoreCase)
                            || ny.StartsWith(nx + "\\", StringComparison.OrdinalIgnoreCase);
        }
        private static string Norm(string p) => Path.GetFullPath(p).TrimEnd('\\', '/');

        // ---- UI helpers -------------------------------------------------------------------

        private FrameworkElement Intro() => new TextBlock
        {
            Text = "Combine two extracted mods into a new one. When both edit the same cfg.bin, their entries are "
                 + "unioned (new records from B are inserted and group counts updated); other clashing binaries fall "
                 + "back to the conflict policy.",
            Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        };

        private FrameworkElement FolderRow(string label, TextBox box)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(new TextBlock { Text = label, Foreground = Theme.FgMuted, Margin = new Thickness(0, 0, 0, 3) });
            var row = new DockPanel();
            var browse = new Button { Content = "Browse…", MinWidth = 84, Margin = new Thickness(6, 0, 0, 0) };
            browse.Click += (s, e) =>
            {
                string picked = FolderPicker.Pick(label, new WindowInteropHelper(this).Handle);
                if (picked != null) box.Text = picked;
            };
            DockPanel.SetDock(browse, Dock.Right);
            row.Children.Add(browse);
            row.Children.Add(box);
            panel.Children.Add(row);
            return panel;
        }

        private static TextBox Field() => new TextBox
        {
            Background = Theme.FieldBg, Foreground = Theme.Fg, BorderBrush = Theme.Border,
            Padding = new Thickness(4, 3, 4, 3), VerticalContentAlignment = VerticalAlignment.Center,
        };
    }
}
