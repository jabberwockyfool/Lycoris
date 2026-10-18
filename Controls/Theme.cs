using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lycoris
{
    /// <summary>
    /// Dark-theme brushes for the code-built windows (the XAML controls are themed via App.xaml implicit
    /// styles; these mirror the same palette for brushes set directly in code). All frozen.
    /// </summary>
    public static class Theme
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>
        /// Paint a window's native title bar dark (the OS chrome WPF styles can't reach). Uses the DWM
        /// immersive-dark-mode attribute (20 on Win10 2004+, falls back to 19 on older builds).
        /// </summary>
        public static void ApplyDarkTitleBar(Window w)
        {
            try
            {
                var helper = new WindowInteropHelper(w);
                IntPtr hwnd = helper.Handle != IntPtr.Zero ? helper.Handle : helper.EnsureHandle();
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch { /* pre-Win10 or unsupported: leave default chrome */ }
        }

        public static readonly Brush WindowBg = Frozen(0x1E, 0x1E, 0x1E);
        public static readonly Brush PanelBg = Frozen(0x25, 0x25, 0x26);
        public static readonly Brush FieldBg = Frozen(0x33, 0x33, 0x37);
        public static readonly Brush Fg = Frozen(0xE6, 0xE6, 0xE6);
        public static readonly Brush FgMuted = Frozen(0x9A, 0xA0, 0xA6);
        public static readonly Brush Border = Frozen(0x3F, 0x3F, 0x46);
        public static readonly Brush Accent = Frozen(0xD1, 0x62, 0xA4);
        public static readonly Brush Selection = Frozen(0x5A, 0x3A, 0x50);
        public static readonly Brush Error = Frozen(0xE0, 0x6C, 0x75);

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
