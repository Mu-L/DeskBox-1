namespace DeskBox.Tests;

public sealed class InstallerUninstallContractTests
{
    private static readonly string[] InstallerLanguages =
    [
        "english",
        "chinesesimplified",
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

    [Theory]
    [InlineData("hindi", "hi-IN")]
    [InlineData("spanish", "es-ES")]
    [InlineData("french", "fr-FR")]
    [InlineData("arabic", "ar-SA")]
    [InlineData("bengali", "bn-BD")]
    [InlineData("russian", "ru-RU")]
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
