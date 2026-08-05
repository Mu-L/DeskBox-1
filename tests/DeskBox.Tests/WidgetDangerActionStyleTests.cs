namespace DeskBox.Tests;

public sealed class WidgetDangerActionStyleTests
{
    [Fact]
    public void WidgetCloseActions_UseTheSharedFluentCriticalStyle()
    {
        string root = FindRepositoryRoot();
        string sharedStyle = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/WidgetDangerActionStyle.cs"));
        string contentMenus = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        string quickCaptureMenus = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/QuickCaptureWidgetWindow.Menus.cs"));
        string confirmationBuilder = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Services/WidgetCompactConfirmationMenuBuilder.cs"));
        string shell = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetShell.xaml"));

        Assert.Contains(
            "SystemFillColorCriticalBrush",
            sharedStyle,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetDangerActionStyle.Apply(disableWidget)",
            contentMenus,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetDangerActionStyle.Apply(disableWidget)",
            quickCaptureMenus,
            StringComparison.Ordinal);
        Assert.Contains(
            "WidgetDangerActionStyle.Apply(confirmItem)",
            confirmationBuilder,
            StringComparison.Ordinal);
        Assert.Contains(
            "Foreground=\"{ThemeResource SystemFillColorCriticalBrush}\"",
            shell,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Colors.Red", contentMenus, StringComparison.Ordinal);
        Assert.DoesNotContain("Colors.Red", confirmationBuilder, StringComparison.Ordinal);
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
