using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;

namespace CioStats
{
    // Hosts a hidden WebView2 logged into claude.ai and captures the usage JSON the
    // settings/usage page fetches for itself. No cookie harvesting, no guessed endpoint.
    public sealed class ClaudeUsageMonitor
    {
        private const string UsageUrl = "https://claude.ai/settings/usage";
        private const string LoginUrl = "https://claude.ai/login";
        private static readonly Point OffScreen = new Point(-32000, -32000);
        private static readonly string DataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClawdMeter");

        private readonly Form _host;
        private readonly WebView2 _web;
        private readonly object _logLock = new object();
        private readonly Dictionary<string, string> _api = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _ready;
        private bool _busy;
        private bool _hideOnClose = true;
        private TaskCompletionSource<bool> _navDone;
        private volatile string _capturedUsageJson;

        public event Action<UsageSnapshot> Updated;
        public event Action SignedIn;
        public bool IsReady { get { return _ready; } }

        public ClaudeUsageMonitor()
        {
            Directory.CreateDirectory(DataDir);
            _host = new Form
            {
                Text = "CioStat — sign in to claude.ai",
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.Manual,
                Location = OffScreen,
                Size = new Size(520, 680),
                ShowInTaskbar = false,
                MinimizeBox = false,
                MaximizeBox = false,
                Icon = AppIcons.Get()
            };
            _host.FormClosing += (s, e) => { if (_hideOnClose) { e.Cancel = true; HideLogin(); } };
            _web = new WebView2 { Dock = DockStyle.Fill };
            _host.Controls.Add(_web);
        }

        public async Task InitAsync()
        {
            _host.Show();
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"), null);
            await _web.EnsureCoreWebView2Async(env);
            CoreWebView2 core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.WebResourceResponseReceived += OnResponse;
            core.NavigationCompleted += OnNavCompleted;
            _ready = true;
            _host.Hide();
        }

        public async Task PollAsync()
        {
            if (!_ready || _busy) return;
            _busy = true;
            try
            {
                _capturedUsageJson = null;
                lock (_logLock) _api.Clear();
                await NavigateAsync(UsageUrl);

                for (int i = 0; i < 24 && _capturedUsageJson == null; i++)   // give the SPA time for its own XHRs
                    await Task.Delay(500);

                string innertext = await GetInnerText();

                string src = SafeSource();
                var snap = new UsageSnapshot { FetchedAtUtc = DateTime.UtcNow, SignedIn = IsSignedIn(src) };
                if (!snap.SignedIn)
                {
                    snap.Note = "Not signed in";
                }
                else
                {
                    snap.Plan = ExtractPlan(innertext);
                    snap.SubscriberName = ExtractName();
                    if (_capturedUsageJson != null)
                    {
                        try { File.WriteAllText(Path.Combine(DataDir, "last_usage.json"), _capturedUsageJson); } catch { }
                        ParseInto(snap, _capturedUsageJson);
                    }
                    else snap.Note = "Signed in — no usage data captured";
                }
                Updated?.Invoke(snap);
            }
            catch (Exception ex)
            {
                Updated?.Invoke(new UsageSnapshot { FetchedAtUtc = DateTime.UtcNow, SignedIn = false, Note = ex.Message });
            }
            finally { _busy = false; }
        }

        private async Task<string> GetInnerText()
        {
            try { return await _web.CoreWebView2.ExecuteScriptAsync("(document.body&&document.body.innerText)||''"); }
            catch { return null; }
        }

        private static string ExtractPlan(string innertextJson)
        {
            if (string.IsNullOrEmpty(innertextJson)) return null;
            try
            {
                string s = (string)JToken.Parse(innertextJson);
                string[] lines = s.Split('\n');
                for (int i = 0; i < lines.Length - 1; i++)
                    if (lines[i].Trim().StartsWith("Plan usage limits"))
                        for (int j = i + 1; j < lines.Length; j++)
                            if (lines[j].Trim().Length > 0) return lines[j].Trim();
            }
            catch { }
            return null;
        }

        private string ExtractName()
        {
            try
            {
                lock (_logLock)
                    foreach (string body in _api.Values)
                    {
                        Match m = Regex.Match(body, "\"full_name\"\\s*:\\s*\"([^\"]+)\"");
                        if (m.Success && m.Groups[1].Value.Trim().Length > 0) return m.Groups[1].Value;
                    }
            }
            catch { }
            return null;
        }

        public void ShowLogin()
        {
            if (!_ready) return;
            _hideOnClose = true;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            _host.Location = new Point(wa.X + (wa.Width - _host.Width) / 2, wa.Y + (wa.Height - _host.Height) / 2);
            try { _web.CoreWebView2.Navigate(LoginUrl); } catch { }
            _host.Show();
            _host.BringToFront();
            _host.Activate();
        }

        public async Task SignOutAsync()
        {
            try { await _web.CoreWebView2.Profile.ClearBrowsingDataAsync(); } catch { }
        }

        public void Dispose()
        {
            _hideOnClose = false;
            try { _web.Dispose(); } catch { }
            try { _host.Dispose(); } catch { }
        }

        private void HideLogin()
        {
            _host.Hide();
            _host.Location = OffScreen;
        }

        private Task NavigateAsync(string url)
        {
            _navDone = new TaskCompletionSource<bool>();
            try { _web.CoreWebView2.Navigate(url); } catch { _navDone.TrySetResult(false); }
            return Task.WhenAny(_navDone.Task, Task.Delay(20000));
        }

        private void OnNavCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            TaskCompletionSource<bool> t = _navDone;
            if (t != null && !t.Task.IsCompleted) t.TrySetResult(e.IsSuccess);
            if (_host.Visible && IsSignedIn(SafeSource()))
            {
                HideLogin();
                SignedIn?.Invoke();
            }
        }

        private async void OnResponse(object sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                string uri = e.Request.Uri;
                if (uri.IndexOf("/api/", StringComparison.OrdinalIgnoreCase) < 0) return;
                if (e.Response.StatusCode < 200 || e.Response.StatusCode >= 300) return;
                System.IO.Stream content;
                try { content = await e.Response.GetContentAsync(); } catch { return; }
                if (content == null) return;
                string body;
                using (var sr = new StreamReader(content)) body = sr.ReadToEnd();
                if (string.IsNullOrWhiteSpace(body)) return;
                char c = body.TrimStart()[0];
                if (c != '{' && c != '[') return;
                lock (_logLock) _api[uri] = body.Length > 20000 ? body.Substring(0, 20000) : body;
                if (StrictUsage(uri, body)) _capturedUsageJson = body;
            }
            catch { }
        }

        private string SafeSource()
        {
            try { return _web.CoreWebView2.Source ?? string.Empty; } catch { return string.Empty; }
        }

        private static bool IsSignedIn(string src)
        {
            return src.IndexOf("claude.ai", StringComparison.OrdinalIgnoreCase) >= 0
                && src.IndexOf("/login", StringComparison.OrdinalIgnoreCase) < 0
                && src.IndexOf("/magic-link", StringComparison.OrdinalIgnoreCase) < 0
                && src.IndexOf("/auth", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool StrictUsage(string url, string json)
        {
            if (url.IndexOf("usage", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            bool resets = json.IndexOf("resets_at", StringComparison.OrdinalIgnoreCase) >= 0
                       || json.IndexOf("reset_at", StringComparison.OrdinalIgnoreCase) >= 0;
            bool util = json.IndexOf("utilization", StringComparison.OrdinalIgnoreCase) >= 0
                     || json.IndexOf("five_hour", StringComparison.OrdinalIgnoreCase) >= 0
                     || json.IndexOf("seven_day", StringComparison.OrdinalIgnoreCase) >= 0;
            return resets && util;
        }

        private static void ParseInto(UsageSnapshot snap, string json)
        {
            try
            {
                var found = new List<UsageBucket>();
                Walk(JToken.Parse(json), null, found);
                foreach (UsageBucket b in found)
                    if (b.Percent > 0 || b.ResetsAt.HasValue || (b.Used.HasValue && b.Limit.HasValue))
                        snap.Buckets.Add(b);
                snap.Buckets.Sort((a, b) => Priority(a.Label).CompareTo(Priority(b.Label)));
                if (snap.Buckets.Count == 0) snap.Note = "Signed in — usage shape unrecognized";
            }
            catch { snap.Note = "Signed in — usage parse error"; }
        }

        private static void Walk(JToken node, string name, List<UsageBucket> outp)
        {
            JObject obj = node as JObject;
            if (obj != null)
            {
                JToken reset = obj["resets_at"] ?? obj["reset_at"] ?? obj["resetsAt"] ?? obj["reset"];
                JToken util = obj["utilization"] ?? obj["percent_used"] ?? obj["percentUsed"] ?? obj["percent"];
                JToken used = obj["used"] ?? obj["used_tokens"] ?? obj["consumed"];
                JToken limit = obj["limit"] ?? obj["total"] ?? obj["cap"] ?? obj["allowance"];
                if (reset != null || util != null || (used != null && limit != null))
                {
                    var b = new UsageBucket { Label = Pretty(name ?? (string)obj["name"] ?? (string)obj["type"] ?? "Usage") };
                    if (reset != null)
                    {
                        // Newtonsoft auto-parses ISO timestamps into Date tokens, so handle both Date and raw String.
                        if (reset.Type == JTokenType.Date)
                            b.ResetsAt = ((DateTime)reset).ToUniversalTime();
                        else if (reset.Type == JTokenType.String)
                        {
                            DateTimeOffset dto;
                            if (DateTimeOffset.TryParse((string)reset, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
                                b.ResetsAt = dto.UtcDateTime;
                        }
                    }
                    b.Used = SafeLong(used);
                    b.Limit = SafeLong(limit);
                    if (util != null && (util.Type == JTokenType.Float || util.Type == JTokenType.Integer))
                    {
                        double u = (double)util;
                        b.Percent = u <= 1.0 ? u * 100.0 : u;
                    }
                    else if (b.Used.HasValue && b.Limit.HasValue && b.Limit.Value > 0)
                        b.Percent = (double)b.Used.Value / b.Limit.Value * 100.0;
                    outp.Add(b);
                }
                foreach (JProperty p in obj.Properties()) Walk(p.Value, p.Name, outp);
            }
            else
            {
                JArray arr = node as JArray;
                if (arr != null)
                {
                    int i = 0;
                    foreach (JToken it in arr) Walk(it, (name ?? "Item") + " " + (++i), outp);
                }
            }
        }

        private static long? SafeLong(JToken t)
        {
            if (t == null) return null;
            if (t.Type == JTokenType.Integer) return (long)t;
            if (t.Type == JTokenType.Float) return (long)(double)t;
            return null;
        }

        private static int Priority(string label)
        {
            string k = (label ?? string.Empty).ToLowerInvariant();
            if (k.Contains("session") || k.Contains("5")) return 0;
            if (k.Contains("opus")) return 2;
            if (k.Contains("week")) return 1;
            return 5;
        }

        private static string Pretty(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Usage";
            string k = s.ToLowerInvariant();
            if (k.Contains("five_hour") || k.Contains("5_hour") || k == "session" || k.Contains("session")) return "Session (5h)";
            if (k.Contains("opus")) return "Weekly (Opus)";
            if (k.Contains("sonnet")) return "Weekly (Sonnet)";
            if (k.Contains("seven_day") || k.Contains("7_day") || k.Contains("week")) return "Weekly";
            string[] parts = s.Replace('_', ' ').Replace('-', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts.Select(p => char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p.Substring(1) : string.Empty)));
        }
    }
}
