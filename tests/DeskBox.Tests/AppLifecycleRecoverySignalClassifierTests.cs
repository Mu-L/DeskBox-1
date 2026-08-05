using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class AppLifecycleRecoverySignalClassifierTests
{
    private const uint TaskbarCreatedMessage = 0xC123;

    [Theory]
    [InlineData(0x0012)]
    [InlineData(0x0007)]
    [InlineData(0x0006)]
    public void PowerResumeSignals_RequestExternalRecovery(uint powerEvent)
    {
        string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
            AppLifecycleRecoverySignalClassifier.WmPowerBroadcast,
            new UIntPtr(powerEvent),
            TaskbarCreatedMessage);

        Assert.Equal("resume", reason);
    }

    [Fact]
    public void PowerSuspendSignal_WaitsForResumeBeforeRecovery()
    {
        string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
            AppLifecycleRecoverySignalClassifier.WmPowerBroadcast,
            new UIntPtr(0x0004),
            TaskbarCreatedMessage);

        Assert.Null(reason);
    }

    [Theory]
    [InlineData(AppLifecycleRecoverySignalClassifier.WmDisplayChange)]
    [InlineData(AppLifecycleRecoverySignalClassifier.WmDpiChanged)]
    public void DisplayAndDpiSignals_RequestPositionRecovery(uint message)
    {
        string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
            message,
            UIntPtr.Zero,
            TaskbarCreatedMessage);

        Assert.Equal("display-message", reason);
    }

    [Fact]
    public void ExplorerRestartSignal_RequestsShellRecovery()
    {
        string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
            TaskbarCreatedMessage,
            UIntPtr.Zero,
            TaskbarCreatedMessage);

        Assert.Equal("explorer-restart", reason);
    }

    [Theory]
    [InlineData(0x0008, "session-unlock")]
    [InlineData(0x0005, "session-reconnect")]
    [InlineData(0x0009, "session-reconnect")]
    public void SessionRecoverySignals_AreClassified(uint sessionEvent, string expected)
    {
        string? reason = AppLifecycleRecoverySignalClassifier.ResolveRecoveryReason(
            AppLifecycleRecoverySignalClassifier.WmWtsSessionChange,
            new UIntPtr(sessionEvent),
            TaskbarCreatedMessage);

        Assert.Equal(expected, reason);
    }
}
