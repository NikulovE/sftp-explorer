using System;
using System.IO;
using System.Text.Json;

namespace SftpExplorerWinUI.Services;

public enum AtomicJsonLoadSource
{
    NewValue,
    Primary,
    Backup
}

public sealed record AtomicJsonLoadResult<T>(
    T Value,
    AtomicJsonLoadSource Source,
    Exception? PrimaryError = null,
    Exception? RepairError = null);

/// <summary>
/// Persists small JSON documents without exposing a partially-written primary file.
/// The previous valid document is retained beside it with a <c>.bak</c> suffix.
/// </summary>
public static class AtomicJsonFile
{
    public static string GetBackupPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path) + ".bak";
    }

    public static AtomicJsonLoadResult<T> Load<T>(
        string path,
        Func<T> createDefault,
        JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(createDefault);

        var primaryPath = Path.GetFullPath(path);
        var backupPath = GetBackupPath(primaryPath);
        Exception? primaryError = null;

        if (File.Exists(primaryPath))
        {
            try
            {
                return new AtomicJsonLoadResult<T>(ReadRequired<T>(primaryPath, options), AtomicJsonLoadSource.Primary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                primaryError = ex;
            }
        }
        else
        {
            primaryError = new FileNotFoundException("The primary JSON document does not exist.", primaryPath);
        }

        if (File.Exists(backupPath))
        {
            T recovered;
            try
            {
                recovered = ReadRequired<T>(backupPath, options);
            }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                throw CreateLoadException(primaryPath, primaryError, backupError);
            }

            Exception? repairError = null;
            try
            {
                WriteCore(primaryPath, recovered, options, preserveBackup: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // The valid backup still lets the application continue. The caller
                // receives this error and must surface/log the failed repair.
                repairError = ex;
            }

            return new AtomicJsonLoadResult<T>(
                recovered,
                AtomicJsonLoadSource.Backup,
                primaryError,
                repairError);
        }

        if (primaryError is FileNotFoundException)
            return new AtomicJsonLoadResult<T>(createDefault(), AtomicJsonLoadSource.NewValue);

        throw CreateLoadException(primaryPath, primaryError, null);
    }

    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(value);
        WriteCore(Path.GetFullPath(path), value, options, preserveBackup: false);
    }

    private static T ReadRequired<T>(string path, JsonSerializerOptions? options)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);

        var value = JsonSerializer.Deserialize<T>(stream, options);
        return value is null
            ? throw new JsonException($"JSON document '{path}' contains null instead of {typeof(T).Name}.")
            : value;
    }

    private static void WriteCore<T>(
        string primaryPath,
        T value,
        JsonSerializerOptions? options,
        bool preserveBackup)
    {
        var directory = Path.GetDirectoryName(primaryPath)
            ?? throw new ArgumentException("A JSON file must have a parent directory.", nameof(primaryPath));
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(primaryPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, options);
                stream.Flush(flushToDisk: true);
            }

            if (!File.Exists(primaryPath))
            {
                File.Move(temporaryPath, primaryPath);
                return;
            }

            if (preserveBackup)
            {
                // Recovery must never replace the known-good backup with the corrupt
                // primary document that caused recovery.
                File.Move(temporaryPath, primaryPath, overwrite: true);
                return;
            }

            var backupPath = GetBackupPath(primaryPath);
            File.Replace(temporaryPath, primaryPath, backupPath, ignoreMetadataErrors: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // The primary exception is more useful. A uniquely named temp file
                // is harmless and can be removed on a later maintenance pass.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static InvalidDataException CreateLoadException(
        string primaryPath,
        Exception? primaryError,
        Exception? backupError)
    {
        var message = backupError is null
            ? $"The JSON document '{primaryPath}' is unreadable and no valid backup is available."
            : $"Both the JSON document '{primaryPath}' and its backup are unreadable.";

        Exception? inner = backupError is null
            ? primaryError
            : new AggregateException(primaryError!, backupError);
        return new InvalidDataException(message, inner);
    }
}
