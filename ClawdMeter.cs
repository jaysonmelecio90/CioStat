using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CioStats
{
    // Borderless, always-on-top "modern Clippy" widget: a live mini-TV of the active Claude
    // Code session on the left, plus the subscriber name, plan and claude.ai usage on the right.
    public sealed class ClawdMeter : Form
    {
        private const int BaseW = 540;
        private const int BaseH = 196;

        private readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _anim = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _sessionTimer = new System.Windows.Forms.Timer();
        private readonly ClaudeUsageMonitor _monitor = new ClaudeUsageMonitor();
        private UsageSnapshot _snap;
        private AgentSession _session;
        private List<TermRow> _rows = new List<TermRow>();
        private string _error;
        private bool _initialized;
        private bool _loginPrompted;

        private float _scale = 1f;
        private Point _dragOrigin, _formOrigin;
        private bool _dragging, _moved;

        private Font _fTitle, _fBig, _fText, _fSmall, _fTV;

        public Action ExitRequested;
        public Action ToggleAgentRequested;
        public Action NewTerminalRequested;
        public Action ChooseFolderRequested;
        public AgentTerminalForm Agent;

        private static readonly Color ClBgTop = Color.FromArgb(38, 38, 48);
        private static readonly Color ClBgBottom = Color.FromArgb(24, 24, 32);
        private static readonly Color ClBorder = Color.FromArgb(70, 70, 86);
        private static readonly Color ClText = Color.FromArgb(244, 244, 247);
        private static readonly Color ClMuted = Color.FromArgb(150, 152, 164);
        private static readonly Color ClAccent = Color.FromArgb(217, 119, 87);
        private static readonly Color ClTrack = Color.FromArgb(56, 56, 68);
        private static readonly Color ClGreen = Color.FromArgb(74, 222, 128);
        private static readonly Color ClAmber = Color.FromArgb(251, 191, 36);
        private static readonly Color ClRed = Color.FromArgb(248, 113, 113);
        private static readonly Color ClBlue = Color.FromArgb(120, 170, 255);
        private static readonly Color ClScreen = Color.FromArgb(12, 12, 18);
        private static readonly Color ClScreenEdge = Color.FromArgb(44, 44, 56);

        public ClawdMeter()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Opacity = 0.96;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            _monitor.Updated += OnUsage;
            _monitor.SignedIn += () => RefreshNow();

            BuildContextMenu();
            MouseDown += OnMouseDownDrag;
            MouseMove += OnMouseMoveDrag;
            MouseUp += OnMouseUpDrag;
            DoubleClick += (s, e) => { if (ToggleAgentRequested != null) ToggleAgentRequested(); };

            _poll.Tick += (s, e) => RefreshNow();
            _anim.Interval = 60;
            _anim.Tick += (s, e) => Invalidate();
            _sessionTimer.Interval = 1000;
            _sessionTimer.Tick += (s, e) => RefreshSession();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyScale();
            RestoreSize();
            ApplyRegion();
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyScale();
            RestoreSize();
            RestoreLocation();
            ApplyRegion();
            _anim.Start();
            _sessionTimer.Start();
            RefreshSession();
            await InitMonitorAsync();
        }

        private async Task InitMonitorAsync()
        {
            try
            {
                await _monitor.InitAsync();
                _initialized = true;
                _poll.Start();
                await _monitor.PollAsync();
            }
            catch (Exception ex)
            {
                _error = "WebView2 init failed: " + ex.Message;
                Invalidate();
            }
        }

        private void RefreshSession()
        {
            var rows = new List<TermRow>();
            AgentSession active = null;
            if (Agent != null)
            {
                foreach (TermStatus ts in Agent.Terminals())
                {
                    AgentSession s = ClaudeSessionWatcher.Read(ts.Dir, ts.Running);
                    string state = s.State;
                    string activity = !string.IsNullOrEmpty(ts.Live) ? ts.Live : s.Activity;
                    if (ts.Running)
                    {
                        if (ts.Busy) state = "Working";                 // console output is moving right now
                        else if (state == "Working") state = "Idle";    // transcript said working but the console is static
                    }
                    rows.Add(new TermRow { Name = ts.Name, State = state, Activity = activity, Idle = s.IdleSeconds, Running = ts.Running, Active = ts.Active });
                    if (ts.Active) { active = s; if (ts.Busy) active.State = "Working"; }
                }
            }
            if (rows.Count == 0)
            {
                // no live terminals open — list the recent (saved) sessions so they're never "missing"
                string saved = AppSettings.Default.OpenTerminals;
                if (!string.IsNullOrEmpty(saved))
                    foreach (string dir in saved.Split('|'))
                    {
                        if (string.IsNullOrEmpty(dir)) continue;
                        AgentSession s = ClaudeSessionWatcher.Read(dir, false);
                        rows.Add(new TermRow { Name = ShortDir(dir), State = s.State, Activity = s.Activity, Idle = s.IdleSeconds, Running = false, Active = false });
                        if (active == null) active = s;
                    }
            }
            if (active == null)
            {
                string dir = AppSettings.Default.AgentWorkingDir;
                if (string.IsNullOrEmpty(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                active = ClaudeSessionWatcher.Read(dir, false);
            }
            _rows = rows;
            _session = active;
            Invalidate();
        }

        private void OnUsage(UsageSnapshot snap)
        {
            _snap = snap;
            _error = null;
            if (!snap.SignedIn && !_loginPrompted) { _loginPrompted = true; _monitor.ShowLogin(); }
            Invalidate();
        }

        public void RefreshNow()
        {
            if (!_initialized) return;
            _ = _monitor.PollAsync();
        }

        public void SignIn() { _loginPrompted = true; _monitor.ShowLogin(); }

        public async void SignOut()
        {
            await _monitor.SignOutAsync();
            _snap = null; _loginPrompted = false;
            Invalidate();
            _monitor.ShowLogin();
        }

        public void ToggleVisible()
        {
            Visible = !Visible;
            if (Visible) { TopMost = true; BringToFront(); }
        }

        public void ShowSettings()
        {
            using (var f = new SettingsForm(SignIn, SignOut))
                if (f.ShowDialog(this) == DialogResult.OK) { ReloadConfig(); RefreshNow(); }
        }

        public void ReloadConfig()
        {
            int secs = AppSettings.Default.PollIntervalSeconds;
            if (secs < 30) secs = 30;
            _poll.Interval = secs * 1000;
        }

        private void OnMouseDownDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (CloseRect().Contains(e.Location)) { Visible = false; return; }
            _dragging = true; _moved = false;
            _dragOrigin = Cursor.Position; _formOrigin = Location;
        }

        private void OnMouseMoveDrag(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            Point now = Cursor.Position;
            int dx = now.X - _dragOrigin.X, dy = now.Y - _dragOrigin.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 2) _moved = true;
            Location = new Point(_formOrigin.X + dx, _formOrigin.Y + dy);
        }

        private void OnMouseUpDrag(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            if (_moved) { SaveLocation(); return; }
            if (_snap != null && !_snap.SignedIn) SignIn();
        }

        private void SaveLocation()
        {
            AppSettings.Default.WindowLocation =
                Location.X.ToString(CultureInfo.InvariantCulture) + "," + Location.Y.ToString(CultureInfo.InvariantCulture);
            AppSettings.Default.Save();
        }

        private void RestoreLocation()
        {
            string saved = AppSettings.Default.WindowLocation;
            int x, y;
            if (!string.IsNullOrEmpty(saved))
            {
                string[] p = saved.Split(',');
                if (p.Length == 2 &&
                    int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) &&
                    int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y))
                { Location = Clamp(new Point(x, y)); return; }
            }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = Clamp(new Point(wa.Right - Width - 24, wa.Bottom - Height - 24));
        }

        private Point Clamp(Point p)
        {
            Rectangle wa = Screen.FromPoint(new Point(p.X + Width / 2, p.Y + Height / 2)).WorkingArea;
            int cx = Math.Max(wa.Left, Math.Min(p.X, wa.Right - Width));
            int cy = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - Height));
            return new Point(cx, cy);
        }

        private void ApplyScale()
        {
            _scale = DeviceDpi / 96f;
            DisposeFonts();
            _fTitle = new Font("Segoe UI Semibold", 11.5f, FontStyle.Bold);
            _fBig = new Font("Segoe UI", 16f, FontStyle.Bold);
            _fText = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            _fSmall = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            _fTV = new Font("Cascadia Mono", 8f, FontStyle.Regular);
            if (_fTV.Name != "Cascadia Mono") { _fTV.Dispose(); _fTV = new Font("Consolas", 8f, FontStyle.Regular); }
        }

        private void ApplyRegion()
        {
            Region old = Region;
            using (GraphicsPath p = Rounded(new Rectangle(0, 0, Width, Height), S(16)))
                Region = new Region(p);
            if (old != null) old.Dispose();
        }

        private int S(float dip) { return (int)Math.Round(dip * _scale); }

        private void RestoreSize()
        {
            int w = S(BaseW), h = S(BaseH);
            string saved = AppSettings.Default.WindowSize;
            if (!string.IsNullOrEmpty(saved))
            {
                string[] p = saved.Split(',');
                int sw, sh;
                if (p.Length == 2 &&
                    int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out sw) &&
                    int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sh)) { w = sw; h = sh; }
            }
            MinimumSize = new Size(S(380), S(150));
            Size = new Size(Math.Max(MinimumSize.Width, w), Math.Max(MinimumSize.Height, h));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_fTitle != null) ApplyRegion();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            AppSettings.Default.WindowSize = Width.ToString(CultureInfo.InvariantCulture) + "," + Height.ToString(CultureInfo.InvariantCulture);
            AppSettings.Default.Save();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084) // WM_NCHITTEST — edge/corner resize on a borderless form
            {
                base.WndProc(ref m);
                int lp = m.LParam.ToInt32();
                Point pos = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                int grip = S(8);
                bool r = pos.X >= ClientSize.Width - grip, b = pos.Y >= ClientSize.Height - grip, l = pos.X <= grip, t = pos.Y <= grip;
                if (r && b) m.Result = (IntPtr)17;
                else if (l && b) m.Result = (IntPtr)16;
                else if (r && t) m.Result = (IntPtr)14;
                else if (l && t) m.Result = (IntPtr)13;
                else if (r) m.Result = (IntPtr)11;
                else if (l) m.Result = (IntPtr)10;
                else if (b) m.Result = (IntPtr)15;
                else if (t) m.Result = (IntPtr)12;
                return;
            }
            base.WndProc(ref m);
        }

        private Rectangle CloseRect() { return new Rectangle(Width - S(21), S(7), S(13), S(13)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_fTitle == null) ApplyScale();
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle card = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Rounded(card, S(16)))
            using (var bg = new LinearGradientBrush(card, ClBgTop, ClBgBottom, LinearGradientMode.Vertical))
            {
                g.FillPath(bg, path);
                using (var pen = new Pen(ClBorder, 1f)) g.DrawPath(pen, path);
            }

            Rectangle cr = CloseRect();
            using (var xp = new Pen(ClMuted, 1.4f))
            {
                g.DrawLine(xp, cr.Left + S(3), cr.Top + S(3), cr.Right - S(3), cr.Bottom - S(3));
                g.DrawLine(xp, cr.Right - S(3), cr.Top + S(3), cr.Left + S(3), cr.Bottom - S(3));
            }

            int pad = S(12);
            int tvW = Math.Max(S(120), Math.Min(S(260), Width - S(230)));
            Rectangle screen = new Rectangle(pad, pad, tvW, Height - 2 * pad);
            DrawTV(g, screen);

            int rx = pad + tvW + S(12);
            int rw = Width - rx - pad;
            DrawInfo(g, rx, rw);
        }

        private void DrawTV(Graphics g, Rectangle r)
        {
            using (GraphicsPath p = Rounded(r, S(10)))
            using (var b = new SolidBrush(ClScreen))
            using (var pen = new Pen(ClScreenEdge, 1f))
            {
                g.FillPath(b, p);
                g.DrawPath(pen, p);
            }

            int ip = S(8);
            int n = _rows != null ? _rows.Count : 0;
            using (var hb = new SolidBrush(ClMuted)) g.DrawString("terminals (" + n + ")", _fSmall, hb, r.X + ip, r.Y + ip);
            int headB = r.Y + ip + S(16);

            Region old = g.Clip;
            g.SetClip(new Rectangle(r.X + ip, headB, r.Width - 2 * ip, r.Bottom - ip - headB));
            if (n == 0)
            {
                using (var mb = new SolidBrush(ClMuted)) g.DrawString("no terminals — double-click CioStat", _fTV, mb, r.X + ip, headB + S(2));
            }
            else
            {
                int availH = r.Bottom - ip - S(2) - headB;             // shrink rows so every terminal fits
                int rowH = Math.Min(S(32), Math.Max(S(13), availH / n));
                bool showAct = rowH >= S(27);
                int y = headB + S(2);
                foreach (TermRow row in _rows)
                {
                    Color sc = StatusColor(row);
                    if (row.Active)
                        using (var hl = new SolidBrush(Color.FromArgb(30, 30, 42)))
                            g.FillRectangle(hl, r.X + ip - S(3), y - S(1), r.Width - 2 * ip + S(6), rowH - S(2));
                    using (var d = new SolidBrush(sc)) g.FillEllipse(d, r.X + ip, y + S(3), S(7), S(7));
                    using (var nb = new SolidBrush(row.Active ? ClText : ClMuted))
                        g.DrawString(Truncate(g, row.Name, _fText, r.Width - 2 * ip - S(66)), _fText, nb, r.X + ip + S(13), y - S(1));
                    string st = StatusWord(row);
                    SizeF stz = g.MeasureString(st, _fSmall);
                    using (var sb2 = new SolidBrush(sc)) g.DrawString(st, _fSmall, sb2, r.Right - ip - stz.Width, y);
                    if (showAct && !string.IsNullOrEmpty(row.Activity))
                        using (var ab = new SolidBrush(ClMuted))
                            g.DrawString(Truncate(g, row.Activity, _fTV, r.Width - 2 * ip - S(13)), _fTV, ab, r.X + ip + S(13), y + S(14));
                    y += rowH;
                }
            }
            g.Clip = old;
        }

        private static Color StatusColor(TermRow row)
        {
            if (!row.Running) return ClMuted;
            switch (row.State) { case "Working": return ClGreen; case "Idle": return ClAmber; case "Ready": return ClBlue; default: return ClMuted; }
        }

        private static string StatusWord(TermRow row)
        {
            if (!row.Running) return "done";
            switch (row.State) { case "Working": return "ongoing"; case "Idle": return "idle"; case "Ready": return "ready"; default: return "—"; }
        }

        private void DrawInfo(Graphics g, int rx, int rw)
        {
            int pad = S(12);
            int mascot = S(40);
            int tick = Environment.TickCount;
            float bob = (float)Math.Sin(tick / 600.0) * S(2);

            AgentSession sess = _session;
            string state = sess != null ? sess.State : "Off";
            bool working = state == "Working";
            double maxUse = _snap != null ? _snap.MaxPercent : 0;
            Color stateColor = _error != null ? ClRed : AgentColor(state);
            bool worried = maxUse >= 90 || (_snap != null && !_snap.SignedIn);
            float jitter = working ? (float)Math.Sin(tick / 110.0) * S(1) : 0;

            ClawdArt.Draw(g, new RectangleF(rx, pad + bob + jitter, mascot, mascot), ClAccent, Blink(tick), worried);

            int tx = rx + mascot + S(10);
            int right = rx + rw;
            int y = pad;

            // line 1: name + status dot + plan (inline)
            string name = (_snap != null && !string.IsNullOrEmpty(_snap.SubscriberName)) ? _snap.SubscriberName : "CioStat";
            name = Truncate(g, name, _fTitle, rw - mascot - S(10) - S(78));
            using (var b = new SolidBrush(ClText)) g.DrawString(name, _fTitle, b, tx, y);
            SizeF tsz = g.MeasureString(name, _fTitle);
            int cx = tx + (int)tsz.Width + S(5);
            using (var dot = new SolidBrush(stateColor)) g.FillEllipse(dot, cx, y + S(6), S(8), S(8));
            cx += S(12);
            string plan = _snap != null ? _snap.Plan : null;
            if (!string.IsNullOrEmpty(plan))
                using (var b = new SolidBrush(ClMuted)) g.DrawString(Truncate(g, "· " + plan, _fSmall, right - cx - S(16), false), _fSmall, b, cx, y + S(3));

            // line 2: big agent state
            int sy = y + (int)tsz.Height + S(1);
            using (var b = new SolidBrush(_error != null ? ClRed : stateColor))
                g.DrawString(_error != null ? "error" : StateText(state), _fBig, b, tx, sy);
            int bigH = (int)Math.Ceiling(g.MeasureString("Wg", _fBig).Height);

            // usage bars: current session, all models, sonnet only
            int by = Math.Max(pad + mascot + S(6), sy + bigH + S(4));
            int footerY = Height - pad - S(12);
            if (_snap != null && _snap.SignedIn && _snap.Buckets.Count > 0)
            {
                int rowH = S(16);
                int labelW = S(58);
                int barX = rx + labelW;
                int shown = 0;
                foreach (UsageBucket bk in _snap.Buckets)
                {
                    if (by + S(13) > footerY - S(2) || shown >= 4) break;
                    using (var lb = new SolidBrush(ClMuted)) g.DrawString(BarLabel(bk.Label), _fSmall, lb, rx, by);
                    string pc = Math.Round(bk.Percent).ToString(CultureInfo.InvariantCulture) + "%";
                    string rs = ResetShort(bk.ResetsAt);
                    SizeF ps = g.MeasureString(pc, _fSmall);
                    float resetW = string.IsNullOrEmpty(rs) ? 0 : g.MeasureString(rs, _fSmall).Width;
                    float resetX = rx + rw - resetW;
                    float pctX = resetX - (resetW > 0 ? S(5) : 0) - ps.Width;
                    int gW = Math.Max(S(20), (int)pctX - S(6) - barX);
                    DrawGauge(g, barX, by + S(4), gW, S(6), bk.Percent, GaugeColor(bk.Percent));
                    using (var pb = new SolidBrush(GaugeColor(bk.Percent))) g.DrawString(pc, _fSmall, pb, pctX, by);
                    if (resetW > 0) using (var rb = new SolidBrush(ClMuted)) g.DrawString(rs, _fSmall, rb, resetX, by);
                    by += rowH;
                    shown++;
                }
            }
            else
            {
                using (var b = new SolidBrush(_error != null ? ClRed : ClMuted))
                    g.DrawString(Truncate(g, _error != null ? "—" : UsageLine(), _fSmall, rw), _fSmall, b, rx, by);
            }

            using (var b = new SolidBrush(_error != null ? ClRed : ClMuted))
                g.DrawString(Truncate(g, _error != null ? "⚠ " + _error : Footer(sess), _fSmall, rw), _fSmall, b, rx, footerY);
        }

        private static string StateText(string s)
        {
            switch (s) { case "Working": return "Working…"; case "Idle": return "Idle"; case "Ready": return "Ready"; default: return "Agent off"; }
        }

        private static Color AgentColor(string s)
        {
            switch (s) { case "Working": return ClGreen; case "Idle": return ClAmber; case "Ready": return ClBlue; default: return ClMuted; }
        }

        private static Color RecentColor(string line)
        {
            if (line.StartsWith("you:")) return ClBlue;
            if (line.StartsWith("Claude:")) return ClText;
            if (line.StartsWith("tool")) return ClAmber;
            return ClMuted;
        }

        private static string ShortDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return "no terminal";
            try { string n = new DirectoryInfo(path).Name; return string.IsNullOrEmpty(n) ? path : n; } catch { return path; }
        }

        private string UsageLine()
        {
            if (_snap == null) return "usage: starting…";
            if (!_snap.SignedIn) return "usage: click to sign in";
            if (_snap.Buckets.Count == 0) return "usage: " + (_snap.Note ?? "—");
            var parts = new List<string>();
            foreach (UsageBucket b in _snap.Buckets)
            {
                parts.Add(ShortLabel(b.Label) + " " + Math.Round(b.Percent).ToString(CultureInfo.InvariantCulture) + "%");
                if (parts.Count >= 3) break;
            }
            return string.Join("  ·  ", parts);
        }

        private static string ShortLabel(string label)
        {
            string k = (label ?? string.Empty).ToLowerInvariant();
            if (k.Contains("session") || k.Contains("5h")) return "5h";
            if (k.Contains("opus")) return "Opus";
            if (k.Contains("sonnet")) return "Son";
            if (k.Contains("week")) return "Wk";
            return label;
        }

        // Compact "resets in" countdown shown next to each usage percent (e.g. ↻2h, ↻45m, ↻5d).
        private static string ResetShort(DateTime? resetUtc)
        {
            if (resetUtc == null) return "";
            TimeSpan d = resetUtc.Value.ToUniversalTime() - DateTime.UtcNow;
            if (d.TotalSeconds <= 0) return "↻now";
            if (d.TotalDays >= 1) return "↻" + (int)Math.Round(d.TotalDays) + "d";
            if (d.TotalHours >= 1) return "↻" + (int)Math.Floor(d.TotalHours) + "h";
            return "↻" + Math.Max(1, (int)Math.Round(d.TotalMinutes)) + "m";
        }

        private static string BarLabel(string label)
        {
            string k = (label ?? string.Empty).ToLowerInvariant();
            if (k.Contains("session") || k.Contains("5h")) return "Session";
            if (k.Contains("opus")) return "Opus";
            if (k.Contains("sonnet")) return "Sonnet";
            if (k.Contains("week")) return "All models";
            return label;
        }

        private string Footer(AgentSession s)
        {
            string left = (s != null && s.HasSession) ? "active " + Age(s.IdleSeconds) + " ago"
                                                       : "agent: " + (s != null ? s.State : "off").ToLowerInvariant();
            string right = (_snap != null && _snap.SignedIn) ? "usage " + _snap.FetchedAtUtc.ToLocalTime().ToString("HH:mm")
                                                             : "not signed in";
            return left + "  ·  " + right;
        }

        private static string Age(double secs)
        {
            if (secs < 60) return (int)secs + "s";
            if (secs < 3600) return (int)(secs / 60) + "m";
            return (int)(secs / 3600) + "h";
        }

        private string Truncate(Graphics g, string text, Font f, int maxW) { return Truncate(g, text, f, maxW, true); }

        private string Truncate(Graphics g, string text, Font f, int maxW, bool ellipsis)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            if (g.MeasureString(text, f).Width <= maxW) return text;
            while (text.Length > 1 && g.MeasureString(text + (ellipsis ? "…" : ""), f).Width > maxW) text = text.Substring(0, text.Length - 1);
            return text + (ellipsis ? "…" : "");
        }

        private void DrawGauge(Graphics g, int x, int y, int w, int h, double pct, Color color)
        {
            using (GraphicsPath track = Rounded(new Rectangle(x, y, w, h), h / 2))
            using (var tb = new SolidBrush(ClTrack))
                g.FillPath(tb, track);
            double frac = pct / 100.0;
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;
            int fw = (int)Math.Round(w * frac);
            if (fw >= h)
                using (GraphicsPath fill = Rounded(new Rectangle(x, y, fw, h), h / 2))
                using (var fb = new SolidBrush(color))
                    g.FillPath(fb, fill);
        }

        private static Color GaugeColor(double pct)
        {
            if (pct >= 90) return ClRed;
            if (pct >= 70) return ClAmber;
            return ClGreen;
        }

        private static bool Blink(int tick) { return (tick % 4000) < 140; }

        internal static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius * 2);
            d = Math.Min(d, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Agent terminal", null, (s, e) => { if (ToggleAgentRequested != null) ToggleAgentRequested(); });
            menu.Items.Add("New terminal", null, (s, e) => { if (NewTerminalRequested != null) NewTerminalRequested(); });
            menu.Items.Add("New terminal in folder…", null, (s, e) => { if (ChooseFolderRequested != null) ChooseFolderRequested(); });
            menu.Items.Add("Sign in", null, (s, e) => SignIn());
            menu.Items.Add("Refresh now", null, (s, e) => RefreshNow());
            menu.Items.Add("Settings…", null, (s, e) => ShowSettings());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Hide", null, (s, e) => Visible = false);
            menu.Items.Add("Exit", null, (s, e) => { if (ExitRequested != null) ExitRequested(); else Application.Exit(); });
            ContextMenuStrip = menu;
        }

        private void DisposeFonts()
        {
            if (_fTitle != null) _fTitle.Dispose();
            if (_fBig != null) _fBig.Dispose();
            if (_fText != null) _fText.Dispose();
            if (_fSmall != null) _fSmall.Dispose();
            if (_fTV != null) _fTV.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _poll.Dispose();
                _anim.Dispose();
                _sessionTimer.Dispose();
                _monitor.Dispose();
                DisposeFonts();
            }
            base.Dispose(disposing);
        }
    }

    // One row in the widget's terminal-list TV.
    internal sealed class TermRow
    {
        public string Name;
        public string State;
        public string Activity;
        public double Idle;
        public bool Running;
        public bool Active;
    }

    // The "modern Clippy" mascot: a coral paperclip with googly, blinking eyes.
    internal static class ClawdArt
    {
        public static void Draw(Graphics g, RectangleF box, Color color, bool blink, bool worried)
        {
            float w = box.Width, h = box.Height;
            float pw = Math.Max(2f, w * 0.13f);
            using (var pen = new Pen(color, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                using (GraphicsPath po = Stadium(new RectangleF(box.X + w * 0.30f, box.Y + h * 0.08f, w * 0.40f, h * 0.84f))) g.DrawPath(pen, po);
                using (GraphicsPath pi = Stadium(new RectangleF(box.X + w * 0.42f, box.Y + h * 0.26f, w * 0.16f, h * 0.50f))) g.DrawPath(pen, pi);
            }
            float eyeR = w * 0.115f;
            float ey = box.Y + h * 0.34f;
            DrawEye(g, box.X + w * 0.40f, ey, eyeR, blink, worried);
            DrawEye(g, box.X + w * 0.60f, ey, eyeR, blink, worried);
        }

        private static void DrawEye(Graphics g, float cx, float cy, float r, bool blink, bool worried)
        {
            if (blink)
            {
                using (var pen = new Pen(Color.FromArgb(30, 30, 40), Math.Max(1.5f, r * 0.35f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, cx - r * 0.7f, cy, cx + r * 0.7f, cy);
                return;
            }
            using (var white = new SolidBrush(Color.White)) g.FillEllipse(white, cx - r, cy - r, r * 2, r * 2);
            float pr = r * 0.5f;
            float dx = (float)Math.Sin(Environment.TickCount / 900.0) * r * 0.3f;
            float dy = worried ? -r * 0.15f : (float)Math.Cos(Environment.TickCount / 1100.0) * r * 0.2f;
            using (var pupil = new SolidBrush(Color.FromArgb(30, 30, 40))) g.FillEllipse(pupil, cx + dx - pr, cy + dy - pr, pr * 2, pr * 2);
        }

        private static GraphicsPath Stadium(RectangleF r)
        {
            float d = Math.Min(r.Width, r.Height);
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
