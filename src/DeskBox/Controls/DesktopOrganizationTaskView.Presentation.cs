using DeskBox.Models;
using DeskBox.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

public sealed partial class DesktopOrganizationTaskView
{
    private readonly List<FrameworkElement> _targetCards = [];
    private readonly Dictionary<string, DesktopOrganizationTargetSelection> _targetSelections =
        new(StringComparer.Ordinal);

    private const double TargetCardWidth = 260;
    private const double TargetCardHeight = 374;
    private const double TargetCardGap = 12;

    private void RenderPlan(DesktopOrganizationPlan plan)
    {
        var previousSelections = _targetSelections.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
        TargetRows.Children.Clear();
        TargetRows.ColumnDefinitions.Clear();
        TargetRows.RowDefinitions.Clear();
        _targetCards.Clear();
        _targetSelections.Clear();
        StoragePathText.Text = plan.StorageRootPath;

        foreach (DesktopOrganizationTargetPlan target in plan.Targets)
        {
            DesktopOrganizationTargetSelection selection =
                previousSelections.TryGetValue(
                    target.SourceBucketId,
                    out DesktopOrganizationTargetSelection? previous)
                    ? previous
                    : new DesktopOrganizationTargetSelection
            {
                SourceBucketId = target.SourceBucketId,
                IsSelected = true,
                DestinationMode = target.CreatesWidget
                    ? DesktopOrganizationDestinationMode.Dynamic
                    : DesktopOrganizationDestinationMode.ExistingWidget,
                ExistingWidgetId = target.CreatesWidget ? null : target.TargetWidgetId
            };
            _targetSelections[target.SourceBucketId] = selection;
            FrameworkElement card = CreateTargetCard(target);
            _targetCards.Add(card);
            TargetRows.Children.Add(card);
        }

        UpdateSummary(plan);
        RenderExcludedItems(plan);
        LayoutTargetCards();
    }

    private void RenderExcludedItems(DesktopOrganizationPlan plan)
    {
        IReadOnlyList<DesktopOrganizationFileSnapshot> sourceExcluded =
            _basePlan?.ExcludedItems ?? plan.ExcludedItems;
        int optionalCount = sourceExcluded.Count(item => item.CanOptIn);
        int userChoiceCount = sourceExcluded.Count(item =>
            item.ExclusionReason == DesktopOrganizationExclusionReason.UserChoice);
        int protectedCount = sourceExcluded.Count(item =>
            !item.CanOptIn &&
            item.ExclusionReason != DesktopOrganizationExclusionReason.UserChoice);
        int retainedCount = plan.ExcludedItems.Count + _runtimeRetainedItems.Count;
        bool hasRetainedItems = sourceExcluded.Count > 0 ||
            _runtimeRetainedItems.Count > 0;
        ExcludedItemsInfo.IsOpen = hasRetainedItems;
        if (hasRetainedItems)
        {
            ExcludedItemsButton.Content = Format(
                "DesktopOrganization.Preview.ExcludedHeader",
                retainedCount);
            ExcludedItemsInfo.Title = Format(
                "DesktopOrganization.Preview.RetainedTitle",
                retainedCount);
            ExcludedItemsInfo.Message = _hasCompletedExecution
                ? T("DesktopOrganization.Result.RetainedHelp")
                : Format(
                    "DesktopOrganization.Preview.RetainedDescription",
                    optionalCount,
                    _optionalIncludedPaths.Count,
                    protectedCount,
                    userChoiceCount);
        }
    }

    private async void ExcludedItemsButton_Click(object sender, RoutedEventArgs e)
    {
        DesktopOrganizationPlan? sourcePlan = _basePlan ?? _plan;
        if (sourcePlan is null ||
            sourcePlan.ExcludedItems.Count == 0 && _runtimeRetainedItems.Count == 0 ||
            XamlRoot is null)
        {
            return;
        }

        var rows = new StackPanel { Spacing = 12 };
        var optionalChecks = new Dictionary<string, CheckBox>(
            StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<DesktopOrganizationExclusionReason, DesktopOrganizationFileSnapshot> group in
                 sourcePlan.ExcludedItems.GroupBy(item => item.ExclusionReason).OrderBy(group => group.Key))
        {
            var groupRows = new StackPanel { Spacing = 6 };
            groupRows.Children.Add(new TextBlock
            {
                Text = Format(
                    "DesktopOrganization.Preview.ExcludedReason",
                    T($"DesktopOrganization.Exclusion.{group.Key}"),
                    group.Count()),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            foreach (DesktopOrganizationFileSnapshot item in group.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var itemDetails = new StackPanel { Spacing = 1 };
                itemDetails.Children.Add(new TextBlock
                {
                    Text = item.Name,
                    TextWrapping = TextWrapping.Wrap
                });
                var pathText = new TextBlock
                {
                    Text = item.SourcePath,
                    Style = (Style)Resources["DesktopOrganizationSecondaryTextStyle"],
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                ToolTipService.SetToolTip(pathText, item.SourcePath);
                itemDetails.Children.Add(pathText);
                if (item.CanOptIn && !_hasCompletedExecution)
                {
                    var checkBox = new CheckBox
                    {
                        Content = itemDetails,
                        IsChecked = _optionalIncludedPaths.Contains(item.SourcePath),
                        Padding = new Thickness(8, 4, 8, 4),
                        HorizontalAlignment = HorizontalAlignment.Stretch
                    };
                    optionalChecks[item.SourcePath] = checkBox;
                    groupRows.Children.Add(checkBox);
                }
                else
                {
                    var itemRow = new Grid
                    {
                        ColumnSpacing = 8,
                        Padding = new Thickness(8, 4, 8, 4)
                    };
                    itemRow.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = GridLength.Auto
                    });
                    itemRow.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(1, GridUnitType.Star)
                    });
                    itemRow.Children.Add(new FontIcon
                    {
                        Glyph = item.CanOptIn ||
                            item.ExclusionReason == DesktopOrganizationExclusionReason.UserChoice
                                ? "\uE73A"
                                : "\uE72E",
                        FontSize = 13,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 4, 0, 0)
                    });
                    Grid.SetColumn(itemDetails, 1);
                    itemRow.Children.Add(itemDetails);
                    groupRows.Children.Add(itemRow);
                }
            }

            rows.Children.Add(groupRows);
        }

        if (_runtimeRetainedItems.Count > 0)
        {
            var runtimeRows = new StackPanel { Spacing = 6 };
            runtimeRows.Children.Add(new TextBlock
            {
                Text = T("DesktopOrganization.Result.RetainedDuringRun"),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            foreach (DesktopOrganizationRetainedItem item in _runtimeRetainedItems)
            {
                var retainedText = new TextBlock
                {
                    Text = $"{item.Name} · {T($"DesktopOrganization.Retention.{item.Reason}")}",
                    TextWrapping = TextWrapping.Wrap
                };
                ToolTipService.SetToolTip(retainedText, item.Detail);
                runtimeRows.Children.Add(retainedText);
            }

            rows.Children.Add(runtimeRows);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Format(
                "DesktopOrganization.Preview.ExcludedHeader",
                sourcePlan.ExcludedItems.Count + _runtimeRetainedItems.Count),
            Content = new ScrollViewer
            {
                Content = rows,
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            },
            PrimaryButtonText = optionalChecks.Count > 0
                ? T("DesktopOrganization.Preview.ApplyOptionalSelection")
                : string.Empty,
            CloseButtonText = T("DesktopOrganization.Window.Done"),
            DefaultButton = optionalChecks.Count > 0
                ? ContentDialogButton.Primary
                : ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _optionalIncludedPaths.Clear();
            foreach ((string path, CheckBox checkBox) in optionalChecks)
            {
                if (checkBox.IsChecked == true)
                {
                    _optionalIncludedPaths.Add(path);
                }
            }

            try
            {
                DesktopOrganizationPlan updated = CreateCoordinator()
                    .CreatePreviewPlanWithOptionalItems(
                        sourcePlan,
                        _optionalIncludedPaths);
                _plan = updated;
                RenderPlan(updated);
            }
            catch (Exception ex)
            {
                App.Log($"[DesktopOrganization] Optional preview failed: {ex}");
                ResultInfo.Severity = InfoBarSeverity.Error;
                ResultInfo.Title = T("DesktopOrganization.Result.FailedTitle");
                ResultInfo.Message = T("DesktopOrganization.Result.FailedBody");
                ResultInfo.IsOpen = true;
            }
        }
    }

    private FrameworkElement CreateTargetCard(DesktopOrganizationTargetPlan target)
    {
        var card = new Border
        {
            Width = TargetCardWidth,
            Height = TargetCardHeight,
            Style = (Style)Resources["DesktopOrganizationTargetCardStyle"],
            Tag = target.SourceBucketId
        };

        var layout = new Grid { RowSpacing = 8 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identity = new StackPanel { Spacing = 2 };
        identity.Children.Add(new TextBlock
        {
            Text = target.SuggestedDisplayName,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(identity, 0);
        header.Children.Add(identity);
        var count = new TextBlock
        {
            Text = Format("DesktopOrganization.Preview.ItemCount", target.Items.Count),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Resources["DesktopOrganizationSecondaryTextStyle"],
            FontSize = 12
        };
        Grid.SetColumn(count, 1);
        header.Children.Add(count);
        Grid.SetRow(header, 0);
        layout.Children.Add(header);

        var itemScroll = new ScrollViewer
        {
            Height = 210,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            IsVerticalScrollChainingEnabled = false,
            Content = CreateIconPreview(target.Items)
        };
        Grid.SetRow(itemScroll, 1);
        layout.Children.Add(itemScroll);

        var destinationPath = new TextBlock
        {
            Text = target.TargetDirectoryPath,
            Style = (Style)Resources["DesktopOrganizationSecondaryTextStyle"],
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        ToolTipService.SetToolTip(destinationPath, target.TargetDirectoryPath);
        Grid.SetRow(destinationPath, 2);
        layout.Children.Add(destinationPath);

        var footer = new Grid { ColumnSpacing = 7 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var selection = _targetSelections[target.SourceBucketId];
        var checkBox = new CheckBox
        {
            IsChecked = selection.IsSelected,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
            Padding = new Thickness(0),
            FontSize = 12,
            Width = 28
        };
        AutomationProperties.SetName(
            checkBox,
            T("DesktopOrganization.Preview.SelectTarget"));
        checkBox.Checked += (_, _) =>
        {
            selection.IsSelected = true;
            ApplyCardState(card, selection);
            UpdateSummary(_plan);
        };
        checkBox.Unchecked += (_, _) =>
        {
            selection.IsSelected = false;
            ApplyCardState(card, selection);
            UpdateSummary(_plan);
        };
        footer.Children.Add(checkBox);

        var destinationCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            PlaceholderText = T("DesktopOrganization.Preview.DestinationPlaceholder"),
            FontSize = 14,
            MinWidth = 0
        };
        ToolTipService.SetToolTip(
            destinationCombo,
            T("DesktopOrganization.Preview.DestinationHelp"));
        DesktopOrganizationDestinationOption? currentDestination = null;
        if (target.CreatesWidget)
        {
            destinationCombo.Items.Add(new ComboBoxItem
            {
                Content = T("DesktopOrganization.Preview.DynamicDestination"),
                Tag = null
            });
        }
        else
        {
            currentDestination = new DesktopOrganizationDestinationOption(
                target.TargetWidgetId,
                target.SuggestedDisplayName,
                target.TargetDirectoryPath,
                IsDynamic: false);
            destinationCombo.Items.Add(new ComboBoxItem
            {
                Content = Format(
                    "DesktopOrganization.Preview.CurrentRuleDestination",
                    target.SuggestedDisplayName),
                Tag = currentDestination
            });
        }

        foreach (DesktopOrganizationDestinationOption option in CreateDestinationOptions()
                     .Where(option => currentDestination is null ||
                         !string.Equals(option.Id, currentDestination.Id, StringComparison.Ordinal)))
        {
            var item = new ComboBoxItem
            {
                Content = option.DisplayName,
                Tag = option
            };
            ToolTipService.SetToolTip(item, option.DirectoryPath);
            destinationCombo.Items.Add(item);
        }
        int selectedIndex = 0;
        if (target.CreatesWidget &&
            selection.DestinationMode == DesktopOrganizationDestinationMode.ExistingWidget &&
            !string.IsNullOrWhiteSpace(selection.ExistingWidgetId))
        {
            for (int index = 0; index < destinationCombo.Items.Count; index++)
            {
                if (destinationCombo.Items[index] is ComboBoxItem comboItem &&
                    comboItem.Tag is DesktopOrganizationDestinationOption option &&
                    string.Equals(option.Id, selection.ExistingWidgetId, StringComparison.Ordinal))
                {
                    selectedIndex = index;
                    break;
                }
            }
        }
        destinationCombo.SelectedIndex = selectedIndex;
        destinationCombo.SelectionChanged += (_, _) =>
        {
            if (destinationCombo.SelectedItem is not ComboBoxItem comboItem)
            {
                return;
            }

            if (comboItem.Tag is DesktopOrganizationDestinationOption option)
            {
                selection.DestinationMode = DesktopOrganizationDestinationMode.ExistingWidget;
                selection.ExistingWidgetId = option.Id;
                destinationPath.Text = option.DirectoryPath;
            }
            else
            {
                selection.DestinationMode = DesktopOrganizationDestinationMode.Dynamic;
                selection.ExistingWidgetId = null;
                destinationPath.Text = target.TargetDirectoryPath;
            }

            UpdateSummary(_plan);
        };
        Grid.SetColumn(destinationCombo, 1);
        footer.Children.Add(destinationCombo);
        Grid.SetRow(footer, 3);
        layout.Children.Add(footer);

        card.Child = layout;
        ApplyCardState(card, selection);
        return card;
    }

    private IReadOnlyList<DesktopOrganizationDestinationOption> CreateDestinationOptions()
    {
        try
        {
            return CreateCoordinator().GetDestinationOptions();
        }
        catch
        {
            return [];
        }
    }

    private Grid CreateIconPreview(IReadOnlyList<DesktopOrganizationFileSnapshot> items)
    {
        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 7 };
        const int columns = 4;
        for (int index = 0; index < columns; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int index = 0; index < items.Count; index++)
        {
            DesktopOrganizationFileSnapshot item = items[index];
            int row = index / columns;
            while (grid.RowDefinitions.Count <= row)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var tile = new StackPanel
            {
                Spacing = 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                MinHeight = 62
            };
            var iconHost = new Grid
            {
                Width = 36,
                Height = 36,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var nativeIcon = new Image
            {
                Width = 36,
                Height = 36,
                Stretch = Stretch.Uniform,
                Visibility = Visibility.Collapsed
            };
            var fallbackIcon = new FontIcon
            {
                Glyph = GetFileGlyph(item),
                FontSize = 30,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconHost.Children.Add(nativeIcon);
            iconHost.Children.Add(fallbackIcon);
            tile.Children.Add(iconHost);
            tile.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                Height = 26,
                VerticalAlignment = VerticalAlignment.Top
            });
            Grid.SetColumn(tile, index % columns);
            Grid.SetRow(tile, row);
            grid.Children.Add(tile);
            _ = LoadPreviewIconAsync(item, nativeIcon, fallbackIcon);
        }

        if (items.Count == 0)
        {
            grid.Children.Add(new TextBlock
            {
                Text = App.Current.LocalizationService.T("DesktopOrganization.Preview.NoItems"),
                Style = (Style)Resources["DesktopOrganizationSecondaryTextStyle"],
                TextWrapping = TextWrapping.Wrap
            });
        }

        return grid;
    }

    private static async Task LoadPreviewIconAsync(
        DesktopOrganizationFileSnapshot item,
        Image nativeIcon,
        FontIcon fallbackIcon)
    {
        try
        {
            var icon = await IconHelper.GetIconAsync(
                item.SourcePath,
                hideShortcutArrowOverlay: false,
                showImageFilesAsIcons: false,
                decodePixelWidth: 64,
                cacheScope: "desktop-organization-preview");
            if (icon is null)
            {
                return;
            }

            nativeIcon.Source = icon;
            nativeIcon.Visibility = Visibility.Visible;
            fallbackIcon.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // The fallback glyph remains visible for offline, removed, or
            // temporarily inaccessible desktop items.
        }
    }

    private static string GetFileGlyph(DesktopOrganizationFileSnapshot item)
    {
        return item.ExclusionReason == DesktopOrganizationExclusionReason.None
            ? item.CategoryId switch
            {
                DesktopOrganizationCategoryIds.Images => "\uE91B",
                DesktopOrganizationCategoryIds.Media => "\uE714",
                DesktopOrganizationCategoryIds.Shortcuts => "\uE71B",
                DesktopOrganizationCategoryIds.Packages => "\uE7B8",
                _ => "\uE7C3"
            }
            : "\uE7BA";
    }

    private void ApplyCardState(
        Border card,
        DesktopOrganizationTargetSelection selection)
    {
        card.Opacity = selection.IsSelected ? 1 : 0.52;
        // Keep selection calm and Fluent-like. The checkbox carries the
        // accent state; the card itself uses the neutral stroke so a row of
        // selected cards does not become a wall of orange outlines.
    }

    private void UpdateSummary(DesktopOrganizationPlan? plan)
    {
        if (plan is null)
        {
            return;
        }

        int selectedItems = plan.Targets
            .Where(target => !_targetSelections.TryGetValue(target.SourceBucketId, out DesktopOrganizationTargetSelection? selection) || selection.IsSelected)
            .Sum(target => target.Items.Count);
        int selectedTargets = plan.Targets
            .Count(target => !_targetSelections.TryGetValue(target.SourceBucketId, out DesktopOrganizationTargetSelection? selection) || selection.IsSelected);
        int selectedNewTargets = plan.Targets
            .Where(target => !_targetSelections.TryGetValue(target.SourceBucketId, out DesktopOrganizationTargetSelection? selection) || selection.IsSelected)
            .Count(target => target.CreatesWidget &&
                (!_targetSelections.TryGetValue(target.SourceBucketId, out DesktopOrganizationTargetSelection? selection) ||
                 selection.DestinationMode == DesktopOrganizationDestinationMode.Dynamic));

        if (selectedItems == 0)
        {
            SummaryTitle.Text = T("DesktopOrganization.Preview.NothingSelectedTitle");
            SummaryDescription.Text = T("DesktopOrganization.Preview.NothingSelectedBody");
            ExecuteButton.Content = T("DesktopOrganization.Preview.NothingAction");
            ExecuteButton.IsEnabled = false;
            return;
        }

        int total = plan.EligibleItemCount + plan.ExcludedItems.Count;
        SummaryTitle.Text = Format(
            "DesktopOrganization.Preview.Headline",
            selectedItems,
            selectedTargets);
        SummaryDescription.Text = Format(
            "DesktopOrganization.Preview.TotalSummary",
            total,
            plan.ExcludedItems.Count,
            selectedNewTargets);
        ExecuteButton.Content = Format(
            "DesktopOrganization.Preview.Confirm",
            selectedItems);
        ExecuteButton.IsEnabled = true;
    }

    private void TargetRows_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTargetCards();

    private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTargetCards();

    private void PreviewScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTargetCards();

    private void LayoutTargetCards()
    {
        if (_targetCards.Count == 0)
        {
            return;
        }

        double viewportWidth = PreviewScrollViewer?.ViewportWidth ?? 0;
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = PreviewViewport?.ActualWidth ?? 0;
        }

        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            viewportWidth = TargetRows.ActualWidth;
        }

        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
        {
            return;
        }

        // Keep the rows constrained to the current viewport. Without this,
        // a vertical ScrollViewer can preserve the previous desired width,
        // leaving too many columns after the window is narrowed.
        if (!double.IsFinite(TargetRows.Width) || Math.Abs(TargetRows.Width - viewportWidth) > 0.5)
        {
            TargetRows.Width = viewportWidth;
        }

        double availableWidth = viewportWidth -
            TargetRows.Padding.Left -
            TargetRows.Padding.Right;
        int columns = Math.Max(
            1,
            Math.Min(
                _targetCards.Count,
                (int)Math.Floor((availableWidth + TargetCardGap) /
                    (TargetCardWidth + TargetCardGap))));
        TargetRows.ColumnDefinitions.Clear();
        TargetRows.RowDefinitions.Clear();
        for (int column = 0; column < columns; column++)
        {
            TargetRows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TargetCardWidth) });
        }

        int rows = (int)Math.Ceiling(_targetCards.Count / (double)columns);
        for (int row = 0; row < rows; row++)
        {
            TargetRows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int index = 0; index < _targetCards.Count; index++)
        {
            Grid.SetColumn(_targetCards[index], index % columns);
            Grid.SetRow(_targetCards[index], index / columns);
            _targetCards[index].Width = TargetCardWidth;
            _targetCards[index].Margin = new Thickness(
                0,
                0,
                0,
                TargetCardGap);
        }
    }
}
