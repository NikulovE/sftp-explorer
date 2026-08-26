using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SftpExplorerWinUI.Services;

internal static class DragCacheService
{
    private const string CacheFolderName = "SftpDragTemp";
    private static readonly TimeSpan OrphanRetention = TimeSpan.FromHours(24);

    internal static Task CleanupExpiredSessionsAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                if (IsAnotherInstanceRunning())
                {
                    Log.Info("Drag cache cleanup skipped because another SFTP Explorer instance is running");
                    return;
                }

                var rootPath = GetRootPath();
                if (!Directory.Exists(rootPath))
                {
                    return;
                }

                var cutoff = DateTime.UtcNow - OrphanRetention;
                foreach (var candidate in Directory.EnumerateDirectories(rootPath, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (!TryValidateSessionPath(candidate, out var sessionPath) ||
                            Directory.GetLastWriteTimeUtc(sessionPath) > cutoff)
                        {
                            continue;
                        }

                        var attributes = File.GetAttributes(sessionPath);
                        Directory.Delete(
                            sessionPath,
                            recursive: (attributes & FileAttributes.ReparsePoint) == 0);
                        Log.Info($"Deleted expired drag cache session '{sessionPath}'");
                    }
                    catch (Exception ex)
                    {
                        // Explorer may still hold a file open. Leave the session intact and try
                        // again on the next application start rather than risking an active copy.
                        Log.Warning($"Could not delete drag cache session '{candidate}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Drag cache cleanup failed: {ex.Message}");
            }
        });
    }

    private static bool IsAnotherInstanceRunning()
    {
        using var currentProcess = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(currentProcess.ProcessName);
        try
        {
            foreach (var process in processes)
            {
                if (process.Id != currentProcess.Id)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool TryValidateSessionPath(string candidate, out string validatedPath)
    {
        validatedPath = string.Empty;
        try
        {
            var rootPath = GetRootPath();
            var fullPath = Path.GetFullPath(candidate)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parentPath = Path.GetDirectoryName(fullPath);
            var sessionName = Path.GetFileName(fullPath);

            if (!string.Equals(parentPath, rootPath, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(sessionName, "N", out _))
            {
                return false;
            }

            validatedPath = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetRootPath()
    {
        return Path.GetFullPath(Path.Combine(Path.GetTempPath(), CacheFolderName))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
