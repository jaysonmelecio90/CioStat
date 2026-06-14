using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace CioStats
{
    // Live status of the embedded Claude Code agent's current session.
    public sealed class AgentSession
    {
        public bool HasSession;
        public string State = "Off";     // Working | Idle | Ready | Off
        public string Activity = "";
        public DateTime LastWriteUtc;
        public double IdleSeconds;
        public List<string> Recent = new List<string>();
    }

    public sealed class ProjectInfo
    {
        public string Cwd;
        public DateTime LastWriteUtc;
    }

    // Reads Claude Code's per-session JSONL transcripts under ~/.claude/projects/<encoded-cwd>/.
    internal static class ClaudeSessionWatcher
    {
        private static readonly string ProjectsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

        public static AgentSession Read(string cwd, bool running)
        {
            var s = new AgentSession();
            try
            {
                if (string.IsNullOrEmpty(cwd)) cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string dir = Path.Combine(ProjectsDir, Encode(cwd));
                FileInfo newest = null;
                if (Directory.Exists(dir))
                    foreach (FileInfo f in new DirectoryInfo(dir).GetFiles("*.jsonl"))
                        if (newest == null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = f;

                if (newest == null) { s.State = running ? "Ready" : "Off"; return s; }

                s.HasSession = true;
                s.LastWriteUtc = newest.LastWriteTimeUtc;
                s.IdleSeconds = (DateTime.UtcNow - newest.LastWriteTimeUtc).TotalSeconds;
                s.Activity = ReadRecent(newest.FullName, s.Recent);

                if (!running) s.State = "Off";
                else if (s.IdleSeconds < 12) s.State = "Working";
                else if (s.IdleSeconds < 300) s.State = "Idle";
                else s.State = "Ready";
            }
            catch { s.State = running ? "Ready" : "Off"; }
            return s;
        }

        public static List<ProjectInfo> ListProjects()
        {
            var list = new List<ProjectInfo>();
            try
            {
                if (!Directory.Exists(ProjectsDir)) return list;
                foreach (string dir in Directory.GetDirectories(ProjectsDir))
                {
                    var di = new DirectoryInfo(dir);
                    FileInfo newest = null;
                    foreach (FileInfo f in di.GetFiles("*.jsonl"))
                        if (newest == null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = f;
                    if (newest == null) continue;
                    string cwd = ReadCwd(newest.FullName);
                    if (string.IsNullOrEmpty(cwd)) continue;
                    list.Add(new ProjectInfo { Cwd = cwd, LastWriteUtc = newest.LastWriteTimeUtc });
                }
                list.Sort((a, b) => b.LastWriteUtc.CompareTo(a.LastWriteUtc));
            }
            catch { }
            return list;
        }

        private static string ReadCwd(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                    for (int i = 0; i < 6; i++)
                    {
                        string line = sr.ReadLine();
                        if (line == null) break;
                        if (line.IndexOf("\"cwd\"", StringComparison.Ordinal) < 0) continue;
                        try { string c = (string)JObject.Parse(line)["cwd"]; if (!string.IsNullOrEmpty(c)) return c; } catch { }
                    }
            }
            catch { }
            return null;
        }

        private static string Encode(string cwd)
        {
            var sb = new StringBuilder(cwd.Length);
            foreach (char c in cwd) sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            return sb.ToString();
        }

        private static string ReadRecent(string path, List<string> outRecent)
        {
            string tail;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, fs.Length - 32768);
                    fs.Seek(start, SeekOrigin.Begin);
                    using (var sr = new StreamReader(fs)) tail = sr.ReadToEnd();
                }
            }
            catch { return ""; }

            var all = new List<string>();
            foreach (string raw in tail.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length < 2 || line[0] != '{') continue;
                try { string a = Summarize(JObject.Parse(line)); if (a != null) all.Add(a); }
                catch { }
            }
            int take = Math.Min(8, all.Count);
            for (int i = all.Count - take; i < all.Count; i++) outRecent.Add(all[i]);
            return all.Count > 0 ? all[all.Count - 1] : "";
        }

        private static string Summarize(JObject o)
        {
            string type = (string)o["type"];
            JToken msg = o["message"];
            if (type == "assistant" && msg != null)
            {
                JArray content = msg["content"] as JArray;
                string last = null;
                if (content != null)
                    foreach (JToken b in content)
                    {
                        string bt = (string)b["type"];
                        if (bt == "tool_use") last = "tool: " + ((string)b["name"] ?? "?");
                        else if (bt == "text") { string t = (string)b["text"]; if (!string.IsNullOrWhiteSpace(t)) last = "Claude: " + Clip(t); }
                    }
                return last;
            }
            if (type == "user" && msg != null)
            {
                JToken c = msg["content"];
                if (c != null && c.Type == JTokenType.String) return "you: " + Clip((string)c);
                JArray arr = c as JArray;
                if (arr != null)
                    foreach (JToken b in arr)
                    {
                        string bt = (string)b["type"];
                        if (bt == "text") return "you: " + Clip((string)b["text"]);
                        if (bt == "tool_result") return "tool result";
                    }
            }
            return null;
        }

        private static string Clip(string t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            t = t.Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length > 46 ? t.Substring(0, 46) + "…" : t;
        }
    }
}
