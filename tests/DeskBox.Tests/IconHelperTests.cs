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
}
