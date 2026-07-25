using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lycoris.Formats;

namespace Lycoris
{
    /// <summary>
    /// Prompts for an old charabase ID and a new one, then reports the new value. Each field accepts either a
    /// hex id (0x…) or a plain name that is converted with CRC32 — so you can rename a base by typing a model
    /// name. The caller applies the replacement across charaparam / charabase / charascale.
    /// </summary>
    internal sealed class ChangeBaseIdDialog : Window
    {
        private readonly TextBox _old = new TextBox { Width = 200, FontFamily = new FontFamily("Consolas") };
        private readonly TextBox _new = new TextBox { Width = 200 };
        private readonly TextBlock _oldPrev = new TextBlock { Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _newPrev = new TextBlock { Foreground = Theme.FgMuted, VerticalAlignment = VerticalAlignment.Center };

        public int OldId { get; private set; }
        public int NewId { get; private set; }

        public ChangeBaseIdDialog(Window owner, int currentBaseId)
        {
            Owner = owner;
            Title = "Change charabase ID";
            Width = 460; Height = 280;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            _old.Text = $"0x{unchecked((uint)currentBaseId):X8}";

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
                Text = "Replaces every instance of the old charabase ID — in charaparam, charabase and charascale — " +
                       "with the new one. Each field can be a hex id (0x…) or a name that is converted with CRC32."
            });
            root.Children.Add(Row("Old base ID / name", _old, _oldPrev));
            root.Children.Add(Row("New base ID / name", _new, _newPrev));
            _old.TextChanged += (s, e) => _oldPrev.Text = Preview(_old.Text);
            _new.TextChanged += (s, e) => _newPrev.Text = Preview(_new.Text);
            _oldPrev.Text = Preview(_old.Text);

            var ok = new Button { Content = "Replace all", IsDefault = true, Width = 100, Margin = new Thickness(0, 14, 6, 0) };
            ok.Click += (s, e) => Accept();
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Margin = new Thickness(0, 14, 0, 0) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            root.Children.Add(btns);
            Content = root;
        }

        private static FrameworkElement Row(string label, FrameworkElement field, FrameworkElement preview)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            sp.Children.Add(new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            sp.Children.Add(field);
            sp.Children.Add(new TextBlock { Text = "  = ", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            sp.Children.Add(preview);
            return sp;
        }

        private static string Preview(string s) =>
            string.IsNullOrWhiteSpace(s) ? "—" : $"0x{unchecked((uint)Resolve(s)):X8}";

        private static int Resolve(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("0x") || s.StartsWith("0X"))
            {
                if (uint.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint u))
                    return unchecked((int)u);
            }
            return unchecked((int)Crc32.Standard(Encoding.UTF8.GetBytes(s)));
        }

        private void Accept()
        {
            if (string.IsNullOrWhiteSpace(_old.Text)) { DarkMessage.Show("Enter the old base ID or name.", "Change base ID"); return; }
            if (string.IsNullOrWhiteSpace(_new.Text)) { DarkMessage.Show("Enter the new base ID or name.", "Change base ID"); return; }
            OldId = Resolve(_old.Text);
            NewId = Resolve(_new.Text);
            if (OldId == NewId) { DarkMessage.Show("The new ID is the same as the old one.", "Change base ID"); return; }
            DialogResult = true;
        }
    }
}
