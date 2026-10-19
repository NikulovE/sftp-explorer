using SftpExplorerWinUI.Services;

namespace SftpExplorerWinUI.Tests;

public sealed class DialogQueueTests
{
    [Fact]
    public async Task ConcurrentRequestsWaitUntilTheVisibleDialogCloses()
    {
        var queue = new DialogQueue();
        var closed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.ShowAsync(() => closed.Task, CancellationToken.None);
        var secondShown = false;
        var second = queue.ShowAsync(() =>
        {
            secondShown = true;
            return Task.FromResult(2);
        }, CancellationToken.None);

        Assert.False(secondShown);
        closed.SetResult(1);
        Assert.Equal(1, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task CancelledQueuedDialogIsNeverShownAndDoesNotReleaseTheActiveDialog()
    {
        var queue = new DialogQueue();
        var closed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = queue.ShowAsync(() => closed.Task, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = queue.ShowAsync<int>(() => throw new InvalidOperationException("Must not show"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var next = queue.ShowAsync(() => Task.FromResult(3), CancellationToken.None);
        Assert.False(next.IsCompleted);
        closed.SetResult(1);
        await active.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, await next.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedDialogReleasesQueueForTheNextRequest(bool synchronousFailure)
    {
        var queue = new DialogQueue();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ShowAsync<int>(
            () => synchronousFailure
                ? throw new InvalidOperationException()
                : Task.FromException<int>(new InvalidOperationException()), CancellationToken.None));

        Assert.Equal(2, await queue.ShowAsync(() => Task.FromResult(2), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WindowShutdownCancelsAllWaitingDialogs()
    {
        var queue = new DialogQueue();
        using var lifetime = new CancellationTokenSource();
        var closed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = queue.ShowAsync(() => closed.Task, lifetime.Token);
        var waiting = Enumerable.Range(0, 20).Select(_ => queue.ShowAsync<int>(
            () => throw new InvalidOperationException("Must not show after shutdown"), lifetime.Token)).ToArray();

        lifetime.Cancel();
        foreach (var request in waiting)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        closed.SetResult(1);
        await active.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
