using DeskBox.Models;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace DeskBox.Controls;

public sealed partial class WidgetGroupTitleSwitcher
{
    private static readonly TimeSpan WheelCooldown =
        TimeSpan.FromMilliseconds(220);
    private DateTimeOffset _pointerEnteredAt;
    private DateTimeOffset _lastWheelSwitchAt;
    private DateTimeOffset _pendingWheelTargetAt;
    private string? _pendingWheelTargetId;
    private double _wheelAccumulator;
    private bool _pickerOpen;
    private bool _isPointerOverSelector;
    private bool _isSelectorPressed;
    private Storyboard? _identityStoryboard;
    private Storyboard? _interactionChromeStoryboard;
    private double _pendingIdentityWidth;

    private void RegisterKeyboardAccelerators()
    {
        AddKeyboardAccelerator(
            VirtualKey.Tab,
            VirtualKeyModifiers.Control,
            delta: 1,
            WidgetGroupSwitchOrigin.Keyboard);
        AddKeyboardAccelerator(
            VirtualKey.Tab,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            delta: -1,
            WidgetGroupSwitchOrigin.Keyboard);

        var openPicker = new KeyboardAccelerator
        {
            Key = VirtualKey.Down,
            Modifiers = VirtualKeyModifiers.Menu
        };
        openPicker.Invoked += (_, args) =>
        {
            if (_presentation is null || _pickerOpen)
            {
                return;
            }

            OpenPicker(SelectorButton);
            args.Handled = true;
        };
        KeyboardAccelerators.Add(openPicker);
    }

    private void AddKeyboardAccelerator(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        int delta,
        WidgetGroupSwitchOrigin origin)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers
        };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = SwitchRelative(delta, origin);
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private void SelectorButton_KeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        CoreVirtualKeyStates controlState =
            InputKeyboardSource.GetKeyStateForCurrentThread(
                VirtualKey.Control);
        CoreVirtualKeyStates shiftState =
            InputKeyboardSource.GetKeyStateForCurrentThread(
                VirtualKey.Shift);
        CoreVirtualKeyStates menuState =
            InputKeyboardSource.GetKeyStateForCurrentThread(
                VirtualKey.Menu);
        bool control =
            controlState.HasFlag(CoreVirtualKeyStates.Down);
        bool shift =
            shiftState.HasFlag(CoreVirtualKeyStates.Down);
        bool menu =
            menuState.HasFlag(CoreVirtualKeyStates.Down);

        if (e.Key == VirtualKey.Tab && control)
        {
            e.Handled = SwitchRelative(
                shift ? -1 : 1,
                WidgetGroupSwitchOrigin.Keyboard);
            return;
        }

        if (e.Key == VirtualKey.Down && menu)
        {
            OpenPicker(SelectorButton);
            e.Handled = true;
        }

        // Enter and Space intentionally use Button's native invocation path,
        // preserving standard focus, pressed-state and accessibility behavior.
    }

    private void SelectorButton_PointerEntered(
        object sender,
        PointerRoutedEventArgs e)
    {
        _isPointerOverSelector = true;
        _pointerEnteredAt = DateTimeOffset.UtcNow;
        _wheelAccumulator = 0;
        UpdateInteractionChrome();
    }

    private void SelectorButton_PointerExited(
        object sender,
        PointerRoutedEventArgs e)
    {
        _isPointerOverSelector = false;
        _isSelectorPressed = false;
        _wheelAccumulator = 0;
        _pendingWheelTargetId = null;
        UpdateInteractionChrome();
    }

    private void SelectorButton_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(SelectorButton).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isSelectorPressed = true;
        UpdateInteractionChrome();
    }

    private void SelectorButton_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        _isSelectorPressed = false;
        UpdateInteractionChrome();
    }

    internal void HandleTitleBarPointerWheel(
        UIElement source,
        PointerRoutedEventArgs e)
    {
        ProcessPointerWheel(source, e);
    }

    private void TabsPanel_PointerEntered(
        object sender,
        PointerRoutedEventArgs e)
    {
        _pointerEnteredAt = DateTimeOffset.UtcNow;
        _wheelAccumulator = 0;
    }

    private void TabsPanel_PointerExited(
        object sender,
        PointerRoutedEventArgs e)
    {
        _wheelAccumulator = 0;
        _pendingWheelTargetId = null;
    }

    private void ProcessPointerWheel(
        UIElement source,
        PointerRoutedEventArgs e)
    {
        if (!WheelSwitchEnabled ||
            _pickerOpen ||
            _presentation is null ||
            _presentation.Members.Count < 2)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastWheelSwitchAt < WheelCooldown)
        {
            // The pointer is already inside the dedicated title switcher hot
            // zone. Consume cooldown/arming input instead of letting it leak
            // into the active member's scrollable content.
            _wheelAccumulator = 0;
            e.Handled = true;
            return;
        }

        double delta =
            e.GetCurrentPoint(source).Properties.MouseWheelDelta;
        if (!WidgetGroupNavigationInteractionPolicy.TryConsumeWheelStep(
                ref _wheelAccumulator,
                delta,
                out int direction))
        {
            e.Handled = true;
            return;
        }

        if (SwitchRelative(direction, WidgetGroupSwitchOrigin.Wheel))
        {
            _lastWheelSwitchAt = now;
        }

        // The pointer is over the explicit switcher hot zone. Consume even an
        // edge attempt so it cannot accidentally scroll member content.
        e.Handled = true;
    }

    private bool SwitchRelative(
        int delta,
        WidgetGroupSwitchOrigin origin)
    {
        if (_presentation is null || delta == 0 || _pickerOpen)
        {
            return false;
        }

        int activeIndex = FindMemberIndex(
            _presentation.Members,
            _presentation.ActiveMemberId);
        if (origin == WidgetGroupSwitchOrigin.Wheel &&
            !string.IsNullOrWhiteSpace(_pendingWheelTargetId))
        {
            if (DateTimeOffset.UtcNow - _pendingWheelTargetAt <=
                TimeSpan.FromMilliseconds(1200))
            {
                int pendingIndex = FindMemberIndex(
                    _presentation.Members,
                    _pendingWheelTargetId);
                if (pendingIndex >= 0)
                {
                    activeIndex = pendingIndex;
                }
            }
            else
            {
                _pendingWheelTargetId = null;
            }
        }
        if (activeIndex < 0)
        {
            activeIndex = FindActiveFlagIndex(_presentation.Members);
        }
        if (!WidgetGroupNavigationInteractionPolicy.TryResolveRelativeTarget(
                activeIndex,
                _presentation.Members.Count,
                delta,
                out int targetIndex))
        {
            return false;
        }

        string targetId = _presentation.Members[targetIndex].WidgetId;
        if (origin == WidgetGroupSwitchOrigin.Wheel)
        {
            // The manager commits ActiveMemberId only after preparation,
            // persistence and the content transition. Keep a short-lived
            // optimistic target so another wheel detent does not calculate
            // from the stale committed member and repeatedly cancel the same
            // request.
            _pendingWheelTargetId = targetId;
            _pendingWheelTargetAt = DateTimeOffset.UtcNow;
        }
        else
        {
            _pendingWheelTargetId = null;
        }

        MemberInvoked?.Invoke(
            this,
            new WidgetGroupMemberEventArgs(
                targetId,
                origin));
        return true;
    }

    private void ReconcilePendingWheelTarget(
        WidgetGroupPresentation? presentation)
    {
        if (presentation is null ||
            string.IsNullOrWhiteSpace(_pendingWheelTargetId))
        {
            _pendingWheelTargetId = null;
            return;
        }

        if (string.Equals(
                presentation.ActiveMemberId,
                _pendingWheelTargetId,
                StringComparison.Ordinal) ||
            DateTimeOffset.UtcNow - _pendingWheelTargetAt >
                TimeSpan.FromMilliseconds(1200))
        {
            _pendingWheelTargetId = null;
        }
    }

    private void AnimateIdentityTransition(
        IdentitySnapshot previous,
        IdentitySnapshot next,
        WidgetGroupSwitchOrigin origin,
        bool forward)
    {
        CancelIdentityAnimation();
        SetIdentity(
            OutgoingIcon,
            OutgoingTitle,
            OutgoingPositionText,
            previous);
        SetIdentity(
            CurrentIcon,
            CurrentTitle,
            CurrentPositionText,
            next);
        OutgoingIdentityLayer.Opacity = 1;
        CurrentIdentityLayer.Opacity = 0;
        double currentIdentityWidth = IdentityViewport.Width;
        _pendingIdentityWidth = MeasureIdentityWidth(next);

        bool animationsEnabled = AreSystemAnimationsEnabled();
        bool directional =
            animationsEnabled &&
            origin is not WidgetGroupSwitchOrigin.Programmatic;
        WidgetContentTransitionProfile profile =
            WidgetContentTransitionProfile.Create(
                animationsEnabled,
                directional);
        if (profile.DurationMilliseconds <= 0)
        {
            IdentityViewport.Width = _pendingIdentityWidth;
            CancelIdentityAnimation();
            SetIdentity(
                OutgoingIcon,
                OutgoingTitle,
                OutgoingPositionText,
                null);
            return;
        }

        int durationMs = profile.DurationMilliseconds;
        int outgoingDurationMs = profile.OutgoingDurationMilliseconds;
        int incomingBeginTimeMs =
            outgoingDurationMs + profile.SwapGapMilliseconds;
        int incomingDurationMs = profile.IncomingDurationMilliseconds;
        double sign = forward ? 1 : -1;
        double distance = profile.TranslationDistance;
        var outgoingTransform = new TranslateTransform();
        var incomingTransform = new TranslateTransform
        {
            Y = directional ? distance * sign : 0
        };
        OutgoingIdentityLayer.RenderTransform = outgoingTransform;
        CurrentIdentityLayer.RenderTransform = incomingTransform;

        var outgoingFade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(outgoingDurationMs),
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseIn
            }
        };
        Storyboard.SetTarget(outgoingFade, OutgoingIdentityLayer);
        Storyboard.SetTargetProperty(outgoingFade, nameof(Opacity));

        var incomingFade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(incomingBeginTimeMs),
            Duration = TimeSpan.FromMilliseconds(incomingDurationMs),
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };
        Storyboard.SetTarget(incomingFade, CurrentIdentityLayer);
        Storyboard.SetTargetProperty(incomingFade, nameof(Opacity));

        var storyboard = new Storyboard();
        storyboard.Children.Add(outgoingFade);
        storyboard.Children.Add(incomingFade);
        var widthAnimation = new DoubleAnimation
        {
            From = currentIdentityWidth,
            To = _pendingIdentityWidth,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseInOut
            }
        };
        Storyboard.SetTarget(widthAnimation, IdentityViewport);
        Storyboard.SetTargetProperty(
            widthAnimation,
            nameof(FrameworkElement.Width));
        storyboard.Children.Add(widthAnimation);
        if (directional)
        {
            var outgoingMotion = new DoubleAnimation
            {
                From = 0,
                To = -distance * sign,
                Duration = TimeSpan.FromMilliseconds(outgoingDurationMs),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseIn
                }
            };
            Storyboard.SetTarget(outgoingMotion, outgoingTransform);
            Storyboard.SetTargetProperty(outgoingMotion, nameof(TranslateTransform.Y));
            storyboard.Children.Add(outgoingMotion);

            var incomingMotion = new DoubleAnimation
            {
                From = distance * sign,
                To = 0,
                BeginTime = TimeSpan.FromMilliseconds(incomingBeginTimeMs),
                Duration = TimeSpan.FromMilliseconds(incomingDurationMs),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };
            Storyboard.SetTarget(incomingMotion, incomingTransform);
            Storyboard.SetTargetProperty(incomingMotion, nameof(TranslateTransform.Y));
            storyboard.Children.Add(incomingMotion);

        }
        storyboard.Completed += IdentityStoryboard_Completed;
        _identityStoryboard = storyboard;
        storyboard.Begin();
    }

    private void UpdateInteractionChrome(bool animate = true)
    {
        _interactionChromeStoryboard?.Stop();
        _interactionChromeStoryboard = null;

        CapsuleChrome.Background = ResolveThemeBrush(
            _isSelectorPressed
                ? "SubtleFillColorTertiaryBrush"
                : "SubtleFillColorSecondaryBrush",
            new SolidColorBrush(Colors.Transparent));
        double targetSurfaceOpacity = _pickerOpen
            ? 0.34
            : _isSelectorPressed
                ? 0.4
                : _isPointerOverSelector
                    ? 0.26
                    : 0;
        CapsuleChrome.Opacity = 0;
        bool animationsEnabled = animate && AreSystemAnimationsEnabled();
        if (!animationsEnabled || XamlRoot is null)
        {
            TitleInteractionChrome.Opacity = targetSurfaceOpacity;
            return;
        }

        TimeSpan duration = TimeSpan.FromMilliseconds(
            WidgetMotion.FeedbackMilliseconds);
        var storyboard = new Storyboard();
        AddOpacityAnimation(
            TitleInteractionChrome,
            targetSurfaceOpacity);
        _interactionChromeStoryboard = storyboard;
        storyboard.Begin();

        void AddOpacityAnimation(UIElement target, double opacity)
        {
            var animation = new DoubleAnimation
            {
                To = opacity,
                Duration = duration,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(
                animation,
                nameof(UIElement.Opacity));
            storyboard.Children.Add(animation);
        }
    }

    private void IdentityStoryboard_Completed(object? sender, object e)
    {
        if (sender is Storyboard storyboard)
        {
            storyboard.Completed -= IdentityStoryboard_Completed;
        }

        OutgoingIdentityLayer.Opacity = 0;
        CurrentIdentityLayer.Opacity = 1;
        OutgoingIdentityLayer.RenderTransform = null;
        CurrentIdentityLayer.RenderTransform = null;
        IdentityViewport.Width = _pendingIdentityWidth;
        SetIdentity(
            OutgoingIcon,
            OutgoingTitle,
            OutgoingPositionText,
            null);
        _identityStoryboard = null;
    }

    private void CancelIdentityAnimation()
    {
        if (_identityStoryboard is not null)
        {
            _identityStoryboard.Completed -= IdentityStoryboard_Completed;
            _identityStoryboard.Stop();
            _identityStoryboard = null;
        }

        OutgoingIdentityLayer.Opacity = 0;
        CurrentIdentityLayer.Opacity = 1;
        OutgoingIdentityLayer.RenderTransform = null;
        CurrentIdentityLayer.RenderTransform = null;
    }

    private bool CanAnimateIdentity()
    {
        return XamlRoot is not null;
    }

    private static bool AreSystemAnimationsEnabled()
    {
        try
        {
            return new UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
        }
    }
}
