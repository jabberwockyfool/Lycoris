using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Integrity checker: lists dangling references (a move → a missing skill, a drop → a missing item, an
    /// evolution → a missing yo-kai…) and duplicate keys. Problems introduced this session (since the folder
    /// loaded) are shown by default; pre-existing problems in the original files are hidden behind a toggle.
    /// </summary>
    public sealed class IntegrityWindow : Window
    {
        private readonly YokaiDatabase _db;
        private readonly ListView _list = new ListView();
        private readonly TextBlock _summary = new TextBlock { Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap };
        private readonly CheckBox _showPre = new CheckBox { Content = "Also show pre-existing issues (original files)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        private ICollectionView _view;
        private System.Collections.Generic.List<IntegrityIssue> _issues;

        public IntegrityWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            Owner = owner;
            Title = "Lycoris — Integrity checker";
            Width = 780; Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var recheck = new Button { Content = "Re-check", Padding = new Thickness(10, 4, 10, 4) };
            recheck.Click += (s, e) => Run();
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(recheck);
            _showPre.Checked += (s, e) => _view?.Refresh();
            _showPre.Unchecked += (s, e) => _view?.Refresh();
            toolbar.Children.Add(_showPre);
            DockPanel.SetDock(toolbar, Dock.Top);
            DockPanel.SetDock(_summary, Dock.Top);

            _list.View = BuildColumns();
            _list.GroupStyle.Add(new GroupStyle { HeaderTemplate = GroupHeader() });
            // Colour error rows.
            var itemStyle = new Style(typeof(ListViewItem));
            var trig = new DataTrigger { Binding = new Binding("LevelText"), Value = "Error" };
            trig.Setters.Add(new Setter(ForegroundProperty, Theme.Error));
            itemStyle.Triggers.Add(trig);
            _list.ItemContainerStyle = itemStyle;

            var root = new DockPanel();
            root.Children.Add(toolbar);
            root.Children.Add(_summary);
            root.Children.Add(_list);
            Content = root;

            Run();
        }

        private void Run()
        {
            _issues = _db.CheckIntegrity();
            int newErr = _issues.Count(i => !i.Preexisting && i.Level == IssueLevel.Error);
            int newWarn = _issues.Count(i => !i.Preexisting && i.Level == IssueLevel.Warning);
            int pre = _issues.Count(i => i.Preexisting);

            if (newErr + newWarn == 0)
                _summary.Text = pre == 0
                    ? "✔ No issues detected."
                    : $"✔ No issues introduced by your changes.  ({pre} pre-existing issue(s) in the original files — tick the box to see them.)";
            else
                _summary.Text = $"⚠ {newErr} error(s) and {newWarn} warning(s) introduced by your changes.  ({pre} pre-existing.)";

            _view = CollectionViewSource.GetDefaultView(_issues);
            _view.Filter = o => _showPre.IsChecked == true || !((IntegrityIssue)o).Preexisting;
            _view.SortDescriptions.Add(new SortDescription("Level", ListSortDirection.Ascending));
            _view.SortDescriptions.Add(new SortDescription("Category", ListSortDirection.Ascending));
            _view.SortDescriptions.Add(new SortDescription("Subject", ListSortDirection.Ascending));
            _view.GroupDescriptions.Add(new PropertyGroupDescription("Category"));
            _list.ItemsSource = _view;
        }

        private static GridView BuildColumns()
        {
            var gv = new GridView();
            gv.Columns.Add(Col("Level", "LevelText", 90));
            gv.Columns.Add(Col("Origin", "OriginText", 90));
            gv.Columns.Add(Col("Subject", "Subject", 260));
            gv.Columns.Add(Col("Issue", "Detail", 300));
            return gv;
        }

        private static GridViewColumn Col(string header, string path, double width) =>
            new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new Binding(path) };

        private static DataTemplate GroupHeader()
        {
            var sp = new FrameworkElementFactory(typeof(StackPanel));
            sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            sp.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 8, 0, 2));
            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            name.SetValue(TextBlock.ForegroundProperty, Theme.Accent);
            var count = new FrameworkElementFactory(typeof(TextBlock));
            count.SetBinding(TextBlock.TextProperty, new Binding("ItemCount") { StringFormat = "  ({0})" });
            count.SetValue(TextBlock.ForegroundProperty, Theme.FgMuted);
            sp.AppendChild(name);
            sp.AppendChild(count);
            return new DataTemplate { VisualTree = sp };
        }
    }
}
