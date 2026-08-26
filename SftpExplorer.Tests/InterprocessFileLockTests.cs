using SftpExplorerWinUI.Services;

namespace SftpExplorerWinUI.Tests;

public sealed class InterprocessFileLockTests
{
    [Fact]
    public async Task ExclusiveLeaseTimesOutWhileHeldAndCanBeReacquiredAfterRelease()
    {
        using var temp = new TemporaryDirectory();
        using (InterprocessFileLock.Acquire(temp.Path, ".test.lock", TimeSpan.FromSeconds(1)))
        {
            await Assert.ThrowsAsync<IOException>(() => Task.Run(() =>
            {
                using var _ = InterprocessFileLock.Acquire(
                    temp.Path,
                    ".test.lock",
                    TimeSpan.FromMilliseconds(150));
            }));
        }

        using var reacquired = await InterprocessFileLock.AcquireAsync(
            temp.Path,
            ".test.lock",
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
    }

    [Fact]
    public async Task WaitingForLeaseHonorsCancellation()
    {
        using var temp = new TemporaryDirectory();
        using var held = InterprocessFileLock.Acquire(
            temp.Path,
            ".test.lock",
            TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InterprocessFileLock.AcquireAsync(
                temp.Path,
                ".test.lock",
                TimeSpan.FromSeconds(5),
                cancellation.Token));
    }

    [Fact]
    public async Task AsyncLeaseTimesOutWhileHeldAndReleasesAfterDispose()
    {
        using var temp = new TemporaryDirectory();
        await using (var held = await InterprocessFileLock.AcquireAsync(
                       temp.Path, ".test.lock", TimeSpan.FromSeconds(1), CancellationToken.None))
        {
            await Assert.ThrowsAsync<IOException>(() => InterprocessFileLock.AcquireAsync(
                temp.Path, ".test.lock", TimeSpan.FromMilliseconds(150), CancellationToken.None));
        }

        // The same lease can be taken again after the async dispose.
        await using var reacquired = await InterprocessFileLock.AcquireAsync(
            temp.Path, ".test.lock", TimeSpan.FromSeconds(1), CancellationToken.None);
    }

    [Fact]
    public void SecondAcquisitionOnTheSameThreadIsStillExclusive()
    {
        // The lease is a plain exclusive file handle: even the same thread that
        // holds it cannot open the lock file a second time. ConnectionManager
        // relies on this and nests leases through its depth counter instead.
        using var temp = new TemporaryDirectory();
        using var first = InterprocessFileLock.Acquire(temp.Path, ".test.lock", TimeSpan.FromSeconds(1));

        Assert.Throws<IOException>(() => InterprocessFileLock.Acquire(
            temp.Path, ".test.lock", TimeSpan.FromMilliseconds(150)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AcquireRejectsBlankDirectories(string directory)
    {
        Assert.Throws<ArgumentException>(() => InterprocessFileLock.Acquire(
            directory, ".test.lock", TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..\\..\\escape.lock")]
    [InlineData(".")]
    [InlineData("..")]
    public void AcquireRejectsEscapingOrInvalidLockNames(string lockFileName)
    {
        using var temp = new TemporaryDirectory();
        Assert.Throws<ArgumentException>(() => InterprocessFileLock.Acquire(
            temp.Path, lockFileName, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AcquireRejectsNonPositiveTimeouts(double seconds)
    {
        using var temp = new TemporaryDirectory();
        Assert.Throws<ArgumentOutOfRangeException>(() => InterprocessFileLock.Acquire(
            temp.Path, ".test.lock", TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        using var temp = new TemporaryDirectory();
        var lease = InterprocessFileLock.Acquire(temp.Path, ".test.lock", TimeSpan.FromSeconds(1));
        lease.Dispose();
        lease.Dispose(); // Must not throw.

        // The lock file is intentionally kept after release.
        Assert.True(File.Exists(Path.Combine(temp.Path, ".test.lock")));
    }

    [Fact]
    public void AcquireCreatesMissingStorageDirectory()
    {
        using var temp = new TemporaryDirectory();
        var nested = Path.Combine(temp.Path, "does-not-exist", "yet");

        using var lease = InterprocessFileLock.Acquire(nested, ".test.lock", TimeSpan.FromSeconds(1));

        Assert.True(File.Exists(Path.Combine(nested, ".test.lock")));
    }
}
