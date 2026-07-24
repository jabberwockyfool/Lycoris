using System.Windows;
using System.Windows.Controls;

namespace Lycoris
{
    /// <summary>Minimal modal dialog to collect the fields needed to create a new yo-kai.</summary>
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
        private readonly TextBox _tribe = new TextBox { Text = "0", Margin = new Thickness(0, 2, 0, 8) };
        private readonly TextBox _rank = new TextBox { Text = "0", Margin = new Thickness(0, 2, 0, 8) };

        public string YokaiName => _name.Text;
        public string Description => _desc.Text;
        public int Tribe => int.TryParse(_tribe.Text, out int t) ? t : 0;
        public int Rank => int.TryParse(_rank.Text, out int r) ? r : 0;

        public AddYokaiDialog(Window owner)
        {
            Owner = owner;
            Title = "Add a Yo-kai";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = "Name" });
            panel.Children.Add(_name);
            panel.Children.Add(new TextBlock { Text = "Description" });
            panel.Children.Add(_desc);

            var stats = new Grid();
            stats.ColumnDefinitions.Add(new ColumnDefinition());
            stats.ColumnDefinitions.Add(new ColumnDefinition());
            var tribeLbl = new TextBlock { Text = "Tribe (index)" };
            var rankLbl = new TextBlock { Text = "Rank (index)" };
            Grid.SetColumn(tribeLbl, 0); Grid.SetColumn(rankLbl, 1);
            var tribeStack = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
            tribeStack.Children.Add(tribeLbl); tribeStack.Children.Add(_tribe);
            var rankStack = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
            rankStack.Children.Add(rankLbl); rankStack.Children.Add(_rank);
            Grid.SetColumn(tribeStack, 0); Grid.SetColumn(rankStack, 1);
            stats.Children.Add(tribeStack); stats.Children.Add(rankStack);
            panel.Children.Add(stats);

            panel.Children.Add(new TextBlock
            {
                Text = "Stats are copied from an existing template — edit them afterwards in the grid.",
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
                    DarkMessage.Show("The name is required.", "Add a Yo-kai");
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
