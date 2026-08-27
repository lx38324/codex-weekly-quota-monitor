param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProject = Join-Path $projectRoot 'WeeklyQuotaMonitor\WeeklyQuotaMonitor.csproj'
$testProject = Join-Path $projectRoot 'WeeklyQuotaMonitor.BusinessTests\WeeklyQuotaMonitor.BusinessTests.csproj'
$testOutput = Join-Path $projectRoot 'release-test-output'

dotnet build $testProject --configuration Release --output $testOutput
if ($LASTEXITCODE -ne 0) {
    throw '业务测试构建失败。'
}

dotnet (Join-Path $testOutput 'WeeklyQuotaMonitor.BusinessTests.dll')
if ($LASTEXITCODE -ne 0) {
    throw '业务测试执行失败。'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
dotnet publish $appProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $OutputDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw '单文件发布失败。'
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'install.ps1') -Destination (Join-Path $OutputDirectory 'install.ps1') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $OutputDirectory 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.en.md') -Destination (Join-Path $OutputDirectory 'README.en.md') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $OutputDirectory 'LICENSE') -Force
$resolvedOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path
$documentationOutput = Join-Path $resolvedOutput 'docs'
if (Test-Path -LiteralPath $documentationOutput) {
    $resolvedDocumentationOutput = (Resolve-Path -LiteralPath $documentationOutput).Path
    if (-not $resolvedDocumentationOutput.StartsWith(
            $resolvedOutput + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "文档输出目录越过发布目录：$resolvedDocumentationOutput"
    }

    Remove-Item -LiteralPath $resolvedDocumentationOutput -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $documentationOutput -Recurse -Force
Set-Content `
    -LiteralPath (Join-Path $OutputDirectory 'codex-path.txt') `
    -Value 'codex-desktop://current' `
    -Encoding utf8NoBOM `
    -NoNewline
Write-Host "发布完成：$OutputDirectory"
