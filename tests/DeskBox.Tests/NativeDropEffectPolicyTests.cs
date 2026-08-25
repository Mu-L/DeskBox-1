using DeskBox.Helpers;

namespace DeskBox.Tests;

public sealed class NativeDropEffectPolicyTests
{
    [Fact]
    public void Feedback_DefaultsToMoveForPhysicalFiles()
    {
        Assert.Equal(
            NativeDropEffectPolicy.Move,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: NativeDropEffectPolicy.Copy |
                                NativeDropEffectPolicy.Move));
    }

    [Fact]
    public void Feedback_PhysicalFilesIgnoreAnAvailableLinkEffect()
    {
        Assert.Equal(
            NativeDropEffectPolicy.Move,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: NativeDropEffectPolicy.Copy |
                                NativeDropEffectPolicy.Move |
                                NativeDropEffectPolicy.Link));
    }

    [Fact]
    public void Feedback_UsesCopyForControlOrVirtualFiles()
    {
        const uint controlKeyState = 0x0008;
        uint allowed = NativeDropEffectPolicy.Copy |
                       NativeDropEffectPolicy.Move;

        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: controlKeyState,
                allowedEffects: allowed));
        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: true,
                keyState: 0,
                allowedEffects: allowed));
    }

    [Fact]
    public void Feedback_UsesConfiguredCopyUnlessShiftForcesMove()
    {
        const uint shiftKeyState = 0x0004;
        uint allowed = NativeDropEffectPolicy.Copy |
                       NativeDropEffectPolicy.Move;

        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: allowed,
                defaultMove: false));
        Assert.Equal(
            NativeDropEffectPolicy.Move,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: shiftKeyState,
                allowedEffects: allowed,
                defaultMove: false));
    }

    [Fact]
    public void Feedback_ControlWinsWhenControlAndShiftAreBothPressed()
    {
        const uint controlAndShiftKeyState = 0x0008 | 0x0004;

        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: controlAndShiftKeyState,
                allowedEffects: NativeDropEffectPolicy.Copy |
                                NativeDropEffectPolicy.Move));
    }

    [Fact]
    public void Completion_NeverAuthorizesSourceMoveCleanup()
    {
        uint allowed = NativeDropEffectPolicy.Copy |
                       NativeDropEffectPolicy.Move;

        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveCompletionEffect(
                hasExtractedPaths: true,
                allowedEffects: allowed));
        Assert.Equal(
            NativeDropEffectPolicy.None,
            NativeDropEffectPolicy.ResolveCompletionEffect(
                hasExtractedPaths: true,
                allowedEffects: NativeDropEffectPolicy.Move));
        Assert.Equal(
            NativeDropEffectPolicy.None,
            NativeDropEffectPolicy.ResolveCompletionEffect(
                hasExtractedPaths: false,
                allowedEffects: allowed));
    }

    [Fact]
    public void ShellApplications_UseLinkWithoutChangingFileCopyMovePolicy()
    {
        uint allowed = NativeDropEffectPolicy.Copy |
                       NativeDropEffectPolicy.Move |
                       NativeDropEffectPolicy.Link;

        Assert.Equal(
            NativeDropEffectPolicy.Link,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: allowed,
                hasShellApplicationData: true));
        Assert.Equal(
            NativeDropEffectPolicy.Link,
            NativeDropEffectPolicy.ResolveCompletionEffect(
                hasExtractedPaths: true,
                allowedEffects: allowed,
                createdShellApplicationLinks: true));
        Assert.Equal(
            NativeDropEffectPolicy.Copy,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: NativeDropEffectPolicy.Copy,
                hasShellApplicationData: true));
        Assert.Equal(
            NativeDropEffectPolicy.None,
            NativeDropEffectPolicy.ResolveFeedbackEffect(
                hasFileData: true,
                hasVirtualFileData: false,
                keyState: 0,
                allowedEffects: NativeDropEffectPolicy.Move,
                hasShellApplicationData: true));
    }
}
