using System.Windows;
using System.Windows.Controls;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Modal dialog to collect the fields for creating (or duplicating) a yo-kai: name, description,
    /// tribe/rank as named dropdowns, and the model (e.g. "y152000"). The model drives the BaseID
    /// (CRC32 of the model) and the creation of blank face_icon / medal_icon to replace later.
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

        public string YokaiName => _name.Text;
        public string Description => _desc.Text;
        public string Model => _model.Text.Trim();
        public int Tribe => _tribe.SelectedValue is int t ? t : 0;
        public int Rank => _rank.SelectedValue is int r ? r : 0;

        public AddYokaiDialog(Window owner, string title = "Add a Yo-kai",
            string name = "", string desc = "", int tribe = 0, int rank = 0, string model = "")
        {
            Owner = owner;
            Title = title;
            Width = 420;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

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
                Text = "e.g. y152000 — sets the BaseID to CRC32(model) and creates a blank face_icon " +
                       "and medal_icon in the mod (data/menu/face_icon, data/menu/medal_icon) to replace later.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Theme.FgMuted,
                Margin = new Thickness(0, 2, 0, 8)
            });

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
                DialogResult = true;
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            Content = panel;
            _name.Focus();
        }
    }
}
