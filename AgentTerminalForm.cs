using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CioStats
{
    // Status of one open terminal, surfaced to the widget's TV list.
    public sealed class TermStatus
    {
        public string Dir;
        public string Name;
        public bool Running;
        public bool Active;
        public bool Busy;       // console output changed within the last couple of seconds
        public string Live;     // last meaningful line currently on the console
    }

    // Win32 helpers for reparenting a real conhost console into a WinForms control.
    internal static class TermNative
    {
        private const int GWL_STYLE = -16;
        private const uint WS_CHILD = 0x40000000, WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_BORDER = 0x00800000;
        private const int SW_SHOW = 5;

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int idx, int val);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr h);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private delegate bool EnumWindowsProc(IntPtr h, IntPtr p);

        public static void FocusConsole(IntPtr child)
        {
            if (child == IntPtr.Zero) return;
            int pid;
            uint con = (uint)GetWindowThreadProcessId(child, out pid);
            uint cur = GetCurrentThreadId();
            AttachThreadInput(cur, con, true);
            SetForegroundWindow(child);
            SetFocus(child);
            AttachThreadInput(cur, con, false);
        }

        public static void Reparent(IntPtr h, IntPtr parent)
        {
            uint style = (uint)GetWindowLong(h, GWL_STYLE);
            style = (style | WS_CHILD) & ~(WS_CAPTION | WS_THICKFRAME | WS_POPUP | WS_BORDER);
            SetWindowLong(h, GWL_STYLE, unchecked((int)style));
            SetParent(h, parent);
            ShowWindow(h, SW_SHOW);
        }

        public static IntPtr FindConsoleWindow(int rootPid)
        {
            HashSet<int> pids = Descendants(rootPid);
            IntPtr result = IntPtr.Zero;
            EnumWindows((h, p) =>
            {
                int pid; GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid)) return true;
                var sb = new StringBuilder(64); GetClassName(h, sb, 64);
                if (sb.ToString() == "ConsoleWindowClass" && IsWindowVisible(h)) { result = h; return false; }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public static HashSet<int> Descendants(int root)
        {
            var set = new HashSet<int> { root };
            try
            {
                var byParent = new Dictionary<int, List<int>>();
                using (var s = new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId FROM Win32_Process"))
                using (var col = s.Get())
                    foreach (ManagementObject mo in col)
                    {
                        int pid = Convert.ToInt32(mo["ProcessId"]);
                        int ppid = Convert.ToInt32(mo["ParentProcessId"]);
                        List<int> l;
                        if (!byParent.TryGetValue(ppid, out l)) { l = new List<int>(); byParent[ppid] = l; }
                        l.Add(pid);
                    }
                var stack = new Stack<int>(); stack.Push(root);
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    List<int> kids;
                    if (byParent.TryGetValue(cur, out kids))
                        foreach (int k in kids) if (set.Add(k)) stack.Push(k);
                }
            }
            catch { }
            return set;
        }
    }

    // Reads the live visible text of an embedded conhost console — the real-time signal for "is this terminal working".
    internal static class ConsoleMirror
    {
        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        private static readonly IntPtr INVALID = new IntPtr(-1);
        private static bool _ctrlIgnored;

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
        [DllImport("kernel32.dll")] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleScreenBufferInfo(IntPtr h, out CONSOLE_SCREEN_BUFFER_INFO info);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ReadConsoleOutputCharacterW(IntPtr h, [Out] char[] buf, uint len, COORD coord, out uint read);

        [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct SMALL_RECT { public short Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct CONSOLE_SCREEN_BUFFER_INFO { public COORD dwSize; public COORD dwCursorPosition; public short wAttributes; public SMALL_RECT srWindow; public COORD dwMaximumWindowSize; }

        // Returns the bottom ~40 visible rows of the console as text, or null if unreadable.
        public static string Read(uint pid)
        {
            if (pid == 0) return null;
            bool attached = false;
            IntPtr h = INVALID;
            try
            {
                FreeConsole();
                if (!AttachConsole(pid)) return null;
                attached = true;
                if (!_ctrlIgnored) { SetConsoleCtrlHandler(IntPtr.Zero, true); _ctrlIgnored = true; }
                h = CreateFileW("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == INVALID) return null;
                CONSOLE_SCREEN_BUFFER_INFO csbi;
                if (!GetConsoleScreenBufferInfo(h, out csbi)) return null;
                int left = csbi.srWindow.Left, right = csbi.srWindow.Right, top = csbi.srWindow.Top, bottom = csbi.srWindow.Bottom;
                int w = right - left + 1;
                if (w <= 0 || w > 1000) return null;
                int startRow = Math.Max(top, bottom - 40);
                var sb = new StringBuilder();
                char[] line = new char[w];
                for (int row = startRow; row <= bottom; row++)
                {
                    uint read;
                    if (!ReadConsoleOutputCharacterW(h, line, (uint)w, new COORD { X = (short)left, Y = (short)row }, out read)) break;
                    int len = (int)read;
                    while (len > 0 && (line[len - 1] == ' ' || line[len - 1] == '\0')) len--;
                    sb.Append(line, 0, len).Append('\n');
                }
                return sb.ToString();
            }
            catch { return null; }
            finally { if (h != INVALID) CloseHandle(h); if (attached) FreeConsole(); }
        }

        // The last content-bearing line (skips blank lines and pure TUI border rows).
        public static string LastLine(string buf)
        {
            if (string.IsNullOrEmpty(buf)) return null;
            string[] lines = buf.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string t = lines[i].Trim(' ', '│', '─', '╭', '╮', '╰', '╯', '┌', '┐', '└', '┘', '├', '┤', '┬', '┴', '┼', '║', '═', '╔', '╗', '╚', '╝', '▌', '▐', '█', '·', '>', ' ').Trim();
                if (t.Length >= 2) return t;
            }
            return null;
        }

        // Inject keystrokes into a console's input buffer — used to (re)start Claude in an existing shell.
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteConsoleInputW(IntPtr h, INPUT_RECORD[] buffer, uint length, out uint written);

        [StructLayout(LayoutKind.Sequential)] private struct KEY_EVENT_RECORD { public int bKeyDown; public ushort wRepeatCount; public ushort wVirtualKeyCode; public ushort wVirtualScanCode; public ushort UnicodeChar; public uint dwControlKeyState; }
        [StructLayout(LayoutKind.Explicit)] private struct INPUT_RECORD { [FieldOffset(0)] public ushort EventType; [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent; }

        public static bool SendText(uint pid, string text)
        {
            if (pid == 0 || string.IsNullOrEmpty(text)) return false;
            bool attached = false; IntPtr h = INVALID;
            try
            {
                FreeConsole();
                if (!AttachConsole(pid)) return false;
                attached = true;
                if (!_ctrlIgnored) { SetConsoleCtrlHandler(IntPtr.Zero, true); _ctrlIgnored = true; }
                h = CreateFileW("CONIN$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == INVALID) return false;
                var recs = new INPUT_RECORD[text.Length * 2];
                for (int i = 0; i < text.Length; i++) { recs[i * 2] = Key(text[i], true); recs[i * 2 + 1] = Key(text[i], false); }
                uint written;
                return WriteConsoleInputW(h, recs, (uint)recs.Length, out written);
            }
            catch { return false; }
            finally { if (h != INVALID) CloseHandle(h); if (attached) FreeConsole(); }
        }

        private static INPUT_RECORD Key(char c, bool down)
        {
            return new INPUT_RECORD
            {
                EventType = 1, // KEY_EVENT
                KeyEvent = new KEY_EVENT_RECORD { bKeyDown = down ? 1 : 0, wRepeatCount = 1, wVirtualKeyCode = (ushort)(c == '\r' ? 0x0D : 0), wVirtualScanCode = 0, UnicodeChar = (ushort)c, dwControlKeyState = 0 }
            };
        }
    }

    // One embedded Claude Code terminal (a conhost console reparented into a tab page).
    internal sealed class TerminalTab
    {
        public readonly TabPage Page;
        public readonly string WorkingDir;
        public string Name { get; private set; }
        private readonly Label _status;
        private readonly System.Windows.Forms.Timer _find = new System.Windows.Forms.Timer();
        private readonly bool _resume;
        private Process _proc;
        private IntPtr _child = IntPtr.Zero;
        private int _tries;
        private bool _fellBack;
        private bool _canReopen;
        private string _lastBuf;
        private DateTime _lastChangeUtc = DateTime.UtcNow;
        private int _attachPid;
        private bool _resolvedClients;
        public bool Busy { get; private set; }
        public string Live { get; private set; }

        public TerminalTab(string dir, bool resume = false, string customName = null)
        {
            WorkingDir = dir;
            Name = string.IsNullOrWhiteSpace(customName) ? Title(dir) : customName.Trim();
            _resume = resume;
            Page = new TabPage(Name) { BackColor = Color.FromArgb(12, 12, 16), ToolTipText = dir, UseVisualStyleBackColor = false };
            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(150, 152, 164), Text = "Starting Claude Code…", Cursor = Cursors.Hand };
            _status.Click += (s, e) => { if (_canReopen) Reopen(); };
            Page.Controls.Add(_status);
            _find.Interval = 200;
            _find.Tick += (s, e) => TryEmbed();
            Page.Resize += (s, e) => Fit();
            Launch(false, _resume);
        }

        public void Rename(string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return;
            Name = newName.Trim().Replace("|", "/");   // '|' is the saved-state record separator
            Page.Text = Name;
            Page.ToolTipText = Name + " — " + WorkingDir;
        }

        // "Open Claude": if the shell is live, type claude into it; otherwise relaunch the terminal.
        public void StartClaude()
        {
            if (IsRunning && _child != IntPtr.Zero)
            {
                int pid = _attachPid != 0 ? _attachPid : _proc.Id;
                if (!ConsoleMirror.SendText((uint)pid, "claude\r"))
                    foreach (int p in TermNative.Descendants(_proc.Id))
                        if (p != _proc.Id && ConsoleMirror.SendText((uint)p, "claude\r")) { _attachPid = p; break; }
                Focus();
            }
            else Reopen();
        }

        // Relaunch a dead/unattached terminal from scratch.
        public void Reopen()
        {
            try { Kill(); } catch { }
            _fellBack = false; _canReopen = false; _child = IntPtr.Zero;
            _status.Visible = true; _status.Text = "Starting Claude Code…";
            Launch();
        }

        public bool IsRunning { get { return _proc != null && !_proc.HasExited; } }

        private void Launch(bool plain = false, bool resume = false)
        {
            _child = IntPtr.Zero;
            try
            {
                // cmd /k keeps the shell open after Claude exits; "plain" is the fallback when Claude won't start.
                _proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "conhost.exe",
                    Arguments = plain ? "cmd.exe /k" : "cmd.exe /k claude" + (resume ? " --continue" : ""),
                    UseShellExecute = false,
                    WorkingDirectory = WorkingDir
                });
            }
            catch (Exception ex) { _status.Text = "Launch failed: " + ex.Message; _canReopen = true; return; }
            _tries = 0;
            _find.Start();
        }

        private void TryEmbed()
        {
            _tries++;
            if (_proc == null || _proc.HasExited)
            {
                // Claude exited before we could embed — force a plain terminal so there's always a usable shell.
                if (!_fellBack) { _fellBack = true; _status.Text = "Claude Code exited — opening a terminal…"; Launch(true); return; }
                _find.Stop(); _canReopen = true; _status.Text = "Terminal closed — click to reopen.";
                return;
            }
            IntPtr h = TermNative.FindConsoleWindow(_proc.Id);
            if (h != IntPtr.Zero) { _find.Stop(); _child = h; TermNative.Reparent(h, Page.Handle); _status.Visible = false; Fit(); TermNative.FocusConsole(h); }
            else if (_tries > 60)
            {
                if (!_fellBack) { _fellBack = true; Kill(); _status.Text = "Opening a terminal…"; Launch(true); return; }
                _find.Stop(); _canReopen = true; _status.Text = "Couldn't attach — click to reopen.";
            }
        }

        public void Fit() { if (_child != IntPtr.Zero) TermNative.MoveWindow(_child, 0, 0, Page.ClientSize.Width, Page.ClientSize.Height, true); }
        public void Focus() { if (_child != IntPtr.Zero) TermNative.FocusConsole(_child); }

        // Poll the live console: "busy" when its visible text changed recently (catches long turns the transcript misses).
        public void Sample()
        {
            try
            {
                if (!IsRunning) { Busy = false; return; }
                if (_attachPid == 0) _attachPid = _proc.Id;
                string buf = ConsoleMirror.Read((uint)_attachPid);
                if (buf == null && !_resolvedClients)
                {
                    _resolvedClients = true;
                    foreach (int pid in TermNative.Descendants(_proc.Id))
                    {
                        if (pid == _proc.Id) continue;
                        string b2 = ConsoleMirror.Read((uint)pid);
                        if (b2 != null) { _attachPid = pid; buf = b2; break; }
                    }
                }
                if (buf == null) { Busy = false; return; }
                if (!string.Equals(buf, _lastBuf, StringComparison.Ordinal)) { _lastBuf = buf; _lastChangeUtc = DateTime.UtcNow; }
                Busy = (DateTime.UtcNow - _lastChangeUtc).TotalSeconds < 2.5;
                string ll = ConsoleMirror.LastLine(buf);
                if (!string.IsNullOrEmpty(ll)) Live = ll;
            }
            catch { Busy = false; }
        }

        public void Kill()
        {
            _find.Stop();
            try
            {
                if (IsRunning)
                    foreach (int pid in TermNative.Descendants(_proc.Id))
                        try { Process.GetProcessById(pid).Kill(); } catch { }
            }
            catch { }
        }

        private static string Title(string dir)
        {
            try { string n = new DirectoryInfo(dir).Name; return string.IsNullOrEmpty(n) ? dir : n; }
            catch { return "agent"; }
        }
    }

    // TabControl whose empty tab-strip background is painted flat-dark, with a per-tab close ✕.
    internal sealed class FlatTabControl : TabControl
    {
        public FlatTabControl()
        {
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SizeMode = TabSizeMode.Fixed;
            Appearance = TabAppearance.Normal;
            DoubleBuffered = true;
        }

        public static Rectangle CloseRect(Rectangle tab) { return new Rectangle(tab.Right - 20, tab.Y + (tab.Height - 14) / 2, 14, 14); }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F && IsHandleCreated && TabCount > 0)
                using (var g = Graphics.FromHwnd(Handle))
                using (var dark = new SolidBrush(Color.FromArgb(16, 16, 22)))
                {
                    Rectangle last = GetTabRect(TabCount - 1);
                    g.FillRectangle(dark, last.Right, last.Y - 3, Width - last.Right, last.Height + 6);
                }
        }
    }

    // Borderless, flat host window with one tab per embedded Claude Code terminal.
    public sealed class AgentTerminalForm : Form
    {
        private readonly FlatTabControl _tabs = new FlatTabControl { Dock = DockStyle.Fill };
        private readonly List<TerminalTab> _terms = new List<TerminalTab>();
        private bool _restored;

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        public AgentTerminalForm()
        {
            FormBorderStyle = FormBorderStyle.Sizable;   // system chrome: native drag / resize / maximize / Aero-Snap dock
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1000, 600);
            MinimumSize = new Size(520, 340);
            ShowInTaskbar = true;
            BackColor = Color.FromArgb(10, 10, 14);
            Icon = AppIcons.Get();
            Text = "CioStat — Claude Code";
            Font = new Font("Segoe UI", 9f);

            _tabs.ItemSize = new Size(176, 30);
            _tabs.Font = new Font("Segoe UI", 9f);
            _tabs.DrawItem += DrawTab;
            _tabs.MouseDown += OnTabMouseDown;
            _tabs.MouseDoubleClick += OnTabDoubleClick;   // double-click a tab to rename it
            _tabs.ContextMenuStrip = BuildTabMenu();      // right-click a tab for its menu
            _tabs.SelectedIndexChanged += (s, e) => { TerminalTab t = Active(); if (t != null) { t.Fit(); t.Focus(); } SaveState(); };

            Controls.Add(_tabs);
        }

        // Double-click a tab (not its ✕) to rename it.
        private void OnTabDoubleClick(object sender, MouseEventArgs e)
        {
            for (int i = 0; i < _tabs.TabCount; i++)
            {
                Rectangle r = _tabs.GetTabRect(i);
                Rectangle ri = r; ri.Inflate(2, 2);
                if (FlatTabControl.CloseRect(ri).Contains(e.Location)) return;
                if (r.Contains(e.Location)) { BeginRename(i); return; }
            }
        }

        // Framework-managed right-click menu (reliable via WM_CONTEXTMENU); rebuilt for the tab under the cursor.
        private ContextMenuStrip BuildTabMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Opening += (s, e) =>
            {
                Point cp = _tabs.PointToClient(Cursor.Position);
                int idx = -1;
                for (int i = 0; i < _tabs.TabCount; i++) { Rectangle r = _tabs.GetTabRect(i); r.Inflate(2, 2); if (r.Contains(cp)) { idx = i; break; } }
                if (idx < 0) { e.Cancel = true; return; }
                menu.Items.Clear();
                menu.Items.Add("Open Claude", null, (a, b) => { TerminalTab t = TabAt(idx); if (t != null) t.StartClaude(); });
                menu.Items.Add("Rename…", null, (a, b) => BeginRename(idx));
                menu.Items.Add("Close", null, (a, b) => CloseAt(idx));
            };
            return menu;
        }

        private TerminalTab TabAt(int index)
        {
            if (index < 0 || index >= _tabs.TabPages.Count) return null;
            TabPage page = _tabs.TabPages[index];
            foreach (TerminalTab x in _terms) if (x.Page == page) return x;
            return null;
        }

        private void BeginRename(int index)
        {
            TerminalTab t = TabAt(index);
            if (t == null) return;
            string name = PromptRename(t.Name);
            if (!string.IsNullOrWhiteSpace(name)) { t.Rename(name); SaveState(); _tabs.Invalidate(); }
        }

        // Small modal dialog — reliable input without the inline-editor focus problems.
        private string PromptRename(string current)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Rename terminal";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.MinimizeBox = false; dlg.MaximizeBox = false; dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(324, 100);
                dlg.BackColor = Color.FromArgb(24, 24, 32);
                dlg.Icon = AppIcons.Get();
                var tb = new TextBox { Text = current, Bounds = new Rectangle(14, 16, 296, 24), BackColor = Color.FromArgb(30, 30, 42), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
                var ok = new Button { Text = "Rename", DialogResult = DialogResult.OK, Bounds = new Rectangle(150, 56, 76, 28), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(217, 119, 87) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(234, 56, 76, 28), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(40, 40, 52) };
                ok.FlatAppearance.BorderSize = 0; cancel.FlatAppearance.BorderSize = 0;
                dlg.Controls.Add(tb); dlg.Controls.Add(ok); dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok; dlg.CancelButton = cancel;
                tb.Select(); tb.SelectAll();
                return dlg.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
            }
        }

        // Paint the system title bar dark to match the app (Win10 1809+ / Win11).
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int on = 1; if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4); } catch { }
        }

        // The window's ✕ hides it (terminals keep running); real teardown happens on app exit.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            base.OnFormClosing(e);
        }

        private void DrawTab(object sender, DrawItemEventArgs e)
        {
            TabPage tp = _tabs.TabPages[e.Index];
            bool sel = e.Index == _tabs.SelectedIndex;
            Rectangle r = _tabs.GetTabRect(e.Index); r.Inflate(2, 2);
            using (var bg = new SolidBrush(sel ? Color.FromArgb(30, 30, 42) : Color.FromArgb(18, 18, 26)))
                e.Graphics.FillRectangle(bg, r);
            if (sel)
                using (var ac = new SolidBrush(Color.FromArgb(217, 119, 87)))
                    e.Graphics.FillRectangle(ac, r.X, _tabs.Alignment == TabAlignment.Bottom ? r.Y : r.Bottom - 3, r.Width, 3);
            Rectangle textR = new Rectangle(r.X + 4, r.Y, r.Width - 26, r.Height);
            TextRenderer.DrawText(e.Graphics, tp.Text, _tabs.Font, textR, sel ? Color.White : Color.FromArgb(150, 152, 164),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Rectangle x = FlatTabControl.CloseRect(r);
            using (var pen = new Pen(sel ? Color.FromArgb(210, 212, 220) : Color.FromArgb(120, 122, 134), 1.4f))
            {
                e.Graphics.DrawLine(pen, x.Left + 3, x.Top + 3, x.Right - 3, x.Bottom - 3);
                e.Graphics.DrawLine(pen, x.Right - 3, x.Top + 3, x.Left + 3, x.Bottom - 3);
            }
        }

        private void OnTabMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            for (int i = 0; i < _tabs.TabCount; i++)
            {
                Rectangle r = _tabs.GetTabRect(i); r.Inflate(2, 2);
                if (FlatTabControl.CloseRect(r).Contains(e.Location)) { CloseAt(i); return; }
            }
        }

        private void CloseAt(int index)
        {
            if (index < 0 || index >= _tabs.TabPages.Count) return;
            TabPage page = _tabs.TabPages[index];
            TerminalTab t = null;
            foreach (TerminalTab x in _terms) if (x.Page == page) { t = x; break; }
            if (t == null) return;
            t.Kill();
            _tabs.TabPages.Remove(page);
            _terms.Remove(t);
            SaveState();
            if (_terms.Count == 0) Hide();
        }

        private TerminalTab Active()
        {
            TabPage p = _tabs.SelectedTab;
            foreach (TerminalTab t in _terms) if (t.Page == p) return t;
            return _terms.Count > 0 ? _terms[_terms.Count - 1] : null;
        }

        public string ActiveWorkingDir { get { TerminalTab t = Active(); return t != null ? t.WorkingDir : null; } }
        public bool IsRunning { get { TerminalTab t = Active(); return t != null && t.IsRunning; } }

        public List<TermStatus> Terminals()
        {
            var list = new List<TermStatus>();
            TabPage sel = _tabs.SelectedTab;
            foreach (TerminalTab t in _terms)
            {
                t.Sample();
                list.Add(new TermStatus { Dir = t.WorkingDir, Name = t.Name, Running = t.IsRunning, Active = t.Page == sel, Busy = t.Busy, Live = t.Live });
            }
            return list;
        }

        public void Toggle()
        {
            if (Visible) { Hide(); return; }
            EnsureRestored();
            if (_terms.Count == 0) OpenTerminal(null);
            Show(); Activate();
            TerminalTab t = Active(); if (t != null) t.Focus();
        }

        public void OpenTerminal(string dir) { OpenTerminal(dir, false); }

        // merge: if a terminal for this folder is already open, switch to it instead of opening a duplicate.
        public void OpenTerminal(string dir, bool merge)
        {
            EnsureRestored();
            if (string.IsNullOrEmpty(dir))
            {
                dir = AppSettings.Default.AgentWorkingDir;
                if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            if (merge)
                foreach (TerminalTab x in _terms)
                    if (string.Equals(x.WorkingDir, dir, StringComparison.OrdinalIgnoreCase))
                    {
                        _tabs.SelectedTab = x.Page;
                        if (!Visible) Show();
                        Activate(); x.Focus();
                        return;
                    }
            var t = new TerminalTab(dir);
            _terms.Add(t);
            _tabs.TabPages.Add(t.Page);
            _tabs.SelectedTab = t.Page;
            if (!Visible) Show();
            Activate();
            t.Focus();
            SaveState();
        }

        public void SetWorkingDir(string path) { OpenTerminal(path, true); }

        // Recover the terminals that were open when the app last closed (resume their Claude sessions).
        private void EnsureRestored()
        {
            if (_restored) return;
            _restored = true;
            string saved = AppSettings.Default.OpenTerminals;
            if (string.IsNullOrEmpty(saved)) return;
            string activeDir = AppSettings.Default.ActiveTerminalDir;
            Dictionary<string, string> names = ParseNameMap(AppSettings.Default.TerminalNames);
            TabPage activePage = null;
            foreach (string dir in saved.Split('|'))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                bool exists = false;
                foreach (TerminalTab x in _terms) if (string.Equals(x.WorkingDir, dir, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                if (exists) continue;
                string custom; names.TryGetValue(dir, out custom);
                var t = new TerminalTab(dir, true, custom);
                _terms.Add(t);
                _tabs.TabPages.Add(t.Page);
                if (string.Equals(dir, activeDir, StringComparison.OrdinalIgnoreCase)) activePage = t.Page;
            }
            if (activePage != null) _tabs.SelectedTab = activePage;
        }

        private void SaveState()
        {
            // The agent is created at startup but may never be opened. If we haven't restored yet, _terms is empty
            // NOT because the user closed everything — so saving here would wipe the real list. Skip it.
            if (!_restored) return;
            var sb = new StringBuilder();
            var names = new StringBuilder();
            foreach (TerminalTab t in _terms)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(t.WorkingDir);
                if (names.Length > 0) names.Append('|');
                names.Append(t.WorkingDir).Append('?').Append(t.Name);
            }
            AppSettings.Default.OpenTerminals = sb.ToString();
            AppSettings.Default.TerminalNames = names.ToString();
            string active = ActiveWorkingDir;
            AppSettings.Default.ActiveTerminalDir = active ?? string.Empty;
            if (!string.IsNullOrEmpty(active)) AppSettings.Default.AgentWorkingDir = active;
            AppSettings.Default.Save();
        }

        // dir ? name, records joined by | — control chars never occur in paths or typed names.
        private static Dictionary<string, string> ParseNameMap(string s)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(s)) return map;
            foreach (string rec in s.Split('|'))
            {
                int i = rec.IndexOf('?');
                if (i > 0) { string d = rec.Substring(0, i); if (!map.ContainsKey(d)) map[d] = rec.Substring(i + 1); }
            }
            return map;
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); TerminalTab t = Active(); if (t != null) t.Focus(); }
        protected override void OnActivated(EventArgs e) { base.OnActivated(e); TerminalTab t = Active(); if (t != null) t.Focus(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { SaveState(); foreach (TerminalTab t in _terms) t.Kill(); }
            base.Dispose(disposing);
        }
    }
}
