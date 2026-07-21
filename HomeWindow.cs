using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Home launcher: open a mod folder once, then choose which editor to use (yo-kai or items).
    /// Both editors share the same in-memory database, so edits made in one are visible in the other
    /// and a single "Sauver le mod" in each writes the corresponding files.
    /// </summary>
    public sealed class HomeWindow : Window
    {
        private readonly YokaiDatabase _db = new YokaiDatabase(YokaiSchema.Yw3);
        private readonly string _referenceFolder = MainWindow.FindDefaultReference();

        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 12, 0, 0) };
        private readonly Button _yokaiBtn;
        private readonly Button _itemBtn;

        private MainWindow _yokaiWindow;
        private ItemEditorWindow _itemWindow;

        public HomeWindow()
        {
            Title = "Lycoris — Éditeur Yo-kai Watch 3";
            Width = 460; Height = 380;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;

            var root = new StackPanel { Margin = new Thickness(24) };

            root.Children.Add(new TextBlock
            {
                Text = "Lycoris",
                FontSize = 30, FontWeight = FontWeights.Bold, Foreground = Brushes.MediumVioletRed
            });
            root.Children.Add(new TextBlock
            {
                Text = "Éditeur de mods Yo-kai Watch 3",
                FontSize = 13, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 18)
            });

            var open = new Button { Content = "📂  Ouvrir un dossier (mod extrait)…", Padding = new Thickness(12, 8, 12, 8), FontSize = 14 };
            open.Click += (s, e) => OpenFolder();
            root.Children.Add(open);

            _yokaiBtn = BigButton("👹  Éditeur Yo-kai", "Stats, moves, évolutions, Blaster T, drops, charabase, portraits…", OpenYokaiEditor);
            _itemBtn = BigButton("🎁  Éditeur d'items", "Nom, description, prix, ordre d'inventaire, icône de l'atlas…", OpenItemEditor);
            _yokaiBtn.IsEnabled = false;
            _itemBtn.IsEnabled = false;
            root.Children.Add(_yokaiBtn);
            root.Children.Add(_itemBtn);

            root.Children.Add(_status);
            _status.Text = _referenceFolder != null
                ? "Astuce: ouvre le dossier de ton mod extrait (YWML). Le dossier « cfg » sert de référence pour les noms manquants."
                : "Ouvre le dossier de ton mod extrait (YWML).";

            Content = root;
        }

        private Button BigButton(string title, string subtitle, Action onClick)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = subtitle, FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
            var b = new Button
            {
                Content = sp,
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            b.Click += (s, e) => onClick();
            return b;
        }

        private void OpenFolder()
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Dossier extrait (YWML) contenant chara_param / chara_base / chara_text…"
            })
            {
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                try
                {
                    _db.LoadFolder(dlg.SelectedPath, _referenceFolder);

                    // A freshly loaded db invalidates any editor windows still bound to the previous state.
                    _yokaiWindow?.Close(); _yokaiWindow = null;
                    _itemWindow?.Close(); _itemWindow = null;

                    _yokaiBtn.IsEnabled = _db.ParamFile != null;
                    _itemBtn.IsEnabled = _db.Items.Count > 0;

                    string moves = _db.MoveOptions.Count > 0 ? $"moves nommés {_db.MoveNameCount}" : "moves non nommés";
                    _status.Text = $"Chargé — {_db.Yokai.Count} yo-kai, {_db.Items.Count} items  ({moves}).\n" +
                                   "Choisis un éditeur ci-dessus.";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
                    _status.Text = "Erreur: " + ex.Message;
                }
            }
        }

        private void OpenYokaiEditor()
        {
            if (_db.ParamFile == null) return;
            if (_yokaiWindow != null && _yokaiWindow.IsLoaded) { _yokaiWindow.Activate(); return; }
            _yokaiWindow = new MainWindow(_db, _referenceFolder) { Owner = this };
            _yokaiWindow.Closed += (s, e) => _yokaiWindow = null;
            _yokaiWindow.Show();
        }

        private void OpenItemEditor()
        {
            if (_db.Items.Count == 0) return;
            if (_itemWindow != null && _itemWindow.IsLoaded) { _itemWindow.Activate(); return; }
            _itemWindow = new ItemEditorWindow(this, _db) { Owner = this };
            _itemWindow.Closed += (s, e) => _itemWindow = null;
            _itemWindow.Show();
        }
    }
}
