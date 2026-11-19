using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FastFind
{
    /// <summary>Portable settings: exe\data when writable, otherwise %LOCALAPPDATA%\FastFind.</summary>
    internal sealed class Settings
    {
        public string AppDir;
        public string DataDir;
        public List<string> Volumes = new List<string>();
        public bool AutoRefresh = true;
        public bool EnableHotkey = true;
        public bool DarkTheme = false;
        public int CacheMinutes = 10;
        public int ResultLimit = 20000;
        public int FileTypeMask;
        /// <summary>Search scope saved by the chips. null means "not configured" (all volumes enabled).</summary>
        public string ScopeRaw;
        public List<string> ScopeVolumes = new List<string>();
        public int WindowWidth = 1100;
        public int WindowHeight = 700;
        public int WindowX = -1;
        public int WindowY = -1;
        public bool FirstRun;

        public static Settings Load()
        {
            var s = new Settings();
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            s.AppDir = Path.GetDirectoryName(exe);
            s.DataDir = ChooseDataDir(s.AppDir);
            string ini = Path.Combine(s.DataDir, "settings.ini");
            s.FirstRun = !File.Exists(ini);
            if (File.Exists(ini))
            {
                try
                {
                    foreach (string line in File.ReadAllLines(ini, Encoding.UTF8))
                    {
                        string t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("#") || t.StartsWith(";")) continue;
                        int eq = t.IndexOf('=');
                        if (eq <= 0) continue;
                        s.ApplySetting(t.Substring(0, eq).Trim(), t.Substring(eq + 1).Trim());
                    }
                }
                catch (Exception) { }
            }
            if (s.Volumes.Count == 0) s.Volumes = DetectFixedNtfsVolumes();
            if (s.ResultLimit < 1000) s.ResultLimit = 1000;
            if (s.ResultLimit > 200000) s.ResultLimit = 200000;
            return s;
        }

        internal void ApplySetting(string key, string val)
        {
            switch (key.ToLowerInvariant())
            {
                case "volumes":
                    Volumes.Clear();
                    AddRoots(Volumes, val);
                    break;
                case "scope":
                    ScopeRaw = val;
                    ScopeVolumes.Clear();
                    AddRoots(ScopeVolumes, val);
                    break;
                case "autorefresh": AutoRefresh = ParseBool(val, true); break;
                case "hotkey": EnableHotkey = ParseBool(val, true); break;
                case "dark": DarkTheme = ParseBool(val, false); break;
                case "cacheminutes":
                case "cachemintues":
                    CacheMinutes = ParseInt(val, 10);
                    break;
                case "limit": ResultLimit = ParseInt(val, 20000); break;
                case "filetypes": FileTypeMask = ParseInt(val, 0); break;
                case "width": WindowWidth = ParseInt(val, 1100); break;
                case "height": WindowHeight = ParseInt(val, 700); break;
                case "x": WindowX = ParseInt(val, -1); break;
                case "y": WindowY = ParseInt(val, -1); break;
            }
        }

        private static void AddRoots(List<string> target, string value)
        {
            foreach (string part in value.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string r = NormalizeRoot(part);
                if (r != null) target.Add(r);
            }
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
                var sb = new StringBuilder();
                sb.AppendLine("# FastFind settings");
                sb.AppendLine("volumes=" + string.Join(",", Volumes.ToArray()));
                if (ScopeRaw != null || ScopeVolumes.Count > 0)
                    sb.AppendLine("scope=" + string.Join(",", ScopeVolumes.ToArray()));
                sb.AppendLine("autorefresh=" + (AutoRefresh ? "1" : "0"));
                sb.AppendLine("hotkey=" + (EnableHotkey ? "1" : "0"));
                sb.AppendLine("dark=" + (DarkTheme ? "1" : "0"));
                sb.AppendLine("cacheminutes=" + CacheMinutes.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("limit=" + ResultLimit.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("filetypes=" + FileTypeMask.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("width=" + WindowWidth.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("height=" + WindowHeight.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("x=" + WindowX.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("y=" + WindowY.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(Path.Combine(DataDir, "settings.ini"), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        public string IndexDir
        {
            get { return Path.Combine(DataDir, "index"); }
        }

        public string CachePath(string root)
        {
            string letter = root.Length > 0 ? root.Substring(0, 1).ToUpperInvariant() : "X";
            return Path.Combine(IndexDir, "index-" + letter + ".dat");
        }

        public static List<string> DetectFixedNtfsVolumes()
        {
            var list = new List<string>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed) continue;
                        if (!string.Equals(d.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) continue;
                        string root = d.Name.EndsWith("\\") ? d.Name : d.Name + "\\";
                        int bus = Native.GetBusType(root);
                        if (bus == 7) continue;   // BusTypeUsb: 移动硬盘/U 盘不默认索引
                        if (bus == 12) continue;  // BusTypeSd
                        if (bus == 13) continue;  // BusTypeMmc
                        list.Add(root);
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            if (list.Count == 0) list.Add("C:\\");
            return list;
        }

        public static string NormalizeRoot(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim().ToUpperInvariant();
            if (s.Length == 1 && s[0] >= 'A' && s[0] <= 'Z') return s + ":\\";
            if (s.Length == 2 && s[1] == ':') return s + "\\";
            if (s.Length == 3 && s[1] == ':' && s[2] == '\\') return s;
            return null;
        }

        private static string ChooseDataDir(string appDir)
        {
            string portable = Path.Combine(appDir, "data");
            try
            {
                Directory.CreateDirectory(portable);
                string probe = Path.Combine(portable, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return portable;
            }
            catch (Exception) { }
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FastFind");
            try { Directory.CreateDirectory(local); } catch (Exception) { }
            return local;
        }

        private static bool ParseBool(string s, bool def)
        {
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase) || s.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
            return def;
        }

        private static int ParseInt(string s, int def)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }
    }
}
