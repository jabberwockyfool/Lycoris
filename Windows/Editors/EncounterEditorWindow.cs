using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lycoris.Formats;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Wild-encounter editor for a map: lists the ENCOUNT_TABLE entries ("Battles"), and for the selected one
    /// shows its up to 6 yo-kai slots (icon + name + level) with a per-slot toggle (off = offset -1 / empty).
    /// Yo-kai are pickable from the loaded roster. Saves by repacking the encounter file into the map's .pck.
    /// </summary>
    public sealed class EncounterEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private readonly EncounterSet _set;
        private readonly string _outPckPath;
        private readonly ListBox _list = new ListBox();
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };

        private readonly CheckBox[] _tog = new CheckBox[6];
        private readonly Image[] _icon = new Image[6];
        private readonly TextBlock[] _name = new TextBlock[6];
        private readonly TextBox[] _level = new TextBox[6];
        private readonly Button[] _change = new Button[6];
        private EncTable _table;
        private bool _suppress;

        public EncounterEditorWindow(Window owner, YokaiDatabase db, EncounterSet set, string outPckPath, string mapLabel)
        {
            _db = db; _set = set; _outPckPath = outPckPath;
            Owner = owner;
            Title = "Lycoris — Wild Encounters: " + mapLabel;
            Width = 720; Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var save = new Button { Content = "Save mod", Padding = new Thickness(10, 4, 10, 4) };
            save.Click += (s, e) => Save();
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(save);
            toolbar.Children.Add(new TextBlock { Text = $"  {_set.Tables.Count} battles, {_set.Charas.Count} yo-kai", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            DockPanel.SetDock(toolbar, Dock.Top);

            var left = new DockPanel { Width = 210, Margin = new Thickness(6) };
            _list.DisplayMemberPath = "Label";
            _list.ItemsSource = _set.Tables;
            _list.SelectionChanged += (s, e) => ShowTable(_list.SelectedItem as EncTable);
            left.Children.Add(_list);
            DockPanel.SetDock(left, Dock.Left);

            var right = new StackPanel { Margin = new Thickness(8) };
            right.Children.Add(new TextBlock { Text = "Yo-kai for this encounter (6 slots):", Foreground = Theme.FgMuted, Margin = new Thickness(0, 0, 0, 6) });
            for (int i = 0; i < 6; i++) right.Children.Add(BuildSlot(i));

            DockPanel.SetDock(_status, Dock.Bottom);
            var root = new DockPanel();
            root.Children.Add(toolbar);
            root.Children.Add(_status);
            root.Children.Add(left);
            root.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;

            if (_set.Tables.Count > 0) _list.SelectedIndex = 0;
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
            var border = new Border { Width = 46, Height = 46, BorderBrush = Theme.Border, BorderThickness = new Thickness(1), Background = Theme.FieldBg, Child = _icon[i] };
            row.Children.Add(border);

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

        private void Save()
        {
            var f = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            try
            {
                Encounters.Save(_set, _outPckPath);
                _status.Text = "Saved — encounters written to " + _outPckPath;
                DarkMessage.Show("Encounters saved to the mod:\n" + _outPckPath, "Save", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Save encounters", MessageBoxButton.OK, MessageBoxImage.Error); }
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
