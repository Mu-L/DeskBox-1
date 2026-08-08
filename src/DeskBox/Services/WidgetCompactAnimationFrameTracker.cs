// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using DeskBox.Models;

namespace DeskBox.Services;

public readonly record struct WidgetCompactAnimationFrameSummary(
    int RefreshRateHz,
    int FrameCount,
    int EstimatedDroppedFrames,
    double MaximumFrameIntervalMilliseconds,
    double ElapsedMilliseconds)
{
    public double FrameBudgetMilliseconds => 1000d / Math.Max(1, RefreshRateHz);
}

/// <summary>
/// Small allocation-free tracker for diagnosing capsule animation cadence.
/// Timestamps are Stopwatch ticks so the policy can be covered by unit tests.
/// </summary>
public sealed class WidgetCompactAnimationFrameTracker
{
    private readonly long _startedTimestamp;
    private long _lastFrameTimestamp;
    private double _maximumFrameIntervalMilliseconds;
    private int _frameCount;
    private int _estimatedDroppedFrames;

    public WidgetCompactAnimationFrameTracker(long startedTimestamp, int refreshRateHz)
    {
        _startedTimestamp = startedTimestamp;
        _lastFrameTimestamp = startedTimestamp;
        RefreshRateHz = WidgetDisplayRefreshRatePolicy.Normalize((uint)Math.Max(0, refreshRateHz));
    }

    public int RefreshRateHz { get; }

    public void RecordFrame(long timestamp)
    {
        if (timestamp <= _lastFrameTimestamp)
        {
            return;
        }

        double intervalMs = Stopwatch.GetElapsedTime(_lastFrameTimestamp, timestamp).TotalMilliseconds;
        _lastFrameTimestamp = timestamp;
        _frameCount++;
        _maximumFrameIntervalMilliseconds = Math.Max(_maximumFrameIntervalMilliseconds, intervalMs);

        double frameBudgetMs = 1000d / RefreshRateHz;
        if (intervalMs > frameBudgetMs * 1.5)
        {
            _estimatedDroppedFrames += Math.Max(1, (int)Math.Round(intervalMs / frameBudgetMs) - 1);
        }
    }

    public WidgetCompactAnimationFrameSummary Complete(long timestamp)
    {
        long completedTimestamp = Math.Max(timestamp, _startedTimestamp);
        return new WidgetCompactAnimationFrameSummary(
            RefreshRateHz,
            _frameCount,
            _estimatedDroppedFrames,
            _maximumFrameIntervalMilliseconds,
            Stopwatch.GetElapsedTime(_startedTimestamp, completedTimestamp).TotalMilliseconds);
    }
}
