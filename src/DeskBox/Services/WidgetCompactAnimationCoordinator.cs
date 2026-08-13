// Copyright (c) DeskBox. All rights reserved.

using Microsoft.UI.Xaml.Media;

namespace DeskBox.Services;

/// <summary>
/// Shares one compositor-paced Rendering subscription across every capsule
/// transition. Rendering follows the active display/DRR cadence; elapsed time,
/// rather than an assumed frame rate, remains the source of animation progress.
/// </summary>
internal static class WidgetCompactAnimationCoordinator
{
    // A native bounds/clip transition still has one UI-thread coordinator, but
    // multiple capsules may animate concurrently (e.g. one collapsing while the
    // cursor expands the next). Allowing several in-flight transitions avoids
    // dropping a capsule's animation when the slot is occupied. First-frame
    // commit pressure is absorbed by the expansion warm-up instead of by
    // serializing transitions.
    internal const int MaximumConcurrentBoundsTransitions = 4;

    private static readonly Dictionary<long, Action> FrameCallbacks = [];
    private static readonly HashSet<long> BoundsTransitionRegistrations = [];
    private static long s_nextRegistrationId;
    private static bool s_isRenderingSubscribed;
    private static IDisposable? s_clockBoostLease;

    public static IDisposable Register(Action frameCallback)
    {
        return RegisterCore(frameCallback, isBoundsTransition: false);
    }

    public static bool HasBoundsTransitionCapacity =>
        WidgetCompactAnimationConcurrencyPolicy.ShouldAnimate(
            BoundsTransitionRegistrations.Count,
            MaximumConcurrentBoundsTransitions);

    public static IDisposable RegisterBoundsTransition(Action frameCallback)
    {
        if (!HasBoundsTransitionCapacity)
        {
            throw new InvalidOperationException("No compact bounds-transition animation slot is available.");
        }

        return RegisterCore(frameCallback, isBoundsTransition: true);
    }

    private static IDisposable RegisterCore(Action frameCallback, bool isBoundsTransition)
    {
        ArgumentNullException.ThrowIfNull(frameCallback);

        long registrationId = ++s_nextRegistrationId;
        FrameCallbacks.Add(registrationId, frameCallback);
        if (isBoundsTransition)
        {
            BoundsTransitionRegistrations.Add(registrationId);
        }
        if (!s_isRenderingSubscribed)
        {
            s_isRenderingSubscribed = true;
            CompositionTarget.Rendering += OnRendering;
            s_clockBoostLease = CompositorClockBoostCoordinator.Acquire();
        }

        return new Registration(registrationId);
    }

    private static void OnRendering(object? sender, object args)
    {
        // Callbacks may complete and unregister themselves while this snapshot
        // is being dispatched. The registration check avoids invoking an entry
        // that another callback cancelled earlier in the same compositor tick.
        foreach ((long registrationId, Action callback) in FrameCallbacks.ToArray())
        {
            if (!FrameCallbacks.ContainsKey(registrationId))
            {
                continue;
            }

            try
            {
                callback();
            }
            catch (Exception ex)
            {
                App.Log($"[CompactAnimationClock] Frame callback failed: {ex.Message}");
            }
        }
    }

    private static void Unregister(long registrationId)
    {
        FrameCallbacks.Remove(registrationId);
        BoundsTransitionRegistrations.Remove(registrationId);
        if (FrameCallbacks.Count != 0 || !s_isRenderingSubscribed)
        {
            return;
        }

        CompositionTarget.Rendering -= OnRendering;
        s_isRenderingSubscribed = false;
        s_clockBoostLease?.Dispose();
        s_clockBoostLease = null;
    }

    private sealed class Registration(long registrationId) : IDisposable
    {
        private long _registrationId = registrationId;

        public void Dispose()
        {
            long id = Interlocked.Exchange(ref _registrationId, 0);
            if (id != 0)
            {
                Unregister(id);
            }
        }
    }
}

internal static class WidgetCompactAnimationConcurrencyPolicy
{
    public static bool ShouldAnimate(int activeTransitions, int maximumConcurrentTransitions)
    {
        return maximumConcurrentTransitions > 0 &&
            activeTransitions >= 0 &&
            activeTransitions < maximumConcurrentTransitions;
    }
}
