using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Orbit
{
    static class ShellIcons
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int cx, cy; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP
        {
            public int bmType, bmWidth, bmHeight, bmWidthBytes;
            public ushort bmPlanes, bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, [In] ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr handle, int size, ref BITMAP bitmap);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

        public static BitmapSource Load(string path, int px)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = Environment.ExpandEnvironmentVariables(path.Trim());
            IShellItemImageFactory factory = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;
                var size = new SIZE { cx = px, cy = px };
                if (factory.GetImage(size, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm) != 0 || hbm == IntPtr.Zero) return null;
                return FromHBitmap(hbm);
            }
            catch (Exception ex)
            {
                Log.Write("Icon for " + path + ": " + ex.Message);
                return null;
            }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }

        static BitmapSource FromHBitmap(IntPtr hbm)
        {
            var bm = new BITMAP();
            GetObject(hbm, Marshal.SizeOf(typeof(BITMAP)), ref bm);
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0) return null;

            var info = new BITMAPINFOHEADER();
            info.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            info.biWidth = w;
            info.biHeight = -h;
            info.biPlanes = 1;
            info.biBitCount = 32;
            var bits = new byte[w * h * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            try { GetDIBits(dc, hbm, 0, (uint)h, bits, ref info, 0); }
            finally { ReleaseDC(IntPtr.Zero, dc); }

            bool anyAlpha = false, straight = false;
            for (int i = 0; i < bits.Length; i += 4)
            {
                byte a = bits[i + 3];
                if (a != 0) anyAlpha = true;
                if (bits[i] > a || bits[i + 1] > a || bits[i + 2] > a) straight = true;
            }
            PixelFormat format = PixelFormats.Pbgra32;
            if (!anyAlpha) format = PixelFormats.Bgr32;
            else if (straight) format = PixelFormats.Bgra32;

            var src = BitmapSource.Create(w, h, 96, 96, format, null, bits, w * 4);
            src.Freeze();
            return src;
        }
    }

    sealed class IconCache
    {
        const int Workers = 3;
        readonly Dictionary<string, ImageSource> _done = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly BlockingCollection<KeyValuePair<string, int>> _queue = new BlockingCollection<KeyValuePair<string, int>>();
        readonly Dispatcher _ui;
        readonly string _folder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbit", "icons");

        public event Action Loaded;

        public IconCache(Dispatcher ui)
        {
            _ui = ui;
            for (int i = 0; i < Workers; i++)
            {
                var worker = new Thread(Work) { IsBackground = true, Name = "Orbit icons" };
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
            }
        }

        public ImageSource Get(string path, int px)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string key = px + "|" + path;
            ImageSource img;
            if (_done.TryGetValue(key, out img)) return img;
            if (_pending.Add(key))
            {
                img = FromDisk(key);
                if (img != null) _done[key] = img;
                _queue.Add(new KeyValuePair<string, int>(path, px));
            }
            return img;
        }

        public void Request(string path, int px, Action<ImageSource> done)
        {
            ImageSource img = Get(path, px);
            if (img != null) done(img);
            string key = px + "|" + path;
            if (!_pending.Contains(key)) return;
            List<Action<ImageSource>> list;
            if (!_waiters.TryGetValue(key, out list)) _waiters[key] = list = new List<Action<ImageSource>>();
            list.Add(done);
        }

        readonly Dictionary<string, List<Action<ImageSource>>> _waiters = new Dictionary<string, List<Action<ImageSource>>>(StringComparer.OrdinalIgnoreCase);

        public void Clear()
        {
            _done.Clear();
        }

        void Work()
        {
            foreach (var req in _queue.GetConsumingEnumerable())
            {
                var path = req.Key;
                var px = req.Value;
                string key = px + "|" + path;
                var img = ShellIcons.Load(path, px);
                if (img != null) ToDisk(key, img);
                _ui.BeginInvoke(new Action(() =>
                {
                    if (img != null || !_done.ContainsKey(key)) _done[key] = img;
                    _pending.Remove(key);
                    List<Action<ImageSource>> waiting;
                    if (_waiters.TryGetValue(key, out waiting))
                    {
                        _waiters.Remove(key);
                        if (img != null) foreach (var done in waiting) done(img);
                    }
                    if (Loaded != null) Loaded();
                }));
            }
        }

        string FileFor(string key)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key.ToLowerInvariant()));
                return System.IO.Path.Combine(_folder, BitConverter.ToString(hash).Replace("-", "") + ".png");
            }
        }

        ImageSource FromDisk(string key)
        {
            try
            {
                string file = FileFor(key);
                if (!System.IO.File.Exists(file)) return null;
                var img = new BitmapImage();
                using (var stream = System.IO.File.OpenRead(file))
                {
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = stream;
                    img.EndInit();
                }
                img.Freeze();
                return img;
            }
            catch (Exception ex)
            {
                Log.Write("Icon cache read: " + ex.Message);
                return null;
            }
        }

        void ToDisk(string key, BitmapSource img)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_folder);
                string file = FileFor(key), tmp = file + "." + Thread.CurrentThread.ManagedThreadId + ".tmp";
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(img));
                using (var stream = System.IO.File.Create(tmp)) encoder.Save(stream);
                if (System.IO.File.Exists(file)) System.IO.File.Delete(file);
                System.IO.File.Move(tmp, file);
            }
            catch (Exception ex)
            {
                Log.Write("Icon cache write: " + ex.Message);
            }
        }
    }
}
