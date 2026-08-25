namespace DeskBox.Tests;

public sealed class SearchLazyLifecycleContractTests
{
    [Fact]
    public void Startup_PreloadsTheSingleSearchRuntimeButKeepsThePopupShellLazy()
    {
        string source = Read("src/DeskBox/App.xaml.cs");
        string launched = ExtractMethod(
            source,
            "protected override async void OnLaunched(LaunchActivatedEventArgs args)");

        int featureCheck = launched.IndexOf(
            "FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.Search)",
            StringComparison.Ordinal);
        int runtime = launched.IndexOf("EnsureSearchServices();", featureCheck, StringComparison.Ordinal);
        int preload = launched.IndexOf("BeginSearchIndexPreload();", runtime, StringComparison.Ordinal);
        int widgets = launched.IndexOf("WidgetManager = new WidgetManager", StringComparison.Ordinal);
        Assert.InRange(featureCheck, 0, runtime - 1);
        Assert.InRange(runtime, featureCheck + 1, preload - 1);
        Assert.InRange(preload, runtime + 1, widgets - 1);
        Assert.DoesNotContain("CreateSearchPopupWindow", launched, StringComparison.Ordinal);

        string lightweight = ExtractMethod(source, "private void EnsureSearchFeatureShell()");
        Assert.Contains("new SearchHistoryService()", lightweight, StringComparison.Ordinal);
        Assert.Contains("new SearchHotkeyService(", lightweight, StringComparison.Ordinal);
        Assert.DoesNotContain("new SearchIndexService(", lightweight, StringComparison.Ordinal);
        Assert.DoesNotContain("new SearchEngineService(", lightweight, StringComparison.Ordinal);
        Assert.DoesNotContain("new SearchPopupWindow(", lightweight, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleRuntimeAndPopup_FollowTheirSeparateLifecycleBoundaries()
    {
        string source = Read("src/DeskBox/App.xaml.cs");
        string heavy = ExtractMethod(source, "private void EnsureSearchServices()");
        string enable = ExtractMethod(source, "internal void SetSearchFeatureEnabled(bool enabled)");
        string open = ExtractMethod(source, "private void OpenSearchPopupCore(string? initialQuery)");
        string preload = ExtractMethod(
            source,
            "private static async Task<bool> PreloadSearchIndexAsync(");

        Assert.Contains("new SearchIndexService(", heavy, StringComparison.Ordinal);
        Assert.Contains("new SearchEngineService(", heavy, StringComparison.Ordinal);
        Assert.DoesNotContain("new WindowsIndexSearchService(", heavy, StringComparison.Ordinal);
        Assert.DoesNotContain("new UsnJournalIndexService()", heavy, StringComparison.Ordinal);
        Assert.DoesNotContain("StartCustomIndexingAsync", heavy, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateSearchPopupWindow", heavy, StringComparison.Ordinal);

        Assert.Contains("EnsureSearchServices();", enable, StringComparison.Ordinal);
        Assert.Contains("BeginSearchIndexPreload();", enable, StringComparison.Ordinal);

        int ensure = open.IndexOf("EnsureSearchServices();", StringComparison.Ordinal);
        int create = open.IndexOf("CreateSearchPopupWindow();", StringComparison.Ordinal);
        int show = open.IndexOf("popup.ShowPopup();", StringComparison.Ordinal);
        Assert.InRange(ensure, 0, create - 1);
        Assert.InRange(create, ensure + 1, show - 1);
        Assert.True(show > create);
        Assert.Contains("StartCustomIndexingAsync(cancellationToken)", preload, StringComparison.Ordinal);

        Assert.DoesNotContain("ScheduleSearchPopupShellWarmup", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchPopupShellWarmupDelay", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ScheduleSearchPopupIdleCleanup", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ScheduleSearchIndexIdleUnload", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryUnloadCustomIndexForIdleAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitIndexMaintenance_CanActivateHeavyRuntimeWithoutSettingsPageWarmup()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string settings = Read(
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml.cs");

        string maintenance = ExtractMethod(
            app,
            "internal SearchEngineService? EnsureSearchServicesForUserAction()");
        string loaded = ExtractMethod(
            settings,
            "private void OnLoaded(object sender, RoutedEventArgs e)");
        string rebuild = ExtractMethod(
            settings,
            "private void IndexRebuildButton_Click(object sender, RoutedEventArgs e)");
        string noiseToggle = ExtractMethod(
            settings,
            "private void SearchSystemNoiseToggle_Toggled(object sender, RoutedEventArgs e)");
        Assert.Contains("EnsureSearchServices();", maintenance, StringComparison.Ordinal);
        Assert.Contains("BeginSearchIndexPreload();", maintenance, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureSearchServicesForUserAction", loaded, StringComparison.Ordinal);
        Assert.Contains("EnsureSearchEngineForUserAction()", rebuild, StringComparison.Ordinal);
        Assert.Contains("EnsureSearchEngineForUserAction()", noiseToggle, StringComparison.Ordinal);
        Assert.Contains("RebuildIndex()", noiseToggle, StringComparison.Ordinal);
    }

    [Fact]
    public void DisablingSearch_ReleasesLightweightAndHeavyResources()
    {
        string source = Read("src/DeskBox/App.xaml.cs");
        string featureToggle = ExtractMethod(
            source,
            "internal void SetSearchFeatureEnabled(bool enabled)");
        string dispose = ExtractMethod(source, "private void DisposeSearchServices()");

        Assert.Contains("DisposeSearchServices();", featureToggle, StringComparison.Ordinal);
        Assert.Contains("_searchIndexLifecycleCts?.Cancel();", dispose, StringComparison.Ordinal);
        Assert.Contains("popup.Close();", dispose, StringComparison.Ordinal);
        Assert.Contains("_fileMetaService?.Dispose();", dispose, StringComparison.Ordinal);
        Assert.Contains("_searchHotkeyService?.Dispose();", dispose, StringComparison.Ordinal);
        Assert.Contains("_searchEngineService.Dispose();", dispose, StringComparison.Ordinal);
        Assert.Contains("_searchIndexService = null;", dispose, StringComparison.Ordinal);
        Assert.DoesNotContain("_usnIndexService", dispose, StringComparison.Ordinal);
        Assert.Contains("_searchHistoryService = null;", dispose, StringComparison.Ordinal);
        Assert.Contains("_searchActionService = null;", dispose, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledSearchHotkey_DoesNotKeepAWindowSubclassInstalled()
    {
        string source = Read("src/DeskBox/Services/SearchHotkeyService.cs");
        string attach = ExtractMethod(source, "public void Attach(IntPtr windowHandle)");
        string refresh = ExtractMethod(source, "public void RefreshRegistration()");

        Assert.DoesNotContain("SetWindowSubclass", attach, StringComparison.Ordinal);
        int disabled = refresh.IndexOf(
            "!_settingsService.Settings.SearchHotkeyEnabled",
            StringComparison.Ordinal);
        int remove = refresh.IndexOf("RemoveSubclass();", StringComparison.Ordinal);
        int install = refresh.IndexOf("Win32Helper.SetWindowSubclass(", StringComparison.Ordinal);
        Assert.InRange(disabled, 0, remove - 1);
        Assert.InRange(remove, disabled + 1, install - 1);
    }

    private static string ExtractMethod(string source, string signature)
    {
        int signatureStart = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureStart >= 0, $"Method signature not found: {signature}");

        int bodyStart = source.IndexOf('{', signatureStart);
        Assert.True(bodyStart >= 0, $"Method body not found: {signature}");

        int depth = 0;
        bool inString = false;
        bool inVerbatimString = false;
        bool inCharacter = false;
        bool inLineComment = false;
        bool inBlockComment = false;
        for (int index = bodyStart; index < source.Length; index++)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\n')
                {
                    inLineComment = false;
                }
                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }

            if (inString)
            {
                if (inVerbatimString)
                {
                    if (current == '"' && next == '"')
                    {
                        index++;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                        inVerbatimString = false;
                    }
                }
                else if (current == '\\')
                {
                    index++;
                }
                else if (current == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (inCharacter)
            {
                if (current == '\\')
                {
                    index++;
                }
                else if (current == '\'')
                {
                    inCharacter = false;
                }
                continue;
            }

            if (current == '/' && next == '/')
            {
                inLineComment = true;
                index++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                inVerbatimString = index > 0 && source[index - 1] == '@';
                continue;
            }

            if (current == '\'')
            {
                inCharacter = true;
                continue;
            }

            if (current == '{')
            {
                depth++;
            }
            else if (current == '}' && --depth == 0)
            {
                return source[signatureStart..(index + 1)];
            }
        }

        throw new Xunit.Sdk.XunitException($"Unterminated method body: {signature}");
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
