using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Port a yo-kai (with all its records + model/icon assets) from ANOTHER mod into the current one. Pick a
    /// source mod folder, choose a yo-kai from it, and Lycoris merges its chara_param/base/text/desc/scale/
    /// battle/hackslash records into this mod and copies its model files. The mod is reloaded afterwards.
    /// </summary>
    public sealed class PortWindow : Window
    {
        private readonly YokaiDatabase _db;
        private readonly Action _reload;
        private YokaiDatabase _srcDb;
        private string _sourceMod;

        private readonly TextBlock _srcLbl = new TextBlock { Foreground = Theme.Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        private readonly TextBox _search = new TextBox { Margin = new Thickness(0, 0, 0, 4) };
        private readonly ListBox _list = new ListBox { DisplayMemberPath = "DisplayName" };
        private readonly Button _port = new Button { Content = "Port selected →", MinWidth = 130, MinHeight = 30, IsEnabled = false };
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        private ICollectionView _view;

        public PortWindow(Window owner, YokaiDatabase db, Action reload)
        {
            _db = db; _reload = reload;
            Owner = owner;
            Title = "Lycoris — Port yo-kai from another mod";
            Width = 560; Height = 560;
            Background = Theme.WindowBg;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { Margin = new Thickness(14) };

            var top = new StackPanel();
            top.Children.Add(new TextBlock
            {
                Text = "Copy a yo-kai from another mod into this one — its records (chara_param/base/text/desc/scale/" +
                       "battle/hackslash) are merged and its model/icon files are copied. The mod reloads after.",
                Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });
            var srcRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var pick = new Button { Content = "Source mod…", MinWidth = 110, MinHeight = 28 };
            pick.Click += (s, e) => PickSource();
            srcRow.Children.Add(pick);
            srcRow.Children.Add(_srcLbl);
            top.Children.Add(srcRow);
            _search.TextChanged += (s, e) => _view?.Refresh();
            top.Children.Add(_search);
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var bottom = new StackPanel();
            _port.HorizontalAlignment = HorizontalAlignment.Left;
            _port.Margin = new Thickness(0, 8, 0, 0);
            _port.Click += (s, e) => DoPort();
            bottom.Children.Add(_port);
            bottom.Children.Add(_status);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(bottom);

            _list.SelectionChanged += (s, e) => _port.IsEnabled = _list.SelectedItem != null;
            root.Children.Add(_list);

            Content = root;
            _srcLbl.Text = "(no source selected)";
            _status.Text = "Pick the source mod folder to list its yo-kai.";
        }

        private void PickSource()
        {
            string folder = FolderPicker.Pick("Source mod folder (the mod to port a yo-kai FROM)",
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if (folder == null) return;
            if (string.Equals(folder.TrimEnd('\\', '/'), _db.ModFolder?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            { DarkMessage.Show("The source and target mods are the same folder.", "Port yo-kai"); return; }

            _status.Text = "Loading source mod…";
            try
            {
                _srcDb = new YokaiDatabase(YokaiSchema.Yw3);
                _srcDb.LoadFolder(folder, _db.ReferenceFolder);
                _sourceMod = folder;
                _srcLbl.Text = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));

                _view = CollectionViewSource.GetDefaultView(_srcDb.Yokai.OrderBy(y => y.DisplayName, StringComparer.OrdinalIgnoreCase).ToList());
                _view.Filter = o =>
                {
                    string q = _search.Text?.Trim();
                    if (string.IsNullOrEmpty(q)) return true;
                    var y = (YokaiInfo)o;
                    return (y.DisplayName != null && y.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                           || (y.ModelName != null && y.ModelName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                };
                _list.ItemsSource = _view;
                _status.Text = $"{_srcDb.Yokai.Count} yo-kai in the source mod. Pick one, then Port.";
            }
            catch (Exception ex)
            {
                _srcDb = null; _sourceMod = null; _list.ItemsSource = null;
                _status.Text = "Could not load the source mod: " + ex.Message;
                DarkMessage.Show(ex.Message, "Port yo-kai", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DoPort()
        {
            var y = _list.SelectedItem as YokaiInfo;
            if (y == null || _sourceMod == null) return;

            if (DarkMessage.Show(
                    $"Port « {y.DisplayName} » (model {y.ModelName}, param 0x{unchecked((uint)y.ParamHash):X8}) into this mod?\n\n" +
                    "Its records are merged into your config files and its model/icon files are copied.",
                    "Port yo-kai", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

            try
            {
                var r = YokaiPort.Apply(_db, _sourceMod, y.ParamHash, y.BaseHash, y.NameHash, y.DescriptionHash, y.ModelName);
                _reload?.Invoke();
                _status.Text = $"Ported {y.DisplayName}: {r.Files.Count} config file(s), {r.Assets.Count} asset(s).";
                DarkMessage.Show(
                    $"« {y.DisplayName} » ported.\n\n" +
                    $"Config files merged ({r.Files.Count}):\n{(r.Files.Count > 0 ? string.Join("\n", r.Files) : "(none)")}\n\n" +
                    $"Assets copied ({r.Assets.Count}):\n{(r.Assets.Count > 0 ? string.Join("\n", r.Assets.Take(20)) + (r.Assets.Count > 20 ? "\n…" : "") : "(none — model files not found by name; copy them manually if needed)")}",
                    "Port yo-kai", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Port yo-kai", MessageBoxButton.OK, MessageBoxImage.Error); _status.Text = "Failed: " + ex.Message; }
        }
    }
}
