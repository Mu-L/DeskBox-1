using System.Text.Json;

namespace DeskBox.Tests;

public sealed class SettingsCopyAndHierarchyTests
{
    [Fact]
    public void CapsuleModeAndWidgetGroups_AreNestedUnderAppearance()
    {
        string root = FindRepositoryRoot();
        string windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string appearanceXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml"));
        string overviewResources = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Styles/SettingsOverviewResources.xaml"));
        string capsuleXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml"));
        string routes = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml.cs"));

        int menuStart = windowXaml.IndexOf(
            "<NavigationView.MenuItems>",
            StringComparison.Ordinal);
        int menuEnd = windowXaml.IndexOf(
            "</NavigationView.MenuItems>",
            menuStart,
            StringComparison.Ordinal);
        Assert.True(menuStart >= 0 && menuEnd > menuStart);
        string primaryMenu = windowXaml[menuStart..menuEnd];

        Assert.DoesNotContain("Tag=\"CapsuleMode\"", primaryMenu, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag=\"WidgetGroups\"", primaryMenu, StringComparison.Ordinal);
        Assert.Contains("Tag=\"CapsuleMode\"", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetCapsuleModeEnabled", appearanceXaml, StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding AvailableWidgetCollapseBehaviorOptions}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedWidgetCollapseBehavior, Mode=TwoWay}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetCapsuleModeEnabled", capsuleXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Capsule.Enabled.Title", capsuleXaml, StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedWidgetCollapseBehavior, Mode=TwoWay}\"",
            capsuleXaml,
            StringComparison.Ordinal);
        Assert.Contains("Tag=\"WidgetGroups\"", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsWidgetGroupsEnabled", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsWidgetGroupsEnabled", windowXaml, StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding AvailableWidgetGroupNavigationStyleOptions}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedWidgetGroupDefaultNavigationStyle, Mode=TwoWay}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SettingValueTextStyle\"", overviewResources, StringComparison.Ordinal);
        Assert.Contains("ExistingWidgetGroupItems", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.WidgetGroups.Existing.Name.Title", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetGroupNameTextBox_LostFocus", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Section.CapsuleMode", capsuleXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.WidgetGroups.PageDescription", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.WidgetGroups.Default.Title", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.WidgetGroups.Default.Description", windowXaml, StringComparison.Ordinal);
        Assert.Contains("WidgetGroupNavigationComboBox_SelectionChanged", windowXaml, StringComparison.Ordinal);
        Assert.Contains("DissolveWidgetGroupButton_Click", windowXaml, StringComparison.Ordinal);
        int accentColor = appearanceXaml.IndexOf("Settings.Accent.Source.Title", StringComparison.Ordinal);
        int capsuleMode = appearanceXaml.IndexOf("Tag=\"CapsuleMode\"", StringComparison.Ordinal);
        int widgetGroups = appearanceXaml.IndexOf("Tag=\"WidgetGroups\"", StringComparison.Ordinal);
        int material = appearanceXaml.IndexOf("Tag=\"AppearanceMaterialSettings\"", StringComparison.Ordinal);
        Assert.True(accentColor < capsuleMode);
        Assert.True(capsuleMode < widgetGroups);
        Assert.True(widgetGroups < material);
        Assert.Contains(
            "[\"CapsuleMode\"] = new(\"CapsuleMode\", \"Settings.Section.CapsuleMode\", \"Appearance\", \"Appearance\")",
            routes,
            StringComparison.Ordinal);
        Assert.Contains(
            "[\"WidgetGroups\"] = new(\"WidgetGroups\", \"Settings.Section.WidgetGroups\", \"Appearance\", \"Appearance\")",
            routes,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OutcomeBasedSettings_UseDirectChoicesInsteadOfAmbiguousMasterSwitches()
    {
        string root = FindRepositoryRoot();
        string windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));
        string appearanceXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml"));
        string fileWidgetXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml"));
        string routes = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml.cs"));

        Assert.Contains("SelectedAccentColorSource", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding UseSystemAccentColor", appearanceXaml, StringComparison.Ordinal);

        Assert.Contains("SelectedFileOpenMethod", windowXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedShowDesktopBehavior", windowXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedWeatherLocationMode", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding DoubleClickToOpen", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding KeepWidgetsVisibleOnShowDesktop", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding WeatherAutoLocation", windowXaml, StringComparison.Ordinal);

        Assert.Contains("SelectedFileStackMode", windowXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedFileStackMode", fileWidgetXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding FileStacksEnabled", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding FileStacksEnabled", fileWidgetXaml, StringComparison.Ordinal);

        Assert.Contains("HoverButtonActionsSummaryText", windowXaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"HoverButtonActionsDropDown_Click\"", windowXaml, StringComparison.Ordinal);
        Assert.Equal(
            1,
            windowXaml.Split(
                "Click=\"HoverButtonActionsDropDown_Click\"",
                StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("InteractionHoverSettings", windowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("InteractionHoverSettings", routes, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding ShowHoverButtons", windowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedChineseCopy_UsesPreciseTerms()
    {
        string root = FindRepositoryRoot();
        using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Strings/zh-CN.json")));

        IReadOnlyDictionary<string, string> expected = new Dictionary<string, string>
        {
            ["Settings.Nav.CapsuleMode"] = "胶囊模式",
            ["Settings.CollapseBehavior.Title"] = "默认显示方式",
            ["Settings.CollapseBehavior.Expanded"] = "始终展开",
            ["Widget.CollapseBehavior.Title"] = "显示方式",
            ["Widget.CollapseBehavior.System"] = "跟随默认",
            ["Widget.CollapseBehavior.Click"] = "点击展开",
            ["Widget.CollapseBehavior.Smart"] = "悬停自动展开",
            ["Settings.Capsule.Overrides.FollowGlobal"] = "恢复默认",
            ["Widget.OpenStorageFolder"] = "打开格子文件夹",
            ["Widget.ShowInExplorer"] = "在文件资源管理器中显示",
            ["Search.Menu.AttachToTodo"] = "添加到待办",
            ["QuickCapture.SaveToRecords"] = "保存为随记",
            ["QuickCapture.PinToRecords"] = "固定这条随记",
            ["Widget.Stack.FollowDefaults"] = "跟随默认设置",
            ["Widget.Stack.DisableGroup"] = "取消此分组",
            ["Settings.FileStacks.Threshold.Title"] = "自动叠放数量",
            ["Widget.Group.NavigationStyle"] = "标题栏布局",
            ["Settings.WidgetGroupNavigation.Stack"] = "折叠显示",
            ["Settings.WidgetGroupNavigation.Tabs"] = "并排显示",
            ["Settings.WidgetGroupNavigation.Title"] = "标题栏布局",
            ["Settings.WidgetGroups.PageDescription"] = "将多个格子放在同一位置，通过标题栏切换。每个格子的内容仍然相互独立。",
            ["Settings.WidgetGroups.FollowDefaultWithValue"] = "跟随默认（{0}）",
            ["Widget.Group.Join"] = "组合格子…",
            ["Widget.DeleteFolderToRecycleBin"] = "同时移入回收站",
            ["Search.Delete.Action"] = "移入回收站",
            ["Settings.QuickCapture.Format.Title"] = "编辑格式",
            ["Settings.QuickCapture.Format.Description"] = "选择随记编辑器使用 Markdown 或纯文本",
            ["Settings.Accent.Source.Title"] = "主题色来源",
            ["Settings.OpenMethod.Title"] = "打开方式",
            ["Settings.ShowDesktopBehavior.Title"] = "显示桌面时",
            ["Settings.Weather.LocationMode.Title"] = "位置来源",
            ["Settings.FileStacks.Mode.Title"] = "自动归组",
            ["Settings.HoverButtonActions.None"] = "不显示"
        };

        foreach ((string key, string value) in expected)
        {
            Assert.Equal(value, strings.RootElement.GetProperty(key).GetString());
        }

        string json = strings.RootElement.GetRawText();
        Assert.DoesNotContain("...", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Deskbox", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AppearanceSummary_UsesTitleIconSelector()
    {
        string root = FindRepositoryRoot();
        string appearanceXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml"));

        Assert.Contains(
            "Text=\"{Binding SelectedWidgetTitleIconModeText}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding AvailableWidgetTitleIconModeOptions}\"",
            appearanceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedWidgetTitleIconMode, Mode=TwoWay}\"",
            appearanceXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsOverview_PrioritizesOrganizationAndWidgetLayer()
    {
        string root = FindRepositoryRoot();
        string fileWidgetXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml"));
        string windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));

        Assert.True(
            fileWidgetXaml.IndexOf("Tag=\"DesktopOrganizationSettings\"", StringComparison.Ordinal) <
            fileWidgetXaml.IndexOf("Tag=\"FileDisplaySettings\"", StringComparison.Ordinal));
        Assert.Contains("Click=\"OrganizeDesktopButton_Click\"", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("DesktopOrganization.Settings.StartAction", fileWidgetXaml, StringComparison.Ordinal);

        int interactionSection = windowXaml.IndexOf(
            "x:Name=\"InteractionSection\"",
            StringComparison.Ordinal);
        int widgetLayer = windowXaml.IndexOf(
            "svc:Localized.HeaderKey=\"Settings.WidgetLayerMode.Title\"",
            interactionSection,
            StringComparison.Ordinal);
        int globalHotkey = windowXaml.IndexOf(
            "Tag=\"InteractionHotkeySettings\"",
            interactionSection,
            StringComparison.Ordinal);
        int interactionDetail = windowXaml.IndexOf(
            "x:Name=\"InteractionHotkeySettingsSection\"",
            interactionSection,
            StringComparison.Ordinal);

        Assert.True(interactionSection >= 0);
        Assert.True(widgetLayer > interactionSection);
        Assert.True(widgetLayer < globalHotkey);
        Assert.True(globalHotkey < interactionDetail);
        Assert.Equal(
            1,
            windowXaml.Split("Settings.WidgetLayerMode.Title", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void WidgetCreationEntries_AreAvailableFromTrayAndFileWidgetTitleBar()
    {
        string root = FindRepositoryRoot();
        string traySource = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/App.Tray.cs"));
        string widgetCommands = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));

        int trayMapEntry = traySource.IndexOf(
            "contextMenu.Items.Add(mapFolderItem)",
            StringComparison.Ordinal);
        int trayFeatureEntry = traySource.IndexOf(
            "contextMenu.Items.Add(addFeatureWidgetItem)",
            StringComparison.Ordinal);

        Assert.True(trayMapEntry >= 0);
        Assert.True(trayFeatureEntry > trayMapEntry);
        Assert.Contains("OpenFeatureWidgetsFromTray", traySource, StringComparison.Ordinal);
        Assert.Contains("ShowSettings(\"FeatureWidgets\")", traySource, StringComparison.Ordinal);

        Assert.Contains("_config.WidgetKind == WidgetKind.File", widgetCommands, StringComparison.Ordinal);
        Assert.Contains("Common.NewWidget", widgetCommands, StringComparison.Ordinal);
        Assert.Contains("Common.NewFolderMapping", widgetCommands, StringComparison.Ordinal);
        Assert.Contains("Common.AddFeatureWidget", widgetCommands, StringComparison.Ordinal);
        Assert.Contains("CreateFolderWidgetFromPickerAsync", widgetCommands, StringComparison.Ordinal);
        Assert.Contains("ShowSettings(\"FeatureWidgets\")", widgetCommands, StringComparison.Ordinal);
    }

    [Fact]
    public void DropdownOptionCopy_DoesNotUseParentheticalBadges()
    {
        string root = FindRepositoryRoot();
        string stringsDirectory = Path.Combine(root, "src/DeskBox/Strings");
        string[] optionKeys =
        [
            "Settings.WidgetLayerMode.DesktopPinned",
            "Settings.CollapseBehavior.Click",
            "Settings.CollapseBehavior.Manual",
            "Settings.Capsule.WidthMode.Aligned",
            "Settings.CollapsedStyle.Smart",
            "Settings.CompactContent.Smart",
            "Settings.Capsule.Direction.Auto",
            "Settings.Capsule.Animation.Smooth",
            "Settings.Capsule.HoverResponse.Balanced",
            "Settings.Capsule.MediaCorner.FollowWidget",
            "Settings.Density.Standard",
            "Settings.Density.Custom",
            "Settings.Animation.Preset.Standard",
            "Settings.Animation.Preset.Custom",
            "Settings.WidgetGroupNavigation.Auto",
            "Settings.Accent.Source.System",
            "Settings.Accent.Source.Custom",
            "Settings.OpenMethod.SingleClick",
            "Settings.OpenMethod.DoubleClick",
            "Settings.ShowDesktopBehavior.KeepVisible",
            "Settings.ShowDesktopBehavior.HideWithWindows",
            "Settings.Weather.LocationMode.Auto",
            "Settings.Weather.LocationMode.Manual",
            "Settings.FileStacks.Mode.Off",
            "Settings.HoverButtonActions.None"
        ];

        foreach (string path in Directory.EnumerateFiles(stringsDirectory, "*.json"))
        {
            using JsonDocument strings = JsonDocument.Parse(File.ReadAllText(path));
            foreach (string optionKey in optionKeys)
            {
                string value = strings.RootElement.GetProperty(optionKey).GetString()!;
                Assert.DoesNotContain('(', value);
                Assert.DoesNotContain('（', value);
            }
        }
    }

    [Fact]
    public void QuickCaptureAndTodoSettings_UseTheSameCompactHierarchy()
    {
        string root = FindRepositoryRoot();
        string windowXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/SettingsWindow.xaml"));

        string quickCapture = SliceSection(
            windowXaml,
            "x:Name=\"QuickCaptureSettingsSection\"",
            "x:Name=\"TodoSettingsSection\"");
        string todo = SliceSection(
            windowXaml,
            "x:Name=\"TodoSettingsSection\"",
            "x:Name=\"MusicSettingsSection\"");

        Assert.Contains("IsOn=\"{Binding QuickCaptureEnabled, Mode=TwoWay}\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("IsOn=\"{Binding TodoEnabled, Mode=TwoWay}\"", todo, StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(quickCapture, "Loaded=\"FeatureSettingsExpander_Loaded\""));
        Assert.Equal(5, CountOccurrences(todo, "Loaded=\"FeatureSettingsExpander_Loaded\""));

        AssertInOrder(
            quickCapture,
            "Settings.QuickCapture.WideLayout.Title",
            "Settings.QuickCapture.Tabs.Title",
            "Settings.ContentEditor.Group.Title",
            "Settings.QuickCapture.ClipboardTitle",
            "Settings.QuickCapture.Group.Data.Title");
        AssertInOrder(
            todo,
            "Settings.QuickCapture.WideLayout.Title",
            "Settings.Todo.Tabs.Title",
            "Settings.ContentEditor.Group.Title",
            "Settings.Todo.ReminderEnabled.Title",
            "Settings.Todo.Group.FooterActions.Title");

        Assert.Contains("Click=\"QuickCaptureTabsDropDown_Click\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("Click=\"TodoTabsDropDown_Click\"", todo, StringComparison.Ordinal);
        Assert.Contains("Click=\"TodoFooterDisplayDropDown_Click\"", todo, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding VisibleQuickCaptureDefaultViewOptions}\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding VisibleTodoDefaultFilterOptions}\"", todo, StringComparison.Ordinal);

        Assert.DoesNotContain("IsOn=\"{Binding QuickCaptureShowRecordsTab", quickCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("IsOn=\"{Binding TodoShowAllTab", todo, StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding QuickCaptureEditorFormat, Mode=TwoWay}\"",
            quickCapture,
            StringComparison.Ordinal);
        Assert.DoesNotContain("QuickCaptureDefaultFormat", quickCapture, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCapturePreviewLineCount_IsBoundInBothWindowHosts()
    {
        string root = FindRepositoryRoot();
        string standaloneWindow = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml"));
        string sharedSurface = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));

        Assert.Contains(
            "MaxLines=\"{Binding ElementName=ItemsListView, Path=DataContext.ItemPreviewLineCount}\"",
            standaloneWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "MaxLines=\"{Binding ElementName=ItemsList, Path=DataContext.ItemPreviewLineCount}\"",
            sharedSurface,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TextSize}\" MaxLines=\"3\"",
            sharedSurface,
            StringComparison.Ordinal);
    }

    private static string SliceSection(string xaml, string startToken, string endToken)
    {
        int start = xaml.IndexOf(startToken, StringComparison.Ordinal);
        int end = xaml.IndexOf(endToken, start + startToken.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return xaml[start..end];
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static void AssertInOrder(string source, params string[] values)
    {
        int lastIndex = -1;
        foreach (string value in values)
        {
            int index = source.IndexOf(value, lastIndex + 1, StringComparison.Ordinal);
            Assert.True(index > lastIndex, $"Expected '{value}' after index {lastIndex}.");
            lastIndex = index;
        }
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
