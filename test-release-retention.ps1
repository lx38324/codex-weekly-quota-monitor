Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$prune = Join-Path $PSScriptRoot 'prune-release-archives.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('quota-retention-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
foreach ($version in @('1.9.9', '1.10.9', '1.10.10', '1.10.15', '1.10.16')) {
    New-Item -ItemType Directory -Path (Join-Path $root "Codex周限额监控-v$version") | Out-Null
    Set-Content -LiteralPath (Join-Path $root "CodexWeeklyQuotaMonitor-v$version-win-x64.zip") -Value 'test'
}
$unrelated = Join-Path $root 'qa-evidence.txt'
Set-Content -LiteralPath $unrelated -Value 'must remain'
& $prune -ArchiveDirectory $root -WhatIf
if (@(Get-ChildItem -LiteralPath $root).Count -ne 11) { throw 'WhatIf 修改了归档' }
& $prune -ArchiveDirectory $root
if (@(Get-ChildItem -LiteralPath $root).Count -ne 7 -or
    (Test-Path -LiteralPath (Join-Path $root 'Codex周限额监控-v1.10.9')) -or
    -not (Test-Path -LiteralPath (Join-Path $root 'Codex周限额监控-v1.10.10')) -or
    -not (Test-Path -LiteralPath $unrelated)) { throw '未按数值版本保留最近三次或误删其他文件' }
& $prune -ArchiveDirectory $root
if (@(Get-ChildItem -LiteralPath $root).Count -ne 7) { throw '重复清理改变保留结果' }
$guard = Join-Path $root 'Codex周限额监控-v1.0.0'
New-Item -ItemType Directory -Path $guard | Out-Null
Set-Content -LiteralPath (Join-Path $guard 'state.json') -Value 'history must remain'
$rejected = $false
try { & $prune -ArchiveDirectory $root } catch { $rejected = $_.Exception.Message -like '*归档含运行数据*' }
if (-not $rejected -or -not (Test-Path -LiteralPath (Join-Path $guard 'state.json'))) { throw '运行数据保护失效' }

# 模拟两页真实 API 数据，验证只删除最近三次之外的附件，不删除标签和 Release 本身。
$global:QuotaRetentionTestDeletes = [Collections.Generic.List[string]]::new()
function gh {
    # 此测试替身只提供发布清单和记录删除操作，禁止实际网络写入。
    if ($args[0] -ne 'api') { throw '意外 gh 操作' }
    $global:LASTEXITCODE = 0
    if ($args[1] -eq '--method') { $global:QuotaRetentionTestDeletes.Add($args[3]); return }
    @'
[[{"tag_name":"v5","published_at":"2026-11-18T00:00:00Z","draft":false,"prerelease":false,"assets":[{"id":5,"name":"CodexWeeklyQuotaMonitor-v5-win-x64.zip"}]},{"tag_name":"v4","published_at":"2026-11-17T00:00:00Z","draft":false,"prerelease":false,"assets":[]}],[{"tag_name":"v3","published_at":"2026-11-16T00:00:00Z","draft":false,"prerelease":false,"assets":[]},{"tag_name":"v2","published_at":"2026-11-15T00:00:00Z","draft":false,"prerelease":false,"assets":[{"id":2,"name":"CodexWeeklyQuotaMonitor-v2-win-x64.zip"},{"id":22,"name":"audit.txt"}]},{"tag_name":"v1","published_at":"2026-11-14T00:00:00Z","draft":false,"prerelease":false,"assets":[{"id":1,"name":"Codex周限额监控-v1.zip"}]}]]
'@
}
& $prune -GitHubRepository 'example/quota'
if ($global:QuotaRetentionTestDeletes.Count -ne 2 -or $global:QuotaRetentionTestDeletes -notcontains 'repos/example/quota/releases/assets/2' -or
    $global:QuotaRetentionTestDeletes -notcontains 'repos/example/quota/releases/assets/1') { throw '分页发布清理范围错误' }
Remove-Variable -Name QuotaRetentionTestDeletes -Scope Global
Write-Output '归档保留测试通过：版本排序、同版分组、预演、幂等、运行数据保护、分页远端附件清理。'
