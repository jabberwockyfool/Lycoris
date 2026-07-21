using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Standalone skill editor (skill_config / SKILL_CONFIG_INFO): a searchable list of skills with their
    /// name, type, element, power, hits and the other config fields, plus add/delete. Edits are saved into
    /// skill_config (+ skill_text names) inside the mod.
    /// </summary>
    public sealed class SkillEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private readonly ListBox _list = new ListBox();
        private readonly TextBox _search = new TextBox();
        private readonly StackPanel _fields = new StackPanel();
        private readonly TextBlock _status = new TextBlock { Foreground = Brushes.Gray, Margin = new Thickness(4) };
        private readonly TextBlock _countText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, Margin = new Thickness(10, 0, 0, 0) };
        private ICollectionView _view;

        public SkillEditorWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            Owner = owner;
            Title = "Lycoris — Éditeur de skills";
            Width = 720; Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // Toolbar
            var add = new Button { Content = "+ Ajouter", Padding = new Thickness(10, 4, 10, 4) };
            add.Click += (s, e) => AddSkill();
            var dup = new Button { Content = "Dupliquer", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            dup.Click += (s, e) => DuplicateSkill();
            var del = new Button { Content = "Supprimer", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            del.Click += (s, e) => DeleteSkill();
            var save = new Button { Content = "Sauver le mod", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0) };
            save.Click += (s, e) => Save();
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(add);
            toolbar.Children.Add(dup);
            toolbar.Children.Add(del);
            toolbar.Children.Add(save);
            UpdateCount();
            toolbar.Children.Add(_countText);
            DockPanel.SetDock(toolbar, Dock.Top);

            // Left: search + list
            var left = new DockPanel { Width = 260, Margin = new Thickness(6) };
            _search.Margin = new Thickness(0, 0, 0, 4);
            _search.TextChanged += (s, e) => _view?.Refresh();
            DockPanel.SetDock(_search, Dock.Top);
            _list.DisplayMemberPath = "DisplayName";
            _list.SelectionChanged += (s, e) => { _fields.DataContext = _list.SelectedItem; _fields.IsEnabled = _list.SelectedItem != null; };
            _list.GroupStyle.Add(new GroupStyle { HeaderTemplate = BuildGroupHeader() });
            left.Children.Add(_search);
            left.Children.Add(_list);
            DockPanel.SetDock(left, Dock.Left);

            // Right: fields
            _fields.Margin = new Thickness(6);
            BuildFields();
            var right = new ScrollViewer { Content = _fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            DockPanel.SetDock(_status, Dock.Bottom);

            var root = new DockPanel();
            root.Children.Add(toolbar);
            root.Children.Add(_status);
            root.Children.Add(left);
            root.Children.Add(right);
            Content = root;

            _view = CollectionViewSource.GetDefaultView(_db.Skills);
            _view.Filter = Filter;
            _view.SortDescriptions.Add(new SortDescription("CategorySort", ListSortDirection.Ascending));
            _view.SortDescriptions.Add(new SortDescription("DisplayName", ListSortDirection.Ascending));
            _view.GroupDescriptions.Add(new PropertyGroupDescription("CategoryName"));
            // Live shaping: editing a skill's Type/Name moves it to the right group immediately.
            if (_view is ICollectionViewLiveShaping live)
            {
                live.IsLiveGrouping = true; live.LiveGroupingProperties.Add("CategoryName");
                live.IsLiveSorting = true; live.LiveSortingProperties.Add("CategorySort");
                live.LiveSortingProperties.Add("DisplayName");
            }
            _list.ItemsSource = _view;
            if (!_view.IsEmpty) _list.SelectedIndex = 0;
        }

        /// <summary>Group header: category name in bold + item count.</summary>
        private static DataTemplate BuildGroupHeader()
        {
            var sp = new FrameworkElementFactory(typeof(StackPanel));
            sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            sp.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 8, 0, 2));

            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            name.SetValue(TextBlock.ForegroundProperty, Brushes.SteelBlue);

            var count = new FrameworkElementFactory(typeof(TextBlock));
            count.SetBinding(TextBlock.TextProperty, new Binding("ItemCount") { StringFormat = "  ({0})" });
            count.SetValue(TextBlock.ForegroundProperty, Brushes.Gray);

            sp.AppendChild(name);
            sp.AppendChild(count);
            return new DataTemplate { VisualTree = sp };
        }

        private bool Filter(object o)
        {
            string q = _search.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            var s = (SkillInfo)o;
            return (s.Name != null && s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                   || s.SkillIdHex.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void BuildFields()
        {
            _fields.Children.Add(ReadOnlyRow("SkillConfigID", "SkillIdHex"));
            _fields.Children.Add(ReadOnlyRow("NameID", "NameIDHex"));
            _fields.Children.Add(TextRow("Nom", "Name", 260));
            _fields.Children.Add(DescRow());
            _fields.Children.Add(ComboRow("Type", "SkillType", YokaiEnums.SkillTypes));
            _fields.Children.Add(ComboRow("Élément", "Element", YokaiEnums.Attributes));
            _fields.Children.Add(NumRow("Puissance", "Power"));
            _fields.Children.Add(NumRow("Nombre de coups", "Hits"));
            _fields.Children.Add(NumRow("SkillGrowth", "SkillGrowth"));
            _fields.Children.Add(NumRow("SoultChargeSpeed", "SoultChargeSpeed"));
            _fields.Children.Add(NumRow("SoultimateRange", "SoultimateRange"));
            _fields.Children.Add(NumRow("SkillAbility", "SkillAbility"));
            _fields.Children.Add(TextRow("EffectID (hex)", "EffectIDHex", 130));
            _fields.Children.Add(TextRow("BattleAnimation (hex)", "BattleAnimationHex", 130));
            _fields.Children.Add(ReadOnlyRow("DescID", "DescID"));
        }

        // ---------- field builders ----------

        private static UIElement Label(string text) =>
            new TextBlock { Text = text, Width = 150, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };

        private static FrameworkElement ReadOnlyRow(string label, string path)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label(label));
            var tb = new TextBlock { FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
            tb.SetBinding(TextBlock.TextProperty, new Binding(path));
            sp.Children.Add(tb);
            return sp;
        }

        private static FrameworkElement TextRow(string label, string path, double width)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label(label));
            var tb = new TextBox { Width = width };
            tb.SetBinding(TextBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            return sp;
        }

        private static FrameworkElement NumRow(string label, string path) => TextRow(label, path, 90);

        private static FrameworkElement DescRow()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label("Description"));
            var tb = new TextBox { Width = 300, Height = 56, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            tb.SetBinding(TextBox.TextProperty, new Binding("Description") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            return sp;
        }

        private static FrameworkElement ComboRow(string label, string path, System.Collections.IEnumerable source)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            sp.Children.Add(Label(label));
            var cb = new ComboBox { Width = 200, ItemsSource = source, SelectedValuePath = "Key", DisplayMemberPath = "Name" };
            cb.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new Binding(path) { Mode = BindingMode.TwoWay });
            sp.Children.Add(cb);
            return sp;
        }

        // ---------- add / delete / save ----------

        private void UpdateCount() => _countText.Text = $"{_db.Skills.Count} skills";

        private void AddSkill()
        {
            var dlg = new AddSkillDialog(this) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            if (string.IsNullOrWhiteSpace(dlg.SkillName)) return;
            try
            {
                var s = _db.AddSkill(dlg.SkillName, dlg.SkillType);
                _view.Refresh();
                UpdateCount();
                _list.SelectedItem = s;
                _list.ScrollIntoView(s);
                _status.Text = $"Skill ajouté: {s.DisplayName} ({s.SkillIdHex}). Édite puis « Sauver le mod ».";
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Ajout de skill", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void DuplicateSkill()
        {
            var src = _list.SelectedItem as SkillInfo;
            if (src == null) return;
            // flush any pending edit on the source before cloning it
            var f = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            try
            {
                var s = _db.DuplicateSkill(src);
                _view.Refresh();
                UpdateCount();
                _list.SelectedItem = s;
                _list.ScrollIntoView(s);
                _status.Text = $"Dupliqué: {s.DisplayName} ({s.SkillIdHex}). Édite puis « Sauver le mod ».";
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Duplication de skill", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void DeleteSkill()
        {
            var s = _list.SelectedItem as SkillInfo;
            if (s == null) return;
            var confirm = MessageBox.Show(
                $"Supprimer le skill « {s.DisplayName} » ({s.SkillIdHex}) ?\n\n" +
                "Attention: un yo-kai qui l'utilise pointerait dans le vide. Son nom n'est retiré que si aucun " +
                "autre skill ne le partage. À confirmer avec « Sauver le mod ».",
                "Supprimer un skill", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            int idx = _list.SelectedIndex;
            _db.RemoveSkill(s);
            _view.Refresh();
            UpdateCount();
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(idx, _list.Items.Count - 1);
            _status.Text = $"Skill supprimé — {_db.Skills.Count} restants. Sauver pour appliquer.";
        }

        private void Save()
        {
            var f = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            try
            {
                int n = _db.SaveSkills();
                _status.Text = n > 0 ? $"Sauvé — {n} valeur(s) de skills écrites." : "Aucune modification de skill à sauver.";
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Sauvegarde skills", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }

    /// <summary>Small modal asking for a new skill's name and type.</summary>
    internal sealed class AddSkillDialog : Window
    {
        private readonly TextBox _name = new TextBox();
        private readonly ComboBox _type = new ComboBox { ItemsSource = YokaiEnums.SkillTypes, SelectedValuePath = "Key", DisplayMemberPath = "Name" };

        public string SkillName => _name.Text?.Trim();
        public int SkillType => _type.SelectedValue is int k ? k : 1;

        public AddSkillDialog(Window owner)
        {
            Owner = owner;
            Title = "Ajouter un skill";
            Width = 380; Height = 190;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            _type.SelectedIndex = 0;

            var grid = new StackPanel { Margin = new Thickness(12) };
            grid.Children.Add(new TextBlock { Text = "Nom du skill", Foreground = Brushes.DimGray });
            grid.Children.Add(_name);
            grid.Children.Add(new TextBlock { Text = "Type", Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 0) });
            grid.Children.Add(_type);

            var ok = new Button { Content = "Ajouter", IsDefault = true, Width = 90, Margin = new Thickness(0, 12, 6, 0) };
            ok.Click += (s, e) => { DialogResult = !string.IsNullOrWhiteSpace(SkillName); };
            var cancel = new Button { Content = "Annuler", IsCancel = true, Width = 90, Margin = new Thickness(0, 12, 0, 0) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            grid.Children.Add(btns);

            Content = grid;
            Loaded += (s, e) => _name.Focus();
        }
    }
}
