using System.Text.Json;

namespace DeskBox.Tests;

public sealed class OnboardingExperienceTests
{
    private static readonly string[] RequiredTaskFlowKeys =
    [
        "Onboarding.SkipAll",
        "Onboarding.Task.Step1.Title",
        "Onboarding.Task.Step2.PathTitle",
        "Onboarding.Task.Step2.ChangePath",
        "Onboarding.Task.Step2.Warning.SystemDrive",
        "Onboarding.Task.SkipPractice",
        "Onboarding.Task.Continue",
        "Onboarding.Task.Step3.TryButton",
        "Onboarding.Task.Step3.StatusCompleted",
        "Onboarding.Task.Step4.ToggleBody",
        "Onboarding.Task.Step4.StatusHidden",
        "Onboarding.Task.Step4.StatusShown",
        "Onboarding.Task.Step4.StatusCompleted",
        "Onboarding.Task.Step5.Title",
        "Widget.Empty.ActionsHint"
    ];

    [Fact]
    public void TaskFlow_HasOneIntroductionTwoOptionalPracticesAndFinishChoice()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml.cs"));

        Assert.Contains("private static readonly int StepCount = 4", codeBehind, StringComparison.Ordinal);
        Assert.Contains("0 => TaskStep2Panel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("1 => TaskStep3Panel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("2 => TaskStep4Panel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("3 => TaskStep5Panel", codeBehind, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep3TryButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"TaskStep5OpenAppearance_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"TaskStep2ConfirmPathButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("1 when !_hasCompletedFilePractice", codeBehind, StringComparison.Ordinal);
        Assert.Contains("2 when !_hasCompletedVisibilityPractice", codeBehind, StringComparison.Ordinal);

        string activeFlow = xaml[xaml.IndexOf(
            "x:Name=\"TaskStep2Panel\"",
            StringComparison.Ordinal)..xaml.IndexOf(
            "x:Name=\"Step1Panel\"",
            StringComparison.Ordinal)];
        Assert.DoesNotContain("Onboarding.Task.Step4.FeatureEntry", activeFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"TaskStep4OpenTrayMenu_Click\"", activeFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("Onboarding.Task.Step2.ManagedBody", activeFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("Onboarding.Task.Step2.MappedBody", activeFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("Onboarding.Task.Step3.DragBody", activeFlow, StringComparison.Ordinal);
        Assert.DoesNotContain("Onboarding.Task.Step5.OptionalBody", activeFlow, StringComparison.Ordinal);
    }

    [Fact]
    public void Practices_CompleteOnlyAfterRealFileAndVisibilityOperations()
    {
        string root = FindRepositoryRoot();
        string taskFlow = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.TaskFlow.cs"));
        string appCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/App.xaml.cs"));
        string fileSurfaceCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));

        Assert.Contains("OnOnboardingFileImportCompleted", taskFlow, StringComparison.Ordinal);
        Assert.Contains("_hasHiddenWidgetsDuringPractice", taskFlow, StringComparison.Ordinal);
        Assert.Contains("OnboardingFileImportCompleted", appCode, StringComparison.Ordinal);
        Assert.Contains("NotifyOnboardingFileImportCompleted", fileSurfaceCode, StringComparison.Ordinal);
        Assert.DoesNotContain("_hasCompletedFilePractice = true", taskFlow[..taskFlow.IndexOf(
            "OnOnboardingFileImportCompleted",
            StringComparison.Ordinal)], StringComparison.Ordinal);
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
    public void ChineseTaskFlow_ExplainsDefaultMoveAndOptionalPractice()
    {
        string root = FindRepositoryRoot();
        using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Strings/zh-CN.json")));

        Assert.Contains(
            "移动",
            strings.RootElement.GetProperty("Onboarding.Task.Step1.Body").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "原位置",
            strings.RootElement.GetProperty("Onboarding.Task.Step1.Body").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "可以跳过",
            strings.RootElement.GetProperty("Onboarding.Task.Step3.Body").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(
            "用桌面格子收纳文件。",
            strings.RootElement.GetProperty("Onboarding.Intro.Body").GetString());
        Assert.True(
            strings.RootElement.GetProperty("Onboarding.Task.Step1.Body").GetString()!.Length < 40,
            "The first-screen explanation should stay scannable.");
    }

    [Fact]
    public void DefaultManagedDropAction_RemainsMove()
    {
        string root = FindRepositoryRoot();
        string settingsModel = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Models/AppSettings.cs"));
        string userGuide = File.ReadAllText(Path.Combine(
            root,
            "docs/user-guide/01-getting-started.md"));

        Assert.Contains("ManagedDropAction { get; set; } = \"Move\"", settingsModel, StringComparison.Ordinal);
        Assert.Contains("默认拖入行为是移动", userGuide, StringComparison.Ordinal);
        Assert.DoesNotContain("默认拖入行为是复制", userGuide, StringComparison.Ordinal);
    }

    [Fact]
    public void IntroLogoAnimation_RunsForAboutTwoAndAHalfSecondsWithFallbackGrace()
    {
        string root = FindRepositoryRoot();
        string introCode = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.IntroAnimations.cs"));

        Assert.Contains("CreateDeskBoxMark", introCode, StringComparison.Ordinal);
        Assert.Contains("IntroAnimationTargetMilliseconds = 2500", introCode, StringComparison.Ordinal);
        Assert.Contains("IntroAnimationTargetMilliseconds + 1000", introCode, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(640)", introCode, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromSeconds(5)", introCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskFlow_UsesVisualGuidanceAndIconBackedStatus()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/OnboardingWindow.xaml"));
        string activeFlow = xaml[xaml.IndexOf(
            "x:Name=\"TaskStep2Panel\"",
            StringComparison.Ordinal)..xaml.IndexOf(
            "x:Name=\"Step1Panel\"",
            StringComparison.Ordinal)];

        Assert.Contains("Onboarding.Scene.DesktopFile", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Scene.FileWidget", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Task.Step3.DragTitle", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Task.Step3.PasteTitle", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Task.Step3.AddTitle", activeFlow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep3StatusIcon\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStep4StatusIcon\"", activeFlow, StringComparison.Ordinal);
        Assert.Contains("Glyph=\"&#xE713;\"", activeFlow, StringComparison.Ordinal);
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
