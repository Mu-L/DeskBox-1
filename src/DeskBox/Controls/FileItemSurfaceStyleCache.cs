using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls;

public enum FileItemSurfaceVisualState
{
    Normal,
    Hover,
    Pressed,
    DropTarget
}

/// <summary>
/// Shared, cached visual styling for file-item surfaces.
/// Hosts provide the theme and selection state; the cache keeps brush allocation
/// out of the pointer-event hot path for both standalone and grouped widgets.
/// </summary>
public sealed class FileItemSurfaceStyleCache
{
    private SolidColorBrush? _normalSurfaceBrush;
    private SolidColorBrush? _selectedSurfaceBrush;
    private SolidColorBrush? _hoverSurfaceBrush;
    private SolidColorBrush? _pressedSurfaceBrush;
    private SolidColorBrush? _selectedHoverSurfaceBrush;
    private SolidColorBrush? _dropTargetSurfaceBrush;
    private SolidColorBrush? _normalBorderBrush;
    private SolidColorBrush? _dropTargetBorderBrush;
    private bool? _isDark;
    private Windows.UI.Color? _accentColor;

    public void Apply(
        Border border,
        FileItemSurfaceVisualState state,
        ElementTheme theme,
        Windows.UI.Color accentColor,
        bool isSelected,
        bool isCut)
    {
        bool dark = theme == ElementTheme.Dark;
        EnsureBrushes(dark, accentColor);

        border.Background = state switch
        {
            FileItemSurfaceVisualState.DropTarget => _dropTargetSurfaceBrush,
            FileItemSurfaceVisualState.Hover when isSelected => _selectedHoverSurfaceBrush,
            FileItemSurfaceVisualState.Pressed when isSelected => _selectedHoverSurfaceBrush,
            FileItemSurfaceVisualState.Hover => _hoverSurfaceBrush,
            FileItemSurfaceVisualState.Pressed => _pressedSurfaceBrush,
            _ when isSelected => _selectedSurfaceBrush,
            _ => _normalSurfaceBrush
        };
        border.BorderBrush = state == FileItemSurfaceVisualState.DropTarget
            ? _dropTargetBorderBrush
            : _normalBorderBrush;
        border.BorderThickness = state == FileItemSurfaceVisualState.DropTarget
            ? new Thickness(1)
            : new Thickness(0);
        border.Opacity = isCut ? 0.58 : 1.0;
    }

    private void EnsureBrushes(bool isDark, Windows.UI.Color accentColor)
    {
        if (_normalSurfaceBrush is not null &&
            _isDark == isDark &&
            _accentColor is { } cachedAccent &&
            cachedAccent.Equals(accentColor))
        {
            return;
        }

        _isDark = isDark;
        _accentColor = accentColor;

        Windows.UI.Color selectedBackground = GetNeutralStateLayer(
            isDark,
            FileItemSurfaceVisualState.Normal,
            isSelected: true);
        Windows.UI.Color hoverBackground = GetNeutralStateLayer(
            isDark,
            FileItemSurfaceVisualState.Hover,
            isSelected: false);
        Windows.UI.Color pressedBackground = GetNeutralStateLayer(
            isDark,
            FileItemSurfaceVisualState.Pressed,
            isSelected: false);
        Windows.UI.Color selectedHoverBackground = GetNeutralStateLayer(
            isDark,
            FileItemSurfaceVisualState.Hover,
            isSelected: true);
        Windows.UI.Color dropTargetBackground = WithAlpha(
            BuildAccentSurfaceColor(
                isDark,
                accentColor,
                isDark
                    ? ColorHelper.FromArgb(0xFF, 0x35, 0x3D, 0x48)
                    : ColorHelper.FromArgb(0xFF, 0xE7, 0xF3, 0xFF),
                isDark ? 0.42 : 0.30,
                isDark ? 0.06 : 0.04),
            isDark ? (byte)0x92 : (byte)0x9C);

        _normalSurfaceBrush = UpdateBrush(_normalSurfaceBrush, Colors.Transparent);
        _selectedSurfaceBrush = UpdateBrush(_selectedSurfaceBrush, selectedBackground);
        _hoverSurfaceBrush = UpdateBrush(_hoverSurfaceBrush, hoverBackground);
        _pressedSurfaceBrush = UpdateBrush(_pressedSurfaceBrush, pressedBackground);
        _selectedHoverSurfaceBrush = UpdateBrush(_selectedHoverSurfaceBrush, selectedHoverBackground);
        _dropTargetSurfaceBrush = UpdateBrush(_dropTargetSurfaceBrush, dropTargetBackground);
        _normalBorderBrush = UpdateBrush(_normalBorderBrush, Colors.Transparent);
        _dropTargetBorderBrush = UpdateBrush(
            _dropTargetBorderBrush,
            WithAlpha(accentColor, isDark ? (byte)0xF0 : (byte)0xD8));
    }

    private static SolidColorBrush UpdateBrush(SolidColorBrush? brush, Windows.UI.Color color)
    {
        if (brush is null)
        {
            return new SolidColorBrush(color);
        }

        brush.Color = color;
        return brush;
    }

    internal static Windows.UI.Color GetNeutralStateLayer(
        bool isDark,
        FileItemSurfaceVisualState state,
        bool isSelected)
    {
        Windows.UI.Color neutral = isDark
            ? Colors.White
            : Colors.Black;
        byte alpha = (state, isSelected, isDark) switch
        {
            (FileItemSurfaceVisualState.Hover, true, true) => 0x38,
            (FileItemSurfaceVisualState.Hover, true, false) => 0x2D,
            (FileItemSurfaceVisualState.Pressed, true, true) => 0x38,
            (FileItemSurfaceVisualState.Pressed, true, false) => 0x2D,
            (_, true, true) => 0x28,
            (_, true, false) => 0x20,
            (FileItemSurfaceVisualState.Pressed, false, true) => 0x20,
            (FileItemSurfaceVisualState.Pressed, false, false) => 0x18,
            (FileItemSurfaceVisualState.Hover, false, true) => 0x14,
            (FileItemSurfaceVisualState.Hover, false, false) => 0x0F,
            _ => 0x00
        };
        return WithAlpha(neutral, alpha);
    }

    private static Windows.UI.Color BuildAccentSurfaceColor(
        bool isDark,
        Windows.UI.Color accent,
        Windows.UI.Color baseColor,
        double accentMix,
        double overlayMix)
    {
        Windows.UI.Color tinted = BlendColors(baseColor, accent, accentMix);
        Windows.UI.Color overlay = isDark
            ? ColorHelper.FromArgb(0xFF, 0x12, 0x14, 0x18)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        return BlendColors(tinted, overlay, overlayMix);
    }

    private static Windows.UI.Color BlendColors(
        Windows.UI.Color from,
        Windows.UI.Color to,
        double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        static byte Blend(byte first, byte second, double mix) =>
            (byte)Math.Clamp(
                Math.Round(first + ((second - first) * mix)),
                0,
                255);

        return ColorHelper.FromArgb(
            Blend(from.A, to.A, amount),
            Blend(from.R, to.R, amount),
            Blend(from.G, to.G, amount),
            Blend(from.B, to.B, amount));
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha)
    {
        return ColorHelper.FromArgb(alpha, color.R, color.G, color.B);
    }
}
