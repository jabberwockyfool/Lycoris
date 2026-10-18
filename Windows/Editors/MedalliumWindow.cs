using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lycoris.Formats;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Medallium view: lays out the yo-kai's mini medals in a grid ordered by their "Medal" number (the
    /// CHARA_PARAM_INFO medalium offset). Select a medal to change ONLY that yo-kai's number — nothing here ever
    /// touches every yo-kai at once. "Restore from vanilla" copies the original numbers back from the reference.
    /// </summary>
    public sealed class MedalliumWindow : Window
    {
        private readonly YokaiDatabase _db;
        private readonly WrapPanel _grid = new WrapPanel();
        private readonly TextBox _search = new TextBox { Width = 180, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _selLbl = new TextBlock { Foreground = Theme.Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
        private readonly TextBox _num = new TextBox { Width = 70, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };
        private readonly Dictionary<string, BitmapSource> _iconCache = new Dictionary<string, BitmapSource>();
        private List<YokaiInfo> _order = new List<YokaiInfo>();
        private YokaiInfo _selected;

        private const int Cell = 52;

        public MedalliumWindow(Window owner, YokaiDatabase db, YokaiInfo focus = null)
        {
            _db = db;
            Owner = owner;
            Title = "Lycoris — Medallium (order by number)";
            Width = 740; Height = 660;
            Background = Theme.WindowBg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            Resort();

            // Top: search + the selected yo-kai's own number editor (changes ONLY that yo-kai).
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            top.Children.Add(new TextBlock { Text = "Find ", Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center });
            top.Children.Add(_search);
            _search.KeyUp += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) FindAndSelect(); };
            top.Children.Add(Btn("Go", FindAndSelect, 4));
            top.Children.Add(_selLbl);
            top.Children.Add(new TextBlock { Text = "number", Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
            top.Children.Add(_num);
            _num.KeyUp += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) SetNumber(); };
            top.Children.Add(Btn("Set this one", SetNumber, 4));
            DockPanel.SetDock(top, Dock.Top);

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 6) };
            bar.Children.Add(Btn("◀ Swap earlier", () => Swap(-1)));
            bar.Children.Add(Btn("Swap later ▶", () => Swap(+1), 4));
            bar.Children.Add(Btn("Restore from vanilla", RestoreFromVanilla, 20));
            bar.Children.Add(Btn("Save to mod", Save, 8));
            DockPanel.SetDock(bar, Dock.Top);

            DockPanel.SetDock(_status, Dock.Bottom);

            var root = new DockPanel();
            root.Children.Add(top);
            root.Children.Add(bar);
            root.Children.Add(_status);
            root.Children.Add(new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(6) });
            Content = root;

            Select(focus != null && _order.Contains(focus) ? focus : _order.FirstOrDefault());
            _status.Text = $"{_order.Count} yo-kai. Click a medal (or Find), edit its number, Set this one, then Save. Nothing changes every yo-kai.";
        }

        private void Resort()
        {
            _order = _db.Yokai
                .OrderBy(y => y.Medal ?? int.MaxValue)
                .ThenBy(y => y.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void Select(YokaiInfo y)
        {
            _selected = y;
            _selLbl.Text = y == null ? "(none)" : y.DisplayName;
            _num.Text = y?.Medal?.ToString() ?? "";
            Rebuild();
        }

        private void Rebuild()
        {
            _grid.Children.Clear();
            foreach (var y in _order)
            {
                var img = new Image { Width = 40, Height = 40, Source = MedalIcon(y), Stretch = Stretch.Uniform };
                var num = new TextBlock { Text = y.Medal?.ToString() ?? "0", HorizontalAlignment = HorizontalAlignment.Center, Foreground = Theme.Fg, FontSize = 11 };
                var sp = new StackPanel();
                sp.Children.Add(img);
                sp.Children.Add(num);
                bool sel = ReferenceEquals(y, _selected);
                var cell = new Border
                {
                    Width = Cell, Height = Cell + 14, Margin = new Thickness(2),
                    Background = sel ? new SolidColorBrush(Color.FromRgb(0x5B, 0x3B, 0x8C)) : Theme.FieldBg,
                    BorderBrush = sel ? Brushes.White : Theme.FieldBg,
                    BorderThickness = new Thickness(sel ? 2 : 1),
                    CornerRadius = new CornerRadius(6), Child = sp,
                    ToolTip = $"{y.DisplayName}  (medal {y.Medal?.ToString() ?? "0"}, param 0x{unchecked((uint)y.ParamHash):X8})",
                };
                var captured = y;
                cell.MouseLeftButtonUp += (s, e) => Select(captured);
                _grid.Children.Add(cell);
            }
            ScrollToSelected();
        }

        private void ScrollToSelected()
        {
            int i = _order.IndexOf(_selected);
            if (i >= 0 && i < _grid.Children.Count) (_grid.Children[i] as FrameworkElement)?.BringIntoView();
        }

        private void FindAndSelect()
        {
            string q = _search.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return;
            var hit = _order.FirstOrDefault(y => (y.DisplayName != null && y.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                                              || (y.ModelName != null && y.ModelName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
            if (hit != null) Select(hit); else _status.Text = $"No yo-kai matches \"{q}\".";
        }

        /// <summary>Change ONLY the selected yo-kai's medal number.</summary>
        private void SetNumber()
        {
            if (_selected == null) { _status.Text = "Select a yo-kai first."; return; }
            if (!int.TryParse(_num.Text.Trim(), out int n)) { _status.Text = "Enter a whole number."; return; }
            int old = _selected.Medal ?? 0;
            _selected.Medal = n;
            Resort();
            Rebuild();
            _status.Text = $"{_selected.DisplayName}: medal {old} → {n}. Save to write it. (Only this yo-kai changed.)";
        }

        /// <summary>Swap the selected yo-kai's number with its neighbour in the ordering (affects 2 yo-kai only).</summary>
        private void Swap(int dir)
        {
            if (_selected == null) return;
            int i = _order.IndexOf(_selected), j = i + dir;
            if (i < 0 || j < 0 || j >= _order.Count) return;
            var other = _order[j];
            int? a = _selected.Medal, b = other.Medal;
            _selected.Medal = b; other.Medal = a;
            Resort();
            Rebuild();
            _num.Text = _selected.Medal?.ToString() ?? "";
            _status.Text = $"Swapped numbers of {_selected.DisplayName} and {other.DisplayName} (2 yo-kai). Save to write it.";
        }

        /// <summary>Recovery: copy every yo-kai's ORIGINAL medal number back from the vanilla reference chara_param
        /// (undoes an accidental mass change). Does not save on its own.</summary>
        private void RestoreFromVanilla()
        {
            string refParam = FindVanillaParam();
            if (refParam == null) { DarkMessage.Show("Vanilla chara_param not found in the reference — cannot restore.", "Restore"); return; }
            if (DarkMessage.Show("Copy every yo-kai's ORIGINAL medal number back from the vanilla reference?\n\n" +
                    "Use this to undo an accidental mass renumber. Your own custom numbers (if any) are replaced by the vanilla ones. " +
                    "Then Save to write it.", "Restore from vanilla", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            try
            {
                var f = T2bReader.ReadFile(refParam);
                var s = YokaiSchema.Yw3;
                var map = new Dictionary<int, int>();
                foreach (var e in f.Records(s.ParamRecord))
                {
                    int ph = e.GetInt(s.ParamHashIndex) ?? 0;
                    var m = e.GetInt(s.MedaliumOffsetIndex);
                    if (m.HasValue && !map.ContainsKey(ph)) map[ph] = m.Value;
                }
                int n = 0;
                foreach (var y in _db.Yokai)
                    if (map.TryGetValue(y.ParamHash, out int m)) { y.Medal = m; n++; }
                Resort();
                Select(_selected != null && _order.Contains(_selected) ? _selected : _order.FirstOrDefault());
                _status.Text = $"Restored {n} vanilla medal numbers (in memory). Save to write them to the mod.";
                DarkMessage.Show($"Restored {n} yo-kai's medal numbers from vanilla.\n\nReview, then Save to write it into the mod.", "Restore from vanilla", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Restore from vanilla", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private string FindVanillaParam()
        {
            string root = _db.ReferenceFolder;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            try
            {
                return Directory.EnumerateFiles(root, "chara_param*.cfg.bin", SearchOption.AllDirectories)
                    .Where(p => { string nm = Path.GetFileName(p); return nm.StartsWith("chara_param", StringComparison.OrdinalIgnoreCase) && nm.IndexOf("hackslash", StringComparison.OrdinalIgnoreCase) < 0; })
                    .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            catch { return null; }
        }

        private void Save()
        {
            try
            {
                _db.SaveAll();
                _status.Text = "Medal numbers saved to the mod.";
                DarkMessage.Show("Saved (medal numbers written to chara_param).", "Medallium", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Medallium", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private BitmapSource MedalIcon(YokaiInfo y)
        {
            string path = y?.MedalIconFile;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            if (_iconCache.TryGetValue(path, out var cached)) return cached;
            BitmapSource bmp = null;
            try
            {
                var img = Imgc.Decode(File.ReadAllBytes(path));
                var wb = new WriteableBitmap(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, img.Width, img.Height), img.Bgra, img.Width * 4, 0);
                wb.Freeze();
                bmp = wb;
            }
            catch { }
            _iconCache[path] = bmp;
            return bmp;
        }

        private Button Btn(string text, Action onClick, double left = 0)
        {
            var b = new Button { Content = text, MinWidth = 80, MinHeight = 28, Margin = new Thickness(left, 0, 4, 0), Padding = new Thickness(8, 2, 8, 2) };
            b.Click += (s, e) => onClick();
            return b;
        }
    }
}
