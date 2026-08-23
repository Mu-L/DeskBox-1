using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class Arm64NativeRuntimeGateTests
{
    private const string RequiredEnvironmentVariable =
        "DESKBOX_REQUIRE_ARM64_RUNTIME_GATE";

    [Fact]
    public void OptInGate_LoadsBothArm64ModulesAndExecutesSearchCoreProductCall()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(RequiredEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        Assert.Equal(Architecture.Arm64, RuntimeInformation.OSArchitecture);
        Assert.Equal(Architecture.Arm64, RuntimeInformation.ProcessArchitecture);

        string nativePath = Path.Combine(
            AppContext.BaseDirectory,
            ShortcutNativeModule.DllName);
        ShortcutNativeLoadResult native = ShortcutNativeModule.Load(nativePath);
        Assert.True(native.Success, $"{native.Failure}: {native.Detail}");
        Assert.NotNull(native.Module);
        Assert.Equal(2U, native.Module.ProbeAbiVersion());
        Assert.Equal(511UL, native.Module.ProbeCapabilities());

        string searchCorePath = Path.Combine(
            AppContext.BaseDirectory,
            SearchCoreNativeBackend.DllName);
        Assert.True(
            SearchCoreNativeBackend.TryCreate(
                searchCorePath,
                initialEntryCapacity: 2,
                initialUtf16CapacityChars: 64,
                out SearchCoreNativeBackend? searchCore,
                out string error),
            error);
        Assert.NotNull(searchCore);
        using (searchCore)
        {
            DateTime modified = new(638_900_000_000_000_000L, DateTimeKind.Utc);
            searchCore.AddEntries(
            [
                new SearchCoreSourceEntry(
                    @"C:\Arm64Gate",
                    "github-runtime-项目.txt",
                    IsDirectory: false,
                    modified),
                new SearchCoreSourceEntry(
                    @"C:\Arm64Gate",
                    "unrelated.bin",
                    IsDirectory: false,
                    modified.AddTicks(-1))
            ]);
            searchCore.Seal();

            SearchCoreQuerySnapshot result = searchCore.Query("项目", maxResults: 8);
            SearchCoreQueryItem item = Assert.Single(result.Items);
            Assert.Equal("github-runtime-项目.txt", item.FileName);
            Assert.Equal(@"C:\Arm64Gate", item.DirectoryPath);
            Assert.Equal(2U, result.ScannedEntryCount);
        }
    }
}
