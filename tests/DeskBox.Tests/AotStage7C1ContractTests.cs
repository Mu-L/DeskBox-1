namespace DeskBox.Tests;

public sealed class AotStage7C1ContractTests
{
    [Fact]
    public void StoreNativeAot_PackagesRustModuleAndRemovesManagedRuntimeMetadata()
    {
        string project = Read("src/DeskBox/DeskBox.csproj");

        foreach (string token in new[]
                 {
                     "PrepareDeskBoxStoreNativeAotPayload",
                     "BeforeTargets=\"_ComputeAppxPackagePayload\"",
                     "DependsOnTargets=\"BuildDeskBoxRustNative\"",
                     "<TargetPath>deskbox_native.dll</TargetPath>",
                     "<TargetPath>deskbox_native.pdb</TargetPath>",
                     "DeskBox.deps.json",
                     "DeskBox.runtimeconfig.json"
                 })
        {
            Assert.Contains(token, project, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StoreBuild_ExposesNativeAotAndStoreUploadModesWithStableOutputRoot()
    {
        string script = Read("scripts/build-store-msix.ps1");

        foreach (string token in new[]
                 {
                     "[switch]$NativeAot",
                     "StoreUpload",
                     "DeskBoxAotAudit=true",
                     "PublishAot=true",
                     "DeskBoxRustNative=true",
                     "DeskBoxRustCrtLinkage=Static",
                     "UapAppxPackageBuildMode=$PackageBuildMode",
                     "$appxPackageDir"
                 })
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StoreAudit_RequiresNativePayloadIdentitySymbolsAndStrictAbsenceList()
    {
        string script = Read("scripts/audit-store-native-aot-package.ps1");

        foreach (string token in new[]
                 {
                     "D1FC332A.DeskBoxWidgets",
                     "Microsoft.WindowsAppRuntime.2",
                     "deskbox_native.dll",
                     "DeskBox\\.deps\\.json",
                     "DeskBox\\.runtimeconfig\\.json",
                     "deskbox_search_core",
                     "DeskBox\\.Updater",
                     "HasClrHeader",
                     "publishPayloadHashesMatch",
                     "DeskBox.pdb",
                     "deskbox_native.pdb",
                     "signingAndWackExecuted = $false"
                 })
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DirectInstallers_UseNativeAotModeAndSkipOnlyDotNetRuntime()
    {
        foreach (string relativePath in new[]
                 {
                     "installer/DeskBox.iss",
                     "installer/DeskBox.arm64.iss"
                 })
        {
            string installer = Read(relativePath);
            Assert.Contains("#define DeskBoxNativeAot 0", installer, StringComparison.Ordinal);
            Assert.Contains("#elif DeskBoxNativeAot", installer, StringComparison.Ordinal);
            Assert.Contains("deskbox_native.dll", installer, StringComparison.Ordinal);
        }

        foreach (string relativePath in new[]
                 {
                     "installer/DeskBox.Dependencies.iss",
                     "installer/DeskBox.Dependencies.arm64.iss"
                 })
        {
            string dependencies = Read(relativePath);
            Assert.Contains("#if DeskBoxNativeAot", dependencies, StringComparison.Ordinal);
            Assert.Contains("ShouldInstallDotNetRuntime := False", dependencies, StringComparison.Ordinal);
            Assert.Contains(
                "ShouldInstallWindowsAppRuntime := not IsWindowsAppRuntime22Installed",
                dependencies,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DistributionWorkflow_UsesNativeX64AndArm64RunnersAndPreservesEvidenceBoundaries()
    {
        string workflow = Read(".github/workflows/distribution-audit.yml");
        string orchestrator = Read("scripts/build-stage-7c1-distribution.ps1");

        foreach (string token in new[]
                 {
                     "windows-2025-vs2026",
                     "windows-11-vs2026-arm",
                     "build-stage-7c1-distribution.ps1",
                     "stage7c1-${{ matrix.rid }}-distribution",
                     "Cross-architecture evidence manifest",
                     "physicalUserDeviceExecuted",
                     "signingExecuted",
                     "wackExecuted",
                     "inPlaceUpgradeExecuted"
                 })
        {
            Assert.Contains(token, workflow, StringComparison.Ordinal);
        }

        foreach (string token in new[]
                 {
                     "publish-aot-audit.ps1",
                     "publish-arm64-aot-static-audit.ps1",
                     "DeskBoxNativeAot=1",
                     "PackageBuildMode\", \"StoreUpload",
                     "installerInstallationExecuted = $false",
                     "msixInstallationExecuted = $false",
                     "storeFlightExecuted = $false"
                 })
        {
            Assert.Contains(token, orchestrator, StringComparison.Ordinal);
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
