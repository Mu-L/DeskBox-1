using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SearchIndexPathPolicyTests
{
    [Theory]
    [InlineData(@"C:\$Recycle.Bin\item.dat")]
    [InlineData(@"D:\System Volume Information\tracking.log")]
    [InlineData(@"E:\Recovery\image.wim")]
    public void DefaultPolicy_FiltersCanonicalRootRelativeNoise(string path)
    {
        var settings = new AppSettings { SearchHideSystemNoise = true };

        Assert.True(SearchIndexPathPolicy.ShouldExcludeFromIndex(path, settings));
    }

    [Fact]
    public void DefaultPolicy_DoesNotFilterAUserFolderByNameAlone()
    {
        var settings = new AppSettings { SearchHideSystemNoise = true };

        Assert.False(SearchIndexPathPolicy.ShouldExcludeFromIndex(
            @"C:\Users\simon\Documents\Windows\bin\project.txt",
            settings));
    }

    [Theory]
    [InlineData(@"D:\Projects\DeskBox\node_modules\package\index.js")]
    [InlineData(@"D:\Projects\DeskBox\.git\objects\pack.dat")]
    [InlineData(@"E:\Models\runtime\Lib\site-packages\module.py")]
    [InlineData(@"E:\Models\.venv\Scripts\python.exe")]
    public void DefaultPolicy_FiltersConventionalDependencyAndCacheDirectories(string path)
    {
        var settings = new AppSettings { SearchHideSystemNoise = true };

        Assert.True(SearchIndexPathPolicy.ShouldExcludeFromIndex(path, settings));
    }

    [Fact]
    public void DefaultPolicy_DoesNotFilterOrdinaryBuildLikeFolderNames()
    {
        var settings = new AppSettings { SearchHideSystemNoise = true };

        Assert.False(SearchIndexPathPolicy.ShouldExcludeFromIndex(
            @"D:\Documents\Windows\bin\target\release-notes.txt",
            settings));
    }

    [Fact]
    public void DefaultPolicy_FiltersCanonicalWindowsAndProgramDataTrees()
    {
        var settings = new AppSettings { SearchHideSystemNoise = true };
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);

        Assert.False(string.IsNullOrWhiteSpace(windows));
        Assert.False(string.IsNullOrWhiteSpace(programData));
        Assert.True(SearchIndexPathPolicy.ShouldExcludeFromIndex(
            Path.Combine(windows, "System32", "kernel32.dll"),
            settings));
        Assert.True(SearchIndexPathPolicy.ShouldExcludeFromIndex(
            Path.Combine(programData, "Microsoft", "cache.dat"),
            settings));

        string localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        Assert.True(SearchIndexPathPolicy.ShouldExcludeFromIndex(
            Path.Combine(localApplicationData, "PackageCache", "cache.dat"),
            settings));

        string programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        Assert.False(SearchIndexPathPolicy.ShouldExcludeFromIndex(
            Path.Combine(programFiles, "DeskBox", "DeskBox.exe"),
            settings));
    }

    [Fact]
    public void SystemNoiseToggle_DisablesDefaultsButKeepsUserExclusions()
    {
        var settings = new AppSettings
        {
            SearchHideSystemNoise = false,
            SearchIndexExcludedPaths = [@"D:\PrivateCache"]
        };

        Assert.False(SearchIndexPathPolicy.ShouldExcludeFromIndex(
            @"C:\$Recycle.Bin\item.dat",
            settings));
        Assert.True(SearchIndexPathPolicy.ShouldExcludeFromIndex(
            @"D:\PrivateCache\nested\item.dat",
            settings));
        Assert.False(SearchIndexPathPolicy.ShouldExcludeFromIndex(
            @"D:\PrivateCacheSibling\item.dat",
            settings));
    }
}
