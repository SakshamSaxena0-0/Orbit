using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Orbit
{
    static class Launcher
    {
        public static void Start(AppEntry app, bool admin, Action<string> onError)
        {
            string path = Environment.ExpandEnvironmentVariables((app.Path ?? "").Trim());
            string args = app.Args;
            bool elevate = admin || app.Admin;
            Dispatcher ui = Dispatcher.CurrentDispatcher;
            var thread = new Thread(() =>
            {
                string error = Run(path, args, elevate);
                if (error != null)
                {
                    Log.Write("Launch " + path + ": " + error);
                    ui.BeginInvoke(new Action(() => onError(error)));
                }
            });
            thread.IsBackground = true;
            thread.Name = "Orbit launch";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        static string Run(string path, string args, bool elevate)
        {
            try
            {
                var psi = new ProcessStartInfo(path) { UseShellExecute = true };
                if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
                if (elevate) psi.Verb = "runas";
                string ext = SafeExtension(path);
                if ((ext == ".exe" || ext == ".bat" || ext == ".cmd") && File.Exists(path))
                    psi.WorkingDirectory = Path.GetDirectoryName(path);
                using (Process.Start(psi)) { }
                return null;
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return null;
                if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && !elevate)
                {
                    try
                    {
                        using (Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true })) { }
                        return null;
                    }
                    catch (Exception) { }
                }
                return ex.Message;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        static string SafeExtension(string path)
        {
            try { return (Path.GetExtension(path) ?? "").ToLowerInvariant(); }
            catch (ArgumentException) { return ""; }
        }

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        public static string FileFor(AppEntry app)
        {
            string path = Environment.ExpandEnvironmentVariables((app.Path ?? "").Trim());
            if (path.StartsWith(AppCatalog.AppsFolder, StringComparison.OrdinalIgnoreCase))
            {
                string id = path.Substring(AppCatalog.AppsFolder.Length);
                int close = id.IndexOf("}\\", StringComparison.Ordinal);
                Guid folder;
                if (!id.StartsWith("{") || close < 0 || !Guid.TryParse(id.Substring(0, close + 1), out folder)) return null;
                IntPtr raw;
                if (SHGetKnownFolderPath(folder, 0, IntPtr.Zero, out raw) != 0) return null;
                try { path = Path.Combine(Marshal.PtrToStringUni(raw), id.Substring(close + 2)); }
                finally { Marshal.FreeCoTaskMem(raw); }
            }
            try { return File.Exists(path) || Directory.Exists(path) ? path : null; }
            catch (ArgumentException) { return null; }
        }

        public static void Reveal(string file)
        {
            try { using (Process.Start("explorer.exe", "/select,\"" + file + "\"")) { } }
            catch (Exception ex) { Log.Write("Reveal " + file + ": " + ex.Message); }
        }

        public static void OpenFile(string file)
        {
            try { using (Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })) { } }
            catch (Win32Exception)
            {
                using (Process.Start("notepad.exe", "\"" + file + "\"")) { }
            }
        }
    }

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "Orbit";

        static string Command { get { return "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --background"; } }

        public static bool IsEnabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && string.Equals(key.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void Set(bool on)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) key.SetValue(Name, Command);
                else if (key.GetValue(Name) != null) key.DeleteValue(Name);
            }
        }
    }
}
