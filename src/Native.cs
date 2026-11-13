using System;
using System.Runtime.InteropServices;

namespace Orbit
{
    /// <summary>Win32 calls Orbit needs. Coordinates are physical pixels.</summary>
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
        public const int ASFW_ANY = -1;

        /// <summary>The whole monitor under a point, taskbar included.</summary>
        public static RECT MonitorAt(POINT pt)
        {
            var info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            GetMonitorInfo(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST), ref info);
            return info.rcMonitor;
        }

        /// <summary>Work area of the monitor under a point (taskbar excluded).</summary>
        public static RECT WorkAreaAt(POINT pt)
        {
            var info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            GetMonitorInfo(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST), ref info);
            return info.rcWork;
        }

        /// <summary>
        /// Brings a window to the front even when Windows didn't hand Orbit the foreground
        /// (started by a background process, for one): without the focus the wheel would never
        /// notice a click elsewhere and stay up.
        /// </summary>
        public static void ForceForeground(IntPtr hwnd)
        {
            if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return;
            uint other = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), mine = GetCurrentThreadId();
            if (other == 0 || other == mine || !AttachThreadInput(mine, other, true)) return;
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally { AttachThreadInput(mine, other, false); }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MARGINS { public int Left, Right, Top, Bottom; }

        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
        const int DWMWCP_DONOTROUND = 1, DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        /// <summary>
        /// Makes a plain (not layered) window see-through by its own alpha: the whole client area
        /// becomes "frame", which the desktop compositor blends, with no corners or border of its own.
        /// </summary>
        public static void SeeThroughByCompositor(IntPtr hwnd)
        {
            var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            if (source != null && source.CompositionTarget != null)
                source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
            var all = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref all);
            int corners = DWMWCP_DONOTROUND, border = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, 4);
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, 4);
        }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] int[] bits, ref BITMAPINFOHEADER info, uint usage);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

        [DllImport("user32.dll")] public static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);

        public const int WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013;
        /// <summary>The screen is off (0), on (1) or dimmed (2).</summary>
        public static Guid ConsoleDisplayState = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");
        /// <summary>On the charger (0), on battery (1), on a UPS (2).</summary>
        public static Guid PowerSource = new Guid("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");

        /// <summary>The screen's pixels in a rectangle as Windows composes them, see-through windows included; null if unreadable.</summary>
        public static int[] CaptureScreen(RECT r)
        {
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return null;
            IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, w, h);
            try
            {
                IntPtr old = SelectObject(memory, bitmap);
                bool copied = BitBlt(memory, 0, 0, w, h, screen, r.Left, r.Top, SRCCOPY | CAPTUREBLT);
                SelectObject(memory, old);
                if (!copied) return null;
                var info = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                var bits = new int[w * h];
                return GetDIBits(memory, bitmap, 0, (uint)h, bits, ref info, 0) == h ? bits : null;
            }
            finally
            {
                DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        /// <summary>Keeps a window out of Alt+Tab and the taskbar.</summary>
        public static void MakeToolWindow(IntPtr hwnd)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        }
    }
}
