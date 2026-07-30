using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class WidgetSurfaceRegistryTests
{
    [Fact]
    public void CommitActive_KeepsSurfaceIdentityAndPromotesPreparedHost()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var previousHost = new object();
        var targetHost = new object();
        registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            previousHost);

        Assert.True(registry.StageCandidate("surface", "b", targetHost));
        WidgetSurfaceSession<object> committed = registry.CommitActive(
            CreateDefinition("surface", "b", "a", "b"),
            targetHost);

        Assert.Equal("surface", committed.SurfaceId);
        Assert.Equal("b", committed.ActiveMemberId);
        Assert.Same(targetHost, committed.Host);
        Assert.Null(committed.CandidateHost);
        Assert.True(registry.TryGetByMember("a", out var fromPreviousAlias));
        Assert.Same(committed, fromPreviousAlias);
        Assert.True(registry.TryGetByMember("b", out var fromActiveAlias));
        Assert.Same(committed, fromActiveAlias);
    }

    [Fact]
    public void CommitActive_AllowsPersistentHostWithoutCandidateReplacement()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var host = new object();
        registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            host);

        WidgetSurfaceSession<object> committed = registry.CommitActive(
            CreateDefinition("surface", "b", "a", "b"),
            host);

        Assert.Same(host, committed.Host);
        Assert.Equal("b", committed.ActiveMemberId);
    }

    [Fact]
    public void CancelCandidate_PreservesActiveHostAndMember()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var previousHost = new object();
        var targetHost = new object();
        WidgetSurfaceSession<object> session = registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            previousHost);
        registry.StageCandidate("surface", "b", targetHost);

        Assert.True(registry.CancelCandidate("surface", targetHost));

        Assert.Same(previousHost, session.Host);
        Assert.Equal("a", session.ActiveMemberId);
        Assert.Null(session.CandidateHost);
    }

    [Fact]
    public void UpdateDefinition_ReindexesMembersWithoutChangingHost()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var host = new object();
        WidgetSurfaceSession<object> session = registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            host);

        Assert.True(registry.UpdateDefinition(
            CreateDefinition("surface", "a", "a", "c")));

        Assert.False(registry.TryGetByMember("b", out _));
        Assert.True(registry.TryGetByMember("c", out var fromNewMember));
        Assert.Same(session, fromNewMember);
        Assert.Same(host, session.Host);
    }

    [Fact]
    public void SynchronizeActive_ReconcilesStableHostAfterTopologyChange()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var standaloneHost = new object();
        var groupHost = new object();
        registry.RegisterActive(
            new WidgetSurfaceDefinition("a", null, ["a"], "a"),
            standaloneHost);

        registry.RemoveSurface("a");
        WidgetSurfaceSession<object> session = registry.SynchronizeActive(
            CreateDefinition("surface", "a", "a", "b"),
            groupHost);

        Assert.Equal("surface", session.SurfaceId);
        Assert.Same(groupHost, session.Host);
        Assert.True(registry.TryGetByMember("b", out var memberSession));
        Assert.Same(session, memberSession);
    }

    [Fact]
    public void DifferentSurfacesOwnDifferentSwitchGates()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        WidgetSurfaceSession<object> first = registry.RegisterActive(
            CreateDefinition("surface-1", "a", "a", "b"),
            new object());
        WidgetSurfaceSession<object> second = registry.RegisterActive(
            CreateDefinition("surface-2", "c", "c", "d"),
            new object());

        Assert.NotSame(first.SwitchGate, second.SwitchGate);
    }

    [Fact]
    public void UnregisteringRetiredHostDoesNotRemovePromotedSurface()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        var previousHost = new object();
        var targetHost = new object();
        registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            previousHost);
        registry.StageCandidate("surface", "b", targetHost);
        registry.CommitActive(
            CreateDefinition("surface", "b", "a", "b"),
            targetHost);

        Assert.Equal(0, registry.UnregisterHost(previousHost));
        Assert.True(registry.TryGet("surface", out var session));
        Assert.Same(targetHost, session!.Host);
    }

    [Fact]
    public async Task RemovingSurfaceWhileGateIsHeld_AllowsInFlightRelease()
    {
        var registry = new WidgetSurfaceRegistry<object>();
        WidgetSurfaceSession<object> session = registry.RegisterActive(
            CreateDefinition("surface", "a", "a", "b"),
            new object());
        await session.SwitchGate.WaitAsync();

        Assert.True(registry.RemoveSurface("surface"));
        Exception? releaseFailure = Record.Exception(
            () => { session.SwitchGate.Release(); });

        Assert.Null(releaseFailure);
        Assert.False(registry.TryGet("surface", out _));
    }

    private static WidgetSurfaceDefinition CreateDefinition(
        string surfaceId,
        string activeMemberId,
        params string[] memberIds)
    {
        return new WidgetSurfaceDefinition(
            surfaceId,
            $"group-{surfaceId}",
            memberIds,
            activeMemberId);
    }
}
