using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Lycoris.Formats;
using Lycoris.Npc;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Mirapo / warp editor: browse the warp destinations (warp_config.cfg.bin), edit each one's spawn
    /// coordinates + rotation, add a warp to any map (id → CRC32 hashes + blob are derived automatically) and
    /// import its preview image (warp_&lt;mapid&gt;.xi) from a PNG. Saves into the mod's romfs.
    /// </summary>
    public sealed class WarpEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private WarpSet _ws;
        private WarpEntry _sel;
        private string _savePath;          // <mod>/…/map/<same filename as loaded>
        private bool _suppress;

        private readonly ListBox _list = new ListBox();
        private readonly TextBox _search = new TextBox { Margin = new Thickness(0, 0, 0, 4) };
        private readonly TextBox _x = Num(), _y = Num(), _z = Num(), _rot = Num();
        private readonly TextBox _custName = new TextBox { Width = 220, FontFamily = new FontFamily("Consolas") };
        private readonly TextBlock _dest = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _hashes = new TextBlock { Foreground = Theme.FgMuted, FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap };
        private readonly Image _preview = new Image { Stretch = Stretch.Uniform, MaxHeight = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };
        private readonly StackPanel _detail = new StackPanel { Margin = new Thickness(8) };

        public WarpEditorWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            Owner = owner;
            Title = "Lycoris — Warp / Mirapo Editor";
            Width = 860; Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(Btn("Save mod", Save, 0));
            DockPanel.SetDock(toolbar, Dock.Top);

            var left = new DockPanel { Width = 300, Margin = new Thickness(6) };
            _search.TextChanged += (s, e) => _view?.Refresh();
            DockPanel.SetDock(_search, Dock.Top);
            var listBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            listBtns.Children.Add(Btn("＋ Add warp…", AddWarp, 0));
            listBtns.Children.Add(Btn("Delete", DeleteWarp));
            DockPanel.SetDock(listBtns, Dock.Bottom);
            _list.DisplayMemberPath = "Display";
            _list.SelectionChanged += (s, e) => Show(_list.SelectedItem as WarpEntry);
            left.Children.Add(_search);
            left.Children.Add(listBtns);
            left.Children.Add(_list);
            DockPanel.SetDock(left, Dock.Left);

            BuildDetail();

            DockPanel.SetDock(_status, Dock.Bottom);
            var root = new DockPanel();
            root.Children.Add(toolbar);
            root.Children.Add(_status);
            root.Children.Add(left);
            root.Children.Add(new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;

            LoadConfig();
        }

        private ICollectionView _view;

        private void BuildDetail()
        {
            _detail.Children.Add(new TextBlock { Text = "Selected warp", FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
            _detail.Children.Add(_dest);
            _detail.Children.Add(_hashes);

            _detail.Children.Add(Row("Custom name", _custName));
            _detail.Children.Add(new TextBlock { Foreground = Theme.FgMuted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(110, 0, 0, 6), Text = "The specific warp-point name shown in the menu (field[2] → system_text), e.g. \"Northbeech - City Hall\". Empty = use the map name." });
            _detail.Children.Add(Row("Spawn X", _x));
            _detail.Children.Add(Row("Spawn Y (2D)", _y));
            _detail.Children.Add(Row("Spawn Z (height)", _z));
            _detail.Children.Add(Row("Rotation (°)", _rot));
            foreach (var tb in new[] { _x, _y, _z, _rot }) tb.LostFocus += (s, e) => Commit();
            _custName.LostFocus += (s, e) => CommitName();

            _detail.Children.Add(new TextBlock
            {
                Foreground = Theme.FgMuted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
                Text = "Spawn = where the player appears when warping here. SAME axes as the NPC editor: X, Y (2D " +
                       "horizontal), Z (height). warp_config stores [X, height, depth]; the editor handles the Y/Z swap."
            });

            var previewBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            previewBar.Children.Add(new TextBlock { Text = "Preview image", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            previewBar.Children.Add(Btn("Import PNG…", ImportPreview));
            _detail.Children.Add(previewBar);
            _detail.Children.Add(_preview);

            var mirBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            mirBar.Children.Add(Btn("🪞 Place Mirapo NPC in map…", PlaceMirapo, 0));
            _detail.Children.Add(mirBar);
            _detail.Children.Add(new TextBlock
            {
                Foreground = Theme.FgMuted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
                Text = "Generates the in-map Mirapo: a type-9 warp NPC (id = CRC32(\"warp_\"+mapid)) + a y130000 mirror " +
                       "in this map's npc_set/npc.pck, merged into the mod. Reuses the NPC compiler. The map's npc.pck " +
                       "must exist in the mod or reference. (The warp_config entry above is what makes it a destination.)"
            });

            _detail.IsEnabled = false;
        }

        private void LoadConfig()
        {
            // Prefer the versioned shipping table (warp_config_0.01b.cfg.bin), in the mod then the reference.
            string modCfg = IncBase != null ? WarpSet.FindConfig(Path.Combine(IncBase, "data", "res", "map")) : null;
            string refCfg = RefCandidates("data", "res", "map").Select(WarpSet.FindConfig).FirstOrDefault(p => p != null);
            string loadPath = modCfg ?? refCfg;
            if (loadPath == null) { _status.Text = "Could not find warp_config*.cfg.bin in the mod or reference."; return; }
            _savePath = IncBase != null ? Path.Combine(IncBase, "data", "res", "map", Path.GetFileName(loadPath)) : null;

            string sysText = FirstExisting(
                new[] { IncBase != null ? Path.Combine(IncBase, "data", "res", "text", "system_text_en.cfg.bin") : null }
                .Concat(RefCandidates("data", "res", "text", "system_text_en.cfg.bin")));

            try { _ws = WarpSet.Load(loadPath, sysText, WarpImgDirs().Concat(MapDirs())); }
            catch (Exception ex) { _status.Text = "Could not read warp_config: " + ex.Message; return; }

            _view = CollectionViewSource.GetDefaultView(_ws.Warps);
            _view.Filter = o =>
            {
                string q = _search.Text?.Trim();
                if (string.IsNullOrEmpty(q)) return true;
                return ((WarpEntry)o).Display.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            };
            _list.ItemsSource = _view;
            bool fromMod = loadPath == modCfg;
            _status.Text = $"{_ws.Warps.Count} warps loaded from {(fromMod ? "the mod" : "the reference")}. " + (IncBase == null ? "Open a mod to save." : "");
        }

        private void Show(WarpEntry w)
        {
            _sel = w;
            _suppress = true;
            if (w == null) { _detail.IsEnabled = false; _dest.Text = ""; _hashes.Text = ""; _preview.Source = null; }
            else
            {
                _detail.IsEnabled = true;
                _dest.Text = "Destination: " + (w.MapName ?? "(unnamed)") + (w.MapId != null ? "  —  " + w.MapId : "");
                _hashes.Text = $"map hash (field1) = {w.HashHex}   |   warp id (field0) = 0x{unchecked((uint)w.Field0):X8}";
                // NPC-editor convention: field "Y (2D)" = the horizontal depth (WarpEntry.Z = field[5]),
                // field "Z (height)" = the height (WarpEntry.Y = field[4]). warp_config keeps [X, height, depth].
                _x.Text = Fmt(w.X); _y.Text = Fmt(w.Z); _z.Text = Fmt(w.Y); _rot.Text = w.Rotation.ToString();
                _custName.Text = w.CustomName ?? "";
                _preview.Source = LoadPreview(w.MapId);
            }
            _suppress = false;
        }

        private void Commit()
        {
            if (_suppress || _sel == null) return;
            // "Y (2D)" → depth (field[5] = _sel.Z), "Z (height)" → height (field[4] = _sel.Y).
            if (double.TryParse(_x.Text?.Trim(), out double x)) _sel.X = x;
            if (double.TryParse(_y.Text?.Trim(), out double y)) _sel.Z = y;
            if (double.TryParse(_z.Text?.Trim(), out double z)) _sel.Y = z;
            if (int.TryParse(_rot.Text?.Trim(), out int r)) _sel.Rotation = r;
            _list.Items.Refresh();
        }

        private void CommitName()
        {
            if (_suppress || _sel == null) return;
            try { _ws.SetCustomName(_sel, _custName.Text); _list.Items.Refresh(); }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Custom name", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void AddWarp()
        {
            if (_ws == null) { DarkMessage.Show("No warp_config loaded.", "Add warp"); return; }
            string mapId = TextPrompt.Ask(this, "Add warp",
                "Destination map id (e.g. t102g00). For a SECOND warp point in a map that already has one, add a " +
                "suffix: t102g00_02, t102g00_03… (the base map still loads; only the warp point differs).", "");
            if (mapId == null) return;
            mapId = mapId.Trim();
            if (mapId.Length == 0) { DarkMessage.Show("Enter a map id.", "Add warp"); return; }
            try
            {
                var w = _ws.AddWarp(mapId, 0, 0, 0, 180);   // mirapo faces the player by default
                _view.Refresh();
                _list.SelectedItem = w;
                _list.ScrollIntoView(w);
                string named = w.MapName != null ? $" — « {w.MapName} »" : " (no name in system_text — add one there for the warp prompt)";
                _status.Text = $"Added warp to {mapId}{named}. Set the spawn coordinates, then Save.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Add warp", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void DeleteWarp()
        {
            if (_ws == null || _sel == null) { DarkMessage.Show("Select a warp to delete.", "Delete warp"); return; }
            if (DarkMessage.Show($"Delete the warp to {_sel.Display}?", "Delete warp", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            _ws.RemoveWarp(_sel);
            _view.Refresh();
            _status.Text = "Warp removed. Save to apply.";
        }

        private void ImportPreview()
        {
            if (_sel == null || string.IsNullOrEmpty(_sel.MapId))
            { DarkMessage.Show("Select a warp with a known map id first.", "Preview"); return; }
            if (IncBase == null) { DarkMessage.Show("Open a mod folder first.", "Preview"); return; }
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG images|*.png", Title = "Warp preview — PNG" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var img = LoadPngExisting(dlg.FileName, out int w, out int h);
                int pw = w / 8 * 8, ph = h / 8 * 8;                 // XI encoder needs multiples of 8 (crop)
                if (pw <= 0 || ph <= 0) { DarkMessage.Show("Image too small.", "Preview"); return; }
                byte[] bgra = (pw == w && ph == h) ? img : Crop(img, w, pw, ph);
                string dir = Path.Combine(IncBase, "data", "menu", "warp_img");
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, "warp_" + _sel.MapId + ".xi");
                File.WriteAllBytes(target, Imgc.EncodeXi(bgra, pw, ph));
                _preview.Source = LoadPreview(_sel.MapId);
                _status.Text = $"Preview saved: {Path.GetFileName(target)}";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Preview import error", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void PlaceMirapo()
        {
            if (_sel == null || string.IsNullOrEmpty(_sel.MapId)) { DarkMessage.Show("Select a warp with a known map id first.", "Mirapo NPC"); return; }
            if (IncBase == null) { DarkMessage.Show("Open a mod folder first — the NPC is written into the mod.", "Mirapo NPC"); return; }
            string mapFolder = _db?.ReferenceFolder ?? _db?.ModFolder;
            if (mapFolder == null) { DarkMessage.Show("No reference/mod folder to read the map's npc.pck from.", "Mirapo NPC"); return; }

            // Same coordinate convention as the daily-fight NPC editor: X, Y (2D horizontal), Z (height).
            // The warp spawn stores [X, height, depth]; present it in that convention (Y=depth, Z=height).
            var c = CoordsDialog.Ask(this, $"Mirapo mirror position in {_sel.MapId}", _sel.X, _sel.Z, _sel.Y, _sel.Rotation, _db);
            if (c == null) return;
            try
            {
                byte[] full = NpcCompiler.MirrorTemplate(), simple = NpcCompiler.MirrorTemplateSimple();
                string mergeMapDir = Path.Combine(IncBase, "data", "res", "map", WarpSet.BaseMapId(_sel.MapId));
                string outRoot = Path.Combine(Path.GetTempPath(), "Lycoris_warp");
                // flag_config: register warp_<mapid> so its global bit flag is recognised/persistent.
                string flagDst = Path.Combine(IncBase, "data", "res", "sys", "flag_config_0.01r.cfg.bin");
                string flagSrc = File.Exists(flagDst) ? flagDst
                    : FirstExisting(new[] { IncBase != null ? Path.Combine(IncBase, "data", "res", "sys", "flag_config_0.01r.cfg.bin") : null }
                        .Concat(RefCandidates("data", "res", "sys", "flag_config_0.01r.cfg.bin")));
                // regionFlag 0 → automatic (copy the map's existing mirapo flag, else Springdale fallback).
                var res = NpcCompiler.CompileWarpNpc(_sel.MapId, mapFolder, outRoot, mergeMapDir,
                    c.Value.x, c.Value.y, c.Value.z, (int)c.Value.rot, full, simple, flagSrc, flagDst, 0, c.Value.model);
                _preview.Source = LoadPreview(_sel.MapId);
                _status.Text = $"Mirapo NPC placed in {_sel.MapId} ({res.NpcIdHex}) — merged into the mod.";
                DarkMessage.Show($"Mirapo warp NPC added to {_sel.MapId}:\n" +
                    $"• type-9 NPC {res.NpcIdHex} (base y130000)\n• mirror npcbin at ({c.Value.x}, {c.Value.y}, {c.Value.z})\n\n" +
                    $"Merged into:\n{mergeMapDir}", "Mirapo NPC", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Mirapo NPC", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void Save()
        {
            var focused = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            focused?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            if (_ws == null) return;
            if (IncBase == null || _savePath == null) { DarkMessage.Show("Open a mod folder first — the warp table is written into the mod.", "Save"); return; }
            try
            {
                string outPath = _savePath;
                string sysOut = Path.Combine(IncBase, "data", "res", "text", "system_text_en.cfg.bin");
                _ws.Save(outPath, sysOut);
                _status.Text = $"Saved {_ws.Warps.Count} warps to the mod" + (_ws.SystemTextDirty ? " (+ custom names in system_text)." : ".");
                DarkMessage.Show($"Warp table saved:\n{outPath}" + (_ws.SystemTextDirty ? $"\nCustom names:\n{sysOut}" : ""), "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Save warps", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private ImageSource LoadPreview(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return null;
            foreach (var dir in WarpImgDirs())
            {
                string p = Path.Combine(dir, "warp_" + mapId + ".xi");
                if (!File.Exists(p)) continue;
                try
                {
                    var img = Imgc.Decode(File.ReadAllBytes(p));
                    var bmp = System.Windows.Media.Imaging.BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Bgra, img.Width * 4);
                    bmp.Freeze();
                    return bmp;
                }
                catch { }
            }
            return null;
        }

        // ---- paths ----
        internal string IncBase
        {
            get
            {
                if (string.IsNullOrEmpty(_db?.ModFolder)) return null;
                string inc = Path.Combine(_db.ModFolder, "include");
                return Directory.Exists(inc) ? inc : _db.ModFolder;
            }
        }

        private IEnumerable<string> RefCandidates(params string[] tail)
        {
            string root = _db?.ReferenceFolder;
            if (string.IsNullOrEmpty(root)) yield break;
            yield return Path.Combine(new[] { root }.Concat(tail).ToArray());
            yield return Path.Combine(new[] { root, "include" }.Concat(tail).ToArray());
        }

        private IEnumerable<string> WarpImgDirs()
        {
            if (IncBase != null) yield return Path.Combine(IncBase, "data", "menu", "warp_img");
            foreach (var p in RefCandidates("data", "menu", "warp_img")) yield return p;
        }

        private IEnumerable<string> MapDirs()
        {
            if (IncBase != null) yield return Path.Combine(IncBase, "data", "res", "map");
            foreach (var p in RefCandidates("data", "res", "map")) yield return p;
        }

        private static string FirstExisting(IEnumerable<string> paths) => paths.FirstOrDefault(p => p != null && File.Exists(p));

        // ---- ui helpers ----
        private static TextBox Num() => new TextBox { Width = 140, FontFamily = new FontFamily("Consolas") };
        private static string Fmt(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static UIElement Row(string label, TextBox tb)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            sp.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            sp.Children.Add(tb);
            return sp;
        }
        private Button Btn(string text, Action onClick, double leftMargin = 6)
        {
            var b = new Button { Content = text, Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(leftMargin, 0, 0, 0) };
            b.Click += (s, e) => onClick();
            return b;
        }

        private static byte[] LoadPngExisting(string path, out int w, out int h)
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit(); bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bmp.UriSource = new Uri(path); bmp.EndInit();
            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            w = conv.PixelWidth; h = conv.PixelHeight;
            var buf = new byte[w * h * 4];
            conv.CopyPixels(buf, w * 4, 0);
            return buf;
        }
        private static byte[] Crop(byte[] src, int srcW, int w, int h)
        {
            var dst = new byte[w * h * 4];
            for (int y = 0; y < h; y++) Array.Copy(src, y * srcW * 4, dst, y * w * 4, w * 4);
            return dst;
        }
    }

    /// <summary>Modal asking for the Mirapo mirror's X/Y/Z/rotation + the mirror yo-kai model (picked from the list,
    /// Mirapo y130000 by default). The region flag is handled automatically (Springdale fallback).</summary>
    internal static class CoordsDialog
    {
        public static (double x, double y, double z, double rot, string model)? Ask(Window owner, string title, double x, double y, double z, double rot, Lycoris.Yokai.YokaiDatabase db)
        {
            var win = new Window { Owner = owner, Title = title, Width = 400, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            TextBox Fx(double v) => new TextBox { Text = v.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 160, FontFamily = new FontFamily("Consolas") };
            var bx = Fx(x); var by = Fx(y); var bz = Fx(z); var br = Fx(rot);
            StackPanel R(string l, UIElement tb)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                sp.Children.Add(new TextBlock { Text = l, Width = 120, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
                sp.Children.Add(tb); return sp;
            }
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = "Where the mirror stands in the map (you talk to it to warp out). Same axes as the NPC editor.", Foreground = Theme.FgMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            panel.Children.Add(R("Mirror X", bx));
            panel.Children.Add(R("Mirror Y (2D)", by));
            panel.Children.Add(R("Mirror Z (height)", bz));
            panel.Children.Add(R("Rotation (°)", br));

            // Mirror yo-kai model — picked from the yo-kai list; default Mirapo (y130000).
            string model = "y130000";
            var modelLabel = new TextBlock { Text = "Mirapo (y130000)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var pickBtn = new Button { Content = "yo-kai…", Padding = new Thickness(9, 4, 9, 4) };
            pickBtn.Click += (s, e) =>
            {
                var pd = new PickYokaiDialog(win, db) { Owner = win };
                if (pd.ShowDialog() == true && pd.Picked != null && !string.IsNullOrEmpty(pd.Picked.ModelName))
                {
                    model = pd.Picked.ModelName;
                    modelLabel.Text = (pd.Picked.Name != null ? pd.Picked.Name + " (" + model + ")" : model);
                }
            };
            var modelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            modelRow.Children.Add(new TextBlock { Text = "Mirror yo-kai", Width = 120, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted });
            modelRow.Children.Add(pickBtn);
            modelRow.Children.Add(modelLabel);
            panel.Children.Add(modelRow);
            panel.Children.Add(new TextBlock { Foreground = Theme.FgMuted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(120, 0, 0, 8), Text = "The mirror model + its dialogue bustup. Default Mirapo (y130000). Pick e.g. Miradox (y252000). Must share Mirapo's rig/p90 motion (Mirapo evolutions do)." });

            (double x, double y, double z, double rot, string model)? result = null;
            var ok = new Button { Content = "Place", Width = 90, Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
            var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };
            ok.Click += (s, e) =>
            {
                if (double.TryParse(bx.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double rx) &&
                    double.TryParse(by.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double ry) &&
                    double.TryParse(bz.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double rz) &&
                    double.TryParse(br.Text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double rr))
                { result = (rx, ry, rz, rr, model); win.DialogResult = true; }
                else DarkMessage.Show("Enter numeric coordinates.", "Mirapo NPC");
            };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            btns.Children.Add(ok); btns.Children.Add(cancel);
            panel.Children.Add(btns);
            win.Content = panel;
            return win.ShowDialog() == true ? result : null;
        }
    }
}
