using System.Windows.Interop;

namespace SftpExplorerWinUI.Tests;

public sealed class NativeWindowResizeOrderTests
{
    [Fact]
    public void RendererResizeIsRequestedAfterHostedWindowAcceptsItsRectangle()
    {
        var operations = new List<string>();

        bool applied = NativeWindowResizeOrder.Apply(
            () =>
            {
                operations.Add("SetWindowPos");
                return true;
            },
            () => operations.Add("TerminalTriggerResize"));

        Assert.True(applied);
        Assert.Equal(["SetWindowPos", "TerminalTriggerResize"], operations);
    }

    [Fact]
    public void FailedHostedWindowResizeDoesNotRequestRendererResize()
    {
        bool rendererResizeRequested = false;

        bool applied = NativeWindowResizeOrder.Apply(
            () => false,
            () => rendererResizeRequested = true);

        Assert.False(applied);
        Assert.False(rendererResizeRequested);
    }

    [Fact]
    public void ReentrantSizeChangeWhileApplyingNativePositionIsNotQueued()
    {
        bool shouldQueue = NativeWindowPositionUpdatePolicy.ShouldQueue(
            isDisposed: false,
            isApplyingWindowPosition: true,
            hasValidSize: true,
            matchesLastXamlSize: false);

        Assert.False(shouldQueue);
    }

    [Fact]
    public void NewXamlSizeQueuesAfterThePreviousNativePositionCompletes()
    {
        bool shouldQueue = NativeWindowPositionUpdatePolicy.ShouldQueue(
            isDisposed: false,
            isApplyingWindowPosition: false,
            hasValidSize: true,
            matchesLastXamlSize: false);

        Assert.True(shouldQueue);
    }
}
