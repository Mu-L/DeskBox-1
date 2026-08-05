namespace DeskBox.Tests;

public sealed class SettingsAboutContactTests
{
    [Fact]
    public void AboutSection_ShowsFeedbackEmailAndNoRepositoryButton()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/ViewModels/SettingsViewModel.cs"));
        string responsiveLayout = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml.cs"));

        Assert.Contains("Settings.About.FeedbackTitle", xaml, StringComparison.Ordinal);
        Assert.Contains("FeedbackEmailButton", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"AboutRightPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Spacing=\"6\"", xaml, StringComparison.Ordinal);
        Assert.Contains("1047078635@qq.com", viewModel, StringComparison.Ordinal);
        Assert.Contains("FeedbackEmailButton.HorizontalAlignment", responsiveLayout, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(AboutRightPanel", responsiveLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("AboutRepositoryButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AboutRepositoryButton", responsiveLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenRepositoryButton_Click", xaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "DeskBox repository root was not found.");
    }
}
