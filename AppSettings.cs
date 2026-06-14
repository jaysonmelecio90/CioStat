using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Xml;

namespace CioStats
{
    // User-scoped settings. No secrets here — the claude.ai session lives in WebView2's own profile.
    internal sealed class AppSettings : ApplicationSettingsBase
    {
        private static readonly AppSettings _default = (AppSettings)Synchronized(new AppSettings());
        public static AppSettings Default { get { return _default; } }

        [UserScopedSetting, DefaultSettingValue("60")]
        public int PollIntervalSeconds
        {
            get { return (int)this[nameof(PollIntervalSeconds)]; }
            set { this[nameof(PollIntervalSeconds)] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("")]
        public string WindowLocation
        {
            get { return (string)this[nameof(WindowLocation)]; }
            set { this[nameof(WindowLocation)] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("")]
        public string AgentWorkingDir
        {
            get { return (string)this[nameof(AgentWorkingDir)]; }
            set { this[nameof(AgentWorkingDir)] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("")]
        public string WindowSize
        {
            get { return (string)this[nameof(WindowSize)]; }
            set { this[nameof(WindowSize)] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("")]
        public string OpenTerminals
        {
            get { return (string)this[nameof(OpenTerminals)]; }
            set { this[nameof(OpenTerminals)] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("")]
        public string TerminalNames
        {
            get { return (string)this[nameof(TerminalNames)]; }
            set { this[nameof(TerminalNames)] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("")]
        public string ActiveTerminalDir
        {
            get { return (string)this[nameof(ActiveTerminalDir)]; }
            set { this[nameof(ActiveTerminalDir)] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("False")]
        public bool Migrated
        {
            get { return (bool)this[nameof(Migrated)]; }
            set { this[nameof(Migrated)] = value; }
        }

        // One-time recovery of settings saved under the old "ClawdMeter.AppSettings" namespace section.
        public static void Migrate()
        {
            try
            {
                if (Default.Migrated) return;
                Default.Migrated = true;

                Dictionary<string, string> old = FindOldSettings();
                string oldTerms = old.ContainsKey("OpenTerminals") ? old["OpenTerminals"] : string.Empty;
                var union = new List<string>();
                foreach (string d in (oldTerms + "|" + Default.OpenTerminals).Split('|'))
                    if (!string.IsNullOrEmpty(d) && !union.Contains(d)) union.Add(d);
                Default.OpenTerminals = string.Join("|", union);

                if (string.IsNullOrEmpty(Default.ActiveTerminalDir) && old.ContainsKey("ActiveTerminalDir")) Default.ActiveTerminalDir = old["ActiveTerminalDir"];
                if (string.IsNullOrEmpty(Default.AgentWorkingDir) && old.ContainsKey("AgentWorkingDir")) Default.AgentWorkingDir = old["AgentWorkingDir"];
                if (string.IsNullOrEmpty(Default.WindowLocation) && old.ContainsKey("WindowLocation")) Default.WindowLocation = old["WindowLocation"];
                if (string.IsNullOrEmpty(Default.WindowSize) && old.ContainsKey("WindowSize")) Default.WindowSize = old["WindowSize"];
                Default.Save();
            }
            catch { }
        }

        // Scan LocalAppData for prior ClawdMeter/CioStat user.config files (survives exe + namespace renames).
        private static Dictionary<string, string> FindOldSettings()
        {
            var best = new Dictionary<string, string>();
            int bestLen = -1;
            try
            {
                string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                foreach (string root in new[] { Path.Combine(lad, "ClawdMeter"), Path.Combine(lad, "CioStat") })
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string sub in Directory.GetDirectories(root))
                    {
                        string sn = Path.GetFileName(sub);
                        if (sn.Equals("WebView2", StringComparison.OrdinalIgnoreCase) || sn.Equals("runtime", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (string file in Directory.GetFiles(sub, "user.config", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var doc = new XmlDocument();
                                doc.Load(file);
                                foreach (string sect in new[] { "ClawdMeter.AppSettings", "CioStats.AppSettings" })
                                {
                                    XmlNode section = doc.SelectSingleNode("//*[local-name()='" + sect + "']");
                                    if (section == null) continue;
                                    var map = new Dictionary<string, string>();
                                    foreach (XmlNode setting in section.SelectNodes("setting"))
                                    {
                                        string name = setting.Attributes["name"] != null ? setting.Attributes["name"].Value : null;
                                        XmlNode v = setting.SelectSingleNode("value");
                                        if (name != null) map[name] = v != null ? v.InnerText : string.Empty;
                                    }
                                    int len = map.ContainsKey("OpenTerminals") && map["OpenTerminals"] != null ? map["OpenTerminals"].Length : 0;
                                    if (len > bestLen) { bestLen = len; best = map; }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return best;
        }
    }
}
