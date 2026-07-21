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

                // Feed the move dropdowns.
                AttackCombo.ItemsSource = _db.MoveOptions;
                TechCombo.ItemsSource = _db.MoveOptions;
                InspiritCombo.ItemsSource = _db.MoveOptions;
                GuardCombo.ItemsSource = _db.MoveOptions;
                SoulCombo.ItemsSource = _db.MoveOptions;
                AbilityCombo.ItemsSource = _db.AbilityOptions;
                EvolveCombo.ItemsSource = _db.YokaiOptions;

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

        private void Selector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            Panel.DataContext = y;
            Panel.IsEnabled = y != null;
            PortraitImage.Source = LoadIcon(y);
        }

        private static BitmapSource LoadIcon(YokaiInfo y)
        {
            if (y?.IconFile == null || !System.IO.File.Exists(y.IconFile)) return null;
            try
            {
                var img = Imgc.Decode(System.IO.File.ReadAllBytes(y.IconFile));
                var bmp = new WriteableBitmap(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null);
                bmp.WritePixels(new System.Windows.Int32Rect(0, 0, img.Width, img.Height), img.Bgra, img.Width * 4, 0);
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
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
            // Determine the target .xi path: existing icon, else compute name into the mod's face_icon.
            string target = y.IconFile;
            if (target == null)
            {
                string name = y.IconBaseName
                    ?? (y.FileNamePrefix.HasValue && y.FileNameNumber.HasValue && y.FileNameVariant.HasValue
                        ? IconNaming.GetFileModelText(y.FileNamePrefix.Value, y.FileNameNumber.Value, y.FileNameVariant.Value)
                        : null);
                if (name == null) throw new InvalidOperationException("Impossible de déterminer le nom d'icône de ce yo-kai.");
                target = System.IO.Path.Combine(_db.ModFaceIconDir, name + ".xi");
            }

            // Decode PNG to BGRA 64x64.
            var png = new BitmapImage();
            png.BeginInit(); png.CacheOption = BitmapCacheOption.OnLoad;
            png.UriSource = new Uri(pngPath); png.EndInit();
            var conv = new FormatConvertedBitmap(png, PixelFormats.Bgra32, null, 0);
            var scaled = conv.PixelWidth == 64 && conv.PixelHeight == 64
                ? (BitmapSource)conv
                : new TransformedBitmap(conv, new ScaleTransform(64.0 / conv.PixelWidth, 64.0 / conv.PixelHeight));
            var bgra = new byte[64 * 64 * 4];
            scaled.CopyPixels(bgra, 64 * 4, 0);

            byte[] xi = Imgc.EncodeXi(bgra, 64, 64);
            System.IO.File.WriteAllBytes(target, xi);
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
