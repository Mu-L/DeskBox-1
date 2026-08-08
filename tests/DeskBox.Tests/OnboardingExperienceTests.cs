using System.Text.Json;

namespace DeskBox.Tests;

public sealed class OnboardingExperienceTests
{
    private static readonly string[] RequiredTaskFlowKeys =
    [
        "Onboarding.SkipAll",
        "Onboarding.Task.Step1.Title",
        "Onboarding.Task.Step2.ManagedTitle",
        "Onboarding.Task.Step2.MappedTitle",
        "Onboarding.Task.Step2.PathTitle",
        "Onboarding.Task.Step2.ChangePath",
        "Onboarding.Task.Step2.ConfirmPath",
        "Onboarding.Task.Step2.Warning.SystemDrive",
        "Onboarding.Task.Step2.TransferSummary",
        "Onboarding.Task.Step3.TryButton",
        "Onboarding.Task.Step4.ToggleBody",
        "Onboarding.Task.Step4.StatusHidden",
        "Onboarding.Task.Step4.StatusShown",
        "Onboarding.Task.Step4.TrayButton",
        "Onboarding.Task.Step5.Title",
        "Widget.Empty.ActionsHint"
    ];

    [Fact]
    public void TaskFlow_HasFiveActionOrientedSteps()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml.cs"));

        Assert.Contains("private static readonly int StepCount = 5", codeBehind, StringComparison.Ordinal);
        Assert.Contains("0 => TaskStep1Panel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("4 => TaskStep5Panel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep3TryButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep2ConfirmPathButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"TaskStep4OpenTrayMenu_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"TaskStep5OpenAppearance_Click\"", xaml, StringComparison.Ordinal);

        string activeFlow = xaml[xaml.IndexOf(
            "x:Name=\"TaskStep1Panel\"",
            StringComparison.Ordinal)..xaml.IndexOf(
            "x:Name=\"Step1Panel\"",
            StringComparison.Ordinal)];
        Assert.DoesNotContain("Onboarding.Task.Step4.FeatureEntry", activeFlow, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskFlow_IsLocalizedInEveryLanguage()
    {
        string root = FindRepositoryRoot();
        string stringsDirectory = Path.Combine(root, "src/DeskBox/Strings");

        foreach (string path in Directory.GetFiles(stringsDirectory, "*.json"))
        {
            using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(path));
            foreach (string key in RequiredTaskFlowKeys)
            {
                Assert.True(
                    strings.RootElement.TryGetProperty(key, out JsonElement value) &&
                    !string.IsNullOrWhiteSpace(value.GetString()),
                    $"{Path.GetFileName(path)} is missing {key}.");
            }
        }
    }

    [Fact]
    public void ChineseTaskFlow_PointsToRightSideAndTrayCreation()
    {
        string root = FindRepositoryRoot();
        using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Strings/zh-CN.json")));

        Assert.Contains(
            "屏幕右侧",
            strings.RootElement.GetProperty("Onboarding.Task.Step1.Title").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "托盘",
            strings.RootElement.GetProperty("Onboarding.Task.Step4.TrayBody").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            "用桌面格子收纳文件。",
            strings.RootElement.GetProperty("Onboarding.Intro.Body").GetString());
        Assert.Equal(
            "文件夹映射",
            strings.RootElement.GetProperty("Onboarding.Task.Step2.MappedTitle").GetString());
    }

    [Theory]
    [InlineData("ja-JP.json", "グリッド")]
    [InlineData("de-DE.json", "Raster")]
    [InlineData("pt-BR.json", "grade")]
    public void TaskFlow_UsesWidgetTermInsteadOfLayoutGrid(
        string fileName,
        string forbiddenTerm)
    {
        string root = FindRepositoryRoot();
        using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Strings",
            fileName)));

        foreach (JsonProperty property in strings.RootElement.EnumerateObject()
                     .Where(property => property.Name.StartsWith(
                         "Onboarding.Task.",
                         StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(
                forbiddenTerm,
                property.Value.GetString(),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Onboarding_IsCompletedOnlyByTheWindowAndPersistsProgress()
    {
        string root = FindRepositoryRoot();
        string appCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/App.xaml.cs"));
        string windowCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml.cs"));

        string ensureMethod = appCode[appCode.IndexOf(
            "private async Task<bool> EnsureOnboardingAsync",
            StringComparison.Ordinal)..appCode.IndexOf(
            "public void ShowOnboarding",
            StringComparison.Ordinal)];
        Assert.DoesNotContain("HasCompletedOnboarding = true", ensureMethod, StringComparison.Ordinal);
        Assert.Contains("OnboardingStepIndex = newStep", windowCode, StringComparison.Ordinal);
        Assert.Contains("CompletedOnboardingVersion = CurrentOnboardingVersion", windowCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyFileWidget_KeepsOneConciseActionHint()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));

        Assert.Contains(
            "svc:Localized.Key=\"Widget.Empty.ActionsHint\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"{Binding EmptyStateText}\"",
            xaml,
            StringComparison.Ordinal);
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
