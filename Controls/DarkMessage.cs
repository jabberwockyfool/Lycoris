using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Lycoris
{
    /// <summary>
    /// Drop-in dark replacement for MessageBox.Show (a themed WPF window). Same signatures/return so call
    /// sites only change the class name. Native MessageBox is OS-drawn and ignores the app theme.
    /// </summary>
    public static class DarkMessage
    {
        public static MessageBoxResult Show(string message) =>
            Show(message, "Lycoris", MessageBoxButton.OK, MessageBoxImage.None);
        public static MessageBoxResult Show(string message, string title) =>
            Show(message, title, MessageBoxButton.OK, MessageBoxImage.None);
        public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons) =>
            Show(message, title, buttons, MessageBoxImage.None);

        public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
        {
            var result = MessageBoxResult.None;
            var win = new Window
            {
                Title = string.IsNullOrEmpty(title) ? "Lycoris" : title,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = Theme.WindowBg,
                MaxWidth = 560,
                ShowInTaskbar = false,
            };
            if (Application.Current != null)
                foreach (Window w in Application.Current.Windows)
                    if (w.IsActive) { win.Owner = w; break; }

            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 8, 14, 14) };
            DockPanel.SetDock(bar, Dock.Bottom);
            void Add(string text, MessageBoxResult r, bool def = false, bool cancel = false)
            {
                var b = new Button { Content = text, MinWidth = 86, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), IsDefault = def, IsCancel = cancel };
                b.Click += (s, e) => { result = r; win.DialogResult = true; };
                bar.Children.Add(b);
            }
            switch (buttons)
            {
                case MessageBoxButton.OKCancel: Add("OK", MessageBoxResult.OK, true); Add("Cancel", MessageBoxResult.Cancel, false, true); break;
                case MessageBoxButton.YesNo: Add("Yes", MessageBoxResult.Yes, true); Add("No", MessageBoxResult.No, false, true); break;
                case MessageBoxButton.YesNoCancel: Add("Yes", MessageBoxResult.Yes, true); Add("No", MessageBoxResult.No); Add("Cancel", MessageBoxResult.Cancel, false, true); break;
                default: Add("OK", MessageBoxResult.OK, true, true); break;
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 18, 18, 6) };
            string glyph = null; Brush gcol = Theme.Accent;
            switch (icon)
            {
                case MessageBoxImage.Error: glyph = "✖"; gcol = Theme.Error; break;
                case MessageBoxImage.Warning: glyph = "⚠"; gcol = Warn; break;
                case MessageBoxImage.Question: glyph = "?"; gcol = Theme.Accent; break;
                case MessageBoxImage.Information: glyph = "ℹ"; gcol = Theme.Accent; break;
            }
            if (glyph != null)
                row.Children.Add(new TextBlock { Text = glyph, FontSize = 26, Foreground = gcol, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top });
            row.Children.Add(new TextBlock { Text = message, Foreground = Theme.Fg, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, VerticalAlignment = VerticalAlignment.Center });

            var root = new DockPanel();
            root.Children.Add(bar);
            root.Children.Add(row);
            win.Content = root;

            win.ShowDialog();
            return result;
        }

        private static readonly Brush Warn = Make(0xE5, 0xC0, 0x7B);
        private static Brush Make(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
    }
}
