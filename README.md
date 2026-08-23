# AI Usage Monitor

AI Usage Monitor is a compact, always-on-top Windows desktop widget for monitoring subscription usage across OpenAI Codex, Claude Code, Google Antigravity, and Cursor. It displays used quota, reset times, and provider state without requiring a browser dashboard.

![AI Usage Monitor compact vertical widget](docs/usage-widget.png)

## Download

Download the newest automatic Windows x64 build from [GitHub Releases](https://github.com/ansonliam/AIUsageMonitor/releases/latest), or download the executable directly:

- [AIUsageMonitor.exe](https://github.com/ansonliam/AIUsageMonitor/releases/latest/download/AIUsageMonitor.exe)
- [SHA-256 checksum](https://github.com/ansonliam/AIUsageMonitor/releases/latest/download/AIUsageMonitor.exe.sha256)

The release is a self-contained single-file application and does not require a separate .NET installation. It is currently unsigned, so Windows SmartScreen may display an **Unknown publisher** warning.

## Features

### Usage

- **Codex** — weekly usage
- **Claude Code** — current five-hour session and weekly usage
- **Antigravity** — quota windows reported by the current desktop backend, shown as short "G" (Gemini Models) and "C" (Claude and GPT models) labels with the full group name on hover
- **Cursor** — Cursor Models and Other Models remaining percentage and shared billing/reset date, read from Cursor's own local session token (no separate API key)
- Each card is identified by a compact provider icon, with the full provider name available on hover
- Used-percentage progress bars with configurable five-stage colours and cutoffs
- Compact reset-time labels, with a short "Reset in …, on …" summary and the exact reset date and time in tooltips
- The displayed percentages are **used percentages**: a larger value and longer bar mean more of the quota has been consumed

### Refresh

- Manual refresh plus optional scheduled and hook-triggered refresh
- Optional Codex, Claude Code, Antigravity, and Cursor Stop hooks for automatic refresh after a session
- Scheduled refresh disabled by default, with an independent interval per provider, defaulted from each provider's own observed rate-limit behavior (Codex 15 min, Claude 20 min, Antigravity 20 min, Cursor 5 min)
- Independent non-manual refresh throttle per provider (Codex 3 min, Claude 15 min, Antigravity 10 min, Cursor 5 min) to reduce rate-limit errors; set a provider to 0 for immediate hook refreshes
- A hook that arrives inside the throttle window is not dropped: one follow-up refresh is scheduled for the moment the throttle clears, and the provider's scheduled-poll countdown restarts from that refresh rather than firing again shortly after
- Hidden provider cards are never polled or hook-refreshed; showing a card again triggers an immediate catch-up refresh
- Cached last-known usage and update times across restarts

### Window and layout

- Borderless, translucent, always-on-top widget, with "always on top" itself toggleable from Settings or the widget's right-click menu
- Vertical or side-by-side provider layout with a 2 px horizontal gap, resizable down to a compact 160 px minimum width
- Independently show or hide Codex, Claude, Antigravity, and Cursor
- Five text-size presets from Compact to Extra Large
- Developer-only 512 × 512 icon screenshot preview with large text and reset labels hidden (`Ctrl+Alt+D` in Settings)
- Tray-only operation without a taskbar button
- Movable and resizable window with a shared lock setting
- Remembered position, size, opacity, layout, visibility, typography, and usage stages
- System-tray and widget context menus for Settings, Refresh All, and window controls
- Single-instance handling for hook notifications

## Requirements

To run the published application:

- Windows 10 or Windows 11, x64
- OpenAI Codex installed and signed in for Codex usage
- Claude Code installed and signed in for Claude usage
- Google Antigravity 2.0 desktop installed, signed in, and running for Antigravity usage
- Cursor installed and signed in for Cursor usage
- Internet access to the relevant provider services

To build from source:

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio 2022 or later with the .NET desktop development workload, or the `dotnet` CLI

## Build and run

Clone the repository and build the solution:

```powershell
git clone https://github.com/ansonliam/AIUsageMonitor.git
cd AIUsageMonitor
dotnet build .\AIUsageMonitor.slnx -c Release
```

Run from source:

```powershell
dotnet run --project .\src\AIUsageMonitor\AIUsageMonitor.csproj -c Release
```

Create a self-contained, single-file Windows build:

```powershell
dotnet publish .\src\AIUsageMonitor\AIUsageMonitor.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -o .\dist
```

## Configuration and setup

1. Install and sign in to any providers you want to monitor. Keep Antigravity running while its quota is read.
2. Start AI Usage Monitor.
3. When prompted, choose whether to install the missing automatic-refresh hooks.
4. Right-click the widget or tray icon to open **Settings**.
5. Enable **Scheduled refresh** if wanted and choose a separate interval for each provider.
6. Use Settings to install, repair, uninstall, test, or inspect each provider hook.
7. Choose the provider layout, visible cards, text size, colours, cutoff percentages, opacity, always-on-top, and window lock state.

Hook installation and removal preserve unrelated provider settings and hooks. New handlers carry the unique owner marker `com.ansonliam.ai-usage-monitor`; executable-specific legacy handlers are migrated during repair.

Hooks contain the absolute path of the executable. After moving or renaming a portable copy, use **Settings → Install / Repair Hook** for both providers. Otherwise, the provider may continue invoking an older path.

## Local data and privacy

AI Usage Monitor does not place provider credentials in this repository or its own settings file.

- Widget settings: `%LOCALAPPDATA%\AIUsageMonitor\window-placement.json`
- Last-known usage cache: `%LOCALAPPDATA%\AIUsageMonitor\usage-cache.json`
- Codex hook: `%USERPROFILE%\.codex\hooks.json`
- Claude hook: `%USERPROFILE%\.claude\settings.json`, or the directory selected by `CLAUDE_CONFIG_DIR`
- Antigravity hook: `%USERPROFILE%\.gemini\config\hooks.json`
- Cursor hook: `%USERPROFILE%\.cursor\hooks.json`

Codex authentication is accessed through the locally installed Codex app-server. Claude authentication uses the existing Claude Code credential cache; when required, the application follows the Claude OAuth refresh flow and updates the relevant credential fields in that existing cache. Antigravity usage is requested from the signed-in desktop application's loopback-only local language server. Cursor authentication reads the session token Cursor's own desktop app already stores locally and calls Cursor's usage-summary endpoint with it. AI Usage Monitor does not request an API key for any provider, and does not copy Antigravity's or Cursor's session credentials into its own settings or usage cache.

Do not commit runtime caches, provider configuration, credential files, or locally published binaries. The repository `.gitignore` excludes these files and directories.

### Automatic commit safety check

Install the repository-owned pre-commit hook once after cloning:

```powershell
.\scripts\Install-GitHooks.ps1
```

Every local commit will then scan the exact staged files for credential patterns, private or machine-specific data, sensitive filenames, generated artifacts, and unapproved binary files. Findings report only the issue type, file, and line number so a detected secret is not printed to the terminal.

Run the full repository check manually at any time:

```powershell
.\scripts\Invoke-PrivacyCheck.ps1 -Scope Repository
```

Approved screenshots and icons are content-hash allowlisted in `scripts/privacy-allowlist.json`. Any changed or newly added binary asset is blocked until it has been reviewed and its Git blob hash deliberately added to that file. GitHub Actions runs the same repository scan on every push and pull request.

## Refresh behavior

- **Manual refresh** always requests fresh data, bypassing the throttle.
- **Scheduled refresh** is disabled by default.
- **Scheduled refresh intervals** are configured separately for Codex, Claude, Antigravity, and Cursor, from 5 to 1440 minutes.
- **Installed hooks** request a provider refresh independently of the scheduled-refresh setting.
- **Hook and scheduled refreshes** are limited by a per-provider minimum interval (Codex 3 min, Claude 15 min, Antigravity 10 min, Cursor 5 min), chosen from each provider's own observed rate-limit behavior rather than a single shared value. Set a provider's throttle to **0** to remove that interval so its hook refreshes run immediately.
- A hook that lands inside that minimum interval is not simply dropped: exactly one follow-up refresh is scheduled for when the interval clears, and the provider's scheduled-poll countdown restarts from that refresh so it isn't immediately polled again.
- Hidden provider cards (unchecked in **Visible**) are skipped by scheduled and hook refreshes entirely; making a card visible again triggers one immediate refresh.
- Claude `429 Too Many Requests` responses respect `Retry-After` when present and otherwise use capped exponential backoff.

## Screenshots

The screenshot above shows the compact vertical layout. The same provider cards can be arranged side by side, resized, recoloured, or displayed individually.

## Known limitations

- Windows-only WPF application; no macOS or Linux build is currently provided.
- Provider APIs, local credential formats, and app-server contracts are not guaranteed public interfaces and may change.
- Antigravity currently uses the local service exposed by the Antigravity 2.0 desktop application. A standalone CLI-only installation is not sufficient, and the desktop application must be running. The language server is located by inspecting the running process; when it binds an OS-assigned (dynamic) port, the port is discovered from the process's loopback listeners rather than the command line.
- Antigravity quota buckets that expose only an absolute remaining amount without a total are omitted because a reliable used percentage cannot be calculated.
- The Antigravity integration has been verified end-to-end against a signed-in Antigravity desktop backend, including dynamic language-server port discovery. Provider APIs may still change between desktop releases.
- Hook paths are absolute, so portable copies require hook repair after being moved or renamed.
- Cached values may be shown until the next successful refresh.
- Hook-triggered refresh uses the configured throttle and is not intended as second-by-second monitoring unless that provider's throttle is explicitly set to 0.
- Automatic release executables are not code-signed and may trigger a Windows SmartScreen warning.
- CSV history, alerts, and usage notifications are not currently implemented.
- Automated tests cover Antigravity quota parsing and local-service discovery (including the dynamic `--https_server_port 0` case), notification parsing, refresh intervals, cached-snapshot compatibility, and the existing remaining-to-used conversion. The Antigravity path has additionally been verified live against a signed-in desktop backend; authenticated Codex and Claude calls still require runtime verification with each provider installed.

## License

AI Usage Monitor is available under the [MIT License](LICENSE).
