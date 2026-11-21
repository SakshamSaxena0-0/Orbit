using System;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace Orbit
{
    static class Program
    {
        const string MutexName = @"Local\Orbit.Launcher";
        const string ShowSignal = @"Local\Orbit.Launcher.Show";
        const string EditSignal = @"Local\Orbit.Launcher.Edit";
        const string QuitSignal = @"Local\Orbit.Launcher.Quit";

        public static bool Reopened { get; private set; }

        [STAThread]
        static int Main(string[] args)
        {
            Log.Trace("main");
            bool background = Has(args, "--background"), edit = Has(args, "--edit"), quit = Has(args, "--quit");
            Reopened = Has(args, "--reopen");
            bool first;
            var mutex = new Mutex(true, MutexName, out first);
            if (!first && Reopened)
            {
                try { first = mutex.WaitOne(5000); }
                catch (AbandonedMutexException) { first = true; }
            }
            if (!first)
            {
                if (background) return 0;
                Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                return Signal(quit ? QuitSignal : edit ? EditSignal : ShowSignal) ? 0 : 1;
            }
            if (quit) return 0;

            var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal);
            var editMode = new EventWaitHandle(false, EventResetMode.AutoReset, EditSignal);
            var stop = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignal);

            try
            {
                string profiles = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbit");
                System.IO.Directory.CreateDirectory(profiles);
                System.Runtime.ProfileOptimization.SetProfileRoot(profiles);
                System.Runtime.ProfileOptimization.StartProfile("startup.jit");
            }
            catch (Exception ex) { Log.Write("JIT profile: " + ex.Message); }
            if (Environment.GetEnvironmentVariable("ORBIT_SOFTWARE") == "1")
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            PreferScreenGpu();
            try
            {
                System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
                    typeof(System.Windows.Media.Animation.Timeline), new FrameworkPropertyMetadata { DefaultValue = (int?)240 });
            }
            catch (ArgumentException ex) { Log.Write("frame rate: " + ex.Message); }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            using (var theme = typeof(Program).Assembly.GetManifestResourceStream("Orbit.Theme.xaml"))
                app.Resources = (ResourceDictionary)XamlReader.Load(theme);
            app.Resources["Grain"] = Glow.Grain;
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Write("Unhandled: " + e.Exception);
                e.Handled = true;
            };
            var host = new OrbitHost();
            app.Startup += delegate
            {
                host.Start(!background, edit);
                var listener = new Thread(() => Listen(app.Dispatcher, host, show, editMode, stop)) { IsBackground = true, Name = "Orbit signals" };
                listener.Start();
            };
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }

        static string ExePath { get { return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; } }

        static void PreferScreenGpu()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                    if (key.GetValue(ExePath) == null) key.SetValue(ExePath, "GpuPreference=1;");
            }
            catch (Exception ex) { Log.Write("GPU preference: " + ex.Message); }
        }

        public static void Restart(Action quit)
        {
            Log.Write("restarting Orbit to bring the wheel back");
            System.Diagnostics.Process.Start(ExePath, "--reopen");
            quit();
        }

        static bool Has(string[] args, string flag)
        {
            return Array.Exists(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        }

        static bool Signal(string name)
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                EventWaitHandle handle;
                if (EventWaitHandle.TryOpenExisting(name, out handle))
                {
                    using (handle) handle.Set();
                    return true;
                }
                Thread.Sleep(100);
            }
            return false;
        }

        static void Listen(Dispatcher ui, OrbitHost host, WaitHandle show, WaitHandle edit, WaitHandle stop)
        {
            var handles = new[] { show, edit, stop };
            while (true)
            {
                int which = WaitHandle.WaitAny(handles);
                if (which == 0) ui.BeginInvoke(new Action(() => host.ShowWheel(false)));
                else if (which == 1) ui.BeginInvoke(new Action(() => host.ShowWheel(true)));
                else
                {
                    ui.BeginInvoke(new Action(host.Quit));
                    return;
                }
            }
        }
    }
}
