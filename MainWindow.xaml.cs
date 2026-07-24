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
        private YokaiDatabase _db = new YokaiDatabase(YokaiSchema.Yw3);
        private ICollectionView _view;

        /// <summary>Reference game extract used to resolve move names absent from a mod. Defaults to the "cfg" folder.</summary>
        private string _referenceFolder = FindDefaultReference();

        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Open the yo-kai editor bound to an already-loaded database (used by the home launcher so the
        /// yo-kai and item editors share one in-memory mod). The UI is wired straight from the given db.
        /// </summary>
        public MainWindow(YokaiDatabase db, string referenceFolder) : this()
        {
            _db = db;
            _referenceFolder = referenceFolder;
            ApplyLoadedDb();
            StatusText.Text = $"{_db.Yokai.Count} yo-kai loaded  |  names {_db.NameTableCount}, desc {_db.DescTableCount}";
        }

        /// <summary>Walk up from the exe location to find a "cfg" folder (the bundled game extract).</summary>
        internal static string FindDefaultReference()
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
            string folder = FolderPicker.Pick("Extracted folder (YWML) containing chara_param / chara_base / chara_text…",
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if (folder == null) return;
            LoadSafely(() =>
            {
                _db.LoadFolder(folder, _referenceFolder);
                string moves = _db.MoveOptions.Count > 0
                    ? $"named moves {_db.MoveNameCount}"
                    : "MOVES NOT NAMED — set a “Reference folder” (full game) then reopen";
                string refNote = _db.ResolverFromReference.Count > 0
                    ? $"  |  ref: {string.Join(", ", _db.ResolverFromReference)}" : "";
                return $"{_db.Yokai.Count} yo-kai  |  names {_db.NameTableCount}, desc {_db.DescTableCount}, {moves}{refNote}";
            });
        }

        private void LoadSafely(Func<string> load)
        {
            try
            {
                string status = load();
                ApplyLoadedDb();
                StatusText.Text = status;
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Error: " + ex.Message;
            }
        }

        /// <summary>Wire the UI (dropdowns, selector, toolbar enablement) from the current <see cref="_db"/>.</summary>
        private void ApplyLoadedDb()
        {
            // If the mod ships its own face_icon.xi (which contains the vanilla medals), use it as
            // the working atlas so its medals are shown and edited — not the reference's clean copy.
            _moddedAtlasPath = _db.ModFaceAtlasFile;

            // Searchable move dropdowns (type to filter among ~2500 skills).
            WireMoveCombos();
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
            DuplicateButton.IsEnabled = _db.BaseData != null && _db.TextData != null && _db.DescData != null;
            DeleteButton.IsEnabled = _db.ParamFile != null;
            ItemsButton.IsEnabled = _db.Items.Count > 0;
            CheckButton.IsEnabled = _db.ParamFile != null;
        }

        private void RebuildView()
        {
            _view = CollectionViewSource.GetDefaultView(_db.Yokai.ToList());
            _view.Filter = FilterPredicate;
            Selector.ItemsSource = _view;
        }

        // ----------------------- Selection -----------------------

        private bool _suppressEvolvable;
        private SearchableCombo _scAttack, _scTech, _scInsp, _scGuard, _scSoul, _scAbility;

        private void WireMoveCombos()
        {
            // Each move slot lists ONLY the skills of its category (SkillType): Attack=1, Technique=3,
            // Inspirit=5, Guard=0, Soultimate=4. Ability is a separate table (chara_ability).
            if (_scAttack == null)
            {
                _scAttack = new SearchableCombo(AttackCombo, _db.AttackOptions, y => y.AttackHash, (y, v) => y.AttackHash = v);
                _scTech = new SearchableCombo(TechCombo, _db.TechniqueOptions, y => y.TechniqueHash, (y, v) => y.TechniqueHash = v);
                _scInsp = new SearchableCombo(InspiritCombo, _db.InspiritOptions, y => y.InspiritHash, (y, v) => y.InspiritHash = v);
                _scGuard = new SearchableCombo(GuardCombo, _db.GuardOptions, y => y.GuardHash, (y, v) => y.GuardHash = v);
                _scSoul = new SearchableCombo(SoulCombo, _db.SoultimateSkillOptions, y => y.SoultimateHash, (y, v) => y.SoultimateHash = v);
                _scAbility = new SearchableCombo(AbilityCombo, _db.AbilityOptions, y => y.AbilityHash, (y, v) => y.AbilityHash = v);
            }
            else
            {
                _scAttack.SetSource(_db.AttackOptions); _scTech.SetSource(_db.TechniqueOptions);
                _scInsp.SetSource(_db.InspiritOptions); _scGuard.SetSource(_db.GuardOptions);
                _scSoul.SetSource(_db.SoultimateSkillOptions); _scAbility.SetSource(_db.AbilityOptions);
            }
        }

        private void BindMoveCombos(YokaiInfo y)
        {
            _scAttack?.Bind(y); _scTech?.Bind(y); _scInsp?.Bind(y);
            _scGuard?.Bind(y); _scSoul?.Bind(y); _scAbility?.Bind(y);
        }

        private void Selector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            Panel.DataContext = y;
            Panel.IsEnabled = y != null;
            BindMoveCombos(y);
            PortraitImage.Source = LoadIcon(y);
            RefreshCharabaseImages(y);
            if (y != null) ProfileElementCombo.SelectedValue = y.Resistance ?? 0; // default profile element = its resistance

            _suppressEvolvable = true;
            EvolvableCheck.IsChecked = y != null && y.CanEvolve;
            _suppressEvolvable = false;

            EnableBtButton.IsEnabled = y != null && !y.HasBlasterT;
            EnableDropsButton.IsEnabled = y != null && !y.HasDrops;
        }

        private void EnableBt_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null || y.HasBlasterT) return;
            if (!_db.EnableBlasterT(y))
            {
                DarkMessage.Show("hackslash_chara_param not found (provide it, or open a reference folder).", "Blaster T");
                return;
            }
            EnableBtButton.IsEnabled = false;
            StatusText.Text = $"Blaster T enabled for {y.DisplayName} — edit the moveset then “Save the mod”.";
        }

        private void EnableDrops_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null || y.HasDrops) return;
            if (!_db.EnableDrops(y))
            {
                DarkMessage.Show("battle_chara_param not found (provide it, or open a reference folder).", "Drops");
                return;
            }
            EnableDropsButton.IsEnabled = false;
            StatusText.Text = $"Drops enabled for {y.DisplayName} — edit then “Save the mod”.";
        }

        private void EvolvableCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressEvolvable) return;
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null) return;
            _db.SetEvolvable(y, EvolvableCheck.IsChecked == true);
            StatusText.Text = y.CanEvolve
                ? $"{y.DisplayName} can now evolve — choose the target and level, then Save."
                : $"{y.DisplayName} no longer evolves.";
        }

        private void ApplyPower_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null) return;
            int power = (int)Math.Round(PowerSlider.Value);
            StatCurve.Apply(y, power);
            StatusText.Text = $"Stats of {y.DisplayName} set to power {power}/10.";
        }

        private void ApplyProfile_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null) return;
            int element = ProfileElementCombo.SelectedValue is int el ? el : (y.Resistance ?? 0);
            int power = (int)Math.Round(PowerSlider.Value);
            string summary = AttackProfile.Apply(_db, y, element, power, ProfileBtCheck.IsChecked == true);
            BindMoveCombos(y); // refresh the (non-two-way) move dropdowns to show the new moves
            StatusText.Text = $"Profile {YokaiEnums.Attribute(element)} / power {power} → {summary}";
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
            StatusText.Text = $"Scale of {y.DisplayName} → {ScaleCurve.Morphology(level)}.";
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
                DarkMessage.Show("face_icon.xi atlas not found (open a folder with a face_icon).", "Atlas");
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
                    StatusText.Text = $"{y.DisplayName} — medal at ({picker.PickedX}, {picker.PickedY}).";
                }
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Atlas", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ReplaceMedalIcon_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null) return;
            string baseName = y.IconBaseName ?? (y.FileNamePrefix.HasValue
                ? IconNaming.GetFileModelText(y.FileNamePrefix.Value, y.FileNameNumber ?? 0, y.FileNameVariant ?? 0) : null);
            string src = y.MedalIconFile ?? (_db.ModMedalIconDir != null && baseName != null
                ? System.IO.Path.Combine(_db.ModMedalIconDir, baseName + ".xi") : null);
            if (src == null) { DarkMessage.Show("No medal_icon folder available.", "medal_icon"); return; }

            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG images|*.png", Title = "medal_icon — PNG (ideally 64×64)" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                string target = _db.MirrorToMod(src);
                System.IO.File.WriteAllBytes(target, Imgc.EncodeXi(PngToBgra(dlg.FileName, 64, 64), 64, 64));
                y.MedalIconFile = target;
                MedalIconImage.Source = LoadIconFile(target);
                StatusText.Text = $"medal_icon replaced: {System.IO.Path.GetFileName(target)}";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "medal_icon error", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ReplaceMedalAtlas_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            CommitEdits();
            if (y == null || !y.MedalPosX.HasValue || !y.MedalPosY.HasValue) return;
            if (CurrentAtlasPath() == null) { DarkMessage.Show("face_icon.xi atlas not found.", "Medal"); return; }

            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG images|*.png", Title = "Mini-medal — PNG (32×32)" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var atlas = Imgc.Decode(System.IO.File.ReadAllBytes(CurrentAtlasPath()));
                byte[] cell = PngToBgra(dlg.FileName, MedalCell, MedalCell);
                int x = y.MedalPosX.Value * MedalCell, y0 = y.MedalPosY.Value * MedalCell;
                if (x + MedalCell > atlas.Width || y0 + MedalCell > atlas.Height)
                { DarkMessage.Show("Medal position outside the atlas.", "Medal"); return; }
                for (int ry = 0; ry < MedalCell; ry++)
                    Array.Copy(cell, ry * MedalCell * 4, atlas.Bgra, ((y0 + ry) * atlas.Width + x) * 4, MedalCell * 4);

                string target = _db.MirrorToMod(CurrentAtlasPath());
                System.IO.File.WriteAllBytes(target, Imgc.EncodeXi(atlas.Bgra, atlas.Width, atlas.Height));
                _moddedAtlasPath = target;
                MedalAtlasImage.Source = CropAtlasMedal(y);
                StatusText.Text = $"Medal inserted into the atlas at ({y.MedalPosX},{y.MedalPosY}) → {System.IO.Path.GetFileName(target)}";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Medal atlas error", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ReplaceIconButton_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null) return;
            if (y.IconFile == null && _db.ModFaceIconDir == null)
            {
                DarkMessage.Show("No face_icon folder in the mod to write the icon to.", "Replace the icon");
                return;
            }
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "PNG images|*.png", Title = "Choose a PNG (ideally 64×64)" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                ReplaceIcon(y, dlg.FileName);
                PortraitImage.Source = LoadIcon(y);
                StatusText.Text = $"Icon replaced: {System.IO.Path.GetFileName(y.IconFile)}";
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "PNG→.xi conversion error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ReplaceIcon(YokaiInfo y, string pngPath)
        {
            string name = y.IconBaseName ?? (y.FileNamePrefix.HasValue
                ? IconNaming.GetFileModelText(y.FileNamePrefix.Value, y.FileNameNumber ?? 0, y.FileNameVariant ?? 0) : null);
            string src = y.IconFile ?? (_db.ModFaceIconDir != null && name != null
                ? System.IO.Path.Combine(_db.ModFaceIconDir, name + ".xi") : null);
            if (src == null) throw new InvalidOperationException("Unable to determine this yo-kai's icon file.");

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
                StatusText.Text = $"Added: {y.Name} ({y.ParamIdHex}). Edit its fields then Save the mod.";
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Cannot add", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void DuplicateButton_Click(object sender, RoutedEventArgs e)
        {
            var src = Selector.SelectedItem as YokaiInfo;
            if (src == null) return;
            CommitEdits(); // flush pending edits on the source before cloning it
            try
            {
                var y = _db.DuplicateYokai(src);
                RebuildView();
                Selector.SelectedItem = y;
                StatusText.Text = $"Duplicated: {y.Name} ({y.ParamIdHex}). Edit its fields then Save the mod.";
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Cannot duplicate", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            CommitEdits();
            new IntegrityWindow(this, _db) { Owner = this }.Show();
        }

        private void ItemsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_db.Items.Count == 0)
            {
                DarkMessage.Show("No item loaded (item_config not found — check the reference folder).", "Items");
                return;
            }
            new ItemEditorWindow(this, _db) { Owner = this }.Show();
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var y = Selector.SelectedItem as YokaiInfo;
            if (y == null) return;
            var confirm = DarkMessage.Show(
                $"Delete {y.DisplayName} ({y.ParamIdHex}) from the registry?\n\n" +
                "Its param entry (and hackslash/battle) is removed; shared data " +
                "(base, name, description…) is removed only if no other yo-kai uses it. " +
                "Confirm with “Save the mod”.",
                "Delete a yo-kai", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            int idx = Selector.SelectedIndex;
            _db.RemoveYokai(y);
            RebuildView();
            if (Selector.Items.Count > 0)
                Selector.SelectedIndex = Math.Min(idx, Selector.Items.Count - 1);
            StatusText.Text = $"{y.DisplayName} deleted — {_db.Yokai.Count} yo-kai remaining. Save to apply.";
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_db.ParamFile == null) return;

            // Commit focus so the last edited field is captured.
            var focused = System.Windows.Input.Keyboard.FocusedElement as System.Windows.UIElement;
            focused?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));

            var files = new[] { _db.ParamFile, _db.BaseFile, _db.TextFile, _db.DescFile, _db.ScaleFile }
                .Where(f => f != null).Select(System.IO.Path.GetFileName);
            var confirm = DarkMessage.Show(
                "Overwrite the mod files:\n" + string.Join("\n", files) + "\n\nwith the changes?",
                "Save the mod", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            try
            {
                string summary = _db.SaveAll();
                StatusText.Text = "Saved — " + summary;
                DarkMessage.Show("Save OK.\n" + summary, "Save", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                DarkMessage.Show(ex.Message, "Save error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
