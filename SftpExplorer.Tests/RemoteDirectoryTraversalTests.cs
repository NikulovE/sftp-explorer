using SftpExplorerWinUI.Services;

namespace SftpExplorerWinUI.Tests;

public sealed class RemoteDirectoryTraversalTests
{
    [Fact]
    public void CountsFilesAndBytesWithoutFollowingDirectorySymlinks()
    {
        var visited = new List<string>();
        var result = RemoteDirectoryTraversal.Count("/root", path =>
        {
            visited.Add(path);
            return path == "/root"
                ? [new(".", true, false, 0), new("..", true, false, 0),
                   new("child", true, false, 0), new("loop", true, true, 0),
                   new("file", false, false, 12)]
                : [new RemoteDirectoryEntry("nested", false, false, 30)];
        }, CancellationToken.None);

        Assert.Equal((2, 42L), result);
        Assert.Equal(["/root", "/root/child"], visited);
    }

    [Fact]
    public void DeepTreeIsCountedWithoutRecursiveCalls()
    {
        var depth = 0;
        var result = RemoteDirectoryTraversal.Count("/", _ =>
            ++depth <= RemoteDirectoryTraversal.MaximumDepth
                ? [new RemoteDirectoryEntry("child", true, false, 0)]
                : [new RemoteDirectoryEntry("leaf", false, false, 7)], CancellationToken.None);

        Assert.Equal((1, 7L), result);
    }

    [Fact]
    public void UnboundedServerTreeFailsWithIOExceptionBeforeStackOverflow()
    {
        var visits = 0;
        Assert.Throws<IOException>(() => RemoteDirectoryTraversal.Count("/", _ =>
        {
            visits++;
            return [new RemoteDirectoryEntry("loop", true, false, 0)];
        }, CancellationToken.None));
        Assert.Equal(RemoteDirectoryTraversal.MaximumDepth + 1, visits);
    }

    [Fact]
    public void CancellationStopsTraversalBeforeAnotherDirectoryIsRequested()
    {
        using var cancellation = new CancellationTokenSource();
        var visits = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => RemoteDirectoryTraversal.Count("/", _ =>
        {
            visits++;
            cancellation.Cancel();
            return [new RemoteDirectoryEntry("child", true, false, 0)];
        }, cancellation.Token));
        Assert.Equal(1, visits);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("")]
    [InlineData("bad\0name")]
    public void RejectsInvalidNamesReturnedByTheServer(string name)
    {
        Assert.Throws<IOException>(() => RemoteDirectoryTraversal.Count("/", _ =>
            [new RemoteDirectoryEntry(name, true, false, 0)], CancellationToken.None));
    }

    [Fact]
    public void UnreadableSubdirectoryDoesNotDiscardTotalsForAccessibleFiles()
    {
        var errors = new List<string>();
        var result = RemoteDirectoryTraversal.Count("/root", path => path switch
        {
            "/root" => [new("file", false, false, 12), new("child", true, false, 0),
                        new("denied", true, false, 0)],
            "/root/child" => [new RemoteDirectoryEntry("nested", false, false, 30)],
            _ => throw new IOException("Permission denied")
        }, CancellationToken.None, (path, _) => errors.Add(path));

        Assert.Equal((2, 42L), result);
        Assert.Equal(["/root/denied"], errors);
    }

    [Fact]
    public void RecursiveTransferGuardReportsExcessiveDepthAsAnOperationError()
    {
        RemoteDirectoryTraversal.CheckDepth(RemoteDirectoryTraversal.MaximumDepth);
        Assert.Throws<IOException>(() =>
            RemoteDirectoryTraversal.CheckDepth(RemoteDirectoryTraversal.MaximumDepth + 1));
    }
}
