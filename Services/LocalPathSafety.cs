using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SftpExplorerWinUI.Services;

/// <summary>
/// Converts an untrusted, single remote file name into a path that is safe to use
/// below a caller-owned local directory.
/// </summary>
public static class LocalPathSafety
{
    private static readonly char[] WindowsInvalidNameCharacters =
    [
        '<', '>', ':', '"', '/', '\\', '|', '?', '*'
    ];

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Returns a local path for exactly one remote path component. The name is
    /// validated using Windows rules even when tests run on another operating system.
    /// </summary>
    /// <exception cref="ArgumentException">The remote name is not a safe Windows file name.</exception>
    /// <exception cref="InvalidOperationException">The resulting path is not a strict child of the root.</exception>
    public static string CombineChild(string rootDirectory, string remoteName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ValidateSingleName(remoteName);

        var root = Path.GetFullPath(rootDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, remoteName));
        EnsureStrictDescendant(root, candidate);
        return candidate;
    }

    /// <summary>
    /// Reserves a Windows-equivalent name and returns its safe child path. This
    /// prevents entries such as "file.txt" and "FILE.TXT" from overwriting each
    /// other on the usual case-insensitive Windows file system.
    /// </summary>
    /// <exception cref="IOException">Another remote entry already reserved the same local name.</exception>
    public static string ReserveChild(
        string rootDirectory,
        string remoteName,
        ISet<string> reservedCollisionKeys)
    {
        ArgumentNullException.ThrowIfNull(reservedCollisionKeys);
        var collisionKey = GetCollisionKey(remoteName);
        if (!reservedCollisionKeys.Add(collisionKey))
        {
            throw new IOException($"Multiple remote entries map to the same local name: '{remoteName}'.");
        }

        return CombineChild(rootDirectory, remoteName);
    }

    /// <summary>
    /// Produces a stable key for detecting Windows case and Unicode-normalization collisions.
    /// </summary>
    public static string GetCollisionKey(string remoteName)
    {
        ValidateSingleName(remoteName);
        return remoteName.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    /// <summary>
    /// Ensures a candidate is a strict descendant of a caller-owned root. This is
    /// intended for guarding cleanup operations: the root itself is never accepted.
    /// </summary>
    public static string EnsureStrictDescendant(string rootDirectory, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        var root = Path.GetFullPath(rootDirectory);
        var candidate = Path.GetFullPath(candidatePath);
        var relative = Path.GetRelativePath(root, candidate);

        if (relative == "." ||
            Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Path '{candidate}' is not a child of '{root}'.");
        }

        return candidate;
    }

    public static void ValidateSingleName(string remoteName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteName);

        if (remoteName is "." or "..")
            throw new ArgumentException("Relative path components are not valid remote names.", nameof(remoteName));

        if (remoteName.Length > 255)
            throw new ArgumentException("The remote name is too long for a Windows file name.", nameof(remoteName));

        if (remoteName.EndsWith(' ') || remoteName.EndsWith('.'))
            throw new ArgumentException("Windows file names cannot end with a space or period.", nameof(remoteName));

        if (remoteName.IndexOfAny(WindowsInvalidNameCharacters) >= 0 ||
            remoteName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The remote name contains a path separator or an invalid Windows file-name character.", nameof(remoteName));
        }

        foreach (var character in remoteName)
        {
            if (char.IsControl(character))
                throw new ArgumentException("The remote name contains a control character.", nameof(remoteName));
        }

        var firstDot = remoteName.IndexOf('.');
        var deviceStem = firstDot >= 0 ? remoteName[..firstDot] : remoteName;
        if (WindowsReservedNames.Contains(deviceStem))
            throw new ArgumentException("The remote name is reserved by Windows.", nameof(remoteName));
    }
}
