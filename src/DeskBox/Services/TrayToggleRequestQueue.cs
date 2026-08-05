// Copyright (c) DeskBox. All rights reserved.

namespace DeskBox.Services;

internal sealed record TrayToggleQueueSnapshot(
    int PendingCount,
    bool WorkerRunning,
    long TotalRequests,
    long EffectiveToggles,
    long FoldedNoOpBatches,
    string? LastSource,
    string? LastError);

/// <summary>
/// Serializes tray-toggle requests and folds a burst of toggles by parity.
/// A toggle is an intent, not a request to start another animation while a
/// previous animation is still being prepared. Folding the pending burst
/// prevents a rapid key repeat from creating an unbounded animation backlog.
/// </summary>
internal sealed class TrayToggleRequestQueue
{
    private sealed record Request(string Source, TaskCompletionSource Completion);

    private readonly object _sync = new();
    private readonly Queue<Request> _pending = new();
    private readonly Func<string, Task> _toggleAsync;
    private bool _workerRunning;
    private long _totalRequests;
    private long _effectiveToggles;
    private long _foldedNoOpBatches;
    private string? _lastSource;
    private string? _lastError;

    public TrayToggleRequestQueue(Func<string, Task> toggleAsync)
    {
        _toggleAsync = toggleAsync ?? throw new ArgumentNullException(nameof(toggleAsync));
    }

    public Task EnqueueAsync(string source)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool startWorker;
        int pendingCount;

        lock (_sync)
        {
            _pending.Enqueue(new Request(source, completion));
            _totalRequests++;
            _lastSource = source;
            pendingCount = _pending.Count;
            startWorker = !_workerRunning;
            _workerRunning = true;
        }

        App.LogVerbose(
            $"[TrayToggle] queued source={source} pending={pendingCount} " +
            $"startWorker={startWorker}");

        if (startWorker)
        {
            _ = ProcessAsync();
        }

        return completion.Task;
    }

    public TrayToggleQueueSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new TrayToggleQueueSnapshot(
                _pending.Count,
                _workerRunning,
                _totalRequests,
                _effectiveToggles,
                _foldedNoOpBatches,
                _lastSource,
                _lastError);
        }
    }

    private async Task ProcessAsync()
    {
        while (true)
        {
            Request[] batch;
            lock (_sync)
            {
                if (_pending.Count == 0)
                {
                    _workerRunning = false;
                    return;
                }

                batch = _pending.ToArray();
                _pending.Clear();
            }

            // An even number of toggles returns to the same requested state.
            // Complete all requests after the batch has been accounted for so
            // callers never observe a request as completed while it is still
            // waiting in the queue.
            try
            {
                if ((batch.Length & 1) != 0)
                {
                    string source = batch[^1].Source;
                    lock (_sync)
                    {
                        _effectiveToggles++;
                        _lastSource = source;
                    }
                    App.LogVerbose(
                        $"[TrayToggle] processing source={source} batch={batch.Length} " +
                        $"effective=toggle");
                    await _toggleAsync(source);
                }
                else
                {
                    lock (_sync)
                    {
                        _foldedNoOpBatches++;
                    }
                    App.LogVerbose(
                        $"[TrayToggle] processing batch={batch.Length} effective=no-op");
                }

                foreach (Request request in batch)
                {
                    request.Completion.TrySetResult();
                }
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _lastError = ex.Message;
                }
                App.Log($"[TrayToggle] processing failed batch={batch.Length}: {ex}");
                foreach (Request request in batch)
                {
                    request.Completion.TrySetException(ex);
                }
            }
        }
    }
}
