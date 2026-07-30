using DeskBox.Contracts;

namespace DeskBox.Controls;

/// <summary>
/// Bridges an <see cref="IWidgetContent"/> into a <see cref="WidgetShell"/> while
/// keeping content lifecycle separate from window and z-order behavior.
/// </summary>
public sealed class WidgetShellContentHost
{
    private readonly Action<IWidgetContent> _setContent;
    private readonly Action _clearContent;
    private readonly Action<IWidgetContent, IWidgetContent> _beginTransition;
    private readonly Action _completeTransition;
    private readonly Action<IWidgetContent> _rollbackTransition;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        IWidgetContent,
        object> _disposedContents = new();
    private readonly object _disposedContentGate = new();
    private IWidgetContent? _pendingContent;
    private Task? _pendingInitializationTask;
    private WidgetShellPreparedContent? _preparedContent;
    private WidgetShellContentTransition? _activeTransition;
    private int _contentVersion;
    private bool _isDisposed;
    private bool _isWindowVisible;
    private bool _isActivated;

    public WidgetShellContentHost(WidgetShell shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _setContent = shell.SetContent;
        _clearContent = shell.ClearContent;
        _beginTransition = shell.BeginContentTransition;
        _completeTransition = shell.CompleteContentTransition;
        _rollbackTransition = shell.RollbackContentTransition;
    }

    internal WidgetShellContentHost(
        Action<IWidgetContent> setContent,
        Action? clearContent = null,
        Action<IWidgetContent, IWidgetContent>? beginTransition = null,
        Action? completeTransition = null,
        Action<IWidgetContent>? rollbackTransition = null)
    {
        _setContent = setContent ?? throw new ArgumentNullException(nameof(setContent));
        _clearContent = clearContent ?? (() => { });
        _beginTransition = beginTransition ?? ((_, incoming) => _setContent(incoming));
        _completeTransition = completeTransition ?? (() => { });
        _rollbackTransition = rollbackTransition ?? _setContent;
    }

    public IWidgetContent? CurrentContent { get; private set; }

    internal int LiveContentCount =>
        (CurrentContent is null ? 0 : 1) +
        (_activeTransition?.OutgoingContent is null ? 0 : 1);

    public async Task SetContentAsync(IWidgetContent content)
    {
        using WidgetShellPreparedContent? prepared =
            await PrepareContentAsync(content, CancellationToken.None);
        if (prepared is null)
        {
            return;
        }

        using WidgetShellContentTransition? transition =
            CommitPreparedContent(prepared);
        transition?.Complete();
    }

    internal async Task<WidgetShellPreparedContent?> PrepareContentAsync(
        IWidgetContent content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (cancellationToken.IsCancellationRequested)
        {
            DisposeContentOnce(content);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (_isDisposed)
        {
            DisposeContentOnce(content);
            return null;
        }

        if (_activeTransition is not null)
        {
            DisposeContentOnce(content);
            throw new InvalidOperationException(
                "A content transition must be completed or rolled back before preparing another content.");
        }

        _preparedContent?.Dispose();
        int contentVersion = ++_contentVersion;
        _pendingContent = content;
        Task? initializationTask = null;
        try
        {
            initializationTask = content.InitializeAsync();
            _pendingInitializationTask = initializationTask;
            await initializationTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ClearPendingContent(content, initializationTask);
            if (initializationTask is { IsCompleted: false })
            {
                _ = ObserveInitializationAndDisposeAsync(initializationTask, content);
            }
            else
            {
                DisposeContentOnce(content);
            }

            throw;
        }
        catch
        {
            ClearPendingContent(content, initializationTask);
            DisposeContentOnce(content);
            throw;
        }

        ClearPendingContent(content, initializationTask);
        if (_isDisposed || contentVersion != _contentVersion)
        {
            DisposeContentOnce(content);
            return null;
        }

        var prepared = new WidgetShellPreparedContent(this, content, contentVersion);
        _preparedContent = prepared;
        return prepared;
    }

    internal WidgetShellContentTransition? CommitPreparedContent(
        WidgetShellPreparedContent prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (_isDisposed ||
            !ReferenceEquals(_preparedContent, prepared) ||
            prepared.ContentVersion != _contentVersion ||
            prepared.IsConsumed)
        {
            prepared.Dispose();
            return null;
        }

        _preparedContent = null;
        prepared.MarkConsumed();
        IWidgetContent content = prepared.Content;
        IWidgetContent? outgoingContent = CurrentContent;
        if (ReferenceEquals(outgoingContent, content))
        {
            return null;
        }

        outgoingContent?.OnDeactivated();
        CurrentContent = content;
        try
        {
            if (outgoingContent is null)
            {
                _setContent(content);
            }
            else
            {
                _beginTransition(outgoingContent, content);
            }

            content.ApplyAppearance();
            content.OnWindowVisibilityChanged(_isWindowVisible);
            if (_isActivated)
            {
                content.OnActivated();
            }
        }
        catch
        {
            CurrentContent = outgoingContent;
            DisposeContentOnce(content);
            if (outgoingContent is not null)
            {
                _rollbackTransition(outgoingContent);
                outgoingContent.OnWindowVisibilityChanged(_isWindowVisible);
                if (_isActivated)
                {
                    outgoingContent.OnActivated();
                }
            }
            throw;
        }

        var transition = new WidgetShellContentTransition(
            this,
            content,
            outgoingContent,
            prepared.ContentVersion);
        _activeTransition = transition;
        return transition;
    }

    public Task RefreshAsync()
    {
        return CurrentContent?.RefreshAsync() ?? Task.CompletedTask;
    }

    public void ApplyAppearance()
    {
        CurrentContent?.ApplyAppearance();
    }

    public void OnActivated()
    {
        _isActivated = true;
        CurrentContent?.OnActivated();
    }

    public void OnDeactivated()
    {
        _isActivated = false;
        CurrentContent?.OnDeactivated();
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        CurrentContent?.OnWindowVisibilityChanged(visible);
    }

    public void DisposeContent()
    {
        if (_isDisposed && CurrentContent is null && _pendingContent is null)
        {
            return;
        }

        _isDisposed = true;
        _contentVersion++;
        _preparedContent?.Dispose();
        _preparedContent = null;

        WidgetShellContentTransition? activeTransition = _activeTransition;
        _activeTransition = null;
        activeTransition?.MarkCompleted();

        IWidgetContent? currentContent = CurrentContent;
        CurrentContent = null;
        currentContent?.OnWindowVisibilityChanged(false);
        currentContent?.OnDeactivated();
        _clearContent();
        DisposeContentOnce(currentContent);
        if (activeTransition?.OutgoingContent is { } outgoingContent)
        {
            outgoingContent.OnWindowVisibilityChanged(false);
            DisposeContentOnce(outgoingContent);
        }

        IWidgetContent? pendingContent = _pendingContent;
        Task? pendingInitialization = _pendingInitializationTask;
        _pendingContent = null;
        _pendingInitializationTask = null;
        if (pendingContent is not null &&
            !ReferenceEquals(pendingContent, currentContent))
        {
            if (pendingInitialization is { IsCompleted: false })
            {
                _ = ObserveInitializationAndDisposeAsync(
                    pendingInitialization,
                    pendingContent);
            }
            else
            {
                DisposeContentOnce(pendingContent);
            }
        }
    }

    private void CompleteTransition(WidgetShellContentTransition transition)
    {
        if (!ReferenceEquals(_activeTransition, transition))
        {
            transition.MarkCompleted();
            return;
        }

        // Keep the transition active until the UI presenter has accepted the
        // commit. If presenter cleanup fails, the caller can still roll back
        // to the outgoing content.
        if (transition.OutgoingContent is not null)
        {
            _completeTransition();
        }

        _activeTransition = null;
        transition.MarkCompleted();

        if (transition.OutgoingContent is { } outgoingContent)
        {
            outgoingContent.OnWindowVisibilityChanged(false);
            try
            {
                DisposeContentOnce(outgoingContent);
            }
            catch (Exception ex)
            {
                // The incoming member is already committed visually and in
                // CurrentContent. Cleanup failure must not reopen a settled
                // transaction and attach a partially disposed old member.
                App.Log(
                    $"[WidgetSurface] Outgoing content cleanup failed " +
                    $"member={outgoingContent.WidgetId}: {ex}");
            }
        }
    }

    private void RollbackTransition(WidgetShellContentTransition transition)
    {
        if (!ReferenceEquals(_activeTransition, transition))
        {
            transition.MarkCompleted();
            return;
        }

        _activeTransition = null;
        IWidgetContent incomingContent = transition.IncomingContent;
        incomingContent.OnWindowVisibilityChanged(false);
        incomingContent.OnDeactivated();
        if (transition.OutgoingContent is { } outgoingContent)
        {
            _rollbackTransition(outgoingContent);
            CurrentContent = outgoingContent;
            outgoingContent.ApplyAppearance();
            outgoingContent.OnWindowVisibilityChanged(_isWindowVisible);
            if (_isActivated)
            {
                outgoingContent.OnActivated();
            }
        }
        else
        {
            _clearContent();
            CurrentContent = null;
        }

        DisposeContentOnce(incomingContent);
        transition.MarkCompleted();
    }

    private void CancelPreparedContent(WidgetShellPreparedContent prepared)
    {
        if (!ReferenceEquals(_preparedContent, prepared) || prepared.IsConsumed)
        {
            return;
        }

        _preparedContent = null;
        _contentVersion++;
        prepared.MarkConsumed();
        DisposeContentOnce(prepared.Content);
    }

    private void ClearPendingContent(
        IWidgetContent content,
        Task? initializationTask)
    {
        if (ReferenceEquals(_pendingContent, content))
        {
            _pendingContent = null;
        }

        if (ReferenceEquals(_pendingInitializationTask, initializationTask))
        {
            _pendingInitializationTask = null;
        }
    }

    private async Task ObserveInitializationAndDisposeAsync(
        Task initializationTask,
        IWidgetContent content)
    {
        try
        {
            await initializationTask;
        }
        catch
        {
        }
        finally
        {
            DisposeContentOnce(content);
        }
    }

    private void DisposeContentOnce(IWidgetContent? content)
    {
        if (content is not IDisposable disposable)
        {
            return;
        }

        lock (_disposedContentGate)
        {
            if (_disposedContents.TryGetValue(content, out _))
            {
                return;
            }
            _disposedContents.Add(content, new object());
        }

        disposable.Dispose();
    }

    internal sealed class WidgetShellPreparedContent : IDisposable
    {
        private readonly WidgetShellContentHost _owner;

        internal WidgetShellPreparedContent(
            WidgetShellContentHost owner,
            IWidgetContent content,
            int contentVersion)
        {
            _owner = owner;
            Content = content;
            ContentVersion = contentVersion;
        }

        internal IWidgetContent Content { get; }

        internal int ContentVersion { get; }

        internal bool IsConsumed { get; private set; }

        internal void MarkConsumed()
        {
            IsConsumed = true;
        }

        public void Dispose()
        {
            _owner.CancelPreparedContent(this);
        }
    }

    internal sealed class WidgetShellContentTransition : IDisposable
    {
        private readonly WidgetShellContentHost _owner;
        private bool _isCompleted;

        internal WidgetShellContentTransition(
            WidgetShellContentHost owner,
            IWidgetContent incomingContent,
            IWidgetContent? outgoingContent,
            int contentVersion)
        {
            _owner = owner;
            IncomingContent = incomingContent;
            OutgoingContent = outgoingContent;
            ContentVersion = contentVersion;
        }

        internal IWidgetContent IncomingContent { get; }

        internal IWidgetContent? OutgoingContent { get; }

        internal int ContentVersion { get; }

        public void Complete()
        {
            if (!_isCompleted)
            {
                _owner.CompleteTransition(this);
            }
        }

        public void Rollback()
        {
            if (!_isCompleted)
            {
                _owner.RollbackTransition(this);
            }
        }

        internal void MarkCompleted()
        {
            _isCompleted = true;
        }

        public void Dispose()
        {
            Rollback();
        }
    }
}
