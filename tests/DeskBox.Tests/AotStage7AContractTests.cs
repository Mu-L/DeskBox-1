using System.Diagnostics;
using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class AotStage7AContractTests
{
    [Fact]
    public void RustBuildScripts_MapBothWindowsMsvcTargetsAndUseStaticArm64PeContracts()
    {
        string native = Read("scripts/build-rust-native.ps1");
        string search = Read("scripts/build-rust-search-core.ps1");
        string pe = Read("scripts/native-pe-contract.ps1");
        string environment = Read("scripts/rust-arm64-msvc-environment.ps1");
        string toolchain = Read("rust-toolchain.toml");

        Assert.Contains("aarch64-pc-windows-msvc", toolchain, StringComparison.Ordinal);

        foreach (string script in new[] { native, search })
        {
            Assert.Contains("[ValidateSet(\"x64\", \"ARM64\")]", script, StringComparison.Ordinal);
            Assert.Contains("aarch64-pc-windows-msvc", script, StringComparison.Ordinal);
            Assert.Contains("x86_64-pc-windows-msvc", script, StringComparison.Ordinal);
            Assert.Contains("native-pe-contract.ps1", script, StringComparison.Ordinal);
            Assert.Contains("rust-arm64-msvc-environment.ps1", script, StringComparison.Ordinal);
            Assert.Contains("RuntimeProbeExecuted", script, StringComparison.Ordinal);
            Assert.Contains("static-pe-plus-frozen-source-constants", script, StringComparison.Ordinal);
            Assert.Contains("rust-std 1.96.0", script, StringComparison.Ordinal);
        }

        foreach (string token in new[]
                 {
                     "Get-DeskBoxNativePeContract",
                     "0xAA64",
                     "0x8664",
                     "PE32+",
                     "RequiredExports",
                     "missing required exports"
                 })
        {
            Assert.Contains(token, pe, StringComparison.Ordinal);
        }

        foreach (string token in new[]
                 {
                     "Microsoft.VisualStudio.Component.VC.Tools.ARM64",
                     "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                     "CARGO_TARGET_AARCH64_PC_WINDOWS_MSVC_LINKER",
                     "CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER",
                     "WindowsSdkUcrtLibraryDirectory",
                     "WindowsSdkUmLibraryDirectory",
                     "Get-DeskBoxMsvcEnvironment",
                     "Enter-DeskBoxMsvcEnvironment",
                     "Exit-DeskBoxMsvcEnvironment",
                     "Enter-DeskBoxArm64MsvcEnvironment",
                     "Exit-DeskBoxArm64MsvcEnvironment"
                 })
        {
            Assert.Contains(token, environment, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void X64Audit_UsesOneCompleteExplicitMsvcEnvironmentForNativeAot()
    {
        string script = Read("scripts/publish-aot-audit.ps1");

        foreach (string token in new[]
                 {
                     "Get-DeskBoxMsvcEnvironment -Platform x64",
                     "Enter-DeskBoxMsvcEnvironment",
                     "Exit-DeskBoxMsvcEnvironment",
                     "IlcUseEnvironmentalTools=true",
                     "LinkerDirectory \"dumpbin.exe\""
                 })
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Project_DefaultsDirectArm64SearchCoreOnAfterHostedNativeRuntimeGate()
    {
        string projectText = Read("src/DeskBox/DeskBox.csproj");
        XDocument project = XDocument.Parse(projectText);

        Assert.Contains("'$(DeskBoxDistribution)' == 'Direct'", projectText, StringComparison.Ordinal);
        Assert.Contains("'$(RuntimeIdentifier)' == 'win-arm64'", projectText, StringComparison.Ordinal);
        Assert.Contains("<DeskBoxRustNativePlatform", projectText, StringComparison.Ordinal);
        Assert.Contains("<DeskBoxSearchCorePlatform", projectText, StringComparison.Ordinal);
        Assert.Contains(">ARM64</DeskBoxRustNativePlatform>", projectText, StringComparison.Ordinal);
        Assert.Contains(">ARM64</DeskBoxSearchCorePlatform>", projectText, StringComparison.Ordinal);
        Assert.Contains("native-pe-contract.ps1", projectText, StringComparison.Ordinal);

        XElement defaultPolicy = Assert.Single(
            project.Descendants("DeskBoxSearchCoreDefaultEnabled")
                .Where(element => element.Value == "true"));
        string condition = Assert.IsType<string>((string?)defaultPolicy.Attribute("Condition"));
        Assert.Contains("'$(DeskBoxDistribution)' == 'Direct'", condition, StringComparison.Ordinal);
        Assert.DoesNotContain("'$(Platform)' != 'ARM64'", condition, StringComparison.Ordinal);
        Assert.DoesNotContain("'$(RuntimeIdentifier)' != 'win-arm64'", condition, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeAotValidation_AcceptsMatchingArm64AndRejectsCrossedPairs()
    {
        ProcessResult valid = await RunValidationAsync("ARM64", "win-arm64");
        Assert.Equal(0, valid.ExitCode);

        ProcessResult wrongRid = await RunValidationAsync("ARM64", "win-x64");
        Assert.NotEqual(0, wrongRid.ExitCode);
        Assert.Contains("matching Platform/RuntimeIdentifier pair", wrongRid.Output, StringComparison.Ordinal);

        ProcessResult wrongPlatform = await RunValidationAsync("x64", "win-arm64");
        Assert.NotEqual(0, wrongPlatform.ExitCode);
        Assert.Contains("matching Platform/RuntimeIdentifier pair", wrongPlatform.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Arm64Audit_IsExplicitlyStaticAndSeparatesBuildEvidenceFromDeviceEvidence()
    {
        string script = Read("scripts/publish-arm64-aot-static-audit.ps1");

        foreach (string token in new[]
                 {
                     "cross-compiled-static-only",
                     "targetDeviceExecuted = $false",
                     "runtimeAbiProbeExecuted = $false",
                     "Enter-DeskBoxArm64MsvcEnvironment",
                     "Exit-DeskBoxArm64MsvcEnvironment",
                     "aarch64-pc-windows-msvc",
                     "0xAA64",
                     "IlcUseEnvironmentalTools=true",
                     "DeskBoxSearchCoreDefaultEnabled=true",
                     "defaultDecisionDeferredToStage7B = $false",
                     "sourceStableDuringAudit",
                     "publishMatchesStaging = $true"
                 })
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }

        Assert.Contains("rust-arm64-msvc-environment.ps1", script, StringComparison.Ordinal);
        Assert.Contains("deskbox_native.dll", script, StringComparison.Ordinal);
        Assert.Contains("deskbox_search_core.dll", script, StringComparison.Ordinal);
        Assert.Contains("DeskBox.Updater.exe", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectInstallers_PackageSearchCoreWithoutShippingItsSymbols()
    {
        string x64 = Read("installer/DeskBox.iss");
        string arm64 = Read("installer/DeskBox.arm64.iss");

        Assert.Contains("deskbox_search_core.pdb", x64, StringComparison.Ordinal);
        Assert.Contains("deskbox_search_core.pdb", arm64, StringComparison.Ordinal);
        Assert.Contains("DeskBoxSearchCorePreviewModule=true", arm64, StringComparison.Ordinal);
        Assert.DoesNotContain("deskbox_search_core.dll,deskbox_search_core.pdb", arm64, StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_SeparatesArm64StaticEvidenceFromStage7BDeviceEvidence()
    {
        string report = Read("docs/architecture/rust-stage-7a-arm64-static-report.md");
        string roadmap = Read("docs/architecture/rust-native-aot-roadmap.md");
        string nativeReadme = Read("native/README.md");

        foreach (string token in new[]
                 {
                     "cross-compiled-static-only",
                     "targetDeviceExecuted=false",
                     "runtimeAbiProbeExecuted=false",
                     "0xAA64",
                     "VCRUNTIME140.dll",
                     "7B ARM64 真实设备产品门禁",
                     "94%"
                 })
        {
            Assert.Contains(token, report, StringComparison.Ordinal);
        }

        Assert.Contains("7A 完成复盘与阶段 7B 开放", roadmap, StringComparison.Ordinal);
        Assert.Contains("rust-stage-7a-arm64-static-report.md", nativeReadme, StringComparison.Ordinal);
        Assert.Contains("aarch64-pc-windows-msvc", nativeReadme, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunValidationAsync(
        string platform,
        string runtimeIdentifier)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
                 {
                     "msbuild",
                     TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"),
                     "-nologo",
                     "-t:ValidateDeskBoxNativeAotConfiguration",
                     "-p:Configuration=Release",
                     "-p:PublishAot=true",
                     "-p:DeskBoxRustNative=true",
                     $"-p:Platform={platform}",
                     $"-p:RuntimeIdentifier={runtimeIdentifier}"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(
            process.ExitCode,
            string.Concat(await stdout, await stderr));
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    private sealed record ProcessResult(int ExitCode, string Output);
}
