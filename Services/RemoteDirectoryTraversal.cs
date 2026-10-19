namespace SftpExplorerWinUI.Services;

internal readonly record struct RemoteDirectoryEntry(
    string Name, bool IsDirectory, bool IsSymbolicLink, long Length);

internal static class RemoteDirectoryTraversal
{
    // Bound traversal even when a server exposes a cycle as ordinary directories.
    internal const int MaximumDepth = 256;

    internal static void CheckDepth(int depth)
    {
        if (depth > MaximumDepth)
        {
            throw new IOException($"Remote directory nesting exceeds {MaximumDepth} levels.");
        }
    }

    internal static (int FileCount, long ByteCount) Count(
        string root,
        Func<string, IEnumerable<RemoteDirectoryEntry>> listDirectory,
        CancellationToken cancellationToken,
        Action<string, Exception>? onDirectoryError = null)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        int fileCount = 0;
        long byteCount = 0;
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in listDirectory(directory.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Name is "." or "..") continue;
                    if (string.IsNullOrEmpty(entry.Name) || entry.Name.IndexOfAny(['/', '\0']) >= 0)
                    {
                        throw new IOException("The server returned an invalid directory entry name.");
                    }

                    if (entry.IsDirectory)
                    {
                        if (entry.IsSymbolicLink) continue;
                        CheckDepth(directory.Depth + 1);
                        pending.Push(($"{directory.Path.TrimEnd('/')}/{entry.Name}", directory.Depth + 1));
                    }
                    else
                    {
                        checked
                        {
                            fileCount++;
                            byteCount += entry.Length;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (onDirectoryError != null)
            {
                // Counting is best effort: an unreadable subtree must not discard
                // the totals for accessible files in its siblings.
                onDirectoryError(directory.Path, ex);
            }
        }
        return (fileCount, byteCount);
    }
}
