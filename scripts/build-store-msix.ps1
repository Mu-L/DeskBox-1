param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [string]$PackageCertificateKeyFile = "",

    [switch]$SignPackage,

    [switch]$NativeAot,

    [ValidateSet("SideloadOnly", "StoreUpload", "CI")]
    [string]$PackageBuildMode = "SideloadOnly",

    [string]$AppxBundle = "Never",

    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "src\DeskBox\DeskBox.csproj"
$dotnetFromRepo = Join-Path $repoRoot ".codex-temp\dotnet10\dotnet.exe"
$dotnet = if (Test-Path $dotnetFromRepo) { $dotnetFromRepo } else { "dotnet" }
$runtimeIdentifier = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "artifacts\store-msix\"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = Join-Path $repoRoot $OutputDir
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$appxPackageDir = $OutputDir.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$signingEnabled = if ($SignPackage.IsPresent) { "true" } else { "false" }

$properties = @(
    "-p:Platform=$Platform",
    "-p:RuntimeIdentifier=$runtimeIdentifier",
    "-p:DeskBoxDistribution=Store",
    "-p:DeskBoxCreateMsixPackage=true",
    "-p:SelfContained=true",
    "-p:WindowsAppSDKSelfContained=false",
    "-p:PublishSingleFile=false",
    "-p:PublishTrimmed=false",
    "-p:UapAppxPackageBuildMode=$PackageBuildMode",
    "-p:AppxBundle=$AppxBundle",
    "-p:AppxPackageDir=$appxPackageDir",
    "-p:AppxPackageSigningEnabled=$signingEnabled"
)

if ($NativeAot.IsPresent) {
    $properties += @(
        "-p:DeskBoxAotAudit=true",
        "-p:DeskBoxAotSmokeHarness=false",
        "-p:PublishAot=true",
        "-p:DeskBoxRustNative=true",
        "-p:DeskBoxRustCrtLinkage=Static",
        "-p:DeskBoxSearchCorePreviewModule=false",
        "-p:JsonSerializerIsReflectionEnabledByDefault=false",
        "-p:IlcUseEnvironmentalTools=true",
        "-p:PublishTrimmed=true"
    )
}

if (-not [string]::IsNullOrWhiteSpace($PackageCertificateKeyFile)) {
    $properties += "-p:PackageCertificateKeyFile=$PackageCertificateKeyFile"
}

# dotnet publish does not always import the Visual Studio installation root,
# even when the VC symbol-conversion tool is installed. Pass the newest local
# VC tools directory explicitly so Store builds consistently emit .appxsym.
$visualStudioRoots = @(
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Community",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Professional",
    "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\Enterprise"
)

$vcToolsInstallDir = $visualStudioRoots |
    Where-Object { Test-Path (Join-Path $_ "VC\Tools\MSVC") } |
    ForEach-Object {
        Get-ChildItem -Path (Join-Path $_ "VC\Tools\MSVC") -Directory |
            Sort-Object Name -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    } |
    Select-Object -First 1

if (-not [string]::IsNullOrWhiteSpace($vcToolsInstallDir)) {
    $symbolToolPath = Join-Path $vcToolsInstallDir "bin\Hostx64\x64\mspdbcmf.exe"
    if (Test-Path $symbolToolPath) {
        $properties += "-p:VCToolsInstallDir=$vcToolsInstallDir\"
    }
}

if ($NativeAot.IsPresent) {
    $environmentScript = Join-Path $PSScriptRoot "rust-arm64-msvc-environment.ps1"
    . $environmentScript
    $msvcEnvironment = Get-DeskBoxMsvcEnvironment -Platform $Platform
    $environmentState = Enter-DeskBoxMsvcEnvironment -Toolchain $msvcEnvironment
}

try {
    & $dotnet publish $project -c $Configuration @properties -v:minimal
    $publishExitCode = $LASTEXITCODE
}
finally {
    if ($NativeAot.IsPresent -and $null -ne $environmentState) {
        Exit-DeskBoxMsvcEnvironment -State $environmentState
    }
}

if ($publishExitCode -ne 0) {
    exit $publishExitCode
}

Write-Host "Store MSIX output:" -ForegroundColor Cyan
Get-ChildItem -Path $OutputDir -Recurse -Include *.msix,*.msixbundle,*.msixupload,*.appxsym |
    Sort-Object LastWriteTime -Descending |
    Select-Object FullName, Length, LastWriteTime
