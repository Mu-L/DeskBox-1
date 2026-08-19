using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Services;

/// <summary>
/// Hosts a configured Mica controller for a bounded XAML backdrop surface.
/// Unlike the built-in MicaBackdrop, this keeps the material input-active when
/// its window returns to the desktop layer, matching DeskBox widget windows.
/// </summary>
internal sealed class PersistentMicaBackdrop : SystemBackdrop
{
    private MicaController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private bool _isDark;
    private bool _useAlt;
    private Windows.UI.Color _tintColor;
    private WidgetMaterialOpacityProfile _opacityProfile;

    public PersistentMicaBackdrop(
        bool isDark,
        bool useAlt,
        Windows.UI.Color tintColor,
        WidgetMaterialOpacityProfile opacityProfile)
    {
        Update(isDark, useAlt, tintColor, opacityProfile);
    }

    public void Update(
        bool isDark,
        bool useAlt,
        Windows.UI.Color tintColor,
        WidgetMaterialOpacityProfile opacityProfile)
    {
        _isDark = isDark;
        _useAlt = useAlt;
        _tintColor = tintColor;
        _opacityProfile = opacityProfile;
        ApplyConfiguration();
        ApplyControllerVisuals();
    }

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget,
        XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (_controller is not null)
        {
            return;
        }

        if (!MicaController.IsSupported())
        {
            return;
        }

        _configuration = GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot);
        _controller = new MicaController();
        if (!_controller.AddSystemBackdropTarget(connectedTarget))
        {
            DisposeController();
            return;
        }

        ApplyConfiguration();
        ApplyControllerVisuals();
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(
        ICompositionSupportsSystemBackdrop target,
        XamlRoot xamlRoot)
    {
        base.OnDefaultSystemBackdropConfigurationChanged(target, xamlRoot);
        if (_controller is null)
        {
            return;
        }

        _configuration = GetDefaultSystemBackdropConfiguration(target, xamlRoot);
        ApplyConfiguration();
        ApplyControllerVisuals();
    }

    protected override void OnTargetDisconnected(
        ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        DisposeController(disconnectedTarget);
    }

    private void ApplyConfiguration()
    {
        if (_configuration is null || _controller is null)
        {
            return;
        }

        _configuration.IsInputActive = true;
        _configuration.Theme = _isDark
            ? SystemBackdropTheme.Dark
            : SystemBackdropTheme.Light;
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    private void ApplyControllerVisuals()
    {
        if (_controller is null)
        {
            return;
        }

        _controller.Kind = _useAlt ? MicaKind.BaseAlt : MicaKind.Base;
        _controller.TintColor = _tintColor;
        _controller.FallbackColor = WidgetMaterialVisualCalculator.BuildMicaFallbackColor(
            _isDark,
            _useAlt);
        _controller.TintOpacity = (float)_opacityProfile.TintOpacity;
        _controller.LuminosityOpacity = (float)_opacityProfile.LuminosityOpacity;
    }

    private void DisposeController(
        ICompositionSupportsSystemBackdrop? disconnectedTarget = null)
    {
        try
        {
            if (_controller is not null && disconnectedTarget is not null)
            {
                _controller.RemoveSystemBackdropTarget(disconnectedTarget);
            }
            else
            {
                _controller?.RemoveAllSystemBackdropTargets();
            }

            _controller?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _controller = null;
            _configuration = null;
        }
    }
}
