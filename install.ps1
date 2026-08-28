param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'CodexWeeklyQuotaMonitor'),
    [string]$CodexExecutable = '',
    [switch]$NoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceExecutable = Join-Path $sourceDirectory 'Codex周限额监控.exe'
$targetExecutable = Join-Path $InstallDirectory 'Codex周限额监控.exe'
$stagedExecutable = Join-Path $InstallDirectory 'Codex周限额监控.installing.exe'
$codexPathFile = Join-Path $sourceDirectory 'codex-path.txt'
$desktopPackageLocator = 'codex-desktop://current'

if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
    throw "安装包中缺少 Codex周限额监控.exe：$sourceExecutable"
}

if ([string]::IsNullOrWhiteSpace($CodexExecutable)) {
    if (-not (Test-Path -LiteralPath $codexPathFile -PathType Leaf)) {
        throw '安装包缺少 codex-path.txt；请通过 -CodexExecutable 传入当前 codex.exe 绝对路径。'
    }
    $CodexExecutable = (Get-Content -Raw -LiteralPath $codexPathFile).Trim()
}

if (-not [string]::Equals(
        $CodexExecutable,
        $desktopPackageLocator,
        [System.StringComparison]::OrdinalIgnoreCase) -and
    -not (Test-Path -LiteralPath $CodexExecutable -PathType Leaf)) {
    throw "Codex 可执行文件不存在：$CodexExecutable"
}

New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
Copy-Item -LiteralPath $sourceExecutable -Destination $stagedExecutable -Force

$targetProcessName = [System.IO.Path]::GetFileNameWithoutExtension($targetExecutable)
$targetFullPath = [System.IO.Path]::GetFullPath($targetExecutable)
$installedProcesses = @(Get-Process -Name $targetProcessName -ErrorAction SilentlyContinue | Where-Object {
    [string]::Equals($_.Path, $targetFullPath, [System.StringComparison]::OrdinalIgnoreCase)
})
if ($installedProcesses.Count -gt 0) {
    $installedProcessIds = ($installedProcesses.Id | Sort-Object) -join ', '
    Write-Host "检测到正在运行的旧版本（PID：$installedProcessIds），正在停止以完成升级……"
    Stop-Process -InputObject $installedProcesses -Force

    $exitDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $remainingProcesses = @(Get-Process -Name $targetProcessName -ErrorAction SilentlyContinue | Where-Object {
            [string]::Equals($_.Path, $targetFullPath, [System.StringComparison]::OrdinalIgnoreCase)
        })
    } while ($remainingProcesses.Count -gt 0 -and [DateTime]::UtcNow -lt $exitDeadline)

    if ($remainingProcesses.Count -gt 0) {
        $remainingProcessIds = ($remainingProcesses.Id | Sort-Object) -join ', '
        throw "旧版本未能在 15 秒内退出（PID：$remainingProcessIds）；未覆盖现有程序。"
    }
}

Move-Item -LiteralPath $stagedExecutable -Destination $targetExecutable -Force

foreach ($documentationFile in @('README.md', 'README.en.md', 'LICENSE')) {
    Copy-Item `
        -LiteralPath (Join-Path $sourceDirectory $documentationFile) `
        -Destination (Join-Path $InstallDirectory $documentationFile) `
        -Force
}
$resolvedInstallDirectory = [System.IO.Path]::GetFullPath($InstallDirectory)
$installedDocumentation = Join-Path $resolvedInstallDirectory 'docs'
if (Test-Path -LiteralPath $installedDocumentation) {
    $resolvedInstalledDocumentation = (Resolve-Path -LiteralPath $installedDocumentation).Path
    if (-not $resolvedInstalledDocumentation.StartsWith(
            $resolvedInstallDirectory + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "安装文档目录越过安装目录：$resolvedInstalledDocumentation"
    }

    Remove-Item -LiteralPath $resolvedInstalledDocumentation -Recurse -Force
}
Copy-Item `
    -LiteralPath (Join-Path $sourceDirectory 'docs') `
    -Destination $installedDocumentation `
    -Recurse `
    -Force

$settingsFile = Join-Path $InstallDirectory 'settings.json'
if (Test-Path -LiteralPath $settingsFile -PathType Leaf) {
    $settings = Get-Content -Raw -LiteralPath $settingsFile | ConvertFrom-Json
    $settings.CodexExecutable = $CodexExecutable
}
else {
    $settings = [ordered]@{
        SettingsSchemaVersion = 4
        CodexExecutable = $CodexExecutable
        CodexArguments = 'app-server --listen stdio://'
        SessionRoot = (Join-Path $env:USERPROFILE '.codex\sessions')
        PollIntervalSeconds = 60
        PreferredLimitId = ''
        MinimumWindowMinutes = 1440
        MinimumPercentDelta = 0.1
        InitialContextLookbackHours = 24
        StartWithWindows = $true
        ChartHistoryDays = 90
        EnableOfficialLongContextEstimate = $false
        Language = 0
        Theme = 0
        Regression = [ordered]@{
            Mode = 2
            LinearLookbackPoints = 120
            SegmentWindowHours = 24
            GaussianBandwidthHours = 12
            MaximumSampleUsd = 10000
        }
    }
}
$settings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $settingsFile -Encoding utf8

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Set-ItemProperty -LiteralPath $runKey -Name 'CodexWeeklyQuotaMonitor' -Value ('"{0}" --autostart' -f $targetExecutable)

Write-Host "安装完成：$targetExecutable"
Write-Host "Codex App Server：$CodexExecutable"
if ($NoStart) {
    Write-Host '已写入当前用户开机启动项；按 -NoStart 要求，本次未启动程序。'
}
else {
    Start-Process -FilePath $targetExecutable -WindowStyle Hidden
    Write-Host '程序已启动，并已写入当前用户开机启动项。'
}
