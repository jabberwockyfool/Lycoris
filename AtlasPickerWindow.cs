using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Lycoris
{
    /// <summary>
    /// Shows the full medal atlas (face_icon.xi) and lets the user click a cell to pick MedalPosX/Y,
    /// like Albatross' MedalWindow. Cells are <c>cell</c>×<c>cell</c> pixels with no padding.
    /// </summary>
    public sealed class AtlasPickerWindow : Window
    {
        private readonly int _cell;
        private readonly Rectangle _highlight;
        public int PickedX { get; private set; }
        public int PickedY { get; private set; }

        public AtlasPickerWindow(Window owner, BitmapSource atlas, int cell, int curX, int curY)
        {
            Owner = owner;
            _cell = cell;
            PickedX = curX;
            PickedY = curY;
            Title = "Choisir la médaille dans l'atlas — clique une cellule";
            Width = 820;
            Height = 700;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var img = new Image
            {
                Source = atlas,
                Stretch = Stretch.None,
                SnapsToDevicePixels = true,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);

            _highlight = new Rectangle
            {
                Width = cell,
                Height = cell,
                Stroke = Brushes.Red,
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            UpdateHighlight();

            var canvas = new Canvas
            {
                Width = atlas.PixelWidth,
                Height = atlas.PixelHeight,
                Background = Brushes.Transparent
            };
            canvas.Children.Add(img);
            canvas.Children.Add(_highlight);
            canvas.MouseLeftButtonDown += Canvas_Click;

            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = canvas
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            var ok = new Button { Content = "Valider", Padding = new Thickness(14, 4, 14, 4), IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            var cancel = new Button { Content = "Annuler", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
            ok.Click += (s, e) => { DialogResult = true; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var root = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(scroll);
            Content = root;
        }

        private void Canvas_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var p = e.GetPosition((IInputElement)sender);
            PickedX = (int)(p.X / _cell);
            PickedY = (int)(p.Y / _cell);
            UpdateHighlight();
        }

        private void UpdateHighlight()
        {
            Canvas.SetLeft(_highlight, PickedX * _cell);
            Canvas.SetTop(_highlight, PickedY * _cell);
        }
    }
}
