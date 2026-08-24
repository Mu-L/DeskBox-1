[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string[]]$Platforms = @("x64", "ARM64"),

    [string]$OutputDirectory,

    [ValidateRange(3, 15)]
    [int]$MemoryProbeRounds = 5,

    [switch]$NoRestore,

    [switch]$ProbeOnly,

    [string]$NativeDll,

    [string]$SearchCoreDll
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

if ($ProbeOnly.IsPresent) {
    foreach ($path in @($NativeDll, $SearchCoreDll)) {
        if ([string]::IsNullOrWhiteSpace($path) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "CRT memory probe requires two existing absolute DLL paths."
        }
    }

    if (-not ("DeskBoxCrtIsolatedProbe" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public sealed class DeskBoxCrtProbeContract
{
    public DeskBoxCrtProbeContract(uint nativeAbi, ulong capabilities, uint searchAbi)
    {
        NativeAbi = nativeAbi;
        Capabilities = capabilities;
        SearchAbi = searchAbi;
    }

    public uint NativeAbi { get; private set; }
    public ulong Capabilities { get; private set; }
    public uint SearchAbi { get; private set; }
}

public static class DeskBoxCrtIsolatedProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint UInt32Probe();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong UInt64Probe();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string exportName);

    private static IntPtr Load(string path)
    {
        const uint LoadLibrarySearchDllLoadDir = 0x00000100;
        const uint LoadLibrarySearchSystem32 = 0x00000800;
        IntPtr module = LoadLibraryExW(
            path,
            IntPtr.Zero,
            LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
        if (module == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to load CRT probe module.");
        }
        return module;
    }

    private static IntPtr Export(IntPtr module, string name)
    {
        IntPtr value = GetProcAddress(module, name);
        if (value == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException(name);
        }
        return value;
    }

    public static DeskBoxCrtProbeContract LoadAndProbe(string nativePath, string searchPath)
    {
        IntPtr native = Load(nativePath);
        IntPtr search = Load(searchPath);
        var nativeAbi = (UInt32Probe)Marshal.GetDelegateForFunctionPointer(
            Export(native, "deskbox_native_abi_version"), typeof(UInt32Probe));
        var capabilities = (UInt64Probe)Marshal.GetDelegateForFunctionPointer(
            Export(native, "deskbox_native_capabilities"), typeof(UInt64Probe));
        var searchAbi = (UInt32Probe)Marshal.GetDelegateForFunctionPointer(
            Export(search, "deskbox_search_core_abi_version"), typeof(UInt32Probe));
        return new DeskBoxCrtProbeContract(nativeAbi(), capabilities(), searchAbi());
    }
}
"@
    }

    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    [GC]::Collect()
    $process = Get-Process -Id $PID
    $process.Refresh()
    $privateBefore = $process.PrivateMemorySize64
    $workingSetBefore = $process.WorkingSet64
    $contract = [DeskBoxCrtIsolatedProbe]::LoadAndProbe(
        [System.IO.Path]::GetFullPath($NativeDll),
        [System.IO.Path]::GetFullPath($SearchCoreDll))
    [System.Threading.Thread]::Sleep(100)
    $process.Refresh()
    $privateAfter = $process.PrivateMemorySize64
    $workingSetAfter = $process.WorkingSet64
    [ordered]@{
        processArchitecture =
            [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        nativeAbi = $contract.NativeAbi
        capabilities = $contract.Capabilities
        searchAbi = $contract.SearchAbi
        privateBeforeBytes = $privateBefore
        privateAfterBytes = $privateAfter
        privateDeltaBytes = $privateAfter - $privateBefore
        workingSetBeforeBytes = $workingSetBefore
        workingSetAfterBytes = $workingSetAfter
        workingSetDeltaBytes = $workingSetAfter - $workingSetBefore
    } | ConvertTo-Json -Compress
    exit 0
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outputRoot = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot ".artifacts\rust-crt-stage7c0"
}
else {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
$cargoRoot = Join-Path $repoRoot ".artifacts\cargo\rust-crt-stage7c0"
$summaryPath = Join-Path $outputRoot "rust-crt-stage7c0-evidence.json"
$nativeBuildScript = Join-Path $PSScriptRoot "build-rust-native.ps1"
$searchBuildScript = Join-Path $PSScriptRoot "build-rust-search-core.ps1"
$testProject = Join-Path $repoRoot "tests\DeskBox.Tests\DeskBox.Tests.csproj"
$hostExecutable = (Get-Process -Id $PID).Path
$processArchitecture =
    [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
$osArchitecture =
    [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()

function Invoke-DeskBoxCrtBuild {
    param(
        [Parameter(Mandatory)]
        [string]$Script,

        [Parameter(Mandatory)]
        [string]$Platform,

        [Parameter(Mandatory)]
        [string]$Linkage,

        [Parameter(Mandatory)]
        [string]$ModuleOutput,

        [Parameter(Mandatory)]
        [string]$CargoOutput
    )

    $emitted = @(& $Script `
        -Platform $Platform `
        -Configuration Release `
        -CrtLinkage $Linkage `
        -OutputDirectory $ModuleOutput `
        -CargoTargetDirectory $CargoOutput)
    $results = @($emitted | Where-Object {
        $null -ne $_ -and
        $_.PSObject.Properties.Name -contains "RuntimeProbeExecuted"
    })
    if ($results.Count -ne 1) {
        throw "'$Script' emitted $($results.Count) structured CRT build results; expected one."
    }

    return $results[0]
}

function Get-DeskBoxCrtModuleEvidence {
    param(
        [Parameter(Mandatory)]
        [psobject]$Build
    )

    $dll = Get-Item -LiteralPath $Build.Dll
    $pdb = Get-Item -LiteralPath $Build.Pdb
    return [ordered]@{
        linkage = $Build.CrtLinkage
        abiVersion = $Build.AbiVersion
        capabilities = if ($Build.PSObject.Properties.Name -contains "Capabilities") {
            $Build.Capabilities
        }
        else {
            $null
        }
        machine = $Build.MachineName
        machineHex = $Build.MachineHex
        exportCount = $Build.ExportCount
        runtimeProbeExecuted = $Build.RuntimeProbeExecuted
        contractValidation = $Build.ContractValidation
        fileBytes = $dll.Length
        imageBytes = $Build.SizeOfImage
        sha256 = (Get-FileHash -LiteralPath $dll.FullName -Algorithm SHA256).Hash
        pdbBytes = $pdb.Length
        imports = @($Build.ImportedModules)
        vcRuntimeImports = @($Build.VcRuntimeImports)
        path = $dll.FullName
    }
}

function Get-DeskBoxMedian {
    param(
        [Parameter(Mandatory)]
        [long[]]$Values
    )

    if ($Values.Count -eq 0) {
        return $null
    }
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 1) {
        return [long]$ordered[$middle]
    }

    return [long][Math]::Round(
        ([double]$ordered[$middle - 1] + [double]$ordered[$middle]) / 2.0)
}

function Invoke-DeskBoxCrtMemoryProbe {
    param(
        [Parameter(Mandatory)]
        [string]$NativePath,

        [Parameter(Mandatory)]
        [string]$SearchPath
    )

    $rounds = @(
        for ($round = 1; $round -le $MemoryProbeRounds; $round++) {
            $output = @(& $hostExecutable `
                -NoProfile `
                -ExecutionPolicy Bypass `
                -File $PSCommandPath `
                -ProbeOnly `
                -NativeDll $NativePath `
                -SearchCoreDll $SearchPath 2>&1)
            if ($LASTEXITCODE -ne 0) {
                throw "Isolated CRT memory probe round $round failed: $($output -join [Environment]::NewLine)"
            }
            $jsonLine = @($output | Where-Object {
                $_.ToString().TrimStart().StartsWith("{", [System.StringComparison]::Ordinal)
            } | Select-Object -Last 1)
            if ($jsonLine.Count -ne 1) {
                throw "Isolated CRT memory probe round $round did not emit one JSON result."
            }
            $sample = $jsonLine[0].ToString() | ConvertFrom-Json
            if ($sample.nativeAbi -ne 2 -or
                $sample.capabilities -ne 511 -or
                $sample.searchAbi -ne 3 -or
                $sample.processArchitecture -ne $processArchitecture) {
                throw "Isolated CRT memory probe round $round returned an invalid runtime contract."
            }

            [ordered]@{
                round = $round
                privateDeltaBytes = [long]$sample.privateDeltaBytes
                workingSetDeltaBytes = [long]$sample.workingSetDeltaBytes
            }
        }
    )
    return [ordered]@{
        measurement = "isolated-host-process-load-and-abi-delta"
        rounds = $rounds
        medianPrivateDeltaBytes = Get-DeskBoxMedian -Values @(
            $rounds | ForEach-Object { [long]$_.privateDeltaBytes })
        medianWorkingSetDeltaBytes = Get-DeskBoxMedian -Values @(
            $rounds | ForEach-Object { [long]$_.workingSetDeltaBytes })
    }
}

function Invoke-DeskBoxStaticProductTests {
    param(
        [Parameter(Mandatory)]
        [string]$Platform
    )

    $runtimeIdentifier = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
    $resultDirectory = Join-Path $outputRoot "$($Platform.ToLowerInvariant())\static-product-tests"
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    $filter = if ($Platform -eq "ARM64") {
        "FullyQualifiedName~DeskBox.Tests.Arm64NativeRuntimeGateTests|FullyQualifiedName~DeskBox.Tests.SearchCoreNativeBackendTests"
    }
    else {
        "FullyQualifiedName=DeskBox.Tests.ShortcutNativeDifferentialTests.LoaderReadsCurrentAbiAndAllStage3C2Capabilities|FullyQualifiedName~DeskBox.Tests.SearchCoreNativeBackendTests"
    }
    $previousGate = [Environment]::GetEnvironmentVariable(
        "DESKBOX_REQUIRE_ARM64_RUNTIME_GATE",
        "Process")
    try {
        if ($Platform -eq "ARM64") {
            [Environment]::SetEnvironmentVariable(
                "DESKBOX_REQUIRE_ARM64_RUNTIME_GATE",
                "1",
                "Process")
        }
        $arguments = @(
            "test", $testProject,
            "--configuration", "Release",
            "-p:Platform=$Platform",
            "-p:RuntimeIdentifier=$runtimeIdentifier",
            "-p:DeskBoxRustCrtLinkage=Static",
            "-p:WindowsAppSdkBootstrapInitialize=false",
            "--results-directory", $resultDirectory,
            "--logger", "trx;LogFileName=static-product-$($Platform.ToLowerInvariant()).trx",
            "--filter", $filter,
            "--blame-hang",
            "--blame-hang-timeout", "5m",
            "--verbosity", "minimal")
        if ($NoRestore.IsPresent) {
            $arguments += "--no-restore"
        }

        & dotnet @arguments | Out-Host
        $exitCode = $LASTEXITCODE
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            "DESKBOX_REQUIRE_ARM64_RUNTIME_GATE",
            $previousGate,
            "Process")
    }

    $trxPath = Join-Path $resultDirectory "static-product-$($Platform.ToLowerInvariant()).trx"
    if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
        throw "Static CRT product tests did not produce '$trxPath'."
    }
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
    if ($null -eq $counters) {
        throw "Static CRT product-test TRX has no counters."
    }
    $result = [ordered]@{
        exitCode = $exitCode
        total = [int]$counters.total
        executed = [int]$counters.executed
        passed = [int]$counters.passed
        failed = [int]$counters.failed
        error = [int]$counters.error
        timeout = [int]$counters.timeout
        aborted = [int]$counters.aborted
        trx = $trxPath
        sha256 = (Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash
    }
    if ($exitCode -ne 0 -or
        $result.executed -lt 2 -or
        $result.failed -ne 0 -or
        $result.error -ne 0 -or
        $result.timeout -ne 0 -or
        $result.aborted -ne 0) {
        throw "Static CRT product runtime tests failed for $Platform."
    }
    return $result
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
New-Item -ItemType Directory -Path $cargoRoot -Force | Out-Null
$startedUtc = [DateTime]::UtcNow
$platformResults = @()

foreach ($platform in $Platforms) {
    $expectedArchitecture = if ($platform -eq "ARM64") { "Arm64" } else { "X64" }
    $matchingRuntime =
        $processArchitecture -eq $expectedArchitecture -and
        $osArchitecture -eq $expectedArchitecture
    $linkageResults = [ordered]@{}
    foreach ($linkage in @("Dynamic", "Static")) {
        $linkageKey = $linkage.ToLowerInvariant()
        $variantRoot = Join-Path $outputRoot "$($platform.ToLowerInvariant())\$linkageKey"
        $cargoOutput = Join-Path $cargoRoot "$($platform.ToLowerInvariant())\$linkageKey"
        $nativeBuild = Invoke-DeskBoxCrtBuild `
            -Script $nativeBuildScript `
            -Platform $platform `
            -Linkage $linkage `
            -ModuleOutput (Join-Path $variantRoot "native") `
            -CargoOutput $cargoOutput
        $searchBuild = Invoke-DeskBoxCrtBuild `
            -Script $searchBuildScript `
            -Platform $platform `
            -Linkage $linkage `
            -ModuleOutput (Join-Path $variantRoot "search") `
            -CargoOutput $cargoOutput
        $nativeEvidence = Get-DeskBoxCrtModuleEvidence -Build $nativeBuild
        $searchEvidence = Get-DeskBoxCrtModuleEvidence -Build $searchBuild
        $runtimeImports = @(
            @($nativeEvidence.vcRuntimeImports) +
            @($searchEvidence.vcRuntimeImports) |
                Sort-Object -Unique)
        if ($linkage -eq "Dynamic" -and $runtimeImports.Count -eq 0) {
            throw "$platform dynamic CRT baseline does not expose a VC runtime dependency; A/B contract is inconclusive."
        }
        if ($linkage -eq "Static" -and $runtimeImports.Count -ne 0) {
            throw "$platform static CRT variant retains VC runtime imports: $($runtimeImports -join ', ')."
        }
        if ($matchingRuntime -and
            (-not $nativeBuild.RuntimeProbeExecuted -or
             -not $searchBuild.RuntimeProbeExecuted)) {
            throw "$platform matching-host builds skipped their runtime ABI probes."
        }
        if (-not $matchingRuntime -and
            ($nativeBuild.RuntimeProbeExecuted -or $searchBuild.RuntimeProbeExecuted)) {
            throw "$platform cross builds incorrectly claimed runtime ABI execution."
        }

        $linkageResults[$linkageKey] = [ordered]@{
            native = $nativeEvidence
            searchCore = $searchEvidence
            pairFileBytes = [long]$nativeEvidence.fileBytes + [long]$searchEvidence.fileBytes
            pairImageBytes = [long]$nativeEvidence.imageBytes + [long]$searchEvidence.imageBytes
            vcRuntimeImports = $runtimeImports
            memory = if ($matchingRuntime) {
                Invoke-DeskBoxCrtMemoryProbe `
                    -NativePath $nativeEvidence.path `
                    -SearchPath $searchEvidence.path
            }
            else {
                $null
            }
        }
    }

    $fileDelta =
        [long]$linkageResults.static.pairFileBytes -
        [long]$linkageResults.dynamic.pairFileBytes
    $imageDelta =
        [long]$linkageResults.static.pairImageBytes -
        [long]$linkageResults.dynamic.pairImageBytes
    $staticIsBounded = $fileDelta -le 1MB -and $imageDelta -le 1MB
    $recommendation = if ($staticIsBounded) { "Static" } else { "Dynamic" }
    $platformResults += [ordered]@{
        platform = $platform
        evidenceLevel = if ($matchingRuntime) {
            "runtime-ab-plus-static-pe"
        }
        else {
            "cross-compiled-static-pe-only"
        }
        matchingRuntime = $matchingRuntime
        dynamic = $linkageResults.dynamic
        static = $linkageResults.static
        comparison = [ordered]@{
            staticMinusDynamicFileBytes = $fileDelta
            staticMinusDynamicImageBytes = $imageDelta
            boundedBelowOneMiB = $staticIsBounded
        }
        productTests = if ($matchingRuntime) {
            Invoke-DeskBoxStaticProductTests -Platform $platform
        }
        else {
            $null
        }
        recommendation = $recommendation
    }
}

$allRecommendStatic = @(
    $platformResults | Where-Object { $_.recommendation -ne "Static" }
).Count -eq 0
$auditedRecommendation = if ($allRecommendStatic) { "Static" } else { "Dynamic" }
$coveredPlatforms = @($platformResults | ForEach-Object { $_.platform })
$matchingPlatforms = @(
    $platformResults | Where-Object { $_.matchingRuntime } | ForEach-Object { $_.platform })
$productionDecision = "Pending"
$finishedUtc = [DateTime]::UtcNow
$evidence = [ordered]@{
    schema = "deskbox.rust-crt-stage7c0-evidence.v1"
    status = "passed"
    evidenceLevel = "architecture-aware-crt-ab"
    startedUtc = $startedUtc.ToString("O")
    finishedUtc = $finishedUtc.ToString("O")
    durationSeconds = [Math]::Round(($finishedUtc - $startedUtc).TotalSeconds, 3)
    host = [ordered]@{
        osArchitecture = $osArchitecture
        processArchitecture = $processArchitecture
        osDescription =
            [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        powershell = $PSVersionTable.PSVersion.ToString()
    }
    source = [ordered]@{
        commit = (& git -C $repoRoot rev-parse HEAD).Trim()
        ref = $env:GITHUB_REF
        sha = $env:GITHUB_SHA
        runId = $env:GITHUB_RUN_ID
    }
    platforms = $platformResults
    decision = [ordered]@{
        recommendationForAuditedPlatforms = $auditedRecommendation
        productionDecision = $productionDecision
        coveredPlatforms = $coveredPlatforms
        runtimeValidatedPlatforms = $matchingPlatforms
        directInstallerAdditionalVcRedistRequired = $null
        storeAdditionalVCLibsDependencyRequiredForRust = $null
        rationale = if ($auditedRecommendation -eq "Static") {
            "Both Rust DLLs remove VC redistributable imports and the combined file/image cost stays below 1 MiB on every architecture in this evidence file; combine native x64 and ARM64 runtime evidence before changing the production default."
        }
        else {
            "At least one audited architecture exceeds the bounded static-link cost."
        }
    }
}
$evidence | ConvertTo-Json -Depth 16 |
    Set-Content -LiteralPath $summaryPath -Encoding utf8

[pscustomobject]@{
    Status = "passed"
    Summary = $summaryPath
    Platforms = $Platforms -join ","
    RecommendedCrtLinkageForAuditedPlatforms = $auditedRecommendation
    ProductionDecision = $productionDecision
}
