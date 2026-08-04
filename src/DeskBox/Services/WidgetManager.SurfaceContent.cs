using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Views;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

public sealed partial class WidgetManager
{
    /// <summary>
    /// Creates content plans for every member kind already migrated off a
    /// top-level, type-specific host. Quick Capture is injected here because
    /// its store is process-wide and owned by WidgetManager/App services.
    /// </summary>
    private ContentWidgetWindowFactory CreateSurfaceContentWindowFactory()
    {
        return new ContentWidgetWindowFactory(
            new WidgetContentFactory(_localizationService),
            _settingsService,
            quickCaptureContentFactory: CreateQuickCaptureSurfaceContent,
            fileContentFactory: CreateFileSurfaceContent);
    }

    private IWidgetContent CreateQuickCaptureSurfaceContent(WidgetConfig config)
    {
        return new QuickCaptureSurfaceContent(
            config,
            _quickCaptureService,
            _settingsService,
            _localizationService,
            DispatcherQueue.GetForCurrentThread());
    }

    private IWidgetContent CreateFileSurfaceContent(WidgetConfig config)
    {
        return new FileSurfaceContent(
            config,
            _fileService,
            _organizerService,
            _settingsService,
            _localizationService,
            DispatcherQueue.GetForCurrentThread());
    }

    /// <summary>
    /// A topology change may start from a loaded standalone legacy window.
    /// Promote it once, while the group is being established, so every later
    /// member switch is a content transaction on the same Surface HWND.
    /// </summary>
    private async Task PromoteGroupToUnifiedSurfaceHostAsync(
        WidgetGroupConfig group,
        Func<Task>? beforeRetireAsync = null,
        bool preserveRaisedLayer = false)
    {
        IDesktopWidgetWindow? loaded = GetLoadedWindow(group.ActiveMemberId);
        if (loaded is ContentWidgetWindow ||
            (loaded is null && !group.IsVisible))
        {
            if (beforeRetireAsync is not null)
            {
                await beforeRetireAsync();
            }
            return;
        }

        WidgetConfig? config = FindConfig(group.ActiveMemberId);
        if (config is null)
        {
            return;
        }

        bool showCandidateRaised = preserveRaisedLayer ||
                                   ShouldPreserveRaisedWidgetLayer(
                                       group.ActiveMemberId);

        // CreateContentWidgetFromConfigAsync registers the new Surface host,
        // so retiring by member id afterwards could resolve to the new window;
        // retain and retire the exact legacy instance in the commit callback.
        ContentWidgetWindow unifiedHost =
            await WidgetSurfacePromotionTransaction.ExecuteAsync(
                prepareCandidateAsync: () => CreateContentWidgetFromConfigAsync(
                    config,
                    keepPreparedForAnimation: true),
                presentCandidateAsync: async candidate =>
                {
                    if (!group.IsVisible)
                    {
                        return;
                    }

                    if (showCandidateRaised)
                    {
                        candidate.ShowPreparedRaisedFromTray(
                            persistVisibility: false);
                    }
                    else
                    {
                        candidate.ShowPreparedAtDesktopLayer(
                            persistVisibility: false);
                    }

                    candidate.CompleteTrayShowWithoutAnimation();
                    if (showCandidateRaised && !_widgetsRaisedFromTray)
                    {
                        candidate.RaiseTemporarilyFromManager();
                    }

                    using var frameTimeout = new CancellationTokenSource(
                        WidgetGroupFirstFrameTimeout);
                    await candidate.WaitForFirstPresentedFrameAsync(
                        frameTimeout.Token);
                },
                commitAndRetireLegacyAsync: async candidate =>
                {
                    if (beforeRetireAsync is not null)
                    {
                        await beforeRetireAsync();
                    }
                    CommitSurfaceHost(group, candidate);
                    if (loaded is not null)
                    {
                        RetireSpecificLoadedWindowForGroup(
                            group.ActiveMemberId,
                            loaded,
                            keepConfigVisible: group.IsVisible);
                    }
                },
                rollbackCandidate: candidate =>
                {
                    if (_contentWidgets.TryGetValue(
                            config.Id,
                            out var registeredCandidate) &&
                        ReferenceEquals(registeredCandidate, candidate))
                    {
                        _contentWidgets.Remove(config.Id);
                    }

                    _widgetWindowHandles.Remove(candidate.WindowHandle);
                    UnregisterSurfaceHost(candidate);
                    CloseFailedCreatedWindow(
                        config.Id,
                        candidate,
                        preserveVisibility: true);
                });

        App.Log(
            $"[WidgetSurface] Promoted group={group.Id} " +
            $"surface={group.SurfaceId} member={group.ActiveMemberId}");
    }
}
