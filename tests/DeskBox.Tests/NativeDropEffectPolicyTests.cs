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
}
