using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Runtime.CompilerServices;

namespace DeskBox.Services;

public static class WidgetSegmentedLayoutHelper
{
    private static DispatcherQueue? s_dispatcher;
    private static readonly ConditionalWeakTable<Segmented, EqualWidthLayoutState>
        s_equalWidthLayoutStates = new();

    private sealed class EqualWidthLayoutState
    {
        public bool FollowUpQueued;
        public double AppliedItemWidth = double.NaN;
        public int AppliedItemCount;
    }

    public static void Initialize(DispatcherQueue dispatcher)
    {
        s_dispatcher = dispatcher;
    }

    public static void ApplyNaturalItemWidths(Segmented segmented)
    {
        s_equalWidthLayoutStates.Remove(segmented);
        var visibleItems = segmented.Items
            .OfType<SegmentedItem>()
            .Where(item => item.Visibility == Visibility.Visible)
            .ToList();
        if (visibleItems.Count == 0)
        {
            return;
        }

        foreach (var item in visibleItems)
        {
            item.Width = double.NaN;
            item.MaxWidth = double.PositiveInfinity;
            item.MinWidth = 0;
            item.ClearValue(Microsoft.UI.Xaml.Controls.Control.PaddingProperty);
            item.ClearValue(FrameworkElement.MinHeightProperty);
        }
    }

    public static void ApplyEqualItemWidths(Segmented segmented)
    {
        ApplyEqualItemWidthsCore(segmented, queueFollowUp: true);
    }

    private static void ApplyEqualItemWidthsCore(
        Segmented segmented,
        bool queueFollowUp)
    {
        var visibleItems = segmented.Items
            .OfType<SegmentedItem>()
            .Where(item => item.Visibility == Visibility.Visible)
            .ToList();
        if (segmented.ActualWidth <= 0 || visibleItems.Count == 0)
        {
            return;
        }

        double itemWidth = Math.Max(0, Math.Floor(segmented.ActualWidth / visibleItems.Count));
        double minHeight = Math.Max(24, segmented.MinHeight - 3);
        var padding = new Thickness(4, 1, 4, 2);
        EqualWidthLayoutState state = s_equalWidthLayoutStates.GetOrCreateValue(segmented);
        bool needsUpdate = state.AppliedItemCount != visibleItems.Count ||
            Math.Abs(state.AppliedItemWidth - itemWidth) >= 0.5 ||
            visibleItems.Any(item =>
                !NearlyEqual(item.Width, itemWidth) ||
                !NearlyEqual(item.MaxWidth, itemWidth) ||
                !NearlyEqual(item.MinHeight, minHeight) ||
                !PaddingEquals(item.Padding, padding));

        if (needsUpdate)
        {
            foreach (var item in visibleItems)
            {
                item.Width = itemWidth;
                item.MaxWidth = itemWidth;
                item.MinWidth = 0;
                item.Padding = padding;
                item.MinHeight = minHeight;
            }

            state.AppliedItemWidth = itemWidth;
            state.AppliedItemCount = visibleItems.Count;
        }

        // A shrink can report a width briefly influenced by the previous fixed
        // item width. Keep the corrective pass, but coalesce it: a compact
        // transition can otherwise queue one dispatcher item per animation frame.
        if (!queueFollowUp || state.FollowUpQueued || s_dispatcher is null)
        {
            return;
        }

        state.FollowUpQueued = true;
        if (!s_dispatcher.TryEnqueue(() =>
            {
                if (!s_equalWidthLayoutStates.TryGetValue(segmented, out var currentState) ||
                    !ReferenceEquals(currentState, state))
                {
                    return;
                }

                state.FollowUpQueued = false;
                ApplyEqualItemWidthsCore(segmented, queueFollowUp: false);
            }))
        {
            state.FollowUpQueued = false;
        }
    }

    private static bool NearlyEqual(double current, double expected)
    {
        return !double.IsNaN(current) && Math.Abs(current - expected) < 0.5;
    }

    private static bool PaddingEquals(Thickness left, Thickness right)
    {
        return Math.Abs(left.Left - right.Left) < 0.01 &&
            Math.Abs(left.Top - right.Top) < 0.01 &&
            Math.Abs(left.Right - right.Right) < 0.01 &&
            Math.Abs(left.Bottom - right.Bottom) < 0.01;
    }
}
