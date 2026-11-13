using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Orbit
{
    /// <summary>
    /// The see-through, topmost window that holds the wheel. It stays alive between uses and is
    /// only hidden, so it can come back instantly; the wheel (<see cref="View"/>) can be moved
    /// to a new window when this one stops reaching the screen.
    /// </summary>
    sealed class WheelWindow : Window
    {
        public readonly DonutView View;
        /// <summary>See-through, but it takes the pointer: a click on it is a click outside the wheel.</summary>
        readonly Canvas _stage = new Canvas();

        /// <summary>A click landed outside the wheel, on the rest of the screen the window covers.</summary>
        public event Action OutsideClicked;

        /// <param name="layered">
        /// A plain window the desktop compositor blends by its alpha is the fast kind: a layered
        /// window is copied off the graphics card every frame, which held opening and closing to
        /// ~85 fps on a 144 Hz display. But only the layered kind kept drawing every time the
        /// laptop woke from sleep, so it is the fallback (and ORBIT_LAYERED=1 forces it).
        /// </param>
        public WheelWindow(DonutView view, bool layered)
        {
            View = view;
            Title = "Orbit";
            WindowStyle = WindowStyle.None;
            // The plain window takes clicks in all of its rectangle, so the wheel decides what a
            // click outside its slices means (it closes). A layered window lets clicks through
            // where it is fully clear, so its stage is all but clear instead: 1/255 opaque.
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
                e.Handled = true; // the app below never sees it
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

        /// <summary>Gives up the wheel, for the window that takes it over; this one is then closed for good.</summary>
        public void Release()
        {
            _check.Stop();
            _stage.Children.Remove(View);
            AllowClose = true;
        }

        public IntPtr Handle { get { return new WindowInteropHelper(this).EnsureHandle(); } }

        /// <summary>Device pixels per DIP for this window.</summary>
        public double DeviceScale
        {
            get
            {
                var source = PresentationSource.FromVisual(this);
                if (source != null && source.CompositionTarget != null) return source.CompositionTarget.TransformToDevice.M11;
                return VisualTreeHelper.GetDpi(this).DpiScaleX;
            }
        }

        /// <summary>Covers <paramref name="area"/> and puts the wheel's corner at <paramref name="wheel"/> (both on the screen, in DIPs).</summary>
        public void Arrange(Rect area, Point wheel)
        {
            Left = area.Left;
            Top = area.Top;
            Width = area.Width;
            Height = area.Height;
            Canvas.SetLeft(View, wheel.X - area.Left);
            Canvas.SetTop(View, wheel.Y - area.Top);
        }

        // ----- making sure the wheel reaches the screen ----------------------------------------
        //
        // After the laptop woke from sleep, the plain (compositor-blended) window sometimes never
        // reached the screen again: invisible, but taking clicks. WPF went on drawing it (in
        // software too, resized too), while new windows of the same Orbit drew fine. So the screen
        // under the hub is read as the wheel shows and again once it has opened: if nothing there
        // changed, the wheel isn't on screen and <see cref="Broken"/> has the host move it to a new
        // window. A read takes ~7 ms, so it runs off the UI thread: neither the opening nor an
        // open wheel waits for it. It all goes to the log.

        /// <summary>The wheel was shown but didn't reach the screen.</summary>
        public event Action Broken;

        volatile int[] _under;
        int _shows;
        DateTime _shown;
        readonly System.Windows.Threading.DispatcherTimer _check = new System.Windows.Threading.DispatcherTimer();

        /// <summary>
        /// Reads the screen where the hub goes as the wheel shows (the wheel starts see-through, so
        /// the read sees what is under it); the check follows once it is open.
        /// </summary>
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

        /// <summary>Checks that the wheel came up on screen once it has had time to open.</summary>
        public void AfterShow()
        {
            if (_check.Tag == null)
            {
                _check.Tag = this;
                _check.Tick += delegate { _check.Stop(); CheckShown(); };
            }
            _shown = DateTime.UtcNow;
            _check.Interval = TimeSpan.FromMilliseconds(300); // the hub is whole 0.12 s into the opening
            _check.Start();
        }

        void CheckShown()
        {
            int[] under = _under;
            if (under == null || !IsVisible || View.IsClosing) return;
            if (View.FramesSinceOpen < 12 && DateTime.UtcNow - _shown < TimeSpan.FromSeconds(2))
            {
                // Orbit is busy (just started, say) and the opening hasn't run yet: wait for it.
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

        /// <summary>What the host did to bring the wheel back, for the log line of the next check.</summary>
        public string Repaired;

        /// <summary>The hub's probe in screen pixels, from where the window and the wheel are put (no layout needed).</summary>
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

        /// <summary>WPF's own idea of whether it draws this window, for the log.</summary>
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
            // The window is reused: closing it (Alt+F4, the system) only dismisses the wheel.
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
