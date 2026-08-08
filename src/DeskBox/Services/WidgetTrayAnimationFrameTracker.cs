// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;

namespace DeskBox.Services;

public readonly record struct WidgetTrayAnimationFrameSummary(
    int RefreshRateHz,
    int ParticipantCount,
    int FrameCount,
    int EstimatedDroppedFrames,
    double MaximumFrameIntervalMilliseconds,
    double ElapsedMilliseconds)
{
    public double FrameBudgetMilliseconds => 1000d / Math.Max(1, RefreshRateHz);
}

/// <summary>
/// Measures one shared tray animation against every refresh-rate group taking
/// part in the batch. A 60 Hz and a 144 Hz display therefore receive separate
/// frame-budget results even though their HWND positions share one clock.
/// </summary>
public sealed class WidgetTrayAnimationFrameTracker
{
    private readonly IReadOnlyList<RefreshRateGroup> _groups;

    public WidgetTrayAnimationFrameTracker(
        long startedTimestamp,
        IEnumerable<int> participantRefreshRates)
    {
        ArgumentNullException.ThrowIfNull(participantRefreshRates);

        List<int> normalizedRates = participantRefreshRates
            .Select(rate => WidgetDisplayRefreshRatePolicy.Normalize(
                (uint)Math.Max(0, rate)))
            .ToList();
        if (normalizedRates.Count == 0)
        {
            normalizedRates.Add(WidgetDisplayRefreshRatePolicy.DefaultRefreshRateHz);
        }

        _groups = normalizedRates
            .GroupBy(rate => rate)
            .OrderBy(group => group.Key)
            .Select(group => new RefreshRateGroup(
                group.Key,
                group.Count(),
                new WidgetCompactAnimationFrameTracker(startedTimestamp, group.Key)))
            .ToList();
    }

    public void RecordFrame(long timestamp)
    {
        foreach (RefreshRateGroup group in _groups)
        {
            group.Tracker.RecordFrame(timestamp);
        }
    }

    public IReadOnlyList<WidgetTrayAnimationFrameSummary> Complete(long timestamp)
    {
        return _groups
            .Select(group =>
            {
                WidgetCompactAnimationFrameSummary summary =
                    group.Tracker.Complete(timestamp);
                return new WidgetTrayAnimationFrameSummary(
                    summary.RefreshRateHz,
                    group.ParticipantCount,
                    summary.FrameCount,
                    summary.EstimatedDroppedFrames,
                    summary.MaximumFrameIntervalMilliseconds,
                    summary.ElapsedMilliseconds);
            })
            .ToList();
    }

    private sealed record RefreshRateGroup(
        int RefreshRateHz,
        int ParticipantCount,
        WidgetCompactAnimationFrameTracker Tracker);
}

internal static class WidgetTrayAnimationDiagnostics
{
    public static void Report(
        WidgetTrayAnimationFrameTracker? tracker,
        long completedTimestamp,
        bool isShowing,
        string outcome,
        string scope,
        Action<string> verboseLog)
    {
        if (tracker is null)
        {
            return;
        }

        foreach (WidgetTrayAnimationFrameSummary summary in tracker.Complete(completedTimestamp))
        {
            string details =
                $"scope={scope} mode={(isShowing ? "show" : "hide")} " +
                $"outcome={outcome} refreshHz={summary.RefreshRateHz} " +
                $"participants={summary.ParticipantCount} frames={summary.FrameCount} " +
                $"dropped={summary.EstimatedDroppedFrames} " +
                $"maxFrameMs={summary.MaximumFrameIntervalMilliseconds:F1} " +
                $"budgetMs={summary.FrameBudgetMilliseconds:F1} " +
                $"elapsedMs={summary.ElapsedMilliseconds:F1}";
            PerformanceLogger.Mark("TrayAnimation", details);
            if (summary.EstimatedDroppedFrames > 0)
            {
                App.Log($"[TrayAnimation] Frame budget missed {details}");
            }
            else
            {
                verboseLog($"[TrayAnimation] {details}");
            }
        }
    }
}
