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
    private static readonly Dictionary<long, Action> FrameCallbacks = [];
    private static long s_nextRegistrationId;
    private static bool s_isRenderingSubscribed;
    private static IDisposable? s_clockBoostLease;

    public static IDisposable Register(Action frameCallback)
    {
        ArgumentNullException.ThrowIfNull(frameCallback);

        long registrationId = ++s_nextRegistrationId;
        FrameCallbacks.Add(registrationId, frameCallback);
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
