using System;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

namespace Orbit
{
    /// <summary>
    /// Orbit: a wheel of apps that pops up around the cursor. Every launch of Orbit.exe shows it;
    /// the first one stays in the tray so later ones (and the hotkey) bring it up instantly.
    /// Flags: --background (start in the tray only), --edit (open in edit mode), --quit.
    /// </summary>
    static class Program
    {
        const string MutexName = @"Local\Orbit.Launcher";
        const string ShowSignal = @"Local\Orbit.Launcher.Show";
        const string EditSignal = @"Local\Orbit.Launcher.Edit";
        const string QuitSignal = @"Local\Orbit.Launcher.Quit";

        /// <summary>Started by an Orbit that restarted itself (see <see cref="Restart"/>): it doesn't do so again.</summary>
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
                // The Orbit that started this one is on its way out: take over when it has gone.
                try { first = mutex.WaitOne(5000); }
                catch (AbandonedMutexException) { first = true; }
            }
            if (!first)
            {
                // Orbit already runs: hand it the request, and with it the right to take the foreground.
                if (background) return 0;
                Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                return Signal(quit ? QuitSignal : edit ? EditSignal : ShowSignal) ? 0 : 1;
            }
            if (quit) return 0;

            var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal);
            var editMode = new EventWaitHandle(false, EventResetMode.AutoReset, EditSignal);
            var stop = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignal);

            // A cold start is mostly JIT and WPF's first window: multicore JIT replays the last
            // start's methods on spare cores. Drawing stays on the graphics card: software drawing
            // starts the first window sooner but can't keep animations smooth (ORBIT_SOFTWARE=1 forces it).
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
            // WPF animations (menus fading in, the folder panel unrolling) run at 60 frames a second
            // unless asked for more: every one asks for 240, so only the display's refresh limits them.
            // The wheel's own frames already come once per refresh (CompositionTarget.Rendering).
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

        /// <summary>
        /// Draws on the graphics chip that drives the screen. On a laptop with two (here AMD and
        /// NVIDIA) WPF otherwise drew Orbit on the NVIDIA one, and when that was switched off and
        /// on (sleep, the charger) the wheel never reached the screen again until Orbit restarted.
        /// This is Windows' own per-app choice (Settings > Display > Graphics: "Power saving"),
        /// set only if nothing was chosen for Orbit there. It must come before WPF draws anything.
        /// </summary>
        static void PreferScreenGpu()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                    if (key.GetValue(ExePath) == null) key.SetValue(ExePath, "GpuPreference=1;");
            }
            catch (Exception ex) { Log.Write("GPU preference: " + ex.Message); }
        }

        /// <summary>Starts a fresh Orbit that shows the wheel, once this one has quit (see <paramref name="quit"/>).</summary>
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
            // The first instance may still be creating its signals.
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
