namespace DeskBox.Tests;

public sealed class InstallerUninstallContractTests
{
    private static readonly string[] InstallerLanguages =
    [
        "english",
        "chinesesimplified",
        "chinesetraditional",
        "japanese",
        "german",
        "brazilianportuguese",
        "hindi",
        "spanish",
        "french",
        "arabic",
        "bengali",
        "russian"
    ];

    private static readonly string[] AppDataChoiceMessages =
    [
        "AppDataChoiceTitle",
        "ConfirmRemoveAppData",
        "KeepAppDataButton",
        "RemoveAppDataButton",
        "AppDataCleanupFailed"
    ];

    private static readonly string[] DependencyMessages =
    [
        "DependencyDownloadCancelled",
        "DependencyDownloadFailed",
        "DependencyDownloadFailedSummary",
        "DependencyInstallStartFailed",
        "DependencyInstallFailed",
        "DependencyInstallFailedSummary",
        "RuntimeDependencyComment"
    ];

    [Fact]
    public void Uninstall_OffersSafeKeepOrPurgeChoice()
    {
        string code = ReadRepositoryFile("installer/DeskBox.Uninstall.iss");

        Assert.Contains("ChooseAppDataRemoval", code, StringComparison.Ordinal);
        Assert.Contains("SuppressibleTaskDialogMsgBox", code, StringComparison.Ordinal);
        Assert.Contains("IDYES", code, StringComparison.Ordinal);
        Assert.Contains("DeskBoxPurgeUserDataParameter = '/PURGEUSERDATA'", code, StringComparison.Ordinal);
        Assert.Contains("DeskBoxAppDataRootPath = '{localappdata}\\DeskBox'", code, StringComparison.Ordinal);
        Assert.Contains("DeskBoxRecoveryRootPath = '{localappdata}\\DeskBox-Recovery'", code, StringComparison.Ordinal);
        Assert.Contains("DeskBoxTemporaryRootPath = '{%TEMP}\\DeskBox'", code, StringComparison.Ordinal);
        Assert.Contains("MB_YESNO or MB_DEFBUTTON2", code, StringComparison.Ordinal);
        Assert.Contains("RemoveNotificationRegistration", code, StringComparison.Ordinal);
        Assert.Contains("if ActivatorId <> '' then", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Format(ExpandConstant('{cm:", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DelTree(GetManagedStorageRootPath", code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("installer/DeskBox.Installation.iss")]
    [InlineData("installer/DeskBox.Dependencies.iss")]
    [InlineData("installer/DeskBox.Dependencies.arm64.iss")]
    public void InstallerCustomMessagePlaceholders_UseFmtMessage(string path)
    {
        string code = ReadRepositoryFile(path);

        Assert.DoesNotContain("Format(ExpandConstant('{cm:", code, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallChoice_IsLocalizedForEveryInstallerLanguage()
    {
        string messages =
            ReadRepositoryFile("installer/DeskBox.iss") + Environment.NewLine +
            ReadRepositoryFile("installer/DeskBox.NewLanguageCustomMessages.iss");

        foreach (string language in InstallerLanguages)
        {
            foreach (string message in AppDataChoiceMessages)
            {
                Assert.Contains(
                    $"{language}.{message}=",
                    messages,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void DependencyMessages_AreLocalizedForEveryInstallerLanguage()
    {
        string messages = ReadRepositoryFile("installer/DeskBox.DependencyCustomMessages.iss");

        foreach (string language in InstallerLanguages)
        {
            foreach (string message in DependencyMessages)
            {
                Assert.Contains(
                    $"{language}.{message}=",
                    messages,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void BundledRuntimeInstallers_SkipExternalDependencySetupAndUseDistinctNames()
    {
        foreach (string script in new[] { "installer/DeskBox.iss", "installer/DeskBox.arm64.iss" })
        {
            string content = ReadRepositoryFile(script);
            Assert.Contains("#ifndef DeskBoxBundledRuntime", content, StringComparison.Ordinal);
            Assert.Contains("{#MyAppPackageSuffix}", content, StringComparison.Ordinal);
        }

        foreach (string dependencyScript in new[]
                 {
                     "installer/DeskBox.Dependencies.iss",
                     "installer/DeskBox.Dependencies.arm64.iss"
                 })
        {
            string content = ReadRepositoryFile(dependencyScript);
            Assert.Contains("#if DeskBoxBundledRuntime", content, StringComparison.Ordinal);
            Assert.Contains("external runtime dependency setup skipped", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CustomMessageKeysAndPlaceholders_AreAlignedAcrossLanguages()
    {
        string messages = string.Join(
            Environment.NewLine,
            ReadRepositoryFile("installer/DeskBox.iss"),
            ReadRepositoryFile("installer/DeskBox.NewLanguageCustomMessages.iss"),
            ReadRepositoryFile("installer/DeskBox.DependencyCustomMessages.iss"));
        var tables = InstallerLanguages.ToDictionary(
            language => language,
            _ => new Dictionary<string, string>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (string line in messages.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int equalsIndex = line.IndexOf('=');
            int dotIndex = line.IndexOf('.');
            if (dotIndex <= 0 || equalsIndex <= dotIndex)
            {
                continue;
            }

            string language = line[..dotIndex].Trim();
            if (!tables.TryGetValue(language, out Dictionary<string, string>? table))
            {
                continue;
            }

            string key = line[(dotIndex + 1)..equalsIndex].Trim();
            Assert.True(table.TryAdd(key, line[(equalsIndex + 1)..]), $"Duplicate {language}.{key}");
        }

        Dictionary<string, string> english = tables["english"];
        foreach ((string language, Dictionary<string, string> table) in tables)
        {
            Assert.Equal(
                english.Keys.OrderBy(key => key, StringComparer.Ordinal),
                table.Keys.OrderBy(key => key, StringComparer.Ordinal));

            foreach ((string key, string englishValue) in english)
            {
                Assert.Equal(
                    GetInnoPlaceholders(englishValue),
                    GetInnoPlaceholders(table[key]));
            }
        }
    }

    [Fact]
    public void TraditionalChinese_IsImmediatelyAfterSimplifiedChinese()
    {
        string simplified =
            "Name: \"chinesesimplified\"; MessagesFile: \"Languages\\ChineseSimplified.isl\"";
        string traditional =
            "Name: \"chinesetraditional\"; MessagesFile: \"Languages\\ChineseTraditional.isl\"";

        foreach (string script in new[] { "installer/DeskBox.iss", "installer/DeskBox.arm64.iss" })
        {
            string content = ReadRepositoryFile(script);
            int simplifiedIndex = content.IndexOf(simplified, StringComparison.Ordinal);
            int traditionalIndex = content.IndexOf(traditional, StringComparison.Ordinal);

            Assert.True(simplifiedIndex >= 0);
            Assert.True(traditionalIndex > simplifiedIndex);
            Assert.Contains(
                "#include \"DeskBox.DependencyCustomMessages.iss\"",
                content,
                StringComparison.Ordinal);
            Assert.Contains(
                "AppComments={cm:RuntimeDependencyComment}",
                content,
                StringComparison.Ordinal);
        }

        string languageFile = ReadRepositoryFile("installer/Languages/ChineseTraditional.isl");
        Assert.Contains("LanguageName=繁體中文", languageFile, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hindi", "hi-IN")]
    [InlineData("spanish", "es-ES")]
    [InlineData("french", "fr-FR")]
    [InlineData("arabic", "ar-SA")]
    [InlineData("bengali", "bn-BD")]
    [InlineData("russian", "ru-RU")]
    [InlineData("chinesetraditional", "zh-TW")]
    public void InstallerLanguage_IsPassedToFirstAppLaunch(
        string installerLanguage,
        string appLanguage)
    {
        foreach (string script in new[] { "installer/DeskBox.iss", "installer/DeskBox.arm64.iss" })
        {
            string content = ReadRepositoryFile(script);
            Assert.Contains(
                $"ActiveLanguage = '{installerLanguage}' then Result := '{appLanguage}'",
                content,
                StringComparison.Ordinal);
            Assert.Contains(
                "#include \"DeskBox.Uninstall.iss\"",
                content,
                StringComparison.Ordinal);
        }
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        string root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string[] GetInnoPlaceholders(string value)
    {
        return System.Text.RegularExpressions.Regex.Matches(value, @"%(?:n|\d+)")
            .Select(match => match.Value)
            .OrderBy(placeholder => placeholder, StringComparer.Ordinal)
            .ToArray();
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

        throw new DirectoryNotFoundException("DeskBox repository root was not found.");
    }
}
