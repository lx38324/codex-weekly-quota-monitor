# Codex Weekly Quota Monitor

[简体中文](README.md) | English

An unofficial Windows tray utility that reads the signed-in Codex subscription usage percentage from the local Codex App Server, attributes local rollout token usage, and estimates a weekly quota in **Standard API-equivalent USD**.

> The amount is a statistical estimate, not an OpenAI bill, subscription cash value, refund value, or official quota commitment. Usage from another device, cloud tasks, deleted logs, and unattributed activity can reduce accuracy.

## Highlights

- Event-assisted monitoring with low-frequency polling as the authoritative safety net.
- Combines local Codex Desktop, VS Code, and sidebar rollout files under the configured sessions root.
- Separately prices input, cached input, cache writes, output, and reasoning output.
- Applies observed ChatGPT Fast credit multipliers and keeps unknown service tiers unpriced.
- Shows both the no-long-context-surcharge estimate and the official `>272K` API surcharge estimate.
- Rebuilds the current weekly history after upgrades without discarding saved samples.
- Four-page dashboard: overview, history and charts, settings, and diagnostics.
- Interactive chart range filters, series visibility, hover values, and crosshairs.
- Automatic Windows display-language detection plus persistent manual Chinese/English selection.
- System, light, and dark themes; PerMonitorV2 high-DPI layouts.
- Safe in-place upgrades that preserve settings, state, history, and Windows startup registration.

## Requirements

- Windows 10 or Windows 11, x64.
- Codex Desktop, or another working `codex.exe` with the signed-in App Server session.
- PowerShell 7 for the provided installer.

The release executable is self-contained and does not require a separately installed .NET runtime.

## Install or upgrade

Download and extract the `CodexWeeklyQuotaMonitor-v1.3.0-win-x64.zip` release, then run:

```powershell
pwsh -ExecutionPolicy Bypass -File .\install.ps1
```

The installer stages the new executable, stops only the running process whose full path exactly matches the installation target, replaces it, restores the current-user startup entry, and starts the new version. Existing `settings.json`, `state.json`, and historical samples remain in place.

Default installation and data directory:

```text
%LOCALAPPDATA%\CodexWeeklyQuotaMonitor
```

## Language and theme

Open **Settings → Appearance**:

- **Automatic (Windows)** reads the current user's Windows UI language at startup. Chinese Windows uses Simplified Chinese; other languages use English.
- **简体中文** and **English** persist as explicit overrides.
- Switching back to **Automatic (Windows)** resumes Windows-language detection.
- Theme choices are **Use system setting**, **Light**, and **Dark**.

## How the estimate works

The monitor reads the authoritative weekly percentage through `account/rateLimits/read`. A percentage change triggers incremental attribution of local `token_count` rollout records. Each valid interval is converted to public Standard API-equivalent cost and extrapolated:

```text
estimated weekly quota USD = attributed interval cost USD × 100 / used-percent delta
```

The chart and regression engine expose both pricing interpretations. Unsupported models, unknown service tiers, malformed intervals, and activity without local logs fail closed and are shown as diagnostics instead of being guessed.

## Privacy

The application stores only settings, quota checkpoints, token aggregates, cursor metadata, estimates, and operational diagnostics. It does not store conversation text, tool-output text, App Server credentials, or raw App Server stderr.

## Build and test

Install the .NET 9 SDK, then run on Windows:

```powershell
dotnet build .\WeeklyQuotaMonitor.BusinessTests\WeeklyQuotaMonitor.BusinessTests.csproj -c Release
dotnet .\WeeklyQuotaMonitor.BusinessTests\bin\Release\net9.0-windows\WeeklyQuotaMonitor.BusinessTests.dll
pwsh -File .\build-release.ps1 -OutputDirectory .\publish
```

The business suite covers pricing, Fast tiers, long-context rules, resets, historical replay, rollout robustness, localization, theme selection, four-page navigation, and 100%/125%/150%/200% layout scaling.

## Support and security

- Use the Diagnostics page to copy non-sensitive facts for a bug report.
- Read [CONTRIBUTING.md](CONTRIBUTING.md) before submitting a pull request.
- Report vulnerabilities according to [SECURITY.md](SECURITY.md), not through a public issue.

Released under the [MIT License](LICENSE).
