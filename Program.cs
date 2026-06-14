using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace CioStats
{
    internal static class AppIcons
    {
        private static Icon _icon;
        private static bool _tried;
        public static Icon Get()
        {
            if (_tried) return _icon;
            _tried = true;
            try { using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CioStats.CioStat.ico")) if (s != null) _icon = new Icon(s); }
            catch { }
            return _icon;
        }
    }

    internal static class Program
    {
        private static Mutex _mutex;

        [STAThread]
        private static void Main()
        {
            bool created;
            _mutex = new Mutex(true, "Local\\ClawdMeter.SingleInstance", out created);
            if (!created) return;

            PrepareNativeLoader();
            AppSettings.Migrate();
            StartupManager.Sync();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayContext());
            GC.KeepAlive(_mutex);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        // Costura embeds the managed DLLs; the native WebView2Loader is embedded here and extracted at startup.
        private static void PrepareNativeLoader()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawdMeter", "runtime");
                Directory.CreateDirectory(dir);
                string dll = Path.Combine(dir, "WebView2Loader.dll");
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CioStats.WebView2Loader.dll"))
                {
                    if (s != null && (!File.Exists(dll) || new FileInfo(dll).Length != s.Length))
                        using (var fs = File.Create(dll)) s.CopyTo(fs);
                }
                SetDllDirectory(dir);
            }
            catch { }
        }

        // System-tray host: owns the widget + notify icon (Sign in / Sign out / Settings / Exit).
        private sealed class TrayContext : ApplicationContext
        {
            private readonly ClawdMeter _widget;
            private readonly AgentTerminalForm _agent;
            private readonly NotifyIcon _tray;
            private Icon _icon;
            private bool _iconDrawn;

            public TrayContext()
            {
                _agent = new AgentTerminalForm();
                _widget = new ClawdMeter();
                _widget.ExitRequested = ExitApp;
                _widget.ToggleAgentRequested = () => _agent.Toggle();
                _widget.NewTerminalRequested = () => _agent.OpenTerminal(null);
                _widget.ChooseFolderRequested = () =>
                {
                    using (var d = new FolderBrowserDialog())
                        if (d.ShowDialog() == DialogResult.OK) _agent.OpenTerminal(d.SelectedPath, true);
                };
                _widget.Agent = _agent;
                _icon = AppIcons.Get();
                if (_icon == null) { _icon = BuildTrayIcon(); _iconDrawn = true; }
                _tray = new NotifyIcon { Icon = _icon, Visible = true, Text = "CioStat" };

                var menu = new ContextMenuStrip();
                menu.Items.Add("Show / Hide", null, (s, e) => _widget.ToggleVisible());
                menu.Items.Add("Agent terminal", null, (s, e) => _agent.Toggle());
                menu.Items.Add("New terminal", null, (s, e) => _agent.OpenTerminal(null));
                var folder = new ToolStripMenuItem("New terminal in folder");
                folder.DropDownOpening += (s, e) => PopulateFolders(folder);
                menu.Items.Add(folder);
                menu.Items.Add("Sign in to claude.ai", null, (s, e) => _widget.SignIn());
                menu.Items.Add("Refresh now", null, (s, e) => _widget.RefreshNow());
                menu.Items.Add("Settings…", null, (s, e) => _widget.ShowSettings());
                menu.Items.Add("Sign out", null, (s, e) => _widget.SignOut());
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Exit", null, (s, e) => ExitApp());
                _tray.ContextMenuStrip = menu;
                _tray.DoubleClick += (s, e) => _widget.ToggleVisible();

                _widget.Show();
            }

            private void ExitApp()
            {
                _tray.Visible = false;
                if (_agent != null) _agent.Dispose();   // persist state + kill embedded terminals (ExitThread won't Dispose us)
                ExitThread();
            }

            private void PopulateFolders(ToolStripMenuItem parent)
            {
                parent.DropDownItems.Clear();
                string cur = AppSettings.Default.AgentWorkingDir ?? string.Empty;
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                AddFolderItem(parent, "Home", home, string.IsNullOrEmpty(cur) || string.Equals(cur, home, StringComparison.OrdinalIgnoreCase));
                foreach (ProjectInfo pi in ClaudeSessionWatcher.ListProjects())
                {
                    AddFolderItem(parent, ShortPath(pi.Cwd), pi.Cwd, string.Equals(cur, pi.Cwd, StringComparison.OrdinalIgnoreCase));
                    if (parent.DropDownItems.Count >= 9) break;
                }
                parent.DropDownItems.Add(new ToolStripSeparator());
                parent.DropDownItems.Add("Browse…", null, (a, b) =>
                {
                    using (var d = new FolderBrowserDialog())
                        if (d.ShowDialog() == DialogResult.OK) _agent.SetWorkingDir(d.SelectedPath);
                });
            }

            private void AddFolderItem(ToolStripMenuItem parent, string text, string path, bool current)
            {
                var it = new ToolStripMenuItem(text) { Checked = current, ToolTipText = path };
                it.Click += (a, b) => _agent.SetWorkingDir(path);
                parent.DropDownItems.Add(it);
            }

            private static string ShortPath(string p)
            {
                if (string.IsNullOrEmpty(p)) return "(unknown)";
                return p.Length <= 44 ? p : "…" + p.Substring(p.Length - 43);
            }

            private static Icon BuildTrayIcon()
            {
                using (var bmp = new Bitmap(32, 32))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        ClawdArt.Draw(g, new RectangleF(2, 1, 28, 30), Color.FromArgb(217, 119, 87), false, false);
                    }
                    return Icon.FromHandle(bmp.GetHicon());
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                    if (_iconDrawn && _icon != null) { DestroyIcon(_icon.Handle); _icon.Dispose(); }
                    if (_agent != null) _agent.Dispose();
                    if (_widget != null) _widget.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
