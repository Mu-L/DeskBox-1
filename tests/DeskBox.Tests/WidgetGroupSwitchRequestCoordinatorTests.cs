using DeskBox.Controls;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetGroupSwitchRequestCoordinatorTests
{
    [Fact]
    public void Begin_CancelsPreviousRequestForTheSameGroup()
    {
        var coordinator = new WidgetGroupSwitchRequestCoordinator();
        WidgetGroupSwitchRequest first = coordinator.Begin("group", "b");

        WidgetGroupSwitchRequest second = coordinator.Begin("group", "c");

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(second.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(first));
        Assert.True(coordinator.IsCurrent(second));
    }

    [Fact]
    public void Begin_PreservesTheInputOriginForTheLatestIntent()
    {
        var coordinator = new WidgetGroupSwitchRequestCoordinator();

        WidgetGroupSwitchRequest request = coordinator.Begin(
            "group",
            "b",
            WidgetGroupSwitchOrigin.Wheel);

        Assert.Equal(WidgetGroupSwitchOrigin.Wheel, request.Origin);
    }

    [Fact]
    public void Begin_DoesNotCancelRequestsForOtherGroups()
    {
        var coordinator = new WidgetGroupSwitchRequestCoordinator();
        WidgetGroupSwitchRequest first = coordinator.Begin("group-1", "b");

        WidgetGroupSwitchRequest second = coordinator.Begin("group-2", "y");

        Assert.False(first.CancellationToken.IsCancellationRequested);
        Assert.False(second.CancellationToken.IsCancellationRequested);
        Assert.True(coordinator.IsCurrent(first));
        Assert.True(coordinator.IsCurrent(second));
    }

    [Fact]
    public void Begin_SameTarget_CancelsThePreviousRequest()
    {
        var coordinator = new WidgetGroupSwitchRequestCoordinator();
        WidgetGroupSwitchRequest first = coordinator.Begin("surface", "member-a");

        WidgetGroupSwitchRequest second = coordinator.Begin("surface", "member-a");

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(first));
        Assert.False(second.CancellationToken.IsCancellationRequested);
        Assert.True(coordinator.IsCurrent(second));
    }

    [Fact]
    public void IsCurrentTarget_RecognizesDuplicateIntentWithoutCancelingIt()
    {
        var coordinator = new WidgetGroupSwitchRequestCoordinator();
        WidgetGroupSwitchRequest request = coordinator.Begin(
            "surface",
            "member-a",
            WidgetGroupSwitchOrigin.Wheel);

        Assert.True(coordinator.IsCurrentTarget("surface", "member-a"));
        Assert.False(coordinator.IsCurrentTarget("surface", "member-b"));
        Assert.False(request.CancellationToken.IsCancellationRequested);
        Assert.True(coordinator.IsCurrent(request));
    }

    [Fact]
    public void CompletingSupersededRequest_DoesNotRemoveLatestRequest()
    {
        var coordinator = new WidgetGroupSwitchRequestCoordinator();
        WidgetGroupSwitchRequest first = coordinator.Begin("group", "b");
        WidgetGroupSwitchRequest second = coordinator.Begin("group", "c");

        coordinator.Complete(first);

        Assert.True(coordinator.IsCurrent(second));
        Assert.False(second.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_CancelsAndRemovesCurrentRequest()
    {
        var coordinator = new WidgetGroupSwitchRequestCoordinator();
        WidgetGroupSwitchRequest request = coordinator.Begin("group", "b");

        coordinator.Cancel("group");

        Assert.True(request.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(request));
    }

    [Fact]
    public void CancelAll_CancelsIndependentSurfaceRequests()
    {
        var coordinator = new WidgetGroupSwitchRequestCoordinator();
        WidgetGroupSwitchRequest first = coordinator.Begin("surface-1", "b");
        WidgetGroupSwitchRequest second = coordinator.Begin("surface-2", "y");

        coordinator.CancelAll();

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.True(second.CancellationToken.IsCancellationRequested);
        Assert.False(coordinator.IsCurrent(first));
        Assert.False(coordinator.IsCurrent(second));
    }

    [Fact]
    public void OneHundredRapidRequests_LeaveOnlyTheLastIntentCurrent()
    {
        var coordinator = new WidgetGroupSwitchRequestCoordinator();
        var requests = Enumerable.Range(0, 100)
            .Select(index => coordinator.Begin("surface", $"member-{index}"))
            .ToList();

        Assert.All(
            requests.Take(99),
            request =>
            {
                Assert.True(request.CancellationToken.IsCancellationRequested);
                Assert.False(coordinator.IsCurrent(request));
            });
        Assert.False(requests[^1].CancellationToken.IsCancellationRequested);
        Assert.True(coordinator.IsCurrent(requests[^1]));
    }

    [Fact]
    public void WheelGestureTail_IsSuppressedUntilTheSurfaceGoesQuiet()
    {
        var coordinator = new WidgetGroupSwitchRequestCoordinator();
        var startedAt = new DateTimeOffset(
            2026,
            8,
            11,
            15,
            45,
            2,
            TimeSpan.Zero);

        Assert.True(
            coordinator.TryAcceptWheelStep(
                "surface",
                startedAt,
                out _));
        WidgetGroupSwitchRequest completedRequest = coordinator.Begin(
            "surface",
            "member-b",
            WidgetGroupSwitchOrigin.Wheel);
        coordinator.Complete(completedRequest);

        // This matches the captured failure: the second request entered
        // about 600 ms after the first completed and switched a two-member
        // group back. Completing the switch must not clear the gesture gate.
        Assert.False(
            coordinator.TryAcceptWheelStep(
                "surface",
                startedAt.AddMilliseconds(600),
                out TimeSpan firstTailElapsed));
        Assert.Equal(TimeSpan.FromMilliseconds(600), firstTailElapsed);

        // A suppressed tail refreshes the quiet window, so a long inertial
        // stream still produces only one member switch.
        Assert.False(
            coordinator.TryAcceptWheelStep(
                "surface",
                startedAt.AddMilliseconds(1100),
                out TimeSpan secondTailElapsed));
        Assert.Equal(TimeSpan.FromMilliseconds(500), secondTailElapsed);

        Assert.True(
            coordinator.TryAcceptWheelStep(
                "surface",
                startedAt.AddMilliseconds(1801),
                out TimeSpan nextGestureElapsed));
        Assert.Equal(TimeSpan.FromMilliseconds(701), nextGestureElapsed);
    }

    [Fact]
    public void WheelGestureGate_IsIndependentPerSurface()
    {
        var coordinator = new WidgetGroupSwitchRequestCoordinator();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.True(coordinator.TryAcceptWheelStep("surface-1", now, out _));
        Assert.True(coordinator.TryAcceptWheelStep("surface-2", now, out _));
        Assert.False(
            coordinator.TryAcceptWheelStep(
                "surface-1",
                now.AddMilliseconds(100),
                out _));
    }
}
