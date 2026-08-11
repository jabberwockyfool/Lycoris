using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lycoris
{
    /// <summary>
    /// Model Editor — ports a Gen-7 Pokémon (Sun/Moon) *_model.bin to a YW3 model archive (_p00.xc)
    /// by driving the validated Python pipeline (tools/pkmnport/lycoris_model.py → pack_p00). Lets you
    /// pick which meshes to port and which get a toon outline. Standalone (no loaded project). Can be
    /// opened pre-filled from the "Add model (.bin)" flow when creating a yo-kai.
    /// </summary>
    public sealed class ModelEditorWindow : Window
    {
        private readonly TextBox _model = Field();
        private readonly TextBox _tex = Field();
        private readonly TextBox _donor = Field();
        private readonly TextBox _out = Field();
        private readonly TextBox _seRoot = Field();
        private readonly ObservableCollection<MeshRow> _meshes = new ObservableCollection<MeshRow>();
        private readonly DataGrid _grid;
        private readonly TextBox _log = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Consolas"), FontSize = 12, MinHeight = 90,
            Background = Theme.FieldBg, Foreground = Theme.Fg, BorderBrush = Theme.Border,
        };
        private readonly string _script;

        public ModelEditorWindow(Window owner, string modelBin = null, string outXc = null)
        {
            Owner = owner;
            Title = "Lycoris — Model Editor (.bin → .xc)";
            Width = 820; Height = 660;
            Background = Theme.WindowBg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _script = FindScript("tools\\pkmnport\\lycoris_model.py");
            _seRoot.Text = @"E:\Yo-kai watch Mods\studio_eleven";
            if (modelBin != null) { _model.Text = modelBin; AutoFillFromModel(); }
            if (outXc != null) _out.Text = outXc;

            var root = new DockPanel { Margin = new Thickness(14) };

            var top = new StackPanel();
            top.Children.Add(Intro());
            top.Children.Add(FileRow("Model .bin (Sun/Moon)", _model, "Pokemon model|*.bin|All files|*.*", m => AutoFillFromModel()));
            top.Children.Add(FileRow("Textures _tex.bin (optional)", _tex, "Textures|*.bin|All files|*.*", null));
            top.Children.Add(FileRow("p00 donor .xc", _donor, "YW3 archive|*.xc|All files|*.*", null));
            top.Children.Add(FileRow("Output .xc", _out, "YW3 archive|*.xc", null, save: true));
            top.Children.Add(FolderRow("studio_eleven folder", _seRoot));
            var scanBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 6) };
            scanBar.Children.Add(Btn("Scan meshes", Scan));
            scanBar.Children.Add(Btn("Build .xc", Build, 12));
            top.Children.Add(scanBar);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            DockPanel.SetDock(_log, Dock.Bottom);
            root.Children.Add(_log);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Background = Theme.FieldBg, Foreground = Theme.Fg, RowBackground = Theme.FieldBg,
                Margin = new Thickness(0, 4, 0, 8), ItemsSource = _meshes,
            };
            _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Port", Binding = Bind(nameof(MeshRow.Include)), Width = 55 });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Mesh", Binding = Bind(nameof(MeshRow.Name)), Width = 320, IsReadOnly = true });
            _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Outline", Binding = Bind(nameof(MeshRow.Outline)), Width = 70 });
            root.Children.Add(_grid);

            Content = root;
            if (_script == null)
                Log("lycoris_model.py not found under tools/pkmnport — set the Lycoris tools folder next to the app.");
            else
                Log("Ready. Pick a Model .bin + p00 donor, Scan meshes, tick Port/Outline, then Build .xc.");
        }

        private void AutoFillFromModel()
        {
            string m = _model.Text.Trim();
            if (m.Length == 0) return;
            if (string.IsNullOrWhiteSpace(_tex.Text))
            {
                string tex = m.Replace("_model.bin", "_tex.bin");
                if (File.Exists(tex)) _tex.Text = tex;
            }
            if (string.IsNullOrWhiteSpace(_out.Text))
                _out.Text = Path.Combine(Path.GetDirectoryName(m) ?? "", Path.GetFileNameWithoutExtension(m).Replace("_model", "") + "_p00.xc");
        }

        private void Scan()
        {
            if (!Preflight(needDonor: false)) return;
            _meshes.Clear();
            var r = RunPy("--scan \"" + _model.Text.Trim() + "\"");
            if (r.code != 0) { Log("Scan failed:\n" + r.err); return; }
            foreach (var line in r.outp.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('\t');
                if (parts.Length < 1 || parts[0].Length == 0) continue;
                _meshes.Add(new MeshRow { Name = parts[0], Include = true, Outline = parts.Length > 1 && parts[1].Trim() == "1" });
            }
            Log($"Found {_meshes.Count} mesh(es). Tick Port / Outline, then Build.");
        }

        private void Build()
        {
            if (!Preflight(needDonor: true)) return;
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            var sb = new StringBuilder();
            sb.Append("--build --model \"").Append(_model.Text.Trim()).Append("\"");
            sb.Append(" --donor \"").Append(_donor.Text.Trim()).Append("\"");
            sb.Append(" --out \"").Append(_out.Text.Trim()).Append("\"");
            if (!string.IsNullOrWhiteSpace(_tex.Text)) sb.Append(" --tex \"").Append(_tex.Text.Trim()).Append("\"");
            if (!string.IsNullOrWhiteSpace(_seRoot.Text)) sb.Append(" --se-root \"").Append(_seRoot.Text.Trim()).Append("\"");
            if (_meshes.Count > 0)
            {
                var inc = _meshes.Where(m => m.Include).Select(m => m.Name).ToList();
                var outl = _meshes.Where(m => m.Outline).Select(m => m.Name).ToList();
                if (inc.Count > 0) sb.Append(" --include ").Append(string.Join(",", inc));
                sb.Append(outl.Count > 0 ? " --outline " + string.Join(",", outl) : " --no-outline");
            }
            Log("Building…");
            var r = RunPy(sb.ToString());
            Log(r.code == 0 ? "✔ " + r.outp.Trim() : "✖ Build failed:\n" + (r.err.Length > 0 ? r.err : r.outp));
        }

        private bool Preflight(bool needDonor)
        {
            if (_script == null) { Log("lycoris_model.py not found."); return false; }
            if (string.IsNullOrWhiteSpace(_model.Text) || !File.Exists(_model.Text.Trim())) { Log("Set a valid Model .bin."); return false; }
            if (needDonor && (string.IsNullOrWhiteSpace(_donor.Text) || !File.Exists(_donor.Text.Trim()))) { Log("Set a p00 donor .xc."); return false; }
            if (needDonor && string.IsNullOrWhiteSpace(_out.Text)) { Log("Set an Output .xc."); return false; }
            return true;
        }

        // --- Python process ---
        private (int code, string outp, string err) RunPy(string args)
        {
            foreach (var exe in new[] { "python", "py" })
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = "\"" + _script + "\" " + args,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        UseShellExecute = false, CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    };
                    using (var p = Process.Start(psi))
                    {
                        string o = p.StandardOutput.ReadToEnd();
                        string e = p.StandardError.ReadToEnd();
                        p.WaitForExit();
                        return (p.ExitCode, o, e);
                    }
                }
                catch (System.ComponentModel.Win32Exception) { /* try next launcher */ }
                catch (Exception ex) { return (1, "", ex.Message); }
            }
            return (1, "", "Python not found (install Python, or add it to PATH).");
        }

        private static string FindScript(string rel)
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string p = Path.Combine(dir, rel);
                if (File.Exists(p)) return p;
                dir = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
            }
            return null;
        }

        private void Log(string s) => _log.AppendText((_log.Text.Length > 0 ? "\n" : "") + s + (s.EndsWith("\n") ? "" : ""));

        // --- UI helpers ---
        private static TextBox Field() => new TextBox { Margin = new Thickness(0, 2, 6, 2) };
        private static System.Windows.Data.Binding Bind(string p) =>
            new System.Windows.Data.Binding(p) { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged };

        private Button Btn(string text, Action onClick, double left = 0)
        {
            var b = new Button { Content = text, MinWidth = 110, MinHeight = 30, Margin = new Thickness(left, 0, 0, 0) };
            b.Click += (s, e) => onClick();
            return b;
        }

        private FrameworkElement FileRow(string label, TextBox box, string filter, Action<string> onPick, bool save = false)
        {
            var g = Row(label, box);
            var browse = new Button { Content = "…", MinWidth = 34, MinHeight = 26 };
            browse.Click += (s, e) =>
            {
                if (save)
                {
                    var d = new Microsoft.Win32.SaveFileDialog { Filter = filter };
                    if (d.ShowDialog() == true) { box.Text = d.FileName; onPick?.Invoke(d.FileName); }
                }
                else
                {
                    var d = new Microsoft.Win32.OpenFileDialog { Filter = filter };
                    if (d.ShowDialog() == true) { box.Text = d.FileName; onPick?.Invoke(d.FileName); }
                }
            };
            ((Grid)g).Children.Add(browse);
            Grid.SetColumn(browse, 2);
            return g;
        }

        private FrameworkElement FolderRow(string label, TextBox box)
        {
            var g = Row(label, box);
            var browse = new Button { Content = "…", MinWidth = 34, MinHeight = 26 };
            browse.Click += (s, e) =>
            {
                using (var d = new System.Windows.Forms.FolderBrowserDialog())
                    if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) box.Text = d.SelectedPath;
            };
            ((Grid)g).Children.Add(browse);
            Grid.SetColumn(browse, 2);
            return g;
        }

        private static Grid Row(string label, TextBox box)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new TextBlock { Text = label, Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lbl, 0); Grid.SetColumn(box, 1);
            g.Children.Add(lbl); g.Children.Add(box);
            return g;
        }

        private static FrameworkElement Intro() => new TextBlock
        {
            Text = "Port a Pokémon Sun/Moon *_model.bin to a YW3 _p00.xc (drives the Python pipeline).",
            Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
    }

    /// <summary>One mesh row in the Model Editor: whether to port it and whether to outline it.</summary>
    public sealed class MeshRow : INotifyPropertyChanged
    {
        private bool _include = true, _outline;
        public string Name { get; set; }
        public bool Include { get => _include; set { _include = value; OnChanged(nameof(Include)); } }
        public bool Outline { get => _outline; set { _outline = value; OnChanged(nameof(Outline)); } }
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
