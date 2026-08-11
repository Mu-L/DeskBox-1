namespace DeskBox.Tests;

public sealed class MarkdownAndSplitterContractTests
{
    [Fact]
    public void Foundation_UsesStableToolkitSplitterWithCompactGutterAndWideHitTarget()
    {
        string project = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"));
        string appXaml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml"));

        Assert.Contains("CommunityToolkit.WinUI.Controls.Sizers", project, StringComparison.Ordinal);
        Assert.Contains("WidgetMasterDetailSplitterStyle", appXaml, StringComparison.Ordinal);
        Assert.Contains("TargetType=\"Control\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SplitterHoverTrack\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"2\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"24\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("SplitterWidth = 8", File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MasterDetailLayoutPolicy.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Editor_PreservesUndoSelectionAndViewportAcrossFormattingCommands()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownSourceEditor.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownSourceEditor.xaml.cs"));

        Assert.Contains("IsDynamicOverflowEnabled=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"40\"", xaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Left\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin\" Value=\"0,-2,0,2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkdownFormattingSymbolIconStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("AllowFocusOnInteraction\" Value=\"False", xaml, StringComparison.Ordinal);
        Assert.Contains("<TranslateTransform Y=\"-7\" />", xaml, StringComparison.Ordinal);
        Assert.Contains("Padding=\"0,0,0,4\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<CommandBar.SecondaryCommands>", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StrikeButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TableButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PrepareEditorCommandViewport", code, StringComparison.Ordinal);
        Assert.Contains("RestoreEditorViewport", code, StringComparison.Ordinal);
        Assert.Contains("previous with", code, StringComparison.Ordinal);
        Assert.DoesNotContain("EditorTextBox.LostFocus +=", code, StringComparison.Ordinal);
        Assert.Contains("PointerPressedEvent", code, StringComparison.Ordinal);
        Assert.Contains("PointerReleasedEvent", code, StringComparison.Ordinal);
        Assert.Contains("TappedEvent", code, StringComparison.Ordinal);
        Assert.Contains("KeyUpEvent", code, StringComparison.Ordinal);
        Assert.Contains("RememberEditorViewport", code, StringComparison.Ordinal);
        Assert.Contains("_isEditorPointerActive", code, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged=\"EditorTextBox_SelectionChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("EditorTextBox.SelectedText = replacement", code, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueuePriority.Low", code, StringComparison.Ordinal);
        Assert.Contains("TryContinueMarkdownList", code, StringComparison.Ordinal);
        Assert.Contains("MarkdownEditCommandEngine.TryCreateEdit", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_DisablesHtmlAndBlocksRemoteImagesByDefault()
    {
        string service = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/MarkdownDocumentService.cs"));
        string reader = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownDocumentView.cs"));

        Assert.Contains(".DisableHtml()", service, StringComparison.Ordinal);
        Assert.Contains("new PropertyMetadata(false, OnDocumentPropertyChanged)", reader, StringComparison.Ordinal);
        Assert.Contains("IsAllowedLink", reader, StringComparison.Ordinal);
        Assert.Contains("AttachmentResolver", reader, StringComparison.Ordinal);
        Assert.Contains("UseInternalScrollViewer", reader, StringComparison.Ordinal);
        Assert.Contains("private readonly RichTextBlock _documentText", reader, StringComparison.Ordinal);
        Assert.Contains("_documentText.Blocks.Add", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("private readonly StackPanel _documentPanel", reader, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_UsesComfortableTypographyThatScalesWithTheSystemFontSize()
    {
        string reader = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/MarkdownDocumentView.cs"));
        string surface = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string surfaceCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains("BodyLineHeightRatio = 1.72", reader, StringComparison.Ordinal);
        Assert.Contains("TaskLineHeightRatio = 2.16", reader, StringComparison.Ordinal);
        Assert.Contains("HeadingLineHeightRatio = 1.42", reader, StringComparison.Ordinal);
        Assert.Contains("CodeLineHeightRatio = 1.60", reader, StringComparison.Ordinal);
        Assert.Contains("LineStackingStrategy.BlockLineHeight", reader, StringComparison.Ordinal);
        Assert.Contains("ListItemSpacing = 3", reader, StringComparison.Ordinal);
        Assert.Contains("Margin = new Thickness(0, 0, 2, 0)", reader, StringComparison.Ordinal);
        Assert.Contains("RenderTransform = new TranslateTransform { Y = 6 }", reader, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailBodyReaderSurface\"", surface, StringComparison.Ordinal);
        Assert.Contains("<Grid\n                            x:Name=\"DetailBodyReaderSurface\"", surface.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("DetailBodyReaderSurface.AddHandler", surfaceCode, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailBackColumn\"", surface, StringComparison.Ordinal);
        Assert.Contains("DetailBackColumn.Width = new GridLength(8)", surfaceCode, StringComparison.Ordinal);
        Assert.Contains("DetailBackColumn.Width = new GridLength(30)", surfaceCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SegmentedTabs_LeaveWidthCalculationToToolkitDuringResponsiveLayout()
    {
        string helper = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetSegmentedLayoutHelper.cs"));

        Assert.Contains("EqualPanel", helper, StringComparison.Ordinal);
        Assert.Contains("item.Width = double.NaN", helper, StringComparison.Ordinal);
        Assert.Contains("item.MaxWidth = double.PositiveInfinity", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("item.Width = itemWidth", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyEqualItemWidthsCore", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoSegmentedTabs_WaitForASafeLayoutSlotBeforeBecomingVisible()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs"));

        Assert.Contains("x:Name=\"TodoFilterSegmented\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Collapsed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"96\"", xaml, StringComparison.Ordinal);
        Assert.Contains("QueueTodoSegmentedRestore", code, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering", code, StringComparison.Ordinal);
        Assert.Contains("_todoSegmentedStableFrameCount < 3", code, StringComparison.Ordinal);
        Assert.Contains("WidgetSegmentedLayoutHelper.MinimumSafeWidth", code, StringComparison.Ordinal);
        Assert.Contains("CancelTodoSegmentedRestore", code, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupContentAnimation_HasACompletionFallback()
    {
        string shell = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));

        Assert.Contains("completionFallback", shell, StringComparison.Ordinal);
        Assert.Contains("profile.DurationMilliseconds + 250", shell, StringComparison.Ordinal);
        Assert.Contains("completionFallback.Tick", shell, StringComparison.Ordinal);
        Assert.Contains("completionFallback?.Stop()", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupContentSwitch_CannotBeCancelledAfterTheLivePresenterSwap()
    {
        string groups = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.Groups.cs"));

        int begin = groups.IndexOf(
            "preparation.BeginTransition()",
            StringComparison.Ordinal);
        int end = groups.IndexOf(
            "SaveWidgetGroupActiveMemberDeferred()",
            begin,
            StringComparison.Ordinal);
        Assert.True(begin >= 0 && end > begin);

        string committedTransaction = groups[begin..end];
        Assert.Contains("new CancellationTokenSource()", committedTransaction, StringComparison.Ordinal);
        Assert.Contains("CancellationToken.None", committedTransaction, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "request.CancellationToken.ThrowIfCancellationRequested()",
            committedTransaction,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_widgetGroupSwitchRequests.IsCurrent(request)",
            committedTransaction,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Todo_UsesSharedResponsiveSplitterAndMarkdownDetailControls()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string layout = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.MasterDetail.cs"));
        string detail = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DetailNotesAndSteps.cs"));

        Assert.Contains("<toolkit:GridSplitter", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetMasterDetailSplitterStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkdownDocumentView", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkdownSourceEditor", xaml, StringComparison.Ordinal);
        Assert.Contains("EnsureWideDetailSelection", layout, StringComparison.Ordinal);
        Assert.Contains("ViewModel?.LayoutPreference", layout, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailMetadataGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DetailMetadataGrid_SizeChanged", xaml, StringComparison.Ordinal);
        Assert.Contains("DetailMetadataColumn3", xaml, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(600)", detail, StringComparison.Ordinal);
        Assert.Contains("TryToggleTask", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCapture_UsesOneSharedSurfaceForStandaloneAndGroupedHosts()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string surface = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        string manager = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.cs"));
        string features = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Services/WidgetManager.FeatureWidgets.cs"));
        string transientState = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Models/WidgetMemberTransientStates.cs"));

        Assert.Contains("x:Name=\"PaneSplitter\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Margin=\"-3,0\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource WidgetMasterDetailSplitterStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:MarkdownDocumentView", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:MarkdownSourceEditor", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailMaterialSurface\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailDeleteButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailAddFileButton\"\n                            Grid.Row=\"2\"", xaml.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailMarkdownEditor\"", xaml, StringComparison.Ordinal);
        Assert.Contains("RightTapped=\"QuickCaptureItem_RightTapped\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MasterPaneWidthMetadataKey", surface, StringComparison.Ordinal);
        Assert.Contains("IWidgetAddActionContent", surface, StringComparison.Ordinal);
        Assert.Contains("DetailMarkdownView_TaskToggleRequested", surface, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(DetailAutoSaveDelayMs)", surface, StringComparison.Ordinal);
        Assert.Contains("DetailMarkdownEditor_EditorTextChanged", surface, StringComparison.Ordinal);
        Assert.Contains("BeginDetailEditing", surface, StringComparison.Ordinal);
        Assert.Contains("SaveDetailAsync(completeEditing: false)", surface, StringComparison.Ordinal);
        Assert.Contains("CreateAppearanceContextSubmenu", surface, StringComparison.Ordinal);
        Assert.Contains("RestorePendingDetailState", surface, StringComparison.Ordinal);
        Assert.Contains("SelectedDetailItemId", transientState, StringComparison.Ordinal);
        Assert.Contains("DetailDraft", transientState, StringComparison.Ordinal);
        Assert.Contains("WidgetKind.QuickCapture,\n                async request => await CreateContentWidgetFromConfigAsync", manager.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("CreateContentWidgetFromConfigAsync(config)", features, StringComparison.Ordinal);
        Assert.Contains("return FeatureWidgetSettings.IsFeatureWidget(kind);", features, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupedQuickCapture_UsesTheSameLayoutAndTabPreferences()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains("x:Name=\"QuickCaptureViewSegmented\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PaneSplitter\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkdownDocumentView", xaml, StringComparison.Ordinal);
        Assert.Contains("MarkdownSourceEditor", xaml, StringComparison.Ordinal);
        Assert.Contains("QuickCaptureWideLayoutSinglePane", code, StringComparison.Ordinal);
        Assert.Contains("QuickCaptureWideLayoutDualPane", code, StringComparison.Ordinal);
        Assert.Contains("WidgetSegmentedStyleHelper.Apply", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.TabStyle", code, StringComparison.Ordinal);
        Assert.Contains("Config.Metadata[MasterPaneWidthMetadataKey]", code, StringComparison.Ordinal);
        Assert.Contains("IWidgetResponsiveLayoutContent", code, StringComparison.Ordinal);
        Assert.Contains("IWidgetHostViewportContent", code, StringComparison.Ordinal);
        Assert.Contains("OnHostViewportSizeChanged", code, StringComparison.Ordinal);
        Assert.Contains("_hostViewportWidth", code, StringComparison.Ordinal);
        Assert.Contains("_isResponsiveLayoutTransitionActive = true;", code, StringComparison.Ordinal);
        Assert.Contains("if (!isCollapsing &&", code, StringComparison.Ordinal);
        Assert.Contains("_hostViewportWidth = targetContentWidth;", code, StringComparison.Ordinal);
        Assert.Contains("Width = targetContentWidth;", code, StringComparison.Ordinal);
        Assert.Contains("_isResponsiveLayoutTransitionActive ||", code, StringComparison.Ordinal);
        Assert.Contains("MasterColumn.MinWidth = 0;", code, StringComparison.Ordinal);
        Assert.Contains("DetailColumn.MinWidth = 0;", code, StringComparison.Ordinal);
        Assert.Contains("DetailColumn.Width = new GridLength(layout.DetailWidth);", code, StringComparison.Ordinal);

        string shell = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetShell.xaml.cs"));
        Assert.Contains(
            "NotifyHostedContentViewportSize(e.NewSize.Width, e.NewSize.Height)",
            shell,
            StringComparison.Ordinal);
        Assert.Contains(
            "viewportContent.OnHostViewportSizeChanged(width, height)",
            shell,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TodoAndQuickCapture_ReadSurfaces_LookEditableAndSupportDoubleClickEditing()
    {
        string todoXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string quickCaptureXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string quickCaptureCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        string todoCode = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DetailNotesAndSteps.cs"));

        Assert.Contains("x:Name=\"DetailNotesReaderHost\"", todoXaml, StringComparison.Ordinal);
        Assert.Contains("DetailNotesReaderHost.AddHandler", todoCode, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", todoCode, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource WidgetLayerFillSecondaryBrush}\"", todoXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailBodyReaderSurface\"", quickCaptureXaml, StringComparison.Ordinal);
        Assert.Contains("DetailBodyReaderSurface.AddHandler", quickCaptureCode, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", quickCaptureCode, StringComparison.Ordinal);
        Assert.Contains("Padding=\"4,0,0,0\"", quickCaptureXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailHeaderActions\"", quickCaptureXaml, StringComparison.Ordinal);
        Assert.Contains("SizeChanged=\"DetailHeader_SizeChanged\"", quickCaptureXaml, StringComparison.Ordinal);
        Assert.Contains("CompactDetailHeaderWidth = 300", quickCaptureCode, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(DetailHeaderActions, useTwoRows ? 1 : 0)", quickCaptureCode, StringComparison.Ordinal);
        Assert.Contains("_detailItem?.IsRecent == true", quickCaptureCode, StringComparison.Ordinal);
        Assert.Contains("BeginDetailEditing()", quickCaptureCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoAndQuickCapture_CardSurfacesShareTheCompactCornerRadius()
    {
        string todoXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml"));
        string quickCaptureXaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));

        Assert.DoesNotContain("WidgetCornerRadiusMedium", todoXaml, StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"DetailNotesReaderHost\"",
            todoXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"TodoDetailSelectionIndicator\"",
            todoXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Width=\"2\"\n                                    Margin=\"-8,-6\"\n                                    HorizontalAlignment=\"Left\"",
            todoXaml.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Name=\"DetailMaterialSurface\"",
            quickCaptureXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CornerRadius=\"8\"", quickCaptureXaml, StringComparison.Ordinal);
        Assert.True(
            todoXaml.Split("WidgetCornerRadiusSmall", StringSplitOptions.None).Length > 10,
            "Todo card, hover, selection, metadata, and note surfaces should share the compact radius.");
        Assert.True(
            quickCaptureXaml.Split("WidgetCornerRadiusSmall", StringSplitOptions.None).Length > 5,
            "Quick Capture add, list, selection, and detail surfaces should share the compact radius.");
        Assert.Contains(
            "Margin=\"0,2\"\n                            MinHeight=\"50\"\n                            Padding=\"8,6\"",
            todoXaml.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "MinHeight=\"42\"\n                        Padding=\"8,5\"",
            todoXaml.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "BorderBrush=\"Transparent\"\n                        BorderThickness=\"0\"",
            todoXaml.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "BorderBrush=\"Transparent\"\n                BorderThickness=\"0\"",
            quickCaptureXaml.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCapture_ViewChangesClearStaleDetailsAndReconcileAfterRefresh()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml"));
        string code = File.ReadAllText(TestPaths.FromRepository(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));

        Assert.Contains("Text=\"{Binding DetailNoSelectionText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PropertyChanged += ViewModel_PropertyChanged", code, StringComparison.Ordinal);
        Assert.Contains("nameof(QuickCaptureWidgetViewModel.ItemsViewTransitionToken)", code, StringComparison.Ordinal);
        Assert.Contains("ClearDetailForViewChange();", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.Items.Count > 0", code, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PropertyChanged -= ViewModel_PropertyChanged", code, StringComparison.Ordinal);
    }
}
