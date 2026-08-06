using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Boss editor: import a boss from a YW2 dump into the loaded YW3 mod. Scans the YW2 dump for bosses
    /// (chara_param entries with BOSS_PARTS), previews a chosen boss's stats + attacks, then recreates it in
    /// YW3 — yo-kai (stats/model/Boss/Unrank), the YW2 mtn2 model, its attacks as skills + battle_commands
    /// (playing the model's real animation clips), the BOSS_PARTS command list and a common_enc encounter.
    /// </summary>
    public sealed class BossEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private readonly YokaiSchema _schema = YokaiSchema.Yw3;
        private readonly TextBox _yw2Path = new TextBox { Width = 380, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly ListBox _list = new ListBox { Width = 240 };
        private readonly TextBox _preview = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), Background = Theme.FieldBg, Foreground = Theme.Fg };
        private readonly Button _portBtn = new Button { Content = "Port this boss into the mod", Padding = new Thickness(10, 5, 10, 5), IsEnabled = false };
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };

        public BossEditorWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            Owner = owner;
            Title = "Lycoris — Boss Editor (YW2 → YW3 port)";
            Width = 860; Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // Top: YW2 dump folder + scan
            _yw2Path.Text = System.IO.Directory.Exists(@"E:\YW2 DUMP") ? @"E:\YW2 DUMP" : "";
            var browse = new Button { Content = "…", Width = 30, Margin = new Thickness(4, 0, 0, 0) };
            browse.Click += (s, e) => { var f = FolderPicker.Pick("YW2 RomFS dump (folder containing data\\res\\…)", new System.Windows.Interop.WindowInteropHelper(this).Handle); if (f != null) _yw2Path.Text = f; };
            var scan = new Button { Content = "Scan bosses", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0) };
            scan.Click += (s, e) => Scan();
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            top.Children.Add(new TextBlock { Text = "YW2 dump:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            top.Children.Add(_yw2Path); top.Children.Add(browse); top.Children.Add(scan);
            DockPanel.SetDock(top, Dock.Top);

            // Left: boss list
            _list.DisplayMemberPath = "Name";
            _list.Margin = new Thickness(6);
            _list.SelectionChanged += (s, e) => Preview();
            DockPanel.SetDock(_list, Dock.Left);

            // Bottom: port button + status
            var bottom = new DockPanel { Margin = new Thickness(6) };
            DockPanel.SetDock(_portBtn, Dock.Right);
            _portBtn.Click += (s, e) => Port();
            bottom.Children.Add(_portBtn);
            bottom.Children.Add(_status);
            DockPanel.SetDock(bottom, Dock.Bottom);

            // Center: preview
            _preview.Margin = new Thickness(6);

            var root = new DockPanel();
            root.Children.Add(top);
            root.Children.Add(bottom);
            root.Children.Add(_list);
            root.Children.Add(_preview);
            Content = root;

            _status.Text = "Pick your YW2 dump folder and Scan. Then select a boss and port it. Keep Lycoris closed elsewhere.";
        }

        private void Scan()
        {
            _list.ItemsSource = null; _preview.Text = ""; _portBtn.IsEnabled = false;
            string folder = _yw2Path.Text?.Trim();
            if (string.IsNullOrEmpty(folder) || !System.IO.Directory.Exists(folder))
            { DarkMessage.Show("Set a valid YW2 dump folder first.", "Boss Editor"); return; }
            try
            {
                var bosses = BossPort.Scan(folder, _schema);
                _list.ItemsSource = bosses;
                _status.Text = bosses.Count > 0
                    ? $"{bosses.Count} YW2 bosses found. Select one to preview, then port."
                    : "No bosses found — is this a YW2 RomFS dump (data\\res\\character\\chara_param…)?";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Scan bosses", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private Yw2BossInfo _selected;

        private void Preview()
        {
            _selected = null; _portBtn.IsEnabled = false; _preview.Text = "";
            if (!(_list.SelectedItem is Yw2BossInfo b)) return;
            try
            {
                _selected = BossPort.Read(_yw2Path.Text.Trim(), b.ModelId, _schema);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{_selected.Name}   (model {_selected.ModelId})");
                sb.AppendLine($"HP {_selected.Hp}   Str {_selected.Str}   Spr {_selected.Spr}   Def {_selected.Def}   Spd {_selected.Spd}");
                sb.AppendLine($"Money {_selected.Money}   Exp {_selected.Exp}");
                sb.AppendLine();
                sb.AppendLine("Attacks (→ recreated as YW3 skills + commands):");
                string[] type = { "Guard", "Attack", "?", "Technique", "Soultimate", "Inspirit" };
                foreach (var a in _selected.Attacks)
                    sb.AppendLine($"   {a.Name,-24} {(a.Yw3Type < type.Length ? type[a.Yw3Type] : a.Yw3Type.ToString()),-10} power {a.Power}");
                sb.AppendLine();
                sb.AppendLine("Port will: create the yo-kai (Boss tribe, Unrank), copy the YW2 model, make each");
                sb.AppendLine("attack a skill+command with the model's own animation, set BOSS_PARTS + a common_enc");
                sb.AppendLine($"encounter (edy_{_selected.ModelId}_01). Refine skill types/animations afterwards if needed.");
                _preview.Text = sb.ToString();
                _portBtn.IsEnabled = true;
            }
            catch (Exception ex) { _preview.Text = "Error: " + ex.Message; }
        }

        private void Port()
        {
            if (_selected == null) return;
            if (_db.ModFolder == null) { DarkMessage.Show("Load a mod first (open a folder from the launcher).", "Boss Editor"); return; }
            var confirm = DarkMessage.Show(
                $"Port “{_selected.Name}” into the loaded mod?\n\nThis writes chara_param/base/text, skill_config, battle_command, " +
                "battle_chara_param and common_enc, and copies the YW2 model. Make sure no other Lycoris instance is editing this mod.",
                "Port boss", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;
            try
            {
                string report = BossPort.Port(_db, _selected, _schema);
                _preview.Text = report;
                _status.Text = $"Ported {_selected.Name}. See the report; rebuild the RomFS to test.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Port boss", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }
}
