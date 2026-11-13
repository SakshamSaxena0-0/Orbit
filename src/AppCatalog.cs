using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Orbit
{
    sealed class CatalogItem
    {
        public string Name;
        public string Path;
    }

    /// <summary>The apps of the Start menu (desktop and Store apps), read from shell:AppsFolder.</summary>
    static class AppCatalog
    {
        public const string AppsFolder = "shell:AppsFolder\\";

        /// <summary>Every launchable Start menu app, sorted by name. Call on an STA thread.</summary>
        public static List<CatalogItem> Enumerate()
        {
            var list = new List<CatalogItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                dynamic folder = shell.NameSpace(AppsFolder);
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name;
                    string id = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                    // Web links and uninstallers sit in the Start menu too; neither belongs on a launcher.
                    if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Add(name + "|" + id)) continue;
                    list.Add(new CatalogItem { Name = name, Path = AppsFolder + id });
                }
            }
            catch (Exception ex)
            {
                Log.Write("App catalog: " + ex.Message);
            }
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        /// <summary>A first wheel made of common apps that are installed here.</summary>
        public static List<AppEntry> Seed(int max)
        {
            string[][] wanted =
            {
                new[] { "File Explorer" },
                new[] { "Google Chrome", "Microsoft Edge", "Brave", "Firefox", "Zen" },
                new[] { "Terminal", "Windows PowerShell" },
                new[] { "Settings" },
                new[] { "Spotify" },
                new[] { "Discord" },
                new[] { "Steam" },
                new[] { "Notepad" },
                new[] { "Calculator" },
                new[] { "Photos" },
            };
            var apps = new List<AppEntry>();
            var catalog = Enumerate();
            foreach (var group in wanted)
            {
                if (apps.Count >= max) break;
                foreach (var name in group)
                {
                    var hit = catalog.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (hit == null) continue;
                    apps.Add(new AppEntry { Name = hit.Name, Path = hit.Path });
                    break;
                }
            }
            if (apps.Count == 0)
            {
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                apps.Add(new AppEntry { Name = "File Explorer", Path = System.IO.Path.Combine(win, "explorer.exe") });
                apps.Add(new AppEntry { Name = "Notepad", Path = System.IO.Path.Combine(win, "System32\\notepad.exe") });
                apps.Add(new AppEntry { Name = "Command Prompt", Path = System.IO.Path.Combine(win, "System32\\cmd.exe") });
            }
            return apps;
        }
    }

    static class AppNames
    {
        /// <summary>A readable name for something dropped on the wheel or picked in a file dialog.</summary>
        public static string FromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "App";
            string p = path.Trim().TrimEnd('\\');
            if (p.StartsWith(AppCatalog.AppsFolder, StringComparison.OrdinalIgnoreCase))
            {
                string id = p.Substring(AppCatalog.AppsFolder.Length);
                int bang = id.IndexOf('!');
                if (bang > 0) id = id.Substring(0, bang);
                int us = id.IndexOf('_');
                if (us > 0) id = id.Substring(0, us);
                int dot = id.LastIndexOfAny(new[] { '.', '\\' });
                return dot >= 0 && dot < id.Length - 1 ? id.Substring(dot + 1) : id;
            }
            Uri uri;
            if (Uri.TryCreate(p, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                return uri.Host.StartsWith("www.") ? uri.Host.Substring(4) : uri.Host;
            try
            {
                string full = Environment.ExpandEnvironmentVariables(p);
                if (Directory.Exists(full)) return System.IO.Path.GetFileName(full).Length > 0 ? System.IO.Path.GetFileName(full) : full;
                if (full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    // "Google Chrome" reads better than "chrome"; long descriptions ("Steam Client Bootstrapper") don't.
                    string desc = FileVersionInfo.GetVersionInfo(full).FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc) && desc.Trim().Length <= 22) return desc.Trim();
                }
                string name = System.IO.Path.GetFileNameWithoutExtension(full);
                if (name.Length > 0) return char.ToUpperInvariant(name[0]) + name.Substring(1);
            }
            catch (ArgumentException) { }
            return p;
        }
    }

    static class Log
    {
        static readonly object Gate = new object();
        static readonly bool Tracing = Environment.GetEnvironmentVariable("ORBIT_TRACE") == "1";

        /// <summary>ORBIT_TRACE=1 logs startup stages with the time since the process started.</summary>
        public static void Trace(string stage)
        {
            if (!Tracing) return;
            Write("trace " + (int)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds + " ms: " + stage);
        }

        public static void Write(string line)
        {
            try
            {
                string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbit");
                Directory.CreateDirectory(folder);
                lock (Gate)
                    File.AppendAllText(System.IO.Path.Combine(folder, "orbit.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
