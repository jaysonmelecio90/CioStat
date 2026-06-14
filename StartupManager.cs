using System;
using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CioStats
{
    // Launch-at-login via the per-user Run key (HKCU — no admin needed, scoped to this Windows account).
    internal static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "CioStat";

        // The true Win32 image path. Application.ExecutablePath mangles paths containing '#' (e.g.
        // "C# Programming") by parsing the assembly's file:// CodeBase as a URI — the '#' starts a
        // fragment, yielding a broken mixed-slash path. MainModule.FileName avoids that entirely.
        private static string ExePath()
        {
            try { return Process.GetCurrentProcess().MainModule.FileName; }
            catch { return Application.ExecutablePath; }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return key != null && !string.IsNullOrEmpty(key.GetValue(ValueName) as string);
            }
            catch { return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enabled) key.SetValue(ValueName, "\"" + ExePath() + "\"");
                    else key.DeleteValue(ValueName, false);
                }
            }
            catch { }
        }

        // If launch-at-login is on, rewrite the path so a moved/renamed exe still points at the right place.
        public static void Sync()
        {
            if (IsEnabled()) SetEnabled(true);
        }
    }
}
