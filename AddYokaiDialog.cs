using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Modal dialog to collect the fields for creating (or duplicating) a yo-kai: name, description,
    /// tribe/rank as named dropdowns, and the model (e.g. "y152000"). The model drives the BaseID
    /// (CRC32 of the model) and the creation of blank face_icon / medal_icon to replace later.
    /// Creation also accepts optional assets that are written straight into the mod: a model folder
    /// (copied to data/character/&lt;model&gt;), a face_icon PNG, a medal_icon PNG, and the medal's
    /// position in the face_icon.xi atlas (X/Y — pickable when the atlas is passed in).
    /// The same dialog is reused for Duplicate, pre-filled with the source's values.
    /// </summary>
    public sealed class AddYokaiDialog : Window
    {
        private readonly TextBox _name = new TextBox { Margin = new Thickness(0, 2, 0, 8) };
        private readonly TextBox _desc = new TextBox
        {
            Margin = new Thickness(0, 2, 0, 8),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 80,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        private readonly TextBox _model = new TextBox { Margin = new Thickness(0, 2, 0, 2) };
        private readonly ComboBox _tribe = new ComboBox
        {
            Margin = new Thickness(0, 2, 0, 8),
            ItemsSource = YokaiEnums.Tribes,
            DisplayMemberPath = "Name",
            SelectedValuePath = "Key",
        };
        private readonly ComboBox _rank = new ComboBox
        {
            Margin = new Thickness(0, 2, 0, 8),
            ItemsSource = YokaiEnums.Ranks,
            DisplayMemberPath = "Name",
            SelectedValuePath = "Key",
        };

        private readonly TextBox _modelBin = new TextBox { Margin = new Thickness(0, 2, 0, 0), IsReadOnly = true };
        private readonly TextBox _modelFolder = new TextBox { Margin = new Thickness(0, 2, 0, 0), IsReadOnly = true };
        private readonly TextBox _faceIcon = new TextBox { Margin = new Thickness(0, 2, 0, 0), IsReadOnly = true };
        private readonly TextBox _medalIcon = new TextBox { Margin = new Thickness(0, 2, 0, 0), IsReadOnly = true };
        private readonly TextBox _atlasX = new TextBox { Width = 50 };
        private readonly TextBox _atlasY = new TextBox { Width = 50 };
        private readonly BitmapSource _atlas;
        private readonly int _atlasCell;

        public string YokaiName => _name.Text;
        public string Description => _desc.Text;
        public string Model => _model.Text.Trim();
        public int Tribe => _tribe.SelectedValue is int t ? t : 0;
        public int Rank => _rank.SelectedValue is int r ? r : 0;

        /// <summary>Folder of model files to copy into the mod (data/character/&lt;model&gt;), or "" if none.</summary>
        /// <summary>A Pokémon *_model.bin to port to a _p00.xc (opens the Model Editor after create), or null.</summary>
        public string ModelBin => string.IsNullOrWhiteSpace(_modelBin.Text) ? null : _modelBin.Text.Trim();
        public string ModelFolder => _modelFolder.Text.Trim();
        /// <summary>PNG to write as the yo-kai's face_icon, or null.</summary>
        public string FaceIconPng => string.IsNullOrWhiteSpace(_faceIcon.Text) ? null : _faceIcon.Text.Trim();
        /// <summary>PNG to write as the yo-kai's medal_icon, or null.</summary>
        public string MedalIconPng => string.IsNullOrWhiteSpace(_medalIcon.Text) ? null : _medalIcon.Text.Trim();
        /// <summary>Medal X position in the face_icon.xi atlas, or null.</summary>
        public int? AtlasX => int.TryParse(_atlasX.Text, out int v) ? (int?)v : null;
        /// <summary>Medal Y position in the face_icon.xi atlas, or null.</summary>
        public int? AtlasY => int.TryParse(_atlasY.Text, out int v) ? (int?)v : null;

        public AddYokaiDialog(Window owner, string title = "Add a Yo-kai",
            string name = "", string desc = "", int tribe = 0, int rank = 0, string model = "",
            BitmapSource atlas = null, int atlasCell = 32)
        {
            Owner = owner;
            Title = title;
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            _atlas = atlas;
            _atlasCell = atlasCell;

            _name.Text = name ?? "";
            _desc.Text = desc ?? "";
            _model.Text = model ?? "";
            _tribe.SelectedValue = tribe;
            _rank.SelectedValue = rank;

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = "Name" });
            panel.Children.Add(_name);
            panel.Children.Add(new TextBlock { Text = "Description" });
            panel.Children.Add(_desc);

            var stats = new Grid();
            stats.ColumnDefinitions.Add(new ColumnDefinition());
            stats.ColumnDefinitions.Add(new ColumnDefinition());
            var tribeStack = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
            tribeStack.Children.Add(new TextBlock { Text = "Tribe" });
            tribeStack.Children.Add(_tribe);
            var rankStack = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
            rankStack.Children.Add(new TextBlock { Text = "Rank" });
            rankStack.Children.Add(_rank);
            Grid.SetColumn(tribeStack, 0); Grid.SetColumn(rankStack, 1);
            stats.Children.Add(tribeStack); stats.Children.Add(rankStack);
            panel.Children.Add(stats);

            panel.Children.Add(new TextBlock { Text = "Model (optional)" });
            panel.Children.Add(_model);
            panel.Children.Add(new TextBlock
            {
                Text = "e.g. y152000 — sets the BaseID to CRC32(model) and names the face_icon / medal_icon.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Theme.FgMuted,
                Margin = new Thickness(0, 2, 0, 8)
            });

            // --- optional creation assets, all written straight into the mod ---
            panel.Children.Add(Section("Add model .bin (optional) — Sun/Moon *_model.bin, ported to a _p00.xc"));
            panel.Children.Add(PathRow(_modelBin, "Bin…", (s, e) => BrowseBin(_modelBin)));

            panel.Children.Add(Section("Model folder (optional) — copied to data/character/<model>"));
            panel.Children.Add(PathRow(_modelFolder, "Folder…", (s, e) => BrowseFolder(_modelFolder)));

            panel.Children.Add(Section("face_icon PNG (optional, ideally 64×64)"));
            panel.Children.Add(PathRow(_faceIcon, "PNG…", (s, e) => BrowsePng(_faceIcon)));

            panel.Children.Add(Section("medal_icon PNG (optional, ideally 64×64)"));
            panel.Children.Add(PathRow(_medalIcon, "PNG…", (s, e) => BrowsePng(_medalIcon)));

            panel.Children.Add(Section("Medal position in the face_icon.xi atlas (optional)"));
            var atlasRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
            atlasRow.Children.Add(new TextBlock { Text = "X", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            atlasRow.Children.Add(_atlasX);
            atlasRow.Children.Add(new TextBlock { Text = "Y", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) });
            atlasRow.Children.Add(_atlasY);
            if (_atlas != null)
            {
                var pick = new Button { Content = "Choose in the atlas…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(10, 0, 0, 0) };
                pick.Click += (s, e) => PickAtlas();
                atlasRow.Children.Add(pick);
            }
            panel.Children.Add(atlasRow);

            panel.Children.Add(new TextBlock
            {
                Text = "Stats are copied from a template — edit them afterwards in the grid.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Theme.FgMuted,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "Create", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
            ok.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_name.Text))
                {
                    DarkMessage.Show("The name is required.", Title);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(_model.Text) &&
                    !IconNaming.TryParse(_model.Text.Trim(), out _, out _, out _))
                {
                    DarkMessage.Show("Model must be a 7-char name like y152000 (or left empty).", Title);
                    return;
                }
                if ((FaceIconPng != null || MedalIconPng != null || !string.IsNullOrEmpty(ModelFolder) ||
                     AtlasX.HasValue || AtlasY.HasValue) && string.IsNullOrWhiteSpace(_model.Text))
                {
                    DarkMessage.Show("Set the Model first — the icons / model files are named after it.", Title);
                    return;
                }
                DialogResult = true;
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            Content = panel;
            _name.Focus();
        }

        private static TextBlock Section(string text) => new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Theme.FgMuted,
            Margin = new Thickness(0, 4, 0, 0)
        };

        private static FrameworkElement PathRow(TextBox tb, string browseText, RoutedEventHandler onBrowse)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(tb, 0);
            var b = new Button { Content = browseText, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
            b.Click += onBrowse;
            Grid.SetColumn(b, 1);
            g.Children.Add(tb);
            g.Children.Add(b);
            return g;
        }

        private void BrowseFolder(TextBox target)
        {
            using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose the model folder" })
                if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) target.Text = d.SelectedPath;
        }

        private void BrowsePng(TextBox target)
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "PNG images|*.png", Title = "Choose a PNG" };
            if (d.ShowDialog() == true) target.Text = d.FileName;
        }

        private void BrowseBin(TextBox target)
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "Pokemon model|*_model.bin;*.bin|All files|*.*", Title = "Choose a Sun/Moon *_model.bin" };
            if (d.ShowDialog() == true) target.Text = d.FileName;
        }

        private void PickAtlas()
        {
            if (_atlas == null) return;
            int cx = int.TryParse(_atlasX.Text, out int x) ? x : 0;
            int cy = int.TryParse(_atlasY.Text, out int y) ? y : 0;
            var p = new AtlasPickerWindow(this, _atlas, _atlasCell, cx, cy);
            if (p.ShowDialog() == true)
            {
                _atlasX.Text = p.PickedX.ToString();
                _atlasY.Text = p.PickedY.ToString();
            }
        }
    }
}
