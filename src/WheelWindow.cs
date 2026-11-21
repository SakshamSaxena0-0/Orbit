using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Orbit
{
    sealed class WheelWindow : Window
    {
        public readonly DonutView View;
        readonly Canvas _stage = new Canvas();

        public event Action OutsideClicked;

        public WheelWindow(DonutView view, bool layered)
        {
            View = view;
            Title = "Orbit";
            WindowStyle = WindowStyle.None;
            UsesDwm = !layered && Environment.GetEnvironmentVariable("ORBIT_LAYERED") != "1";
            AllowsTransparency = !UsesDwm;
            _stage.Background = UsesDwm ? Brushes.Transparent : NearlyClear;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Manual;
            ShowInTaskbar = false;
            ShowActivated = true;
            Topmost = true;
            _stage.Children.Add(View);
            _stage.MouseDown += (s, e) =>
            {
                if (e.OriginalSource != _stage) return;
                e.Handled = true;
                if (OutsideClicked != null) OutsideClicked();
            };
            Content = _stage;
            SourceInitialized += delegate
            {
                Native.MakeToolWindow(Handle);
                if (UsesDwm) Native.SeeThroughByCompositor(Handle);
            };
            PreviewKeyDown += OnPreviewKeyDown;
        }

        public readonly bool UsesDwm;

        static readonly Brush NearlyClear = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

        public void Release()
        {
            _check.Stop();
            _stage.Children.Remove(View);
            AllowClose = true;
        }

        public IntPtr Handle { get { return new WindowInteropHelper(this).EnsureHandle(); } }

        public double DeviceScale
        {
            get
            {
                var source = PresentationSource.FromVisual(this);
                if (source != null && source.CompositionTarget != null) return source.CompositionTarget.TransformToDevice.M11;
                return VisualTreeHelper.GetDpi(this).DpiScaleX;
            }
        }

        public void Arrange(Rect area, Point wheel)
        {
            Left = area.Left;
            Top = area.Top;
            Width = area.Width;
            Height = area.Height;
            Canvas.SetLeft(View, wheel.X - area.Left);
            Canvas.SetTop(View, wheel.Y - area.Top);
        }

        public event Action Broken;

        volatile int[] _under;
        int _shows;
        DateTime _shown;
        readonly System.Windows.Threading.DispatcherTimer _check = new System.Windows.Threading.DispatcherTimer();

        public void BeforeShow()
        {
            _check.Stop();
            _under = null;
            int show = ++_shows;
            if (IsVisible) return;
            Native.RECT probe = HubOnScreen();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                int[] under = Native.CaptureScreen(probe);
                if (show == _shows) _under = under;
            });
        }

        public void AfterShow()
        {
            if (_check.Tag == null)
            {
                _check.Tag = this;
                _check.Tick += delegate { _check.Stop(); CheckShown(); };
            }
            _shown = DateTime.UtcNow;
            _check.Interval = TimeSpan.FromMilliseconds(300);
            _check.Start();
        }

        void CheckShown()
        {
            int[] under = _under;
            if (under == null || !IsVisible || View.IsClosing) return;
            if (View.FramesSinceOpen < 12 && DateTime.UtcNow - _shown < TimeSpan.FromSeconds(2))
            {
                _check.Interval = TimeSpan.FromMilliseconds(120);
                _check.Start();
                return;
            }
            Native.RECT probe = HubOnScreen();
            int show = _shows;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                int[] now = Native.CaptureScreen(probe);
                bool missing = now != null && Same(now, under);
                Dispatcher.BeginInvoke(new Action(() => { if (show == _shows) Checked(missing); }));
            });
        }

        void Checked(bool missing)
        {
            if (!IsVisible || View.IsClosing) return;
            if (Repaired != null)
            {
                Log.Write(missing ? "wheel: not on screen after " + Repaired : "wheel: on screen after " + Repaired);
                Repaired = null;
            }
            if (!missing) return;
            Log.Write("wheel: shown but not on screen (" + (UsesDwm ? "plain" : "layered") + " window, " + RenderState() + ")");
            if (Broken != null) Broken();
        }

        public string Repaired;

        Native.RECT HubOnScreen()
        {
            Rect r = View.HubProbe;
            double s = DeviceScale, x = Left + Canvas.GetLeft(View) + r.X, y = Top + Canvas.GetTop(View) + r.Y;
            return new Native.RECT
            {
                Left = (int)Math.Round(x * s), Top = (int)Math.Round(y * s),
                Right = (int)Math.Round((x + r.Width) * s), Bottom = (int)Math.Round((y + r.Height) * s)
            };
        }

        static bool Same(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        string RenderState()
        {
            try
            {
                var target = HwndSource.FromHwnd(Handle).CompositionTarget;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Func<string, object> field = name =>
                {
                    var f = typeof(HwndTarget).GetField(name, flags);
                    return f != null ? f.GetValue(target) : "?";
                };
                return string.Format("tier {0}, drawing on {1}, suspended {2}, session off {3}, displays {4}, mode {5}",
                    RenderCapability.Tier >> 16, field("_isRenderTargetEnabled"), field("_isSuspended"),
                    field("_isSessionDisconnected"), field("_displayDevicesAvailable"), target.RenderMode);
            }
            catch (Exception ex) { return ex.GetType().Name; }
        }

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.F4 && (Keyboard.Modifiers & ModifierKeys.Alt) != 0) key = Key.Escape;
            if (View.HandleKey(key, Keyboard.Modifiers)) e.Handled = true;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!AllowClose)
            {
                e.Cancel = true;
                View.HandleKey(Key.Escape, ModifierKeys.None);
            }
            base.OnClosing(e);
        }

        public bool AllowClose;
    }
}
