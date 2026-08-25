using DeskBox.Helpers;

namespace DeskBox.Tests;

public class IconHelperTests
{
    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("clip.MOV")]
    [InlineData(@"C:\media\clip.mkv")]
    [InlineData("clip.webm")]
    [InlineData("clip.m2ts")]
    public void IsVideoFile_RecognizesSupportedVideoExtensions(string path)
    {
        Assert.True(IconHelper.IsVideoFile(path));
        Assert.True(IconHelper.IsMediaFile(path));
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("photo.HEIC")]
    public void IsMediaFile_IncludesImages(string path)
    {
        Assert.True(IconHelper.IsImageFile(path));
        Assert.True(IconHelper.IsMediaFile(path));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("archive.zip")]
    [InlineData("video.mp4.txt")]
    public void IsMediaFile_RejectsNonMediaExtensions(string path)
    {
        Assert.False(IconHelper.IsMediaFile(path));
    }

    [Fact]
    public void ShortcutIconResolution_IsBoundedAndCacheInvalidationAvoidsShellReads()
    {
        string source = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Helpers/IconHelper.cs"));
        int getIconStart = source.IndexOf(
            "public static async Task<BitmapImage?> GetIconAsync(",
            StringComparison.Ordinal);
        int clearCacheStart = source.IndexOf(
            "public static void ClearIconCache(",
            getIconStart,
            StringComparison.Ordinal);
        int clearCacheEnd = source.IndexOf(
            "private static void InvalidateShellIconCache(",
            clearCacheStart,
            StringComparison.Ordinal);
        Assert.True(getIconStart >= 0);
        Assert.True(clearCacheStart > getIconStart);
        Assert.True(clearCacheEnd > clearCacheStart);

        string getIcon = source[getIconStart..clearCacheStart];
        Assert.Contains(
            "await ResolveIconSourceWithCacheKeyAsync(",
            getIcon,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ResolveIconSource(path",
            getIcon,
            StringComparison.Ordinal);

        string clearCache = source[clearCacheStart..clearCacheEnd];
        Assert.DoesNotContain(
            "ResolveIconSource(",
            clearCache,
            StringComparison.Ordinal);
        Assert.DoesNotContain("File.Exists(", clearCache, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Exists(", clearCache, StringComparison.Ordinal);

        Assert.Contains(
            "BoundedBackgroundWorkScheduler.SharedShell",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "IconSourceResolutionTimeout",
            source,
            StringComparison.Ordinal);
    }
}
