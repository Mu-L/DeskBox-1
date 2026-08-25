[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [string]$SourceDataRoot,
    [string]$OutputDirectory,
    [ValidateRange(1, 5)]
    [int]$Repetitions = 2,
    [ValidateRange(2, 60)]
    [int]$SettleSeconds = 8,
    [ValidateRange(3, 30)]
    [int]$SampleCount = 10,
    [ValidateSet("6C", "6D")]
    [string]$StageLabel = "6C",
    [switch]$KeepClones
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $repoRoot "src\DeskBox\bin\Debug\net10.0-windows10.0.22621.0\DeskBox.exe"
}
if ([string]::IsNullOrWhiteSpace($SourceDataRoot)) {
    $SourceDataRoot = Join-Path $env:LOCALAPPDATA "DeskBox"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot ".artifacts\search-core\stage-6c-product"
}

$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
$SourceDataRoot = [System.IO.Path]::GetFullPath($SourceDataRoot)
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$expectedExecutable = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRoot "src\DeskBox\bin\Debug\net10.0-windows10.0.22621.0\DeskBox.exe"))
if (-not [string]::Equals($ExecutablePath, $expectedExecutable, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Stage 6C product memory measurement only accepts the canonical repository Debug executable '$expectedExecutable'."
}
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "DeskBox Debug executable is missing at '$ExecutablePath'."
}
foreach ($required in @(
    (Join-Path (Split-Path -Parent $ExecutablePath) "deskbox_search_core.dll"),
    (Join-Path $SourceDataRoot "data\settings.json"),
    (Join-Path $SourceDataRoot "cache\search-index-v2.json"))) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Stage 6C product memory input is missing '$required'."
    }
}

$repoPrefix = $repoRoot.TrimEnd('\') + '\'
$runningRepoProcesses = @(Get-CimInstance Win32_Process -Filter "Name='DeskBox.exe'" |
    Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.ExecutablePath) -and
        $_.ExecutablePath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)
    })
if ($runningRepoProcesses.Count -ne 0) {
    throw "Stop the running repository DeskBox process before the isolated product memory measurement."
}

function Get-FileFingerprint([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    [PSCustomObject]@{
        Path = $item.FullName
        Length = $item.Length
        LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString("O")
        Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    }
}

function Assert-StrictChildPath([string]$Root, [string]$Candidate) {
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $candidateFull = [System.IO.Path]::GetFullPath($Candidate)
    if (-not $candidateFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing an out-of-root Stage 6C path '$candidateFull'."
    }
}

function Set-PreviewSettings([string]$SettingsPath, [bool]$RustEnabled) {
    $settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
    $settings.SearchCustomIndexerEnabled = $true
    if ($settings.PSObject.Properties.Name -ccontains "searchRustIndexerPreviewEnabled") {
        $settings.searchRustIndexerPreviewEnabled = $RustEnabled
    }
    else {
        $settings | Add-Member -NotePropertyName searchRustIndexerPreviewEnabled -NotePropertyValue $RustEnabled
    }
    $settings | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $SettingsPath -Encoding utf8

    $persisted = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
    $property = $persisted.PSObject.Properties |
        Where-Object { $_.Name -ceq "searchRustIndexerPreviewEnabled" } |
        Select-Object -First 1
    if ($null -eq $property -or [bool]$property.Value -ne $RustEnabled) {
        throw "Failed to persist the exact camel-case Rust preview setting in '$SettingsPath'."
    }
}

function Set-DbixFreshTimestamp([string]$Path) {
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $reader = [System.IO.BinaryReader]::new($stream, [System.Text.Encoding]::UTF8, $true)
        $magic = $reader.ReadInt32()
        $version = $reader.ReadInt32()
        if ($magic -ne 0x58494244 -or $version -ne 1) {
            throw "The isolated search cache is not DBIX v1."
        }
        $stream.Position = 8
        $writer = [System.IO.BinaryWriter]::new($stream, [System.Text.Encoding]::UTF8, $true)
        $writer.Write([DateTime]::UtcNow.Ticks)
        $writer.Flush()
        $stream.Flush($true)
        $writer.Dispose()
        $reader.Dispose()
    }
    finally {
        $stream.Dispose()
    }
}

function Get-DbixMetadata([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream, [System.Text.Encoding]::UTF8, $false)
        $magic = $reader.ReadInt32()
        $version = $reader.ReadInt32()
        $persistedTicks = $reader.ReadInt64()
        $directoryCount = $reader.ReadInt32()
        for ($index = 0; $index -lt $directoryCount; $index++) {
            [void]$reader.ReadString()
        }
        $entryCount = $reader.ReadInt32()
        [PSCustomObject]@{
            Magic = ('0x{0:X8}' -f $magic)
            Version = $version
            PersistedUtc = ([DateTime]::new($persistedTicks, [DateTimeKind]::Utc)).ToString("O")
            DirectoryCount = $directoryCount
            EntryCount = $entryCount
            FileBytes = $stream.Length
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-Median([long[]]$Values) {
    $ordered = @($Values | Sort-Object)
    $midpoint = [int][Math]::Floor($ordered.Count / 2.0)
    if (($ordered.Count % 2) -eq 1) {
        return [long]$ordered[$midpoint]
    }
    $upper = $midpoint
    return [long](($ordered[$upper - 1] + $ordered[$upper]) / 2)
}

function Wait-BackendReady(
    [System.Diagnostics.Process]$Process,
    [string]$LogPath,
    [bool]$RustExpected) {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            throw "DeskBox exited before the search backend became ready (exit $($Process.ExitCode))."
        }
        if (Test-Path -LiteralPath $LogPath) {
            $log = Get-Content -LiteralPath $LogPath -Raw -ErrorAction SilentlyContinue
            if ($RustExpected) {
                if ($log -match "Rust preview fallback") {
                    throw "Rust product preview fell back during measurement. Inspect '$LogPath'."
                }
                if ($log -match "Rust SearchCore preview backend") {
                    return
                }
            }
            elseif ($log -match "Loaded [0-9,]+ persisted entries from compact cache") {
                return
            }
        }
        Start-Sleep -Milliseconds 250
        $Process.Refresh()
    }
    throw "Timed out waiting for the expected search backend. Inspect '$LogPath'."
}

function Measure-Backend(
    [string]$Backend,
    [string]$DataRoot,
    [bool]$RustExpected,
    [int]$Iteration) {
    $logPath = Join-Path $DataRoot "DeskBox.log"
    if (Test-Path -LiteralPath $logPath) {
        Remove-Item -LiteralPath $logPath -Force
    }
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($ExecutablePath)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Environment["DESKBOX_DEV_DATA_ROOT"] = $DataRoot
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start the isolated $Backend DeskBox process."
    }
    try {
        Wait-BackendReady -Process $process -LogPath $logPath -RustExpected $RustExpected
        Start-Sleep -Seconds $SettleSeconds
        $private = [System.Collections.Generic.List[long]]::new()
        $working = [System.Collections.Generic.List[long]]::new()
        for ($sample = 0; $sample -lt $SampleCount; $sample++) {
            $process.Refresh()
            if ($process.HasExited) {
                throw "DeskBox exited during the $Backend memory sample."
            }
            $private.Add($process.PrivateMemorySize64)
            $working.Add($process.WorkingSet64)
            Start-Sleep -Milliseconds 500
        }
        [PSCustomObject]@{
            Backend = $Backend
            Iteration = $Iteration
            ProcessId = $process.Id
            ExecutablePath = $process.MainModule.FileName
            PrivateBytesMedian = Get-Median $private.ToArray()
            PrivateBytesMin = ($private | Measure-Object -Minimum).Minimum
            PrivateBytesMax = ($private | Measure-Object -Maximum).Maximum
            WorkingSetMedian = Get-Median $working.ToArray()
            WorkingSetMin = ($working | Measure-Object -Minimum).Minimum
            WorkingSetMax = ($working | Measure-Object -Maximum).Maximum
            BackendReadyLog = if ($RustExpected) { "Rust SearchCore preview backend" } else { "compact cache" }
            Termination = "Exact isolated process terminated after measurement"
        }
    }
    finally {
        $process.Refresh()
        if (-not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit(10000) | Out-Null
        }
        $process.Dispose()
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$runId = ([DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ")) + "-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
$runRoot = Join-Path $OutputDirectory $runId
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

$sourceSettings = Join-Path $SourceDataRoot "data\settings.json"
$sourceIndex = Join-Path $SourceDataRoot "cache\search-index-v2.json"
$sourceBefore = @(
    Get-FileFingerprint $sourceSettings
    Get-FileFingerprint $sourceIndex
)
$sourceSettingsObject = Get-Content -LiteralPath $sourceSettings -Raw | ConvertFrom-Json
$configuredEnabledWidgets = @($sourceSettingsObject.Widgets | Where-Object { -not $_.IsDisabled }).Count

$backendRoots = @{}
foreach ($backend in @("managed", "rust")) {
    $dataRoot = Join-Path $runRoot ("data-" + $backend)
    Assert-StrictChildPath -Root $runRoot -Candidate $dataRoot
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    foreach ($name in @("data", "cache", "QuickCapture")) {
        $source = Join-Path $SourceDataRoot $name
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination $dataRoot -Recurse -Force
        }
    }
    Set-PreviewSettings `
        -SettingsPath (Join-Path $dataRoot "data\settings.json") `
        -RustEnabled ($backend -eq "rust")
    Set-DbixFreshTimestamp (Join-Path $dataRoot "cache\search-index-v2.json")
    $backendRoots[$backend] = $dataRoot
}

$measurements = [System.Collections.Generic.List[object]]::new()
try {
    for ($iteration = 1; $iteration -le $Repetitions; $iteration++) {
        $measurements.Add((Measure-Backend `
            -Backend "managed" `
            -DataRoot $backendRoots["managed"] `
            -RustExpected $false `
            -Iteration $iteration))
        $measurements.Add((Measure-Backend `
            -Backend "rust" `
            -DataRoot $backendRoots["rust"] `
            -RustExpected $true `
            -Iteration $iteration))
    }
}
finally {
    try {
        $sourceAfter = @(
            Get-FileFingerprint $sourceSettings
            Get-FileFingerprint $sourceIndex
        )
        for ($index = 0; $index -lt $sourceBefore.Count; $index++) {
            if ($sourceBefore[$index].Sha256 -ne $sourceAfter[$index].Sha256 -or
                $sourceBefore[$index].Length -ne $sourceAfter[$index].Length -or
                $sourceBefore[$index].LastWriteTimeUtc -ne $sourceAfter[$index].LastWriteTimeUtc) {
                throw "Production input changed during the isolated Stage 6C measurement: '$($sourceBefore[$index].Path)'."
            }
        }
    }
    finally {
        if (-not $KeepClones.IsPresent) {
            foreach ($dataRoot in $backendRoots.Values) {
                foreach ($cleanupPath in @($dataRoot, ($dataRoot + "-Recovery"))) {
                    Assert-StrictChildPath -Root $runRoot -Candidate $cleanupPath
                    if (Test-Path -LiteralPath $cleanupPath) {
                        Remove-Item -LiteralPath $cleanupPath -Recurse -Force
                    }
                }
            }
        }
    }
}

$managedMedian = Get-Median @($measurements | Where-Object Backend -eq "managed" | ForEach-Object PrivateBytesMedian)
$rustMedian = Get-Median @($measurements | Where-Object Backend -eq "rust" | ForEach-Object PrivateBytesMedian)
$privateReductionPercent = if ($managedMedian -gt 0) {
    [Math]::Round((($managedMedian - $rustMedian) * 100.0 / $managedMedian), 2)
}
else { 0 }
$managedWorkingMedian = Get-Median @($measurements | Where-Object Backend -eq "managed" | ForEach-Object WorkingSetMedian)
$rustWorkingMedian = Get-Median @($measurements | Where-Object Backend -eq "rust" | ForEach-Object WorkingSetMedian)
$workingReductionPercent = if ($managedWorkingMedian -gt 0) {
    [Math]::Round((($managedWorkingMedian - $rustWorkingMedian) * 100.0 / $managedWorkingMedian), 2)
}
else { 0 }

$result = [PSCustomObject]@{
    SchemaVersion = 1
    StageLabel = $StageLabel
    RunId = $runId
    GeneratedAtUtc = [DateTime]::UtcNow.ToString("O")
    ExecutablePath = $ExecutablePath
    ExecutableSha256 = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash
    SearchCoreModuleSha256 = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $ExecutablePath) "deskbox_search_core.dll") -Algorithm SHA256).Hash
    SourceDataRoot = $SourceDataRoot
    SourceInputsUnchanged = $true
    ConfiguredEnabledWidgetCount = $configuredEnabledWidgets
    Dbix = Get-DbixMetadata $sourceIndex
    Repetitions = $Repetitions
    SettleSeconds = $SettleSeconds
    SampleCountPerProcess = $SampleCount
    Measurements = @($measurements)
    ManagedPrivateBytesMedian = $managedMedian
    RustPrivateBytesMedian = $rustMedian
    PrivateBytesReductionPercent = $privateReductionPercent
    ManagedWorkingSetMedian = $managedWorkingMedian
    RustWorkingSetMedian = $rustWorkingMedian
    WorkingSetReductionPercent = $workingReductionPercent
}

$jsonPath = Join-Path $runRoot "product-memory.json"
$markdownPath = Join-Path $runRoot "product-memory.md"
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $jsonPath -Encoding utf8
$markdown = @"
# DeskBox SearchCore Stage $StageLabel product memory measurement

- Run ID: $runId
- Canonical Debug executable: ``$ExecutablePath``
- Configured enabled widgets in both isolated clones: $configuredEnabledWidgets
- DBIX entries: $($result.Dbix.EntryCount)
- DBIX directories: $($result.Dbix.DirectoryCount)
- Repetitions per backend: $Repetitions
- Managed median Private Bytes: $([Math]::Round($managedMedian / 1MB, 2)) MiB
- Rust median Private Bytes: $([Math]::Round($rustMedian / 1MB, 2)) MiB
- Private Bytes reduction: $privateReductionPercent%
- Managed median Working Set: $([Math]::Round($managedWorkingMedian / 1MB, 2)) MiB
- Rust median Working Set: $([Math]::Round($rustWorkingMedian / 1MB, 2)) MiB
- Working Set reduction: $workingReductionPercent%
- Production settings and DBIX fingerprints unchanged: true

Each sample used the same copied widget/settings/cache snapshot, a unique development data root and the same canonical Debug executable. The script waited for the expected backend-ready log, allowed the real widget surfaces to settle, sampled the exact process, then terminated only that isolated process. Configured widget count proves the same full-grid configuration was loaded; it is not a pixel-level visual assertion.
"@
$markdown | Set-Content -LiteralPath $markdownPath -Encoding utf8

[PSCustomObject]@{
    RunId = $runId
    Result = $jsonPath
    Report = $markdownPath
    ConfiguredEnabledWidgetCount = $configuredEnabledWidgets
    DbixEntryCount = $result.Dbix.EntryCount
    ManagedPrivateMiB = [Math]::Round($managedMedian / 1MB, 2)
    RustPrivateMiB = [Math]::Round($rustMedian / 1MB, 2)
    PrivateReductionPercent = $privateReductionPercent
    ManagedWorkingSetMiB = [Math]::Round($managedWorkingMedian / 1MB, 2)
    RustWorkingSetMiB = [Math]::Round($rustWorkingMedian / 1MB, 2)
    WorkingSetReductionPercent = $workingReductionPercent
}
