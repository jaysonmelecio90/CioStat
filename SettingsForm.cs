using System;
using System.Drawing;
using System.Windows.Forms;

namespace CioStats
{
    // Minimal config dialog: poll interval + sign in / sign out of claude.ai.
    public sealed class SettingsForm : Form
    {
        private readonly NumericUpDown _interval = new NumericUpDown();
        private readonly CheckBox _startup = new CheckBox();
        private readonly Action _signIn;
        private readonly Action _signOut;

        public SettingsForm(Action signIn, Action signOut)
        {
            _signIn = signIn;
            _signOut = signOut;
            Text = "CioStat — Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Icon = AppIcons.Get();
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(380, 250);
            AutoScaleMode = AutoScaleMode.Dpi;
            Build();
        }

        private void Build()
        {
            int x = 16, y = 16;

            Controls.Add(new Label { Text = "CioStat reads your claude.ai usage through a private", Location = new Point(x, y), AutoSize = true, ForeColor = Color.Gray });
            y += 18;
            Controls.Add(new Label { Text = "in-app browser. Sign in once; it stays signed in.", Location = new Point(x, y), AutoSize = true, ForeColor = Color.Gray });
            y += 32;

            Controls.Add(new Label { Text = "Poll interval (seconds)", Location = new Point(x, y + 3), AutoSize = true });
            _interval.SetBounds(x + 180, y, 90, 24);
            _interval.Minimum = 30; _interval.Maximum = 3600; _interval.Increment = 30;
            _interval.Value = Clamp(AppSettings.Default.PollIntervalSeconds, 30, 3600);
            Controls.Add(_interval);
            y += 36;

            _startup.Text = "Run automatically when I sign in to Windows";
            _startup.Location = new Point(x, y);
            _startup.AutoSize = true;
            _startup.Checked = StartupManager.IsEnabled();
            Controls.Add(_startup);
            y += 36;

            var bIn = new Button { Text = "Sign in to claude.ai", Location = new Point(x, y), Size = new Size(160, 30) };
            bIn.Click += (s, e) => { if (_signIn != null) _signIn(); };
            Controls.Add(bIn);
            var bOut = new Button { Text = "Sign out", Location = new Point(x + 170, y), Size = new Size(100, 30) };
            bOut.Click += (s, e) => { if (_signOut != null) _signOut(); };
            Controls.Add(bOut);
            y += 48;

            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Size = new Size(75, 28), Location = new Point(ClientSize.Width - 170, y) };
            ok.Click += (s, e) => AppSettings.Default.PollIntervalSeconds = (int)_interval.Value;
            ok.Click += (s, e) => StartupManager.SetEnabled(_startup.Checked);
            ok.Click += (s, e) => AppSettings.Default.Save();
            Controls.Add(ok);
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Size = new Size(75, 28), Location = new Point(ClientSize.Width - 90, y) };
            Controls.Add(close);

            AcceptButton = ok;
            CancelButton = close;
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
