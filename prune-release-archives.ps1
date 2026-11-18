[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Local')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Local')]
    [string]$ArchiveDirectory,
    [Parameter(Mandatory, ParameterSetName = 'GitHub')]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$GitHubRepository,
    [ValidateRange(1, 100)][int]$Keep = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSCmdlet.ParameterSetName -eq 'GitHub') {
    # 仅处理正式发布的二进制附件；保留发布说明、Git 标签和自动生成的源码下载。
    $json = & gh api --paginate --slurp "repos/$GitHubRepository/releases"
    if ($LASTEXITCODE -ne 0) { throw '读取 GitHub Release 列表失败，未清理附件。' }
    $pages = $json | ConvertFrom-Json -NoEnumerate
    $releases = @($pages | ForEach-Object { $_ } | Where-Object { -not $_.draft -and -not $_.prerelease } |
        Sort-Object { [DateTimeOffset]$_.published_at } -Descending)
    foreach ($release in ($releases | Select-Object -Skip $Keep)) {
        foreach ($asset in $release.assets) {
            if ($asset.name -notmatch '^(?:CodexWeeklyQuotaMonitor|Codex周限额监控).*\.(?:zip|exe|msi)$') { continue }
            if ($PSCmdlet.ShouldProcess("$GitHubRepository/$($release.tag_name)/$($asset.name)", '删除旧发布附件')) {
                & gh api --method DELETE "repos/$GitHubRepository/releases/assets/$($asset.id)"
                if ($LASTEXITCODE -ne 0) { throw "删除附件失败：$($asset.name)" }
                Write-Output "已删除发布附件：$($release.tag_name)/$($asset.name)"
            }
        }
    }
    return
}

# 按版本保留整组目录和 ZIP，不把同一版本的两种产物算成两次发布。
$root = (Resolve-Path -LiteralPath $ArchiveDirectory).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
$candidates = @(Get-ChildItem -LiteralPath $root -Force | ForEach-Object {
    if ($_.Name -match '^(?:Codex周限额监控|CodexWeeklyQuotaMonitor)-v(?<version>\d+\.\d+\.\d+)(?:-win-x64)?(?<zip>\.zip)?$' -and
        ($_.PSIsContainer -or $Matches.zip -eq '.zip')) {
        [pscustomobject]@{ Item = $_; Version = [version]$Matches.version }
    }
})
$keepVersions = @($candidates.Version | Sort-Object -Unique -Descending | Select-Object -First $Keep)
$obsolete = @($candidates | Where-Object { $_.Version -notin $keepVersions })

# 全部目标先验证再删除；拒绝符号链接、运行数据和越界路径，不把安装目录当成归档。
foreach ($candidate in $obsolete) {
    $item = $candidate.Item
    $resolved = (Resolve-Path -LiteralPath $item.FullName).Path
    if ([IO.Path]::GetDirectoryName($resolved) -ne $root -or $item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "归档目标越界或为链接，未清理：$resolved"
    }
    if ($item.PSIsContainer) {
        $contents = @(Get-ChildItem -LiteralPath $resolved -Force -Recurse)
        if ($contents | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint -or
            $_.Name -in @('settings.json', 'state.json', 'speed.json', 'telemetry.sqlite', '.git') }) {
            throw "归档含运行数据、源码或链接，未清理：$resolved"
        }
    }
}
foreach ($candidate in $obsolete) {
    $resolved = (Resolve-Path -LiteralPath $candidate.Item.FullName).Path
    if ($PSCmdlet.ShouldProcess($resolved, '删除旧发布归档')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
        Write-Output "已删除本地归档：$resolved"
    }
}
