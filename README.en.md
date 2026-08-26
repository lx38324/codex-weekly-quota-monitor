# Codex Weekly Quota Monitor

[简体中文](README.md) | English

An unofficial Windows tray utility that reads the signed-in Codex weekly usage percentage, attributes local rollout tokens, and estimates a **Standard API-equivalent weekly quota in USD**.

> The amount is a statistical estimate, not an OpenAI bill or official quota commitment. Other devices, cloud tasks, and missing logs affect accuracy.

## Quick install

1. Download and extract the latest `win-x64.zip` from [Releases](https://github.com/lx38324/codex-weekly-quota-monitor/releases).
2. Run in PowerShell 7:

```powershell
pwsh -ExecutionPolicy Bypass -File .\install.ps1
```

The utility installs to `%LOCALAPPDATA%\CodexWeeklyQuotaMonitor`, registers current-user startup, and starts immediately. Run the same command to upgrade safely; settings, state, and samples are preserved.

## Usage

- Hover the tray icon for estimates, usage, and sample count.
- Single-click performs no window action, avoiding conflicts with double-click.
- Double-click for history, regression curves, and per-sample token data.
- Right-click to refresh, open quick details/dashboard/settings/diagnostics, or exit.
- Use **Settings → Appearance** for automatic Windows language, Simplified Chinese, English, and system/light/dark themes.

The main window contains:

- **Dashboard** — estimates, used/remaining percentage, reset time, samples, and connection state.
- **History** — two pricing interpretations, time filters, curves, and token details.
- **Settings** — polling, data sources, regression, language, theme, and startup.
- **Diagnostics** — non-sensitive facts suitable for a GitHub issue.

## Two estimate interpretations

- **No long-context surcharge** uses public Standard API regular prices.
- **Official >272K surcharge** applies the published 2× input-class and 1.5× output rule when request input exceeds 272K.

Both apply recognized ChatGPT Fast credit multipliers. If an offline or legacy log omits its tier, recovery requires explicit `config.toml` evidence that predates the session and is labeled `(config)`; all other unknown models, tiers, or intervals still fail closed.

## Privacy

The utility stores settings, quota checkpoints, token aggregates, cursors, samples, and operational diagnostics. It does not store conversation text, tool-output text, credentials, or raw App Server stderr.

## Development

Requires Windows, PowerShell 7, and the .NET 9 SDK:

```powershell
dotnet build .\CodexWeeklyQuotaMonitor.sln -c Release
dotnet .\WeeklyQuotaMonitor.BusinessTests\bin\Release\net9.0-windows\WeeklyQuotaMonitor.BusinessTests.dll
```

See the [Chinese algorithm notes](docs/算法与数据口径.md), [installation guide](docs/安装升级与故障排查.md), [contribution guide](CONTRIBUTING.md), and [security policy](SECURITY.md).

MIT License.
