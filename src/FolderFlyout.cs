using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Orbit
{
    sealed class FolderFlyout
    {
        const double PanelWidth = 264, MaxListHeight = 400;
        readonly Popup _popup;
        readonly Grid _frame = new Grid();
        readonly Border _panel;
        readonly ListBox _list;
        readonly IconCache _icons;

        public event Action<string> FolderChosen;
        public event Action Closed;

        public FolderFlyout(UIElement target, IconCache icons)
        {
            _icons = icons;
            _list = new ListBox
            {
                ItemTemplate = (DataTemplate)Application.Current.Resources["PickerItem"],
                MaxHeight = MaxListHeight,
            };
            _list.PreviewMouseLeftButtonUp += (s, e) =>
            {
                var item = ItemUnder(e.OriginalSource as DependencyObject);
                if (item != null) Choose(item);
            };
            _list.PreviewKeyDown += OnKey;
            _list.PreviewMouseWheel += OnWheel;
            _panel = new Border
            {
                Width = PanelWidth,
                Padding = new Thickness(6),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Background = (Brush)Application.Current.Resources["Panel"],
                BorderBrush = (Brush)Application.Current.Resources["Rim"],
                Child = _list,
            };
            TextElement.SetForeground(_panel, (Brush)Application.Current.Resources["Ink"]);
            TextElement.SetFontFamily(_panel, (FontFamily)Application.Current.Resources["UiFont"]);
            _frame.Children.Add(_panel);
            _popup = new Popup
            {
                Child = _frame,
                PlacementTarget = target,
                Placement = PlacementMode.Relative,
                AllowsTransparency = true,
                StaysOpen = false,
                Focusable = false,
            };
            _popup.Closed += delegate { if (Closed != null) Closed(); };
        }

        public bool IsOpen { get { return _popup.IsOpen; } }

        public void Show(string root, Point tip, Vector direction)
        {
            _list.ItemsSource = Items(root);
            _list.SelectedIndex = 0;
            _panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = PanelWidth, h = _panel.DesiredSize.Height;
            Point a = tip + direction * 6;

            double x, y, originX = 0.5;
            if (direction.X < -0.35) { x = a.X - w; y = a.Y - h / 2; originX = 1; }
            else if (direction.X > 0.35) { x = a.X; y = a.Y - h / 2; originX = 0; }
            else { x = a.X - w / 2; y = a.Y - h; }
            var target = (UIElement)_popup.PlacementTarget;
            Point screen = target.PointToScreen(new Point(x, y));
            var source = PresentationSource.FromVisual(target);
            if (source != null && source.CompositionTarget != null) screen = source.CompositionTarget.TransformFromDevice.Transform(screen);
            _popup.Placement = PlacementMode.Absolute;
            _popup.HorizontalOffset = screen.X;
            _popup.VerticalOffset = screen.Y;

            _scrollPos = _scrollTarget = 0;
            if (Scroller() != null) _scroller.ScrollToTop();
            _popup.IsOpen = true;
            Unroll(originX, w, h);
            _list.Focus();
        }

        void Unroll(double originX, double w, double h)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Rect closed = originX == 1 ? new Rect(w, 0, 0, h) : originX == 0 ? new Rect(0, 0, 0, h) : new Rect(0, h, w, 0);
            var clip = new RectangleGeometry(closed, 12, 12);
            _panel.Clip = clip;
            var open = new RectAnimation(closed, new Rect(0, 0, w, h), TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
            open.Completed += delegate { _panel.Clip = null; };
            clip.BeginAnimation(RectangleGeometry.RectProperty, open);

            double dx = originX == 1 ? 18 : originX == 0 ? -18 : 0, dy = originX == 0.5 ? 14 : 0;
            _list.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    var row = _list.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
                    if (row == null) continue;
                    var shift = new TranslateTransform(dx, dy);
                    row.RenderTransform = shift;
                    row.Opacity = 0;
                    var begin = TimeSpan.FromMilliseconds(30 + i * 22);
                    row.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = begin });
                    shift.BeginAnimation(dx != 0 ? TranslateTransform.XProperty : TranslateTransform.YProperty,
                        new DoubleAnimation(dx != 0 ? dx : dy, 0, TimeSpan.FromMilliseconds(260)) { BeginTime = begin, EasingFunction = ease });
                }
            }));
        }

        ScrollViewer _scroller;
        double _scrollPos, _scrollTarget, _scrollLast;
        bool _scrolling;
        readonly System.Diagnostics.Stopwatch _scrollClock = System.Diagnostics.Stopwatch.StartNew();

        ScrollViewer Scroller()
        {
            if (_scroller != null) return _scroller;
            for (DependencyObject d = _list; d != null; d = VisualTreeHelper.GetChildrenCount(d) > 0 ? VisualTreeHelper.GetChild(d, 0) : null)
            {
                _scroller = d as ScrollViewer;
                if (_scroller != null)
                {
                    _scroller.CanContentScroll = false;
                    break;
                }
            }
            return _scroller;
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            ScrollViewer sv = Scroller();
            if (sv == null) return;
            e.Handled = true;
            if (!_scrolling) _scrollPos = _scrollTarget = sv.VerticalOffset;
            _scrollTarget = Math.Max(0, Math.Min(sv.ScrollableHeight, _scrollTarget - e.Delta * 0.6));
            if (_scrolling) return;
            _scrolling = true;
            _scrollLast = _scrollClock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += ScrollFrame;
        }

        void ScrollFrame(object sender, EventArgs e)
        {
            double now = _scrollClock.Elapsed.TotalSeconds, dt = Math.Min(0.05, now - _scrollLast);
            _scrollLast = now;
            _scrollPos += (_scrollTarget - _scrollPos) * (1 - Math.Exp(-dt * 16));
            if (Math.Abs(_scrollTarget - _scrollPos) < 0.4 || !_popup.IsOpen)
            {
                _scrollPos = _scrollTarget;
                _scrolling = false;
                CompositionTarget.Rendering -= ScrollFrame;
            }
            _scroller.ScrollToVerticalOffset(_scrollPos);
        }

        public void Close()
        {
            _popup.IsOpen = false;
        }

        List<PickerItem> Items(string root)
        {
            var items = new List<PickerItem>();
            string label = root;
            try
            {
                if (Path.GetPathRoot(root) == root)
                {
                    var drive = new DriveInfo(root);
                    string name = drive.IsReady && drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : "Local Disk";
                    label = name + " (" + root.TrimEnd('\\') + ")";
                }
                else label = Path.GetFileName(root.TrimEnd('\\'));
            }
            catch (IOException) { }
            catch (ArgumentException) { }
            items.Add(Item(label, root, "Open"));

            try
            {
                var folders = new List<DirectoryInfo>(new DirectoryInfo(root).GetDirectories());
                folders.RemoveAll(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0);
                folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                foreach (var folder in folders) items.Add(Item(folder.Name, folder.FullName, null));
            }
            catch (Exception ex)
            {
                Log.Write("Folders of " + root + ": " + ex.Message);
            }
            return items;
        }

        PickerItem Item(string name, string path, string status)
        {
            var item = new PickerItem { Name = name, Path = path, Status = status };
            _icons.Request(path, 48, img => item.Icon = img);
            return item;
        }

        PickerItem ItemUnder(DependencyObject d)
        {
            for (; d != null && d != _list; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            {
                var container = d as ListBoxItem;
                if (container != null) return container.DataContext as PickerItem;
            }
            return null;
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && _list.SelectedItem is PickerItem)
            {
                Choose((PickerItem)_list.SelectedItem);
                e.Handled = true;
            }
        }

        void Choose(PickerItem item)
        {
            Close();
            if (FolderChosen != null) FolderChosen(item.Path);
        }
    }
}
