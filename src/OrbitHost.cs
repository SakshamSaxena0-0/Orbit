using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Orbit
{
    sealed class OrbitHost
    {
        const int HotkeyId = 0x4F52;

        readonly ConfigStore _store = new ConfigStore();
        readonly IconCache _icons = new IconCache(Dispatcher.CurrentDispatcher);
        WheelWindow _wheel;
        Forms.NotifyIcon _tray;
        HwndSource _messages;
        Window _menuAnchor;
        PickerWindow _picker;
        FolderFlyout _folders;
        IntPtr _previous;
        string _hotkey;
        bool _visible, _editMode;
        int _holds;

        Settings S { get { return _store.Current; } }

        public void Start(bool show, bool edit)
        {
            Log.Trace("start");
            _store.Load();
            Log.Trace("settings loaded");
            var view = new DonutView();
            view.Invoked += i => Launch(i, false);
            view.ContextRequested += ShowWheelMenu;
            view.CenterInvoked += () => { if (_editMode) SetEditMode(false); else Dismiss(true); };
            view.DismissRequested += () => { if (_folders.IsOpen) _folders.Close(); else Dismiss(true); };
            view.AddRequested += OpenPicker;
            view.Reordered += Move;
            view.RemoveRequested += Remove;
            view.RenameRequested += Rename;
            view.ItemsDropped += AddDropped;
            view.Closed += OnWheelClosed;
            view.MiddleInvoked += ShowFolders;
            _folders = new FolderFlyout(view, _icons);
            _folders.FolderChosen += OpenFolder;
            _folders.Closed += delegate
            {
                _holds--;
                _wheel.View.SetExpanded(-1);
                _wheel.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (_holds == 0 && _visible && !_wheel.IsActive && !StaysOpen) Dismiss(false);
                }));
            };
            _wheel = NewWheel(view, false);
            ApplySettings();
            Log.Trace("wheel built");

            CreateTray();
            var parameters = new HwndSourceParameters("Orbit messages") { WindowStyle = 0, ParentWindow = new IntPtr(-3) };
            _messages = new HwndSource(parameters);
            _messages.AddHook(WndProc);
            RegisterHotkey();
            Native.RegisterPowerSettingNotification(_messages.Handle, ref Native.ConsoleDisplayState, 0);
            Native.RegisterPowerSettingNotification(_messages.Handle, ref Native.PowerSource, 0);
            Log.Trace("tray and hotkey");

            if (_store.LoadError != null) Balloon("settings.json has an error", _store.LoadError);
            if (!S.Welcomed)
            {
                Balloon("Orbit is in the tray", "Open Orbit again or press " + S.Hotkey + " to bring the wheel up at your cursor. Right-click its centre to add apps.");
                S.Welcomed = true;
                Save();
            }
            if (show) ShowWheel(edit);
        }

        bool StaysOpen { get { return _editMode || S.Apps.Count == 0; } }

        void ApplySettings()
        {
            _wheel.View.Configure(S, _icons);
            _wheel.View.SetApps(S.Apps);
            if (_visible) Arrange();
            Preload();
        }

        void Preload()
        {
            foreach (AppEntry app in S.Apps) _icons.Get(app.IconKey, DonutView.IconPixels);
        }

        public void ShowWheel(bool edit)
        {
            if (_store.ReloadIfChanged())
            {
                _icons.Clear();
                ApplySettings();
                RegisterHotkey();
                if (_store.LoadError != null) Balloon("settings.json has an error", _store.LoadError);
            }
            IntPtr hwnd = _wheel.Handle;
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground != hwnd && (_menuAnchor == null || foreground != new WindowInteropHelper(_menuAnchor).Handle))
                _previous = foreground;

            _editMode = edit;
            _wheel.View.EditMode = edit;
            Native.POINT cursor;
            Native.GetCursorPos(out cursor);
            PlaceAt(cursor);
            Present();
            Log.Trace("wheel shown");
        }

        void Present()
        {
            IntPtr hwnd = _wheel.Handle;
            _wheel.BeforeShow();
            _wheel.View.PlayOpen();
            _visible = true;
            _wheel.Show();
            _wheel.Activate();
            Native.ForceForeground(hwnd);
            _wheel.View.Focus();
            Keyboard.Focus(_wheel.View);
            _wheel.AfterShow();
        }

        bool _screenOff, _rebuildPending;
        int _powerSource = -1;
        System.Windows.Threading.DispatcherTimer _settle;

        WheelWindow NewWheel(DonutView view, bool layered)
        {
            var w = new WheelWindow(view, layered);
            w.Deactivated += delegate { if (w == _wheel && _holds == 0 && !StaysOpen) Dismiss(false); };
            w.OutsideClicked += () =>
            {
                if (w != _wheel) return;
                if (_folders.IsOpen) _folders.Close();
                else Dismiss(true);
            };
            w.Broken += () => { if (w == _wheel) Recover(); };
            return w;
        }

        void RebuildWheel(bool layered)
        {
            WheelWindow old = _wheel;
            DonutView view = old.View;
            old.Release();
            _wheel = NewWheel(view, layered);
            old.Close();
            Arrange();
            new WindowInteropHelper(_wheel).EnsureHandle();
        }

        void Recover()
        {
            if (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime < TimeSpan.FromMinutes(1))
                Log.Write("wheel: not on screen in a fresh Orbit either; leaving it");
            else Program.Restart(Quit);
        }

        void OnPowerSetting(Guid setting, int value)
        {
            Log.Trace("power setting " + (setting == Native.PowerSource ? "power source" : "screen") + " = " + value);
            string reason = null;
            if (setting == Native.ConsoleDisplayState)
            {
                if (value == 0) _screenOff = true;
                else if (value == 1 && _screenOff)
                {
                    _screenOff = false;
                    reason = "the screen came back on";
                }
            }
            else if (setting == Native.PowerSource)
            {
                if (_powerSource >= 0 && value != _powerSource) reason = value == 0 ? "on the charger" : "on battery";
                _powerSource = value;
            }
            if (reason == null) return;
            if (_settle == null)
            {
                _settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _settle.Tick += delegate
                {
                    _settle.Stop();
                    if (_visible) _rebuildPending = true;
                    else FreshWindow();
                };
            }
            Log.Write("wheel: " + reason + "; a new window for it");
            _settle.Stop();
            _settle.Start();
        }

        void FreshWindow()
        {
            _rebuildPending = false;
            RebuildWheel(false);
        }

        void PlaceAt(Native.POINT cursor)
        {
            double scale = _wheel.DeviceScale;
            int width = (int)Math.Round(_wheel.View.ViewWidth * scale), height = (int)Math.Round(_wheel.View.ViewHeight * scale);
            int slack = (int)Math.Round((DonutView.Rim - 4) * scale);
            Native.RECT work = Native.WorkAreaAt(cursor);
            Point anchor = _wheel.View.Anchor;
            int x = cursor.X - (int)Math.Round(anchor.X * scale), y = cursor.Y - (int)Math.Round(anchor.Y * scale);
            x = Math.Max(work.Left - slack, Math.Min(x, work.Right - width + slack));
            y = Math.Max(work.Top - slack, Math.Min(y, work.Bottom - height + slack));
            _wheelPos = new Point(x / scale, y / scale);
            Arrange();
        }

        Point _wheelPos;

        void Arrange()
        {
            double scale = _wheel.DeviceScale;
            Size wheel = new Size(_wheel.View.ViewWidth, _wheel.View.ViewHeight);
            Rect area = new Rect(_wheelPos, wheel);
            if (!StaysOpen)
            {
                Point anchor = _wheel.View.Anchor;
                var at = new Native.POINT { X = (int)((_wheelPos.X + anchor.X) * scale), Y = (int)((_wheelPos.Y + anchor.Y) * scale) };
                Native.RECT screen = Native.MonitorAt(at);
                area = new Rect(screen.Left / scale, screen.Top / scale, (screen.Right - screen.Left - 1) / scale, (screen.Bottom - screen.Top) / scale);
            }
            _wheel.Arrange(area, _wheelPos);
        }

        void Dismiss(bool restoreFocus)
        {
            if (!_visible || _wheel.View.IsClosing) return;
            if (_picker != null) _picker.Close();
            if (_folders.IsOpen) _folders.Close();
            if (restoreFocus && _previous != IntPtr.Zero && Native.IsWindow(_previous))
                Native.SetForegroundWindow(_previous);
            _wheel.View.PlayClose();
        }

        void OnWheelClosed()
        {
            _wheel.Hide();
            _visible = false;
            _editMode = false;
            _wheel.View.EditMode = false;
            if (_rebuildPending) FreshWindow();
        }

        void Toggle()
        {
            if (_visible && !_wheel.View.IsClosing) Dismiss(true);
            else ShowWheel(false);
        }

        void SetEditMode(bool on)
        {
            _editMode = on;
            _wheel.View.EditMode = on;
            if (_visible) Arrange();
            if (!on && _visible && !_wheel.IsActive && _holds == 0) Dismiss(false);
        }

        void Launch(int index, bool admin)
        {
            if (index < 0 || index >= S.Apps.Count) return;
            if (_editMode)
            {
                ShowWheelMenu(index);
                return;
            }
            AppEntry app = S.Apps[index];
            _wheel.View.PlayClose();
            Launcher.Start(app, admin, error => Balloon("Couldn't open " + app.Name, error));
        }

        static string FolderOf(AppEntry app)
        {
            string path = Environment.ExpandEnvironmentVariables((app.Path ?? "").Trim());
            try { if (Directory.Exists(path)) return path; }
            catch (ArgumentException) { }
            if (path.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase))
                return Path.GetPathRoot(Environment.SystemDirectory);
            return null;
        }

        void ShowFolders(int index)
        {
            if (index < 0 || index >= S.Apps.Count || _editMode) return;
            string root = FolderOf(S.Apps[index]);
            if (root == null) return;
            if (_folders.IsOpen) _folders.Close();
            Vector direction;
            Point tip = _wheel.View.TipOf(index, out direction);
            _holds++;
            _wheel.View.SetExpanded(index);
            _folders.Show(root, tip, direction);
        }

        void OpenFolder(string folder)
        {
            _wheel.View.PlayClose();
            Launcher.Start(new AppEntry { Name = Path.GetFileName(folder), Path = folder }, false,
                error => Balloon("Couldn't open " + folder, error));
        }

        bool Contains(string path)
        {
            return S.Apps.Exists(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));
        }

        void AddApp(AppEntry app)
        {
            S.Apps.Add(app);
            Changed();
        }

        void AddDropped(string[] items, int at)
        {
            foreach (string item in items)
            {
                if (Contains(item)) continue;
                S.Apps.Insert(Math.Min(at, S.Apps.Count), new AppEntry { Name = AppNames.FromPath(item), Path = item });
                at++;
            }
            Changed();
        }

        void Move(int from, int to)
        {
            if (from < 0 || from >= S.Apps.Count || to < 0 || to >= S.Apps.Count) return;
            AppEntry app = S.Apps[from];
            S.Apps.RemoveAt(from);
            S.Apps.Insert(to, app);
            Changed();
        }

        void Remove(int index)
        {
            if (index < 0 || index >= S.Apps.Count) return;
            S.Apps.RemoveAt(index);
            Changed();
        }

        void Rename(int index)
        {
            if (index < 0 || index >= S.Apps.Count) return;
            AppEntry app = S.Apps[index];
            var prompt = new PromptWindow("Rename", app.Name);
            if (_visible)
            {
                prompt.Owner = _wheel;
                prompt.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else prompt.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _holds++;
            bool? ok = prompt.ShowDialog();
            _holds--;
            if (ok == true && !string.IsNullOrWhiteSpace(prompt.Value))
            {
                app.Name = prompt.Value.Trim();
                Changed();
            }
            if (_visible) _wheel.Activate();
        }

        void Changed()
        {
            Save();
            _wheel.View.SetApps(S.Apps);
            if (_visible) Arrange();
            Preload();
        }

        void Save()
        {
            try { _store.Save(); }
            catch (Exception ex) { Balloon("Couldn't save settings", ex.Message); }
        }

        void OpenPicker()
        {
            if (_picker != null)
            {
                _picker.Activate();
                return;
            }
            if (!_visible) ShowWheel(true);
            else SetEditMode(true);
            _picker = new PickerWindow(Contains, AddApp, _icons);
            _holds++;
            PlaceBesideWheel(_picker);
            _picker.Closed += delegate
            {
                _picker = null;
                _holds--;
                if (_visible && !_wheel.View.IsClosing) _wheel.Activate();
            };
            _picker.Show();
        }

        void PlaceBesideWheel(Window w)
        {
            double scale = _wheel.DeviceScale, side = _wheel.View.ViewWidth, tall = _wheel.View.ViewHeight;
            var centre = new Native.POINT { X = (int)((_wheelPos.X + side / 2) * scale), Y = (int)((_wheelPos.Y + tall / 2) * scale) };
            Native.RECT work = Native.WorkAreaAt(centre);
            double left = work.Left / scale, top = work.Top / scale, right = work.Right / scale, bottom = work.Bottom / scale;
            double inset = DonutView.Rim;
            double x = _wheelPos.X + side - inset;
            if (x + w.Width > right) x = _wheelPos.X + inset - w.Width;
            if (x < left) x = right - w.Width;
            double y = _wheelPos.Y + tall / 2 - w.Height / 2;
            w.Left = x;
            w.Top = Math.Max(top, Math.Min(y, bottom - w.Height));
        }

        static MenuItem Item(string header, Action action, string gesture = null)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            item.Click += delegate { action(); };
            return item;
        }

        void ShowWheelMenu(int index)
        {
            var menu = new ContextMenu();
            if (index >= 0 && index < S.Apps.Count)
            {
                AppEntry app = S.Apps[index];
                bool editing = _editMode;
                if (editing) menu.Items.Add(Item("Open " + app.Name, () => { SetEditMode(false); Launch(index, false); }));
                else menu.Items.Add(Item("Open", () => Launch(index, false), "Enter"));
                menu.Items.Add(Item("Run as administrator", () => { SetEditMode(false); Launch(index, true); }));
                string file = Launcher.FileFor(app);
                var reveal = Item("Open file location", () => { Launcher.Reveal(file); Dismiss(false); });
                reveal.IsEnabled = file != null;
                menu.Items.Add(reveal);
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Rename…", () => Rename(index), "F2"));
                var admin = Item("Always run as administrator", () => { app.Admin = !app.Admin; Changed(); });
                admin.IsChecked = app.Admin;
                menu.Items.Add(admin);
                menu.Items.Add(Item("Remove from wheel", () => Remove(index), editing ? "Del" : null));
            }
            else
            {
                menu.Items.Add(Item("Add apps…", OpenPicker));
                var edit = Item("Edit wheel", () => SetEditMode(!_editMode));
                edit.IsChecked = _editMode;
                menu.Items.Add(edit);
                menu.Items.Add(new Separator());
                AddCommonItems(menu);
            }
            _holds++;
            menu.PlacementTarget = _wheel.View;
            menu.Placement = PlacementMode.MousePoint;
            menu.Closed += delegate
            {
                _wheel.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    _holds--;
                    if (_holds == 0 && _visible && !_wheel.IsActive && !StaysOpen) Dismiss(false);
                }));
            };
            menu.IsOpen = true;
        }

        void AddCommonItems(ContextMenu menu)
        {
            var startup = Item("Start with Windows", ToggleStartup);
            try { startup.IsChecked = Startup.IsEnabled; }
            catch (Exception) { startup.IsEnabled = false; }
            menu.Items.Add(startup);
            menu.Items.Add(Item("Open settings file", () => { Dismiss(false); Launcher.OpenFile(_store.FilePath); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Quit Orbit", Quit));
        }

        void ToggleStartup()
        {
            try { Startup.Set(!Startup.IsEnabled); }
            catch (Exception ex) { Balloon("Couldn't change startup", ex.Message); }
        }

        void CreateTray()
        {
            _tray = new Forms.NotifyIcon { Text = "Orbit", Icon = LoadIcon(), Visible = true };
            _tray.MouseUp += (s, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) ShowWheel(false);
                else if (e.Button == Forms.MouseButtons.Right) ShowTrayMenu();
            };
        }

        static System.Drawing.Icon LoadIcon()
        {
            var stream = typeof(OrbitHost).Assembly.GetManifestResourceStream("Orbit.orbit.ico");
            if (stream == null) return System.Drawing.SystemIcons.Application;
            using (stream) return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }

        void ShowTrayMenu()
        {
            if (_menuAnchor == null)
            {
                _menuAnchor = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    ShowInTaskbar = false,
                    Topmost = true,
                    Width = 1,
                    Height = 1,
                };
                _menuAnchor.SourceInitialized += delegate { Native.MakeToolWindow(new WindowInteropHelper(_menuAnchor).Handle); };
            }
            Native.POINT cursor;
            Native.GetCursorPos(out cursor);
            double scale = _wheel.DeviceScale;
            _menuAnchor.Left = cursor.X / scale;
            _menuAnchor.Top = cursor.Y / scale;
            _menuAnchor.Show();
            _menuAnchor.Activate();
            Native.SetForegroundWindow(new WindowInteropHelper(_menuAnchor).Handle);

            var menu = new ContextMenu();
            menu.Items.Add(Item("Open wheel", () => ShowWheel(false), S.Hotkey));
            menu.Items.Add(Item("Add apps…", OpenPicker));
            menu.Items.Add(Item("Edit wheel", () => ShowWheel(true)));
            menu.Items.Add(new Separator());
            AddCommonItems(menu);
            menu.PlacementTarget = _menuAnchor;
            menu.Placement = PlacementMode.MousePoint;
            menu.Closed += delegate { _menuAnchor.Hide(); };
            menu.IsOpen = true;
        }

        void Balloon(string title, string text)
        {
            if (_tray != null) _tray.ShowBalloonTip(6000, title, text ?? "", Forms.ToolTipIcon.None);
            Log.Write(title + ": " + text);
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                handled = true;
                Toggle();
            }
            else if (msg == Native.WM_POWERBROADCAST && wParam.ToInt32() == Native.PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
            {
                var setting = (Guid)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam, typeof(Guid));
                OnPowerSetting(setting, System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 20));
            }
            return IntPtr.Zero;
        }

        void RegisterHotkey()
        {
            if (_hotkey == S.Hotkey) return;
            Native.UnregisterHotKey(_messages.Handle, HotkeyId);
            _hotkey = null;
            uint modifiers, vk;
            if (!ParseHotkey(S.Hotkey, out modifiers, out vk))
            {
                Balloon("Hotkey not understood", "\"" + S.Hotkey + "\": use something like Ctrl+Alt+W in the settings file.");
                return;
            }
            if (!Native.RegisterHotKey(_messages.Handle, HotkeyId, modifiers | Native.MOD_NOREPEAT, vk))
            {
                Balloon("Hotkey taken", S.Hotkey + " is used by another app. Pick another \"hotkey\" in the settings file.");
                return;
            }
            _hotkey = S.Hotkey;
            if (_tray != null) _tray.Text = "Orbit (" + S.Hotkey + ")";
        }

        static bool ParseHotkey(string text, out uint modifiers, out uint vk)
        {
            modifiers = 0;
            vk = 0;
            foreach (string raw in (text ?? "").Split('+'))
            {
                string part = raw.Trim();
                switch (part.ToLowerInvariant())
                {
                    case "": continue;
                    case "ctrl": case "control": modifiers |= Native.MOD_CONTROL; continue;
                    case "alt": modifiers |= Native.MOD_ALT; continue;
                    case "shift": modifiers |= Native.MOD_SHIFT; continue;
                    case "win": case "windows": modifiers |= Native.MOD_WIN; continue;
                }
                if (part.Length == 1 && char.IsDigit(part[0])) part = "D" + part;
                Key key;
                if (!Enum.TryParse(part, true, out key) || key == Key.None) return false;
                vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            }
            return vk != 0;
        }

        public void Quit()
        {
            if (_messages != null) Native.UnregisterHotKey(_messages.Handle, HotkeyId);
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            if (_wheel != null) _wheel.AllowClose = true;
            Application.Current.Shutdown();
        }
    }
}
