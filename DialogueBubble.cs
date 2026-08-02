using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lycoris.Formats;

namespace Lycoris
{
    /// <summary>
    /// Renders a Yo-kai Watch text bubble exactly like the game / Kuriimu: the real dialog-box image
    /// (blank_top.png) with the text drawn in the game's own XF font (ft_nrm.xf) — both embedded resources.
    /// Control codes are handled as the game does: &lt;tags&gt; skipped, \n newlines, [base/ruby] furigana.
    /// </summary>
    internal static class DialogueBubble
    {
        private static XfFont _font;
        private static byte[] _bg;              // BGRA background (the dialog box)
        private static int _bgW, _bgH;
        private static bool _init, _ok;

        private static void Init()
        {
            if (_init) return;
            _init = true;
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                _font = new XfFont(ReadRes(asm, "ft_nrm.xf"));
                LoadPng(ReadRes(asm, "blank_top.png"), out _bg, out _bgW, out _bgH);
                _ok = true;
            }
            catch { _ok = false; }
        }

        /// <summary>True once the embedded font + bubble loaded (else callers should fall back to plain text).</summary>
        public static bool Available { get { Init(); return _ok; } }

        /// <summary>Render the bubble for one page of text; null if the resources failed to load.</summary>
        public static ImageSource Render(string text)
        {
            Init();
            if (!_ok) return null;
            var dst = (byte[])_bg.Clone();
            DrawText(dst, _bgW, _bgH, text ?? "");
            var bmp = BitmapSource.Create(_bgW, _bgH, 96, 96, PixelFormats.Bgra32, null, dst, _bgW * 4);
            bmp.Freeze();
            return bmp;
        }

        private static void DrawText(byte[] dst, int w, int h, string raw)
        {
            string s = raw.Replace("<PNAME>", "Nate").Replace("\\n", "\n").Replace("\r", "");
            const byte tR = 0, tG = 0, tB = 0;   // Dialog scene: black text at (40,43)
            float x = 40, y = 43;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') { while (i < s.Length && s[i] != '>') i++; continue; } // skip <tag>
                if (c == '\n') { y += 26; x = 40; continue; }
                if (c == '[') { i = DrawFurigana(dst, w, h, s, i, ref x, y); continue; }
                int adv = _font.DrawChar(dst, w, h, c, tR, tG, tB, x, y, false);
                x += adv - 0.85f;
            }
        }

        // [base/ruby]: base in the normal glyph, ruby small and centred above. Returns the index of ']'.
        private static int DrawFurigana(byte[] dst, int w, int h, string s, int start, ref float x, float y)
        {
            int slash = -1, end = -1;
            for (int j = start + 1; j < s.Length; j++)
            {
                if (s[j] == '/' && slash < 0) slash = j;
                else if (s[j] == ']') { end = j; break; }
            }
            if (end < 0) return start;
            string baseTxt = s.Substring(start + 1, (slash < 0 ? end : slash) - start - 1);
            string ruby = slash < 0 ? "" : s.Substring(slash + 1, end - slash - 1);

            float baseW = 0, rubyW = 0;
            foreach (var ch in baseTxt) baseW += _font.CharWidth(ch, false) - 0.85f;
            foreach (var ch in ruby) rubyW += _font.CharWidth(ch, true) - 0.85f;
            float total = Math.Max(baseW, rubyW);

            float bx = x + (total - baseW) / 2f;
            foreach (var ch in baseTxt) { int a = _font.DrawChar(dst, w, h, ch, 0, 0, 0, bx, y, false); bx += a - 0.85f; }
            float rx = x + (total - rubyW) / 2f;
            foreach (var ch in ruby) { int a = _font.DrawChar(dst, w, h, ch, 0, 0, 0, rx, y - 7, true); rx += a - 0.85f; }
            x += total;
            return end;
        }

        private static byte[] ReadRes(Assembly asm, string name)
        {
            using (var st = asm.GetManifestResourceStream("Lycoris.Resources." + name))
            {
                if (st == null) throw new FileNotFoundException("Embedded resource missing: " + name);
                var ms = new MemoryStream();
                st.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static void LoadPng(byte[] png, out byte[] bgra, out int w, out int h)
        {
            var dec = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource src = dec.Frames[0];
            if (src.Format != PixelFormats.Bgra32) src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            w = src.PixelWidth; h = src.PixelHeight;
            bgra = new byte[w * h * 4];
            src.CopyPixels(bgra, w * 4, 0);
        }
    }
}
