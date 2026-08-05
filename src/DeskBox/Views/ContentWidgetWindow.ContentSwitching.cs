using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Views;

public sealed partial class ContentWidgetWindow
{
    internal int LiveMemberCount => _contentHost.LiveContentCount;

    internal bool HasPresentableContentFrame =>
        ContentWidgetShell.HasPresentableContentFrame;

    internal async Task<ContentWidgetSwitchPreparation?> PrepareContentSwitchAsync(
        WidgetConfig targetConfig,
        IWidgetContent targetContent,
        WidgetContentDescriptor targetDescriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetConfig);
        ArgumentNullException.ThrowIfNull(targetContent);
        ArgumentNullException.ThrowIfNull(targetDescriptor);

        WidgetShellContentHost.WidgetShellPreparedContent? prepared =
            await _contentHost.PrepareContentAsync(targetContent, cancellationToken);
        return prepared is null
            ? null
            : new ContentWidgetSwitchPreparation(
                this,
                targetConfig,
                targetDescriptor,
                prepared);
    }

    private ContentWidgetSwitchTransition? BeginContentSwitch(
        WidgetConfig targetConfig,
        WidgetContentDescriptor targetDescriptor,
        WidgetShellContentHost.WidgetShellPreparedContent prepared)
    {
        WidgetShellContentHost.WidgetShellContentTransition? hostTransition =
            _contentHost.CommitPreparedContent(prepared);
        return hostTransition is null
            ? null
            : new ContentWidgetSwitchTransition(
                this,
                _config,
                _descriptor,
                targetConfig,
                targetDescriptor,
                hostTransition);
    }

    private Task AnimateContentSwitchAsync(
        bool directional,
        bool forward,
        CancellationToken cancellationToken)
    {
        return ContentWidgetShell.AnimateContentTransitionAsync(
            directional,
            forward,
            cancellationToken);
    }

    private void ApplyMemberContext(
        WidgetConfig config,
        WidgetContentDescriptor descriptor,
        IWidgetContent content,
        bool animateGroupIdentity = false,
        WidgetGroupSwitchOrigin origin = WidgetGroupSwitchOrigin.Programmatic,
        bool forward = true)
    {
        ContentWidgetShell.ClearFeedback();
        _config = config;
        _descriptor = descriptor;
        Diagnostics.SetWidgetContext(config);
        _titleViewModel.SetConfig(config);
        ContentWidgetShell.TitleGlyph = descriptor.DefaultGlyph;
        ContentWidgetShell.TitleIconKind =
            WidgetTitleIconKindNames.FromWidgetKind(config.WidgetKind);
        AttachCompactPresentationSource(content);
        AttachFeedbackSource(content);
        AttachHostContextMenuSource(content);
        ApplyLocalizedTitleActionTooltips();
        ApplyTitleBarLayout();
        ApplyAppearancePreview();
        RefreshCompactPresentation();
        RefreshWidgetGroupPresentation(
            animateGroupIdentity,
            origin,
            forward);
    }

    internal IWidgetContent? TakeCachedGroupContent(string widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId) ||
            !_cachedGroupContents.Remove(widgetId, out IWidgetContent? content))
        {
            return null;
        }

        RemoveCachedGroupContentOrderEntry(widgetId);
        App.LogVerbose($"[WidgetGroup] Reusing cached content member={widgetId}");
        return content;
    }

    private bool TryRetainGroupContent(IWidgetContent content)
    {
        if (IsClosing ||
            content is not IWidgetGroupContentCacheable ||
            string.IsNullOrWhiteSpace(content.WidgetId))
        {
            return false;
        }

        if (_cachedGroupContents.Remove(content.WidgetId, out IWidgetContent? replaced))
        {
            RemoveCachedGroupContentOrderEntry(content.WidgetId);
            DisposeCachedGroupContent(replaced);
        }

        while (_cachedGroupContents.Count >= CachedGroupContentCapacity &&
               _cachedGroupContentOrder.First is { } oldest)
        {
            _cachedGroupContentOrder.RemoveFirst();
            if (_cachedGroupContents.Remove(oldest.Value, out IWidgetContent? evicted))
            {
                DisposeCachedGroupContent(evicted);
            }
        }

        _cachedGroupContents[content.WidgetId] = content;
        _cachedGroupContentOrder.AddLast(content.WidgetId);
        App.LogVerbose($"[WidgetGroup] Cached inactive content member={content.WidgetId}");
        return true;
    }

    private void DisposeCachedGroupContents()
    {
        foreach (IWidgetContent content in _cachedGroupContents.Values.ToArray())
        {
            DisposeCachedGroupContent(content);
        }

        _cachedGroupContents.Clear();
        _cachedGroupContentOrder.Clear();
    }

    private void RemoveCachedGroupContentOrderEntry(string widgetId)
    {
        LinkedListNode<string>? node = _cachedGroupContentOrder.First;
        while (node is not null)
        {
            LinkedListNode<string>? next = node.Next;
            if (string.Equals(node.Value, widgetId, StringComparison.Ordinal))
            {
                _cachedGroupContentOrder.Remove(node);
                return;
            }

            node = next;
        }
    }

    private static void DisposeCachedGroupContent(IWidgetContent content)
    {
        try
        {
            (content as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetGroup] Cached content cleanup failed " +
                $"member={content.WidgetId}: {ex}");
        }
    }

    internal sealed class ContentWidgetSwitchPreparation : IDisposable
    {
        private readonly ContentWidgetWindow _owner;
        private readonly WidgetConfig _targetConfig;
        private readonly WidgetContentDescriptor _targetDescriptor;
        private WidgetShellContentHost.WidgetShellPreparedContent? _prepared;

        internal ContentWidgetSwitchPreparation(
            ContentWidgetWindow owner,
            WidgetConfig targetConfig,
            WidgetContentDescriptor targetDescriptor,
            WidgetShellContentHost.WidgetShellPreparedContent prepared)
        {
            _owner = owner;
            _targetConfig = targetConfig;
            _targetDescriptor = targetDescriptor;
            _prepared = prepared;
        }

        internal ContentWidgetSwitchTransition? BeginTransition()
        {
            WidgetShellContentHost.WidgetShellPreparedContent? prepared =
                Interlocked.Exchange(ref _prepared, null);
            return prepared is null
                ? null
                : _owner.BeginContentSwitch(
                    _targetConfig,
                    _targetDescriptor,
                    prepared);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _prepared, null)?.Dispose();
        }
    }

    internal sealed class ContentWidgetSwitchTransition : IDisposable
    {
        private readonly ContentWidgetWindow _owner;
        private readonly WidgetConfig _previousConfig;
        private readonly WidgetContentDescriptor _previousDescriptor;
        private readonly WidgetConfig _targetConfig;
        private readonly WidgetContentDescriptor _targetDescriptor;
        private readonly WidgetShellContentHost.WidgetShellContentTransition _hostTransition;
        private bool _isCompleted;

        internal ContentWidgetSwitchTransition(
            ContentWidgetWindow owner,
            WidgetConfig previousConfig,
            WidgetContentDescriptor previousDescriptor,
            WidgetConfig targetConfig,
            WidgetContentDescriptor targetDescriptor,
            WidgetShellContentHost.WidgetShellContentTransition hostTransition)
        {
            _owner = owner;
            _previousConfig = previousConfig;
            _previousDescriptor = previousDescriptor;
            _targetConfig = targetConfig;
            _targetDescriptor = targetDescriptor;
            _hostTransition = hostTransition;
        }

        internal IWidgetContent IncomingContent => _hostTransition.IncomingContent;

        internal IWidgetContent? OutgoingContent => _hostTransition.OutgoingContent;

        internal void Complete()
        {
            CompleteCore();
        }

        internal async Task CompleteAsync(
            WidgetGroupSwitchOrigin origin,
            bool forward,
            CancellationToken cancellationToken)
        {
            if (_isCompleted)
            {
                return;
            }

            try
            {
                _owner.ApplyMemberContext(
                    _targetConfig,
                    _targetDescriptor,
                    IncomingContent,
                    animateGroupIdentity: true,
                    origin,
                    forward);
                await _owner.AnimateContentSwitchAsync(
                    origin is not WidgetGroupSwitchOrigin.Programmatic,
                    forward,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _hostTransition.Complete();
                _isCompleted = true;
            }
            catch
            {
                try
                {
                    _hostTransition.Rollback();
                    if (OutgoingContent is { } outgoingContent)
                    {
                        _owner.ApplyMemberContext(
                            _previousConfig,
                            _previousDescriptor,
                            outgoingContent);
                    }
                }
                finally
                {
                    _isCompleted = true;
                }

                throw;
            }
        }

        private void CompleteCore()
        {
            if (_isCompleted)
            {
                return;
            }

            try
            {
                _owner.ApplyMemberContext(
                    _targetConfig,
                    _targetDescriptor,
                    IncomingContent);
                _hostTransition.Complete();
                _isCompleted = true;
            }
            catch
            {
                try
                {
                    _hostTransition.Rollback();
                    if (OutgoingContent is { } outgoingContent)
                    {
                        _owner.ApplyMemberContext(
                            _previousConfig,
                            _previousDescriptor,
                            outgoingContent);
                    }
                }
                finally
                {
                    _isCompleted = true;
                }

                throw;
            }
        }

        internal void Rollback()
        {
            if (_isCompleted)
            {
                return;
            }

            _hostTransition.Rollback();
            _isCompleted = true;
        }

        public void Dispose()
        {
            Rollback();
        }
    }
}
