using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Orbit
{
    /// <summary>A borderless rounded panel in the wheel's dark glass; drag it by its background.</summary>
    class GlassWindow : Window
    {
        protected readonly Border Panel;

        public GlassWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            UseLayoutRounding = true;
            FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
            Foreground = (Brush)Application.Current.Resources["Ink"];
            Panel = new Border
            {
                Background = (Brush)Application.Current.Resources["Panel"],
                BorderBrush = (Brush)Application.Current.Resources["Rim"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(16),
                Margin = new Thickness(18),
                Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.5 },
            };
            Content = Panel;
            SourceInitialized += delegate { Native.MakeToolWindow(new WindowInteropHelper(this).Handle); };
            MouseLeftButtonDown += (s, e) => { if (IsBackground(e.OriginalSource as DependencyObject)) DragMove(); };
        }

        static bool IsBackground(DependencyObject d)
        {
            for (; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ListBox || d is TextBox || d is System.Windows.Controls.Primitives.ButtonBase || d is System.Windows.Controls.Primitives.ScrollBar)
                    return false;
            return true;
        }

        protected static Button MakeButton(string text, bool primary)
        {
            return new Button
            {
                Content = text,
                Style = (Style)Application.Current.Resources[primary ? "OrbitPrimaryButton" : "OrbitButton"],
            };
        }

        protected static TextBlock Heading(string text)
        {
            return new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        }
    }

    /// <summary>One line of text, e.g. a new name for a slice.</summary>
    sealed class PromptWindow : GlassWindow
    {
        readonly TextBox _box;

        public PromptWindow(string title, string value)
        {
            Width = 380;
            SizeToContent = SizeToContent.Height;
            var stack = new StackPanel();
            stack.Children.Add(Heading(title));
            _box = new TextBox { Text = value ?? "", Margin = new Thickness(0, 12, 0, 14) };
            stack.Children.Add(_box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = MakeButton("Cancel", false);
            var ok = MakeButton("Save", true);
            ok.Margin = new Thickness(8, 0, 0, 0);
            cancel.IsCancel = true;
            ok.IsDefault = true;
            ok.Click += delegate { DialogResult = true; };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            stack.Children.Add(buttons);
            Panel.Child = stack;
            Loaded += delegate
            {
                _box.Focus();
                _box.SelectAll();
            };
        }

        public string Value { get { return _box.Text; } }
    }

    public sealed class PickerItem : INotifyPropertyChanged
    {
        ImageSource _icon;
        string _status;

        public string Name { get; set; }
        public string Path { get; set; }

        public ImageSource Icon
        {
            get { return _icon; }
            set { _icon = value; Raise("Icon"); }
        }

        public string Status
        {
            get { return _status; }
            set { _status = value; Raise("Status"); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        void Raise(string name)
        {
            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Every Start menu app with its icon, searchable; double-click puts one on the wheel.</summary>
    sealed class PickerWindow : GlassWindow
    {
        const string OnWheel = "On the wheel";
        readonly Func<string, bool> _contains;
        readonly Action<AppEntry> _add;
        readonly IconCache _icons;
        readonly ObservableCollection<PickerItem> _items = new ObservableCollection<PickerItem>();
        readonly ICollectionView _view;
        readonly TextBox _search;
        readonly ListBox _list;
        readonly TextBlock _note;
        volatile bool _closed;

        public PickerWindow(Func<string, bool> contains, Action<AppEntry> add, IconCache icons)
        {
            _contains = contains;
            _add = add;
            _icons = icons;
            Title = "Add apps to Orbit";
            Width = 460;
            Height = 620;

            var dock = new DockPanel { LastChildFill = true };

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var close = MakeButton("✕", false);
            close.Padding = new Thickness(9, 3, 9, 3);
            close.Click += delegate { Close(); };
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            header.Children.Add(Heading("Add apps"));
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);

            _note = new TextBlock
            {
                Text = "Double-click an app to put it on the wheel. You can also drop files on the wheel while this is open.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["InkSoft"],
                Margin = new Thickness(0, 4, 0, 12),
            };
            DockPanel.SetDock(_note, Dock.Top);
            dock.Children.Add(_note);

            _search = new TextBox { Tag = "Search apps", Margin = new Thickness(0, 0, 0, 10) };
            DockPanel.SetDock(_search, Dock.Top);
            dock.Children.Add(_search);

            var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            var browse = MakeButton("Browse for a file…", false);
            var done = MakeButton("Done", true);
            browse.Click += delegate { Browse(); };
            done.Click += delegate { Close(); };
            DockPanel.SetDock(done, Dock.Right);
            footer.Children.Add(done);
            browse.HorizontalAlignment = HorizontalAlignment.Left;
            footer.Children.Add(browse);
            DockPanel.SetDock(footer, Dock.Bottom);
            dock.Children.Add(footer);

            _list = new ListBox { ItemTemplate = (DataTemplate)Application.Current.Resources["PickerItem"], ItemsSource = _items };
            dock.Children.Add(_list);
            Panel.Child = dock;

            _view = CollectionViewSource.GetDefaultView(_items);
            _view.Filter = Matches;
            _search.TextChanged += delegate { _view.Refresh(); SelectFirst(); };
            _search.PreviewKeyDown += OnSearchKey;
            _list.MouseDoubleClick += delegate { AddSelected(); };
            _list.PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter) { AddSelected(); e.Handled = true; } };
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
            Loaded += delegate { _search.Focus(); StartLoading(); };
            Closed += delegate { _closed = true; };
        }

        bool Matches(object o)
        {
            string q = _search.Text.Trim();
            return q.Length == 0 || ((PickerItem)o).Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        void SelectFirst()
        {
            _view.MoveCurrentToFirst();
            _list.SelectedItem = _view.CurrentItem;
            if (_list.SelectedItem != null) _list.ScrollIntoView(_list.SelectedItem);
        }

        void OnSearchKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { AddSelected(); e.Handled = true; }
            else if (e.Key == Key.Down || e.Key == Key.Up)
            {
                int i = _list.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                if (i >= 0 && i < _list.Items.Count)
                {
                    _list.SelectedIndex = i;
                    _list.ScrollIntoView(_list.SelectedItem);
                }
                e.Handled = true;
            }
        }

        void AddSelected()
        {
            var item = (_list.SelectedItem ?? _view.CurrentItem) as PickerItem;
            if (item == null) return;
            if (_contains(item.Path))
            {
                item.Status = OnWheel;
                return;
            }
            _add(new AppEntry { Name = item.Name, Path = item.Path });
            item.Status = "Added ✓";
        }

        void Browse()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Add to Orbit",
                Filter = "Apps and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd;*.ps1;*.msc|All files|*.*",
                Multiselect = true,
                DereferenceLinks = false,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            };
            if (dialog.ShowDialog(this) != true) return;
            foreach (string file in dialog.FileNames)
                if (!_contains(file)) _add(new AppEntry { Name = AppNames.FromPath(file), Path = file });
            _note.Text = dialog.FileNames.Length == 1 ? "Added " + AppNames.FromPath(dialog.FileNames[0]) + "." : "Added " + dialog.FileNames.Length + " files.";
        }

        /// <summary>The Start menu is read on a background STA thread; icons come from the shared cache.</summary>
        void StartLoading()
        {
            var ui = Dispatcher;
            var thread = new Thread(() =>
            {
                var apps = AppCatalog.Enumerate();
                ui.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;
                    foreach (var app in apps)
                    {
                        var item = new PickerItem { Name = app.Name, Path = app.Path, Status = _contains(app.Path) ? OnWheel : null };
                        _items.Add(item);
                        _icons.Request(app.Path, 48, img => item.Icon = img);
                    }
                    SelectFirst();
                }));
            });
            thread.IsBackground = true;
            thread.Name = "Orbit picker";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }
}
