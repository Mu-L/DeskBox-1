using DeskBox.Models;
using DeskBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class QuickCaptureContent
{
    private void OnQuickCaptureActualThemeChanged(FrameworkElement sender, object args)
    {
        ApplyNoteAppearance(_isCreating || _isEditing
            ? _editingAppearance
            : _selectedItem?.AppearancePreset ?? QuickCaptureAppearancePreset.Default);
    }

    private void ApplyNoteAppearance(QuickCaptureAppearancePreset preset)
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        DetailPane.Background = GetSharedSurfaceBrush(
            "CardBackgroundFillColorDefaultBrush",
            isDark
                ? ColorHelper.FromArgb(0xF2, 0x20, 0x20, 0x20)
                : ColorHelper.FromArgb(0xF7, 0xFF, 0xFF, 0xFF));
        DetailPane.BorderBrush = new SolidColorBrush(Colors.Transparent);

        DetailAppearanceTint.Visibility = preset == QuickCaptureAppearancePreset.Default
            ? Visibility.Collapsed
            : Visibility.Visible;
        DetailPaperInset.Visibility = preset == QuickCaptureAppearancePreset.Paper
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailPaperInset.Background = null;

        if (preset == QuickCaptureAppearancePreset.Default)
        {
            DetailAppearanceTint.Background = null;
            ApplyEditorSurfaceAppearance(preset, isDark);
            RefreshItemMaterialSurfaces();
            return;
        }

        DetailAppearanceTint.Background = CreateMaterialBrush(preset, isDark, forDetail: true);

        if (preset == QuickCaptureAppearancePreset.Paper)
        {
            DetailPaperInset.Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop
                    {
                        Color = isDark
                            ? ColorHelper.FromArgb(0x10, 0xFF, 0xF4, 0xD8)
                            : ColorHelper.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
                        Offset = 0
                    },
                    new GradientStop
                    {
                        Color = isDark
                            ? ColorHelper.FromArgb(0x08, 0x00, 0x00, 0x00)
                            : ColorHelper.FromArgb(0x0A, 0xA5, 0x81, 0x45),
                        Offset = 1
                    }
                }
            };
        }
        ApplyEditorSurfaceAppearance(preset, isDark);
        RefreshItemMaterialSurfaces();
    }

    private void QuickCaptureItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QuickCaptureItemViewModel item } root)
        {
            ApplyItemMaterialSurface(root, item);
        }
    }

    private void QuickCaptureItem_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (args.NewValue is QuickCaptureItemViewModel item)
        {
            ApplyItemMaterialSurface(sender, item);
        }
    }

    private static void ApplyItemMaterialSurface(
        DependencyObject itemRoot,
        QuickCaptureItemViewModel item)
    {
        if (FindNamedDescendant<Border>(itemRoot, "ItemMaterialBackground") is not { } surface)
        {
            return;
        }

        bool isDark = (itemRoot as FrameworkElement)?.ActualTheme == ElementTheme.Dark;
        QuickCaptureAppearancePreset preset = item.IsRecent
            ? QuickCaptureAppearancePreset.Default
            : item.AppearancePreset;
        surface.Background = CreateMaterialBrush(preset, isDark, forDetail: false);
        surface.BorderThickness = new Thickness(0);
        surface.BorderBrush = new SolidColorBrush(Colors.Transparent);
    }

    private void RefreshItemMaterialSurfaces()
    {
        foreach (QuickCaptureItemViewModel item in ViewModel.Items)
        {
            if (ItemsList.ContainerFromItem(item) is DependencyObject container)
            {
                ApplyItemMaterialSurface(container, item);
            }
        }
    }

    private static Brush CreateMaterialBrush(
        QuickCaptureAppearancePreset preset,
        bool isDark,
        bool forDetail)
    {
        if (preset == QuickCaptureAppearancePreset.Paper)
        {
            byte baseAlpha = forDetail ? (byte)0x78 : (byte)0x9A;
            byte edgeAlpha = forDetail ? (byte)0x68 : (byte)0x8A;
            return new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop
                    {
                        Color = isDark
                            ? ColorHelper.FromArgb(baseAlpha, 0x3D, 0x38, 0x31)
                            : ColorHelper.FromArgb(baseAlpha, 0xFA, 0xF5, 0xEA),
                        Offset = 0
                    },
                    new GradientStop
                    {
                        Color = isDark
                            ? ColorHelper.FromArgb(edgeAlpha, 0x2E, 0x2B, 0x27)
                            : ColorHelper.FromArgb(edgeAlpha, 0xED, 0xE3, 0xCF),
                        Offset = 1
                    }
                }
            };
        }

        Windows.UI.Color color = (preset, isDark) switch
        {
            (QuickCaptureAppearancePreset.StickyYellow, true) => ColorHelper.FromArgb(forDetail ? (byte)0x68 : (byte)0x92, 0x4A, 0x40, 0x25),
            (QuickCaptureAppearancePreset.StickyYellow, false) => ColorHelper.FromArgb(forDetail ? (byte)0x90 : (byte)0xC8, 0xFF, 0xF0, 0xB3),
            (QuickCaptureAppearancePreset.Rose, true) => ColorHelper.FromArgb(forDetail ? (byte)0x64 : (byte)0x92, 0x47, 0x2E, 0x38),
            (QuickCaptureAppearancePreset.Rose, false) => ColorHelper.FromArgb(forDetail ? (byte)0x8C : (byte)0xC8, 0xFC, 0xE3, 0xEA),
            (QuickCaptureAppearancePreset.Mint, true) => ColorHelper.FromArgb(forDetail ? (byte)0x64 : (byte)0x92, 0x28, 0x42, 0x35),
            (QuickCaptureAppearancePreset.Mint, false) => ColorHelper.FromArgb(forDetail ? (byte)0x8C : (byte)0xC8, 0xDD, 0xF3, 0xE3),
            (QuickCaptureAppearancePreset.MistBlue, true) => ColorHelper.FromArgb(forDetail ? (byte)0x64 : (byte)0x92, 0x2B, 0x3D, 0x53),
            (QuickCaptureAppearancePreset.MistBlue, false) => ColorHelper.FromArgb(forDetail ? (byte)0x8C : (byte)0xC8, 0xDF, 0xEC, 0xF8),
            _ => Colors.Transparent
        };
        return new SolidColorBrush(color);
    }

    private void ApplyEditorSurfaceAppearance(
        QuickCaptureAppearancePreset preset,
        bool isDark)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        EditorTitleTextBox.Background = transparent;
        EditorBodyTextBox.Background = transparent;
        EditorPreviewPane.Background = transparent;
        EditorPreviewPane.BorderBrush = transparent;
        FormattingToolbarHost.Background = transparent;
        FormattingToolbarHost.BorderBrush = transparent;
        UpdateEditorFieldFocusVisual(
            EditorTitleTextBox,
            EditorTitleTextBox.FocusState != FocusState.Unfocused);
        UpdateEditorFieldFocusVisual(
            EditorBodyTextBox,
            EditorBodyTextBox.FocusState != FocusState.Unfocused);
    }

    private static Brush GetSharedSurfaceBrush(
        string resourceKey,
        Windows.UI.Color fallbackColor)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out object? resource))
        {
            return resource switch
            {
                Brush brush => brush,
                Windows.UI.Color color => new SolidColorBrush(color),
                _ => new SolidColorBrush(fallbackColor)
            };
        }

        return new SolidColorBrush(fallbackColor);
    }

    private void UpdateNoteCommandAvailability()
    {
        bool hasSelectedItem = _selectedItem is not null;
        bool hasEditableNote = _selectedItem is { IsRecent: false };
        DetailCommandBar.Visibility = hasSelectedItem || _isEditing || _isCreating
            ? Visibility.Visible
            : Visibility.Collapsed;
        Visibility itemVisibility = hasSelectedItem
            ? Visibility.Visible
            : Visibility.Collapsed;
        Visibility editableVisibility = hasEditableNote
            ? Visibility.Visible
            : Visibility.Collapsed;
        EditButton.Visibility = hasEditableNote && !_isEditing
            ? Visibility.Visible
            : Visibility.Collapsed;
        EditorPreviewModeButton.Visibility = _isEditing
            ? Visibility.Visible
            : Visibility.Collapsed;
        PinButton.Visibility = itemVisibility;
        CopyButton.Visibility = itemVisibility;
        CopyMarkdownButton.Visibility = itemVisibility;
        ExportNoteButton.Visibility = itemVisibility;
        EditTagsButton.Visibility = editableVisibility;
        AppearanceButton.Visibility = hasEditableNote || _isCreating
            ? Visibility.Visible
            : Visibility.Collapsed;
        HistoryButton.Visibility = editableVisibility;
        ArchiveButton.Visibility = editableVisibility;
        DeleteNoteButton.Visibility = itemVisibility;
    }

    private void EditorField_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Control field)
        {
            UpdateEditorFieldFocusVisual(field, focused: true);
        }
    }

    private void EditorField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Control field)
        {
            UpdateEditorFieldFocusVisual(field, focused: false);
        }
    }

    private static void UpdateEditorFieldFocusVisual(Control field, bool focused)
    {
        field.BorderBrush = focused
            ? GetSharedSurfaceBrush(
                "AccentFillColorDefaultBrush",
                ColorHelper.FromArgb(0xFF, 0x00, 0x78, 0xD4))
            : new SolidColorBrush(Colors.Transparent);
    }
}
