namespace DeskBox.Tests;

public sealed class WidgetGroupTitleWheelContractTests
{
    [Fact]
    public void WheelNavigation_WrapsAndKeepsPendingTargetUntilCompletion()
    {
        string root = FindRepositoryRoot();
        string interaction = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetGroupTitleSwitcher.Interaction.cs"));
        string host = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/WidgetWindowBase.Grouping.cs"));

        Assert.Contains("wrap: origin is", interaction, StringComparison.Ordinal);
        Assert.Contains(
            "WidgetGroupSwitchOrigin.Wheel",
            interaction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TimeSpan.FromMilliseconds(1200)",
            interaction,
            StringComparison.Ordinal);
        Assert.Contains(
            "NotifyMemberInvocationCompleted",
            interaction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "!string.Equals(e.WidgetId, Config.Id",
            host,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "DeskBox")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
