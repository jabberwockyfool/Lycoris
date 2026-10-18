using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Lycoris.Yokai;

namespace Lycoris
{
    /// <summary>
    /// Fusion / evolution-recipe editor (combine_config / COMBINE_INFO). Each recipe combines a Base and a
    /// Material into a Result; each of the three is either a yo-kai (chara_param ParamID) or an item, chosen
    /// with the "Item" checkbox next to it. A GlobalBitFlagID gates the recipe behind a story flag. Add,
    /// duplicate, delete and save back into the mod's combine_config.
    /// </summary>
    public sealed class CombineEditorWindow : Window
    {
        private readonly YokaiDatabase _db;
        private readonly ListBox _list = new ListBox();
        private readonly TextBox _search = new TextBox();
        private readonly StackPanel _fields = new StackPanel();
        private readonly TextBlock _status = new TextBlock { Foreground = Theme.FgMuted, Margin = new Thickness(4) };
        private readonly TextBlock _countText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted, Margin = new Thickness(10, 0, 0, 0) };
        private System.ComponentModel.ICollectionView _view;

        public CombineEditorWindow(Window owner, YokaiDatabase db)
        {
            _db = db;
            Owner = owner;
            Title = "Lycoris — Fusion Editor";
            Width = 760; Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var add = TbBtn("+ Add", AddRecipe, 0);
            var dup = TbBtn("Duplicate", DuplicateRecipe, 6);
            var del = TbBtn("Delete", DeleteRecipe, 6);
            var save = TbBtn("Save the mod", Save, 6);
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
            toolbar.Children.Add(add); toolbar.Children.Add(dup); toolbar.Children.Add(del); toolbar.Children.Add(save);
            UpdateCount(); toolbar.Children.Add(_countText);
            DockPanel.SetDock(toolbar, Dock.Top);

            var left = new DockPanel { Width = 300, Margin = new Thickness(6) };
            _search.Margin = new Thickness(0, 0, 0, 4);
            _search.TextChanged += (s, e) => _view?.Refresh();
            DockPanel.SetDock(_search, Dock.Top);
            _list.DisplayMemberPath = "DisplayName";
            _list.SelectionChanged += (s, e) => { _fields.DataContext = _list.SelectedItem; _fields.IsEnabled = _list.SelectedItem != null; };
            left.Children.Add(_search);
            left.Children.Add(_list);
            DockPanel.SetDock(left, Dock.Left);

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

            _view = CollectionViewSource.GetDefaultView(_db.Combines);
            _view.Filter = Filter;
            _list.ItemsSource = _view;
            if (!_view.IsEmpty) _list.SelectedIndex = 0;
            _status.Text = "Base + Material → Result. Tick “Item” when a slot is an item rather than a yo-kai.";
        }

        private Button TbBtn(string text, Action onClick, double leftMargin)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(leftMargin, 0, 0, 0) };
            b.Click += (s, e) => onClick();
            return b;
        }

        private bool Filter(object o)
        {
            string q = _search.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            var r = (CombineRecipe)o;
            return r.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                   || r.FlagHex.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void BuildFields()
        {
            _fields.Children.Add(PartRow("Base", "BaseIsItem", "BaseId", "BaseOptions"));
            _fields.Children.Add(PartRow("Material", "MaterialIsItem", "MaterialId", "MaterialOptions"));
            _fields.Children.Add(PartRow("Result", "ResultIsItem", "ResultId", "ResultOptions"));
            _fields.Children.Add(new Border { Height = 1, Background = Theme.Border, Margin = new Thickness(0, 8, 0, 8) });
            _fields.Children.Add(TextRow("Unlock flag (hex)", "FlagHex", 150));
            _fields.Children.Add(NumRow("Fusion type", "FusionType", "Observed values: 0, 1, 3, 6"));
        }

        // ---------- field builders ----------

        private static UIElement Label(string text) =>
            new TextBlock { Text = text, Width = 130, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.FgMuted };

        private static FrameworkElement PartRow(string label, string isItemPath, string idPath, string optionsPath)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            sp.Children.Add(Label(label));

            var chk = new CheckBox { Content = "Item", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            chk.SetBinding(CheckBox.IsCheckedProperty, new Binding(isItemPath) { Mode = BindingMode.TwoWay });
            sp.Children.Add(chk);

            var cb = new ComboBox { Width = 320, IsTextSearchEnabled = true, DisplayMemberPath = "Name", SelectedValuePath = "Key" };
            TextSearch.SetTextPath(cb, "Name");
            cb.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(optionsPath));
            cb.SetBinding(Selector.SelectedValueProperty, new Binding(idPath) { Mode = BindingMode.TwoWay });
            sp.Children.Add(cb);
            return sp;
        }

        private static FrameworkElement TextRow(string label, string path, double width)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            sp.Children.Add(Label(label));
            var tb = new TextBox { Width = width };
            tb.SetBinding(TextBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            return sp;
        }

        private static FrameworkElement NumRow(string label, string path, string hint)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            sp.Children.Add(Label(label));
            var tb = new TextBox { Width = 80 };
            tb.SetBinding(TextBox.TextProperty, new Binding(path) { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
            sp.Children.Add(tb);
            sp.Children.Add(new TextBlock { Text = hint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.6 });
            return sp;
        }

        // ---------- add / duplicate / delete / save ----------

        private void UpdateCount() => _countText.Text = $"{_db.Combines.Count} recipes";

        private void AddRecipe()
        {
            try
            {
                var r = _db.AddCombine();
                _view.Refresh(); UpdateCount();
                _list.SelectedItem = r; _list.ScrollIntoView(r);
                _status.Text = "Recipe added. Pick Base / Material / Result, then “Save the mod”.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Add recipe", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void DuplicateRecipe()
        {
            var src = _list.SelectedItem as CombineRecipe;
            if (src == null) return;
            var f = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            try
            {
                var r = _db.AddCombine();
                r.BaseIsItem = src.BaseIsItem; r.BaseId = src.BaseId;
                r.MaterialIsItem = src.MaterialIsItem; r.MaterialId = src.MaterialId;
                r.ResultIsItem = src.ResultIsItem; r.ResultId = src.ResultId;
                r.FlagId = src.FlagId; r.FusionType = src.FusionType;
                _view.Refresh(); UpdateCount();
                _list.SelectedItem = r; _list.ScrollIntoView(r);
                _status.Text = "Recipe duplicated. Edit then “Save the mod”.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Duplicate recipe", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void DeleteRecipe()
        {
            var r = _list.SelectedItem as CombineRecipe;
            if (r == null) return;
            var confirm = DarkMessage.Show(
                $"Delete this recipe?\n\n{r.DisplayName}\n\nConfirm with “Save the mod”.",
                "Delete a recipe", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;

            int idx = _list.SelectedIndex;
            _db.RemoveCombine(r);
            _view.Refresh(); UpdateCount();
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(idx, _list.Items.Count - 1);
            _status.Text = $"Recipe deleted — {_db.Combines.Count} remaining. Save to apply.";
        }

        private void Save()
        {
            var f = System.Windows.Input.Keyboard.FocusedElement as UIElement;
            f?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
            try
            {
                int n = _db.SaveCombines();
                _status.Text = n > 0 ? $"Saved — {n} recipe value(s) written." : "No recipe changes to save.";
            }
            catch (Exception ex) { DarkMessage.Show(ex.Message, "Save recipes", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }
}
