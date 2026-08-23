[CmdletBinding()]
param(
    [ValidateSet("x64")]
    [string]$Platform = "x64",

    [ValidateRange(1, 300000)]
    [int[]]$EntryCounts = @(10000, 100000, 300000),

    [string]$OutputDirectory,

    [ValidatePattern("^[0-9][A-Za-z0-9.-]*$")]
    [string]$StageLabel = "6B",

    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$resultRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot ".artifacts\search-core\stage-6b"
}
else {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
$nativeOutput = Join-Path $resultRoot "native"
$toolOutput = Join-Path $resultRoot "tool"
$projectPath = Join-Path $repoRoot "tools\DeskBox.SearchCore.Benchmarks\DeskBox.SearchCore.Benchmarks.csproj"
$modulePath = Join-Path $nativeOutput "deskbox_search_core.dll"
$toolPath = Join-Path $toolOutput "DeskBox.SearchCore.Benchmarks.dll"

New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
if (-not $SkipBuild.IsPresent) {
    & (Join-Path $PSScriptRoot "build-rust-search-core.ps1") `
        -Platform $Platform `
        -Configuration Release `
        -OutputDirectory $nativeOutput
    if ($LASTEXITCODE -ne 0) {
        throw "SearchCore Release native build failed with exit code $LASTEXITCODE."
    }

    & dotnet build $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --output $toolOutput `
        -p:Platform=x64 `
        --verbosity:minimal
    if ($LASTEXITCODE -ne 0) {
        throw "SearchCore benchmark build failed with exit code $LASTEXITCODE."
    }
}

foreach ($requiredPath in @($modulePath, $toolPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Stage 6B input is missing '$requiredPath'."
    }
}

$countsArgument = (($EntryCounts | Sort-Object -Unique) -join ",")
& dotnet $toolPath suite `
    --module $modulePath `
    --output $resultRoot `
    --counts $countsArgument `
    --stage $StageLabel
if ($LASTEXITCODE -ne 0) {
    throw "SearchCore isolated benchmark failed with exit code $LASTEXITCODE."
}

$summaryPath = Join-Path $resultRoot "summary.json"
$reportPath = Join-Path $resultRoot "summary.md"
if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
    throw "Stage 6B benchmark did not emit its summary artifacts."
}

$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
[PSCustomObject]@{
    SchemaVersion = $summary.SchemaVersion
    GeneratedAtUtc = $summary.GeneratedAtUtc
    EntryCounts = @($summary.Comparisons | ForEach-Object { $_.EntryCount })
    Summary = $summaryPath
    Report = $reportPath
    NativeModule = $modulePath
    BenchmarkTool = $toolPath
}
