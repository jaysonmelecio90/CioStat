# CioStat

> A Windows system tray companion for **Claude Code** — monitor your Claude usage limits and manage multiple agent terminals from a single lightweight app.

![System tray icon](Screenshot%202026-06-14%20103920.png)

---

## What it does

CioStat sits quietly in your system tray and gives you:

- **Live usage meters** — Session (5h), All Models (weekly), and Sonnet (weekly) usage shown as progress bars with reset timers
- **Multi-terminal management** — spin up, switch between, and track multiple Claude Code terminals from one place
- **Agent status at a glance** — see which projects are active and their current state (Ready, Running, etc.)
- **Auto mode** — cycle through terminals with `Shift+Tab`
- **No API key required** — authenticates through your existing claude.ai browser session via an embedded WebView2

---

## Screenshots

### Usage panel + terminal manager

![Usage panel](Screenshot%202026-06-14%20103946.png)

![Usage panel expanded](Screenshot%202026-06-14%20104003.png)

### System tray context menu

![Context menu](Screenshot%202026-06-14%20103618.png)

---

## Features

- Session usage % with 5-hour reset countdown
- Weekly all-models usage % with 6-day reset countdown
- Weekly Sonnet usage % tracker
- Shows your Claude plan name and subscriber name
- Multiple named terminals with per-terminal status
- Right-click menu: Agent terminal, New terminal, New terminal in folder, Sign in, Refresh, Settings, Hide, Exit
- Starts with Windows (optional, via Settings)
- Zero token interception — reads directly from claude.ai's own usage page

---

## Requirements

- Windows 10 / 11
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (usually already installed)
- A claude.ai account

---

## Build from source

```bash
git clone https://github.com/jaysonmelecio90/CioStat.git
cd CioStat
```

Open `ClawdMeter.sln` in Visual Studio 2022, restore NuGet packages, and build in Release mode.

**Dependencies (NuGet):**
- `Microsoft.Web.WebView2`
- `Newtonsoft.Json`
- `Costura.Fody` (single-exe bundling)

---

## Usage

1. Build and run `ClawdMeter.exe` — it appears in the system tray
2. Right-click the tray icon → **Sign in** → log into claude.ai in the popup window
3. The window closes automatically once signed in
4. Right-click → **Refresh now** to pull the latest usage data
5. Right-click → **New terminal** to open a new Claude Code terminal
6. Left-click the tray icon to see the usage panel

Usage data is cached locally at `%LocalAppData%\ClawdMeter\last_usage.json`.

---

## How it works

CioStat embeds a hidden WebView2 browser and navigates to `claude.ai/settings/usage`. It intercepts the JSON responses that the page fetches for itself — no cookies are harvested and no undocumented endpoints are guessed. The intercepted payload is parsed for usage buckets (session, weekly, per-model) and reset timestamps, then displayed in the tray popup.

---

## License

MIT

---

*Built by [Jayson Melecio](https://github.com/jaysonmelecio90) — CD Engineering*
