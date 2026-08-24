using DeskBox.Controls.WidgetContents;
using DeskBox.Services;
using System.Text.RegularExpressions;

namespace DeskBox.Tests;

public sealed class MusicWidgetContentLayoutTests
{
    [Theory]
    [InlineData(150, 150)]
    [InlineData(179.9, 240)]
    [InlineData(320, 179.9)]
    public void ShouldUseMinimalLayout_UsesCoverLayoutBelow180(double width, double height)
    {
        Assert.True(MusicWidgetContent.ShouldUseMinimalLayout(width, height));
    }

    [Theory]
    [InlineData(180, 180)]
    [InlineData(320, 190)]
    [InlineData(400, 260)]
    public void ShouldUseMinimalLayout_UsesFullControlsFrom180(double width, double height)
    {
        Assert.False(MusicWidgetContent.ShouldUseMinimalLayout(width, height));
    }

    [Theory]
    [InlineData(150, 150)]
    [InlineData(400, 260)]
    public void ShouldUseMinimalLayout_CoverModeAlwaysUsesCover(double width, double height)
    {
        Assert.True(MusicWidgetContent.ShouldUseMinimalLayout(
            width,
            height,
            SettingsService.MusicDisplayModeCover));
    }

    [Theory]
    [InlineData(150, 150)]
    [InlineData(400, 260)]
    public void ShouldUseMinimalLayout_ControlsModeAlwaysUsesControls(double width, double height)
    {
        Assert.False(MusicWidgetContent.ShouldUseMinimalLayout(
            width,
            height,
            SettingsService.MusicDisplayModeControls));
    }

    [Theory]
    [InlineData(180, 28)]
    [InlineData(250, 30)]
    [InlineData(320, 32)]
    [InlineData(480, 32)]
    public void ResolveTransportButtonSize_UsesOneResponsiveSizeForEveryControl(
        double width,
        double expected)
    {
        Assert.Equal(expected, MusicWidgetContent.ResolveTransportButtonSize(width));
    }

    [Theory]
    [InlineData(219.9, false)]
    [InlineData(220, true)]
    public void ShouldShowHorizontalVolumeControl_ProtectsTheThreeTransportButtons(
        double width,
        bool expected)
    {
        Assert.Equal(expected, MusicWidgetContent.ShouldShowHorizontalVolumeControl(width));
    }

    [Theory]
    [InlineData(291.9, false)]
    [InlineData(292, true)]
    public void ShouldShowHorizontalPlaybackModeControl_RequiresWideLayout(
        double width,
        bool expected)
    {
        Assert.Equal(expected, MusicWidgetContent.ShouldShowHorizontalPlaybackModeControl(width));
    }

    [Fact]
    public void MusicTransportControls_UseFilledVectorPathsAndSubtleButtonStates()
    {
        string musicXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string shellXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml"));
        string shellCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        string transportXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicTransportIcon.xaml"));

        Assert.Equal(3, CountOccurrences(musicXaml, "Kind=\"Previous\""));
        Assert.Equal(4, CountOccurrences(musicXaml, "Kind=\"Play\""));
        Assert.Equal(4, CountOccurrences(musicXaml, "Kind=\"Pause\""));
        Assert.Equal(4, CountOccurrences(musicXaml, "Kind=\"Next\""));
        Assert.Equal(15, CountOccurrences(musicXaml, "<local:MusicTransportIcon"));
        Assert.DoesNotContain("MusicTransportFontIconStyle", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"&#xE768;\"", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"&#xE769;\"", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"&#xE892;\"", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"&#xE893;\"", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"MinimalPreviousButton\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MinimalControlPanel\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource SystemControlAcrylicElementBrush}\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"40\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Padding=\"6,5\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("TextAlignment=\"Left\"", musicXaml, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(musicXaml, "<Grid Width=\"26\" Height=\"26\">"));
        Assert.Contains("<Setter Property=\"FontSize\" Value=\"14\" />", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource SubtleFillColorSecondaryBrush}\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource SubtleFillColorTertiaryBrush}\"", musicXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"CornerRadius\" Value=\"4\" />", musicXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MusicPlayVectorIconStyle", musicXaml, StringComparison.Ordinal);

        Assert.Equal(3, CountOccurrences(shellXaml, "<widgetContents:MusicTransportIcon"));
        Assert.Contains("Kind=\"Previous\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("Kind=\"Next\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CompactPlayPauseIcon\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("MusicTransportIconKind.Pause", shellCode, StringComparison.Ordinal);
        Assert.Contains("MusicTransportIconKind.Play", shellCode, StringComparison.Ordinal);

        Assert.Contains("Width=\"26\"", transportXaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"26\"", transportXaml, StringComparison.Ordinal);
        Assert.Contains("UseLayoutRounding=\"True\"", transportXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Viewbox", transportXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Width=\"120\"", transportXaml, StringComparison.Ordinal);
        Assert.Contains("Data=\"M20 14.5L9 20.75", transportXaml, StringComparison.Ordinal);
        Assert.Contains("Data=\"M9.5 14.5L17.75 19.25", transportXaml, StringComparison.Ordinal);
        Assert.Contains("Data=\"M16.25 14.5L8.125 19.25", transportXaml, StringComparison.Ordinal);
        Assert.Equal(7, CountOccurrences(transportXaml, "Fill=\"{x:Bind Foreground, Mode=OneWay}\""));
        Assert.DoesNotContain("Stroke=", transportXaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeAotBindingProvider_CoversEveryMusicDataContextBinding()
    {
        string musicXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml"));
        string bindableSource = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/ViewModels/MusicWidgetViewModel.AotBindableProperties.cs"));

        string[] dataContextPaths = Regex.Matches(
                musicXaml,
                @"\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)(?<options>[^}]*)\}")
            .Where(match => !match.Groups["options"].Value.Contains(
                "ElementName=",
                StringComparison.Ordinal))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(34, dataContextPaths.Length);
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", bindableSource, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class MusicWidgetViewModel", bindableSource, StringComparison.Ordinal);
        foreach (string path in dataContextPaths)
        {
            Assert.Contains($"nameof({path})", bindableSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NativeAotBindingProvider_CoversWidgetTitleAndRenameRefresh()
    {
        string shellXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml"));
        string contentWindow = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        string commands = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));

        Assert.Contains("LabelText=\"{Binding DisplayName}\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayName}\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty([", contentWindow, StringComparison.Ordinal);
        Assert.Contains("private sealed partial class ContentWidgetTitleViewModel", contentWindow, StringComparison.Ordinal);
        Assert.Contains("nameof(DisplayName)", contentWindow, StringComparison.Ordinal);
        Assert.Contains("nameof(TitleIconSize)", contentWindow, StringComparison.Ordinal);
        Assert.Contains("nameof(TitleTextSize)", contentWindow, StringComparison.Ordinal);
        Assert.Contains("ContentWidgetShell.DataContext = _titleViewModel", contentWindow, StringComparison.Ordinal);
        Assert.Contains("_titleViewModel.RefreshDisplayName()", commands, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;
}
