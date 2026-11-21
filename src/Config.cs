using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Orbit
{
    [DataContract]
    sealed class AppEntry
    {
        [DataMember(Name = "name", Order = 0)] public string Name;
        [DataMember(Name = "path", Order = 1)] public string Path;
        [DataMember(Name = "args", Order = 2, EmitDefaultValue = false)] public string Args;
        [DataMember(Name = "icon", Order = 3, EmitDefaultValue = false)] public string Icon;
        [DataMember(Name = "admin", Order = 4, EmitDefaultValue = false)] public bool Admin;

        public string IconKey { get { return string.IsNullOrEmpty(Icon) ? Path : Icon; } }
    }

    [DataContract]
    sealed class Settings
    {
        [DataMember(Name = "hotkey", Order = 0)] public string Hotkey;
        [DataMember(Name = "style", Order = 1)] public string Style;
        [DataMember(Name = "layout", Order = 1)] public string Layout;
        [DataMember(Name = "outerRadius", Order = 2)] public double OuterRadius;
        [DataMember(Name = "holeRatio", Order = 3)] public double HoleRatio;
        [DataMember(Name = "sliceWidth", Order = 4)] public double SliceWidth;
        [DataMember(Name = "shortRays", Order = 4)] public double ShortRays;
        [DataMember(Name = "plate", Order = 4)] public bool? Plate;
        [DataMember(Name = "roundness", Order = 4)] public double Roundness;
        [DataMember(Name = "gap", Order = 4)] public double Gap;
        [DataMember(Name = "perFace", Order = 5)] public int PerFace;
        [DataMember(Name = "accent", Order = 6)] public string Accent;
        [DataMember(Name = "apps", Order = 7)] public List<AppEntry> Apps;
        [DataMember(Name = "welcomed", Order = 8)] public bool Welcomed;

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "Ctrl+Alt+W";
            Style = string.Equals(Style, "wedge", StringComparison.OrdinalIgnoreCase) ? "wedge" : "slim";
            Layout = string.Equals(Layout, "circle", StringComparison.OrdinalIgnoreCase) ? "circle" : "fan";
            if (OuterRadius <= 0) OuterRadius = IsFan ? 330 : IsSlim ? 230 : 170;
            OuterRadius = Math.Max(110, Math.Min(340, OuterRadius));
            if (HoleRatio <= 0) HoleRatio = IsFan ? 0.39 : IsSlim ? 0.26 : 0.40;
            HoleRatio = Math.Max(0.25, Math.Min(0.6, HoleRatio));
            if (SliceWidth <= 0) SliceWidth = 48;
            SliceWidth = Math.Max(28, Math.Min(90, SliceWidth));
            if (Plate == null) Plate = false;
            if (ShortRays <= 0) ShortRays = 0.7;
            ShortRays = Math.Max(0.4, Math.Min(1, ShortRays));
            if (Roundness <= 0) Roundness = IsSlim ? 6 : 12;
            Roundness = Math.Min(60, Roundness);
            if (Gap <= 0) Gap = 6;
            Gap = Math.Max(2, Math.Min(30, Gap));
            if (PerFace <= 0) PerFace = 8;
            PerFace = Math.Max(2, Math.Min(12, PerFace));
            if (string.IsNullOrWhiteSpace(Accent)) Accent = "#8AB4FF";
            if (Apps == null) Apps = new List<AppEntry>();
            Apps.RemoveAll(a => a == null || string.IsNullOrWhiteSpace(a.Path));
            foreach (var a in Apps)
                if (string.IsNullOrWhiteSpace(a.Name)) a.Name = AppNames.FromPath(a.Path);
        }

        public bool IsSlim { get { return Style != "wedge"; } }
        public bool IsFan { get { return Layout != "circle"; } }

        public static Settings CreateDefault()
        {
            var s = new Settings();
            s.Apps = AppCatalog.Seed(8);
            s.Normalize();
            return s;
        }
    }

    sealed class ConfigStore
    {
        public readonly string Folder;
        public readonly string FilePath;
        public Settings Current;
        public string LoadError;
        DateTime _seenWrite;

        public ConfigStore()
        {
            Folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbit");
            FilePath = System.IO.Path.Combine(Folder, "settings.json");
        }

        public void Load()
        {
            LoadError = null;
            if (!File.Exists(FilePath))
            {
                Current = Settings.CreateDefault();
                Save();
                return;
            }
            try
            {
                using (var fs = File.OpenRead(FilePath))
                    Current = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(fs);
                _seenWrite = File.GetLastWriteTimeUtc(FilePath);
            }
            catch (Exception ex)
            {
                LoadError = ex.Message;
                _seenWrite = File.GetLastWriteTimeUtc(FilePath);
                if (Current == null) Current = Settings.CreateDefault();
            }
            if (Current == null) Current = Settings.CreateDefault();
            Current.Normalize();
        }

        public bool ReloadIfChanged()
        {
            try
            {
                if (!File.Exists(FilePath) || File.GetLastWriteTimeUtc(FilePath) == _seenWrite) return false;
            }
            catch (IOException) { return false; }
            Load();
            return true;
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            string tmp = FilePath + ".tmp";
            using (var fs = File.Create(tmp))
            using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, new UTF8Encoding(false), false, true, "  "))
            {
                new DataContractJsonSerializer(typeof(Settings)).WriteObject(w, Current);
                w.Flush();
            }
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
            _seenWrite = File.GetLastWriteTimeUtc(FilePath);
            LoadError = null;
        }
    }
}
