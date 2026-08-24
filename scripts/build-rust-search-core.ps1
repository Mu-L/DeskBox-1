[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$CargoTargetDirectory,

    [ValidateSet("Dynamic", "Static")]
    [string]$CrtLinkage = "Static",

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$manifestPath = Join-Path $repoRoot "native\Cargo.toml"
$targetTriple = if ($Platform -eq "ARM64") {
    "aarch64-pc-windows-msvc"
}
else {
    "x86_64-pc-windows-msvc"
}
$cargoProfile = if ($Configuration -eq "Release") { "release" } else { "debug" }
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$cargoTargetRoot = if ([string]::IsNullOrWhiteSpace($CargoTargetDirectory)) {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot "native\target"))
}
else {
    [System.IO.Path]::GetFullPath($CargoTargetDirectory)
}
$requiredArtifacts = @(
    "deskbox_search_core.dll",
    "deskbox_search_core.pdb"
)
$requiredExportNames = @(
    "deskbox_search_core_abi_version",
    "deskbox_search_core_open_dbix_v1",
    "deskbox_search_core_create_v1",
    "deskbox_search_core_add_batch_v1",
    "deskbox_search_core_seal_v1",
    "deskbox_search_core_reset_cancel_v1",
    "deskbox_search_core_cancel_v1",
    "deskbox_search_core_query_v1",
    "deskbox_search_core_copy_entries_v1",
    "deskbox_search_core_mutate_batch_v1",
    "deskbox_search_core_project_v1",
    "deskbox_search_core_save_dbix_v1",
    "deskbox_search_core_stats_v1",
    "deskbox_search_core_destroy_v1"
)
$peContractScript = Join-Path $PSScriptRoot "native-pe-contract.ps1"
$arm64EnvironmentScript = Join-Path $PSScriptRoot "rust-arm64-msvc-environment.ps1"
if (-not (Test-Path -LiteralPath $peContractScript -PathType Leaf)) {
    throw "Native PE contract helper was not found: '$peContractScript'."
}
. $peContractScript
if ($Platform -eq "ARM64") {
    if (-not (Test-Path -LiteralPath $arm64EnvironmentScript -PathType Leaf)) {
        throw "ARM64 MSVC environment helper was not found: '$arm64EnvironmentScript'."
    }
    . $arm64EnvironmentScript
}

if (-not $ValidateOnly.IsPresent) {
    $cargo = (Get-Command cargo -ErrorAction Stop).Source
    $rustc = (Get-Command rustc -ErrorAction Stop).Source
    $targetLibDirectory = @(& $rustc --print target-libdir --target $targetTriple 2>$null)
    if ($LASTEXITCODE -ne 0 -or
        $targetLibDirectory.Count -ne 1 -or
        -not (Test-Path -LiteralPath $targetLibDirectory[0] -PathType Container)) {
        throw "Rust target '$targetTriple' is not installed for the pinned toolchain. Install rust-std 1.96.0 for this target before building $Platform."
    }

    $cargoArguments = @(
        "build",
        "--manifest-path", $manifestPath,
        "--package", "deskbox-search-core",
        "--locked",
        "--target", $targetTriple,
        "--target-dir", $cargoTargetRoot
    )
    if ($Configuration -eq "Release") {
        $cargoArguments += "--release"
    }

    $arm64EnvironmentState = $null
    if ($Platform -eq "ARM64") {
        $arm64Toolchain = Get-DeskBoxArm64MsvcEnvironment
        $arm64EnvironmentState =
            Enter-DeskBoxArm64MsvcEnvironment -Toolchain $arm64Toolchain
    }

    $previousCargoColor = [Environment]::GetEnvironmentVariable("CARGO_TERM_COLOR", "Process")
    $previousEncodedRustFlags =
        [Environment]::GetEnvironmentVariable("CARGO_ENCODED_RUSTFLAGS", "Process")
    $previousRustFlags = [Environment]::GetEnvironmentVariable("RUSTFLAGS", "Process")
    $crtTargetFeature = if ($CrtLinkage -eq "Static") {
        "target-feature=+crt-static"
    }
    else {
        "target-feature=-crt-static"
    }
    $encodedRustFlags = @("-C", $crtTargetFeature) -join [char]0x1F
    try {
        [Environment]::SetEnvironmentVariable("CARGO_TERM_COLOR", "never", "Process")
        [Environment]::SetEnvironmentVariable(
            "CARGO_ENCODED_RUSTFLAGS",
            $encodedRustFlags,
            "Process")
        [Environment]::SetEnvironmentVariable("RUSTFLAGS", $null, "Process")
        Push-Location $repoRoot
        try {
            & $cargo @cargoArguments
            if ($LASTEXITCODE -ne 0) {
                throw "Rust SearchCore build failed with exit code $LASTEXITCODE."
            }
        }
        finally {
            Pop-Location
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable("CARGO_TERM_COLOR", $previousCargoColor, "Process")
        [Environment]::SetEnvironmentVariable(
            "CARGO_ENCODED_RUSTFLAGS",
            $previousEncodedRustFlags,
            "Process")
        [Environment]::SetEnvironmentVariable("RUSTFLAGS", $previousRustFlags, "Process")
        if ($null -ne $arm64EnvironmentState) {
            Exit-DeskBoxArm64MsvcEnvironment -State $arm64EnvironmentState
        }
    }

    $cargoOutput = Join-Path $cargoTargetRoot "$targetTriple\$cargoProfile"
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    foreach ($artifactName in $requiredArtifacts) {
        $sourcePath = Join-Path $cargoOutput $artifactName
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Rust SearchCore build did not produce '$sourcePath'."
        }

        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $outputRoot $artifactName) -Force
    }
}

foreach ($artifactName in $requiredArtifacts) {
    $outputPath = Join-Path $outputRoot $artifactName
    if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
        throw "Rust SearchCore output is missing '$outputPath'."
    }
}

if (-not ("DeskBoxSearchCoreContractProbe" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public sealed class DeskBoxSearchCoreContractInfo
{
    public DeskBoxSearchCoreContractInfo(uint abiVersion, string[] requiredExports)
    {
        AbiVersion = abiVersion;
        RequiredExports = requiredExports;
    }

    public uint AbiVersion { get; private set; }
    public string[] RequiredExports { get; private set; }
}

public static class DeskBoxSearchCoreContractProbe
{
    private static readonly string[] RequiredExportNames = new[]
    {
        "deskbox_search_core_abi_version",
        "deskbox_search_core_open_dbix_v1",
        "deskbox_search_core_create_v1",
        "deskbox_search_core_add_batch_v1",
        "deskbox_search_core_seal_v1",
        "deskbox_search_core_reset_cancel_v1",
        "deskbox_search_core_cancel_v1",
        "deskbox_search_core_query_v1",
        "deskbox_search_core_copy_entries_v1",
        "deskbox_search_core_mutate_batch_v1",
        "deskbox_search_core_project_v1",
        "deskbox_search_core_save_dbix_v1",
        "deskbox_search_core_stats_v1",
        "deskbox_search_core_destroy_v1"
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiVersionDelegate();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string exportName);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    public static DeskBoxSearchCoreContractInfo ReadContract(string modulePath)
    {
        const uint LoadLibrarySearchDllLoadDir = 0x00000100;
        const uint LoadLibrarySearchSystem32 = 0x00000800;
        IntPtr module = LoadLibraryExW(
            modulePath,
            IntPtr.Zero,
            LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
        if (module == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to load the DeskBox SearchCore module.");
        }

        try
        {
            foreach (string exportName in RequiredExportNames)
            {
                RequireExport(module, exportName);
            }

            IntPtr abiExport = RequireExport(module, "deskbox_search_core_abi_version");
            var abiProbe = (AbiVersionDelegate)Marshal.GetDelegateForFunctionPointer(
                abiExport,
                typeof(AbiVersionDelegate));
            return new DeskBoxSearchCoreContractInfo(
                abiProbe(),
                (string[])RequiredExportNames.Clone());
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    private static IntPtr RequireExport(IntPtr module, string exportName)
    {
        IntPtr export = GetProcAddress(module, exportName);
        if (export == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException(
                "The DeskBox SearchCore export '" + exportName + "' is missing.");
        }

        return export;
    }
}
"@
}

$copiedDll = Join-Path $outputRoot "deskbox_search_core.dll"
$peContract = Get-DeskBoxNativePeContract `
    -Path $copiedDll `
    -ExpectedPlatform $Platform `
    -RequiredExports $requiredExportNames
$processArchitecture =
    [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
$expectedProcessArchitecture = if ($Platform -eq "ARM64") { "Arm64" } else { "X64" }
$runtimeProbeExecuted = $processArchitecture -eq $expectedProcessArchitecture
$runtimeProbeReason = if ($runtimeProbeExecuted) {
    "host-and-target-architectures-match"
}
else {
    "cross-architecture-static-validation-only"
}
$importedModules = @($peContract.ImportedModules)
$vcruntimeImports = @($importedModules | Where-Object {
    $_.StartsWith("VCRUNTIME", [System.StringComparison]::OrdinalIgnoreCase) -or
    $_.StartsWith("MSVCP", [System.StringComparison]::OrdinalIgnoreCase)
})
if ($CrtLinkage -eq "Static" -and $vcruntimeImports.Count -gt 0) {
    throw "Static CRT build still imports redistributable CRT modules: $($vcruntimeImports -join ', ')."
}
$contractValidation = if ($runtimeProbeExecuted) {
    "runtime-load-plus-static-pe"
}
else {
    "static-pe-plus-frozen-source-constants"
}

if ($runtimeProbeExecuted) {
    $contract = [DeskBoxSearchCoreContractProbe]::ReadContract($copiedDll)
    $abiVersion = $contract.AbiVersion
    $requiredExports = @($contract.RequiredExports)
}
else {
    $header = Get-Content -LiteralPath (Join-Path $repoRoot "native\include\deskbox_search_core.h") -Raw
    $rustSource = Get-Content -LiteralPath (Join-Path $repoRoot "native\deskbox-search-core\src\lib.rs") -Raw
    foreach ($token in @(
            "#define DESKBOX_SEARCH_CORE_ABI_VERSION 3u",
            "pub const DESKBOX_SEARCH_CORE_ABI_VERSION: u32 = 3;")) {
        $source = if ($token.StartsWith("#define", [System.StringComparison]::Ordinal)) {
            $header
        }
        else {
            $rustSource
        }
        if ($source.IndexOf($token, [System.StringComparison]::Ordinal) -lt 0) {
            throw "Rust SearchCore frozen contract is missing '$token'."
        }
    }

    $abiVersion = 3
    $requiredExports = @($peContract.RequiredExports)
}

if ($abiVersion -ne 3) {
    throw "Rust SearchCore ABI mismatch: expected 3, found $abiVersion."
}

[PSCustomObject]@{
    Platform = $Platform
    Configuration = $Configuration
    Target = $targetTriple
    CrtLinkage = $CrtLinkage
    CargoTargetDirectory = $cargoTargetRoot
    OutputDirectory = $outputRoot
    AbiVersion = $abiVersion
    RequiredExports = $requiredExports
    Machine = $peContract.Machine
    MachineHex = $peContract.MachineHex
    MachineName = $peContract.MachineName
    ExportCount = $peContract.ExportCount
    SizeOfImage = $peContract.SizeOfImage
    ImportCount = $peContract.ImportCount
    ImportedModules = $importedModules
    VcRuntimeImports = $vcruntimeImports
    ContractValidation = $contractValidation
    RuntimeProbeExecuted = $runtimeProbeExecuted
    RuntimeProbeReason = $runtimeProbeReason
    ProcessArchitecture = $processArchitecture
    ExpectedProcessArchitecture = $expectedProcessArchitecture
    ValidationOnly = $ValidateOnly.IsPresent
    Dll = $copiedDll
    Pdb = Join-Path $outputRoot "deskbox_search_core.pdb"
}
