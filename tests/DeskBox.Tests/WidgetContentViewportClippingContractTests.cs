namespace DeskBox.Tests;

public sealed class WidgetContentViewportClippingContractTests
{
    [Fact]
    public void GroupContentTransition_ClipsCompositionVisualsToTheBodyViewport()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetShell.xaml"));
        string code = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));

        int viewport = xaml.IndexOf(
            "x:Name=\"ContentTransitionViewport\"",
            StringComparison.Ordinal);
        int incomingPresenter = xaml.IndexOf(
            "x:Name=\"ShellContentPresenter\"",
            viewport,
            StringComparison.Ordinal);
        Assert.True(viewport >= 0);
        Assert.True(incomingPresenter > viewport);
        Assert.Contains(
            "Grid.Row=\"1\"",
            xaml[viewport..incomingPresenter],
            StringComparison.Ordinal);
        Assert.Contains(
            "Background=\"Transparent\"",
            xaml[viewport..incomingPresenter],
            StringComparison.Ordinal);

        Assert.Contains(
            "contentTransitionVisual.Compositor.CreateInsetClip()",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "contentTransitionVisual.Clip = _contentTransitionClip",
            code,
            StringComparison.Ordinal);

        int beginTransition = code.IndexOf(
            "public void BeginContentTransition(",
            StringComparison.Ordinal);
        int ensureBeforeReparent = code.IndexOf(
            "EnsureContentTransitionViewportClip();",
            beginTransition,
            StringComparison.Ordinal);
        int reparentOutgoing = code.IndexOf(
            "OutgoingContentPresenter.Content = outgoingContent.View;",
            beginTransition,
            StringComparison.Ordinal);
        Assert.InRange(
            ensureBeforeReparent,
            beginTransition + 1,
            reparentOutgoing - 1);

        int animateTransition = code.IndexOf(
            "public Task AnimateContentTransitionAsync(",
            StringComparison.Ordinal);
        int ensureBeforeAnimation = code.IndexOf(
            "EnsureContentTransitionViewportClip();",
            animateTransition,
            StringComparison.Ordinal);
        int animationGuard = code.IndexOf(
            "if (OutgoingContentPresenter.Content is null)",
            animateTransition,
            StringComparison.Ordinal);
        Assert.InRange(
            ensureBeforeAnimation,
            animateTransition + 1,
            animationGuard - 1);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate repository root.");
    }
}
