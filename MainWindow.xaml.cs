using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lycoris.Formats;
using Lycoris.Yokai;

namespace Lycoris
{
    public partial class MainWindow : Window
    {
        private readonly YokaiDatabase _db = new YokaiDatabase(YokaiSchema.Yw3);
        private ICollectionView _view;

        /// <summary>Reference game extract used to resolve move names absent from a mod. Defaults to the "cfg" folder.</summary>
        private readonly string _referenceFolder = FindDefaultReference();

        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>Walk up from the exe location to find a "cfg" folder (the bundled game extract).</summary>
        private static string FindDefaultReference()
        {
            try
            {
                var dir = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                for (int i = 0; i < 6 && dir != null; i++)
                {
                    string cfg = System.IO.Path.Combine(dir.FullName, "cfg");
                    if (System.IO.Directory.Exists(cfg)) return cfg;
                    dir = dir.Parent;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        // ----------------------- Loading -----------------------

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Dossier extrait (YWML) contenant chara_param / chara_base / chara_text…"
            })
            {
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                LoadSafely(() =>
                {
                    _db.LoadFolder(dlg.SelectedPath, _referenceFolder);
                    string moves = _db.MoveOptions.Count > 0
                        ? $"moves nommés {_db.MoveNameCount}"
                        : "MOVES NON NOMMÉS — définis un « Dossier de référence » (jeu complet) puis rouvre";
                    string refNote = _db.ResolverFromReference.Count > 0
                        ? $"  |  réf: {string.Join(", ", _db.ResolverFromReference)}" : "";
                    return $"{_db.Yokai.Count} yo-kai  |  noms {_db.NameTableCount}, desc {_db.DescTableCount}, {moves}{refNote}";
                });
            }
        }

        private void LoadSafely(Func<string> load)
        {
            try
            {
                string status = load();
                // If the mod ships its own face_icon.xi (which contains the vanilla medals), use it as
                // the working atlas so its medals are shown and edited — not the reference's clean copy.
                _moddedAtlasPath = _db.ModFaceAtlasFile;

                // Feed the move dropdowns.
                AttackCombo.ItemsSource = _db.MoveOptions;
                TechCombo.ItemsSource = _db.MoveOptions;
                InspiritCombo.ItemsSource = _db.MoveOptions;
                GuardCombo.ItemsSource = _db.MoveOptions;
                SoulCombo.ItemsSource = _db.MoveOptions;
                AbilityCombo.ItemsSource = _db.AbilityOptions;
                EvolveCombo.ItemsSource = _db.YokaiOptions;
                BtAtkACombo.ItemsSource = _db.TechnicOptions;
                BtAtkYCombo.ItemsSource = _db.TechnicOptions;
                BtAtkXCombo.ItemsSource = _db.TechnicOptions;
                BtSoulCombo.ItemsSource = _db.TechnicOptions;
                BtAbilityCombo.ItemsSource = _db.BtAbilityOptions;
                Drop1Combo.ItemsSource = _db.ItemOptions;
                Drop2Combo.ItemsSource = _db.ItemOptions;

                RebuildView();
                if (_view.CurrentItem == null) _view.MoveCurrentToFirst();
                Selector.SelectedItem = _view.CurrentItem;

                SaveButton.IsEnabled = _db.ParamFile != null;
                AddButton.IsEnabled = _db.BaseData != null && _db.TextData != null && _db.DescData != null;
                StatusText.Text = status;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Erreur: " + ex.Message;
            }
        }

        private void RebuildView()
        {
            _view = CollectionViewSource.GetDefaultView(_db.Yokai.ToList());
            _view.Filter = FilterPredicate;
            Selector.ItemsSource = _view;
        }

        // ----------------------- Selection -----------------------

        private bool _suppressEvolvable;

        private void Selector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            Panel.DataContext = y;
            Panel.IsEnabled = y != null;
            PortraitImage.Source = LoadIcon(y);
            RefreshCharabaseImages(y);
            if (y != null) ProfileElementCombo.SelectedValue = y.Resistance ?? 0; // default profile element = its resistance

            _suppressEvolvable = true;
            EvolvableCheck.IsChecked = y != null && y.CanEvolve;
            _suppressEvolvable = false;
        }

        private void EvolvableCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvolvable) return;
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null) return;
            _db.SetEvolvable(y, EvolvableCheck.IsChecked == true);
            StatusText.Text = y.CanEvolve
                ? $"{y.DisplayName} peut maintenant évoluer — choisis la cible et le niveau, puis Sauver."
                : $"{y.DisplayName} n'évolue plus.";
        }

        private void ApplyPower_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null) return;
            int power = (int)Math.Round(PowerSlider.Value);
            StatCurve.Apply(y, power);
            StatusText.Text = $"Stats de {y.DisplayName} réglées sur puissance {power}/10.";
        }

        private void ApplyProfile_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null) return;
            int element = ProfileElementCombo.SelectedValue is int el ? el : (y.Resistance ?? 0);
            int power = (int)Math.Round(PowerSlider.Value);
            string summary = AttackProfile.Apply(_db, y, element, power, ProfileBtCheck.IsChecked == true);
            StatusText.Text = $"Profil {YokaiEnums.Attribute(element)} / puissance {power} → {summary}";
        }

        private void MorphoSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MorphoLabel != null) MorphoLabel.Text = ScaleCurve.Morphology((int)Math.Round(MorphoSlider.Value));
        }

        private void ApplyMorpho_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null) return;
            int level = (int)Math.Round(MorphoSlider.Value);
            ScaleCurve.Apply(y, level);
            StatusText.Text = $"Scale de {y.DisplayName} → {ScaleCurve.Morphology(level)}.";
        }

        private static BitmapSource LoadIcon(YokaiInfo y) => LoadIconFile(y?.IconFile);

        private static BitmapSource LoadIconFile(string path)
        {
            if (path == null || !System.IO.File.Exists(path)) return null;
            try
            {
                var img = Imgc.Decode(System.IO.File.ReadAllBytes(path));
                return ToBitmap(img.Bgra, img.Width, img.Height);
            }
            catch { return null; }
        }

        private static BitmapSource ToBitmap(byte[] bgra, int w, int h)
        {
            var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new System.Windows.Int32Rect(0, 0, w, h), bgra, w * 4, 0);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>Load a PNG and return it as a w×h BGRA32 buffer (scaled if needed).</summary>
        private static byte[] PngToBgra(string pngPath, int w, int h)
        {
            var png = new BitmapImage();
            png.BeginInit(); png.CacheOption = BitmapCacheOption.OnLoad;
            png.UriSource = new Uri(pngPath); png.EndInit();
            var conv = new FormatConvertedBitmap(png, PixelFormats.Bgra32, null, 0);
            BitmapSource src = conv.PixelWidth == w && conv.PixelHeight == h
                ? (BitmapSource)conv
                : new TransformedBitmap(conv, new ScaleTransform((double)w / conv.PixelWidth, (double)h / conv.PixelHeight));
            var bgra = new byte[w * h * 4];
            src.CopyPixels(bgra, w * 4, 0);
            return bgra;
        }

        // ----------------------- Charabase / medal icons -----------------------

        /// <summary>Flush the focused editor so its two-way binding writes back before we read the model.</summary>
        private void CommitEdits()
        {
            var f = System.Windows.Input.Keyboard.FocusedElement as System.Windows.UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
        }

        private const int MedalCell = 32; // YW3 atlas cell size
        private string _moddedAtlasPath;  // the mod's edited atlas once one medal is replaced (cumulative)

        /// <summary>The atlas to read from: the mod's edited copy if we've written one, else the resolved one.</summary>
        private string CurrentAtlasPath()
        {
            if (_moddedAtlasPath != null && System.IO.File.Exists(_moddedAtlasPath)) return _moddedAtlasPath;
            return _db.FaceAtlasFile;
        }

        private void RefreshCharabaseImages(YokaiInfo y)
        {
            MedalIconImage.Source = LoadIconFile(y?.MedalIconFile);
            MedalAtlasImage.Source = CropAtlasMedal(y);
        }

        private BitmapSource CropAtlasMedal(YokaiInfo y)
        {
            string atlasPath = CurrentAtlasPath();
            if (y == null || !y.MedalPosX.HasValue || !y.MedalPosY.HasValue || atlasPath == null) return null;
            try
            {
                var atlas = Imgc.Decode(System.IO.File.ReadAllBytes(atlasPath));
                int x = y.MedalPosX.Value * MedalCell, y0 = y.MedalPosY.Value * MedalCell;
                if (x + MedalCell > atlas.Width || y0 + MedalCell > atlas.Height) return null;
                var cell = new byte[MedalCell * MedalCell * 4];
                for (int ry = 0; ry < MedalCell; ry++)
                    Array.Copy(atlas.Bgra, ((y0 + ry) * atlas.Width + x) * 4, cell, ry * MedalCell * 4, MedalCell * 4);
                return ToBitmap(cell, MedalCell, MedalCell);
            }
            catch { return null; }
        }

        private void PickMedal_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            CommitEdits();
            string atlasPath = CurrentAtlasPath();
            if (y == null || atlasPath == null)
            {
                MessageBox.Show("Atlas face_icon.xi introuvable (ouvre un dossier avec un face_icon).", "Atlas");
                return;
            }
            try
            {
                var img = Imgc.Decode(System.IO.File.ReadAllBytes(atlasPath));
                var atlasBmp = ToBitmap(img.Bgra, img.Width, img.Height);
                var picker = new AtlasPickerWindow(this, atlasBmp, MedalCell, y.MedalPosX ?? 0, y.MedalPosY ?? 0);
                if (picker.ShowDialog() == true)
                {
                    y.MedalPosX = picker.PickedX;
                    y.MedalPosY = picker.PickedY;
                    MedalAtlasImage.Source = CropAtlasMedal(y);
                    StatusText.Text = $"{y.DisplayName} — médaille à ({picker.PickedX}, {picker.PickedY}).";
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Atlas", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ReplaceMedalIcon_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null) return;
            string baseName = y.IconBaseName ?? (y.FileNamePrefix.HasValue
                ? IconNaming.GetFileModelText(y.FileNamePrefix.Value, y.FileNameNumber ?? 0, y.FileNameVariant ?? 0) : null);
            string src = y.MedalIconFile ?? (_db.ModMedalIconDir != null && baseName != null
                ? System.IO.Path.Combine(_db.ModMedalIconDir, baseName + ".xi") : null);
            if (src == null) { MessageBox.Show("Pas de dossier medal_icon disponible.", "medal_icon"); return; }

            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images PNG|*.png", Title = "medal_icon — PNG (64×64 idéalement)" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string target = _db.MirrorToMod(src);
                System.IO.File.WriteAllBytes(target, Imgc.EncodeXi(PngToBgra(dlg.FileName, 64, 64), 64, 64));
                y.MedalIconFile = target;
                MedalIconImage.Source = LoadIconFile(target);
                StatusText.Text = $"medal_icon remplacé: {System.IO.Path.GetFileName(target)}";
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Erreur medal_icon", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ReplaceMedalAtlas_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            CommitEdits();
            if (y == null || !y.MedalPosX.HasValue || !y.MedalPosY.HasValue) return;
            if (CurrentAtlasPath() == null) { MessageBox.Show("Atlas face_icon.xi introuvable.", "Medal"); return; }

            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images PNG|*.png", Title = "Mini-médaille — PNG (32×32)" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var atlas = Imgc.Decode(System.IO.File.ReadAllBytes(CurrentAtlasPath()));
                byte[] cell = PngToBgra(dlg.FileName, MedalCell, MedalCell);
                int x = y.MedalPosX.Value * MedalCell, y0 = y.MedalPosY.Value * MedalCell;
                if (x + MedalCell > atlas.Width || y0 + MedalCell > atlas.Height)
                { MessageBox.Show("Position medal hors de l'atlas.", "Medal"); return; }
                for (int ry = 0; ry < MedalCell; ry++)
                    Array.Copy(cell, ry * MedalCell * 4, atlas.Bgra, ((y0 + ry) * atlas.Width + x) * 4, MedalCell * 4);

                string target = _db.MirrorToMod(CurrentAtlasPath());
                System.IO.File.WriteAllBytes(target, Imgc.EncodeXi(atlas.Bgra, atlas.Width, atlas.Height));
                _moddedAtlasPath = target;
                MedalAtlasImage.Source = CropAtlasMedal(y);
                StatusText.Text = $"Médaille insérée dans l'atlas à ({y.MedalPosX},{y.MedalPosY}) → {System.IO.Path.GetFileName(target)}";
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Erreur atlas medal", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ReplaceIconButton_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null) return;
            if (y.IconFile == null && _db.ModFaceIconDir == null)
            {
                MessageBox.Show("Aucun dossier face_icon dans le mod pour y écrire l'icône.", "Remplacer l'icône");
                return;
            }
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images PNG|*.png", Title = "Choisir un PNG (idéalement 64×64)" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                ReplaceIcon(y, dlg.FileName);
                PortraitImage.Source = LoadIcon(y);
                StatusText.Text = $"Icône remplacée: {System.IO.Path.GetFileName(y.IconFile)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Erreur conversion PNG→.xi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReplaceIcon(YokaiInfo y, string pngPath)
        {
            string name = y.IconBaseName ?? (y.FileNamePrefix.HasValue
                ? IconNaming.GetFileModelText(y.FileNamePrefix.Value, y.FileNameNumber ?? 0, y.FileNameVariant ?? 0) : null);
            string src = y.IconFile ?? (_db.ModFaceIconDir != null && name != null
                ? System.IO.Path.Combine(_db.ModFaceIconDir, name + ".xi") : null);
            if (src == null) throw new InvalidOperationException("Impossible de déterminer le fichier d'icône de ce yo-kai.");

            string target = _db.MirrorToMod(src);
            System.IO.File.WriteAllBytes(target, Imgc.EncodeXi(PngToBgra(pngPath, 64, 64), 64, 64));
            y.IconFile = target;
        }

        private void Prev_Click(object sender, RoutedEventArgs e)
        {
            if (Selector.SelectedIndex > 0) Selector.SelectedIndex--;
        }

        private void Next_Click(object sender, RoutedEventArgs e)
        {
            if (Selector.SelectedIndex < Selector.Items.Count - 1) Selector.SelectedIndex++;
        }

        private bool FilterPredicate(object item)
        {
            string q = FilterBox.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            var y = (YokaiInfo)item;
            return (y.Name != null && y.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                   || y.ParamIdHex.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _view?.Refresh();
            if (Selector.SelectedItem == null && Selector.Items.Count > 0)
                Selector.SelectedIndex = 0;
        }

        // ----------------------- Add / Save -----------------------

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new AddYokaiDialog(this);
            if (dlg.ShowDialog() != true) return;
            try
            {
                var y = _db.AddYokai(dlg.YokaiName, dlg.Description, dlg.Tribe, dlg.Rank);
                RebuildView();
                Selector.SelectedItem = y;
                StatusText.Text = $"Ajouté: {y.Name} ({y.ParamIdHex}). Édite ses champs puis Sauver le mod.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ajout impossible", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_db.ParamFile == null) return;

            // Commit focus so the last edited field is captured.
            var focused = System.Windows.Input.Keyboard.FocusedElement as System.Windows.UIElement;
            focused?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));

            var files = new[] { _db.ParamFile, _db.BaseFile, _db.TextFile, _db.DescFile, _db.ScaleFile }
                .Where(f => f != null).Select(System.IO.Path.GetFileName);
            var confirm = MessageBox.Show(
                "Écraser les fichiers du mod :\n" + string.Join("\n", files) + "\n\navec les modifications ?",
                "Sauver le mod", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            try
            {
                string summary = _db.SaveAll();
                StatusText.Text = "Sauvé — " + summary;
                MessageBox.Show("Sauvegarde OK.\n" + summary, "Sauvegarde", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Erreur de sauvegarde", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
