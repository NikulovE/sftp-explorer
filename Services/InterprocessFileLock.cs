using System.Diagnostics;

namespace SftpExplorerWinUI.Services;

/// <summary>
/// A crash-safe, cross-process lease backed by an exclusively opened file.
/// The file is intentionally kept after release: deleting lock files creates a
/// race where different processes can lock different file objects at once.
/// </summary>
internal sealed class InterprocessFileLock : IDisposable, IAsyncDisposable
{
    private const int RetryDelayMilliseconds = 50;
    private FileStream? _stream;

    private InterprocessFileLock(FileStream stream)
    {
        _stream = stream;
    }

    public static InterprocessFileLock Acquire(
        string directory,
        string lockFileName,
        TimeSpan timeout)
    {
        var lockPath = PrepareLockPath(directory, lockFileName, timeout);
        var stopwatch = Stopwatch.StartNew();
        IOException? lastContention = null;

        while (true)
        {
            try
            {
                return new InterprocessFileLock(OpenExclusive(lockPath));
            }
            catch (IOException ex) when (stopwatch.Elapsed < timeout)
            {
                lastContention = ex;
                Thread.Sleep(RetryDelayMilliseconds);
            }
            catch (IOException ex)
            {
                throw CreateTimeoutException(lockPath, timeout, ex);
            }

            if (stopwatch.Elapsed >= timeout)
            {
                throw CreateTimeoutException(lockPath, timeout, lastContention);
            }
        }
    }

    public static async Task<InterprocessFileLock> AcquireAsync(
        string directory,
        string lockFileName,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var lockPath = PrepareLockPath(directory, lockFileName, timeout);
        var stopwatch = Stopwatch.StartNew();
        IOException? lastContention = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new InterprocessFileLock(OpenExclusive(lockPath));
            }
            catch (IOException ex) when (stopwatch.Elapsed < timeout)
            {
                lastContention = ex;
                await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw CreateTimeoutException(lockPath, timeout, ex);
            }

            if (stopwatch.Elapsed >= timeout)
            {
                throw CreateTimeoutException(lockPath, timeout, lastContention);
            }
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static FileStream OpenExclusive(string lockPath) => new(
        lockPath,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None,
        bufferSize: 1,
        FileOptions.None);

    private static string PrepareLockPath(
        string directory,
        string lockFileName,
        TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFileName);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (lockFileName.IndexOfAny(['/', '\\']) >= 0 ||
            !string.Equals(Path.GetFileName(lockFileName), lockFileName, StringComparison.Ordinal) ||
            lockFileName is "." or "..")
        {
            throw new ArgumentException("The lock name must be a single file name.", nameof(lockFileName));
        }

        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        return Path.Combine(fullDirectory, lockFileName);
    }

    private static IOException CreateTimeoutException(
        string lockPath,
        TimeSpan timeout,
        Exception? innerException) =>
        new(
            $"Timed out after {timeout.TotalSeconds:0.#} seconds waiting for the application data lock '{lockPath}'.",
            innerException);
}
