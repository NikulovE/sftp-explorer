using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SftpExplorerWinUI.Models;

namespace SftpExplorerWinUI.Services;

public class ConnectionManager
{
    private const string InterprocessLockFileName = ".sftpexplorer-connections.lock";
    private static readonly TimeSpan InterprocessLockTimeout = TimeSpan.FromSeconds(10);
    private static readonly string DefaultAppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SftpExplorer"
    );
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SftpExplorer_v1_Salt");
    private static readonly ConcurrentDictionary<string, object> PersistenceLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _connectionsFile;
    private readonly string _groupsFile;
    private readonly string _storageDirectory;
    private readonly object _persistenceLock;
    private readonly ICredentialStore _credentialStore;
    private InterprocessFileLock? _interprocessLock;
    private int _interprocessLockDepth;
    private bool _initialized;

    public ConnectionManager()
        : this(DefaultAppDataFolder, new WindowsCredentialStore())
    {
    }

    public ConnectionManager(string storageDirectory, ICredentialStore credentialStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageDirectory);
        ArgumentNullException.ThrowIfNull(credentialStore);

        var fullStorageDirectory = Path.GetFullPath(storageDirectory);
        _storageDirectory = fullStorageDirectory;
        _connectionsFile = Path.Combine(fullStorageDirectory, "connections.json");
        _groupsFile = Path.Combine(fullStorageDirectory, "groups.json");
        _persistenceLock = PersistenceLocks.GetOrAdd(fullStorageDirectory, static _ => new object());
        _credentialStore = credentialStore;
    }

    public List<SavedConnection> LoadConnections()
    {
        lock (_persistenceLock)
        {
            using var persistenceLease = EnterInterprocessLock();
            EnsureInitialized();
            return LoadDocument(
                _connectionsFile,
                "saved connections",
                static () => new List<SavedConnection>());
        }
    }

    public void SaveConnections(List<SavedConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        lock (_persistenceLock)
        {
            using var persistenceLease = EnterInterprocessLock();
            EnsureInitialized();
            SaveDocument(_connectionsFile, "saved connections", connections);
        }
    }

    public void AddOrUpdateConnection(SavedConnection connection, string? plainPassword = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        lock (_persistenceLock)
        {
            using var persistenceLease = EnterInterprocessLock();
            var connections = LoadConnections();
            var normalizedConnection = CloneConnection(connection);
            var previousConnection = connections.FirstOrDefault(c => c.Id == normalizedConnection.Id);

            normalizedConnection.Group = normalizedConnection.Group?.Trim() ?? "";
            normalizedConnection.Notes = normalizedConnection.Notes?.Trim() ?? "";
            normalizedConnection.PrivateKeyPath = normalizedConnection.AuthenticationMode == SftpAuthenticationMode.PrivateKey
                ? SshConnectionSession.NormalizePrivateKeyPath(normalizedConnection.PrivateKeyPath)
                : null;
            if (!Enum.IsDefined(normalizedConnection.AuthenticationMode) ||
                (normalizedConnection.AuthenticationMode == SftpAuthenticationMode.PrivateKey &&
                 string.IsNullOrWhiteSpace(normalizedConnection.PrivateKeyPath)))
            {
                throw new InvalidOperationException("The connection authentication settings are invalid.");
            }
            normalizedConnection.Glyph = string.IsNullOrEmpty(normalizedConnection.Glyph)
                ? ConnectionAppearanceDefaults.ConnectionGlyph
                : normalizedConnection.Glyph;
            normalizedConnection.Color = string.IsNullOrWhiteSpace(normalizedConnection.Color)
                ? ConnectionAppearanceDefaults.DefaultColor
                : normalizedConnection.Color;

            var authenticationModeChanged = previousConnection != null &&
                                            previousConnection.AuthenticationMode != normalizedConnection.AuthenticationMode;
            if (previousConnection != null)
            {
                var contractChanged = authenticationModeChanged ||
                    !string.Equals(previousConnection.Hostname, normalizedConnection.Hostname, StringComparison.Ordinal) ||
                    previousConnection.Port != normalizedConnection.Port ||
                    !string.Equals(previousConnection.Username, normalizedConnection.Username, StringComparison.Ordinal) ||
                    !string.Equals(previousConnection.PrivateKeyPath, normalizedConnection.PrivateKeyPath, StringComparison.OrdinalIgnoreCase);
                normalizedConnection.AuthenticationRevision = contractChanged || !string.IsNullOrEmpty(plainPassword)
                    ? previousConnection.AuthenticationRevision == long.MaxValue ? long.MinValue : previousConnection.AuthenticationRevision + 1
                    : previousConnection.AuthenticationRevision;
            }
            else
            {
                normalizedConnection.AuthenticationRevision = 0;
            }

            connections.RemoveAll(c => c.Id == normalizedConnection.Id);

            // Do not persist a connection that claims to have a new password when
            // Credential Manager rejected that password. Callers can show this
            // specific exception instead of reporting a false success.
            if (!string.IsNullOrEmpty(plainPassword))
            {
                var previousCredential = _credentialStore.Read(normalizedConnection.Id);
                _credentialStore.Write(normalizedConnection.Id, normalizedConnection.Username, plainPassword);

                try
                {
                    normalizedConnection.EncryptedPassword = null;
                    normalizedConnection.LastUsed = DateTime.Now;
                    connections.Add(normalizedConnection);
                    SaveConnections(connections);
                }
                catch (Exception persistenceError)
                {
                    try
                    {
                        RestoreCredential(normalizedConnection.Id, previousCredential);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new ConnectionPersistenceException(
                            "Saving the connection failed and its previous credential could not be restored.",
                            new AggregateException(persistenceError, rollbackError));
                    }

                    throw;
                }

                return;
            }

            normalizedConnection.LastUsed = DateTime.Now;
            connections.Add(normalizedConnection);
            SaveConnections(connections);
            if (authenticationModeChanged)
            {
                try
                {
                    _credentialStore.Delete(normalizedConnection.Id);
                }
                catch
                {
                    connections.RemoveAll(c => c.Id == normalizedConnection.Id);
                    if (previousConnection != null) connections.Add(previousConnection);
                    SaveConnections(connections);
                    throw;
                }
            }
        }
    }

    public void DeleteConnection(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_persistenceLock)
        {
            using var persistenceLease = EnterInterprocessLock();
            var connections = LoadConnections();
            var removedIndex = connections.FindIndex(c => c.Id == connectionId);
            if (removedIndex < 0)
                return;

            var removedConnection = connections[removedIndex];
            connections.RemoveAt(removedIndex);

            // Save is intentionally first. If it fails, the profile and its
            // credential remain paired and the exception reaches the caller.
            SaveConnections(connections);
            try
            {
                _credentialStore.Delete(connectionId);
            }
            catch (Exception credentialError)
            {
                try
                {
                    connections.Insert(removedIndex, removedConnection);
                    SaveConnections(connections);
                }
                catch (Exception rollbackError)
                {
                    throw new ConnectionPersistenceException(
                        "The profile was removed, but deleting its credential failed and the profile could not be restored.",
                        new AggregateException(credentialError, rollbackError));
                }

                throw;
            }
        }
    }

    public string? GetPassword(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (_persistenceLock)
        {
            using var persistenceLease = EnterInterprocessLock();
            EnsureInitialized();

            var credential = _credentialStore.Read(connectionId);
            if (credential != null)
                return credential.Password;

            // A legacy DPAPI value is deliberately retained when migration to
            // Credential Manager fails. Keep it usable until a later launch can
            // complete that migration instead of turning a storage problem into
            // a misleading SSH authentication failure.
            var legacyConnection = LoadDocument(
                    _connectionsFile,
                    "saved connections",
                    static () => new List<SavedConnection>())
                .FirstOrDefault(connection =>
                    string.Equals(connection.Id, connectionId, StringComparison.Ordinal) &&
                    !string.IsNullOrEmpty(connection.EncryptedPassword));
            return legacyConnection?.EncryptedPassword is { } encryptedPassword
                ? DecryptLegacyPassword(encryptedPassword)
                : null;
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
            return;

        // Construction deliberately performs no I/O so a damaged profile cannot
        // crash MainWindow before its UI exists. The first storage operation can
        // now catch and surface initialization/recovery failures.
        Directory.CreateDirectory(_storageDirectory);
        var connections = LoadDocument(
            _connectionsFile,
            "saved connections",
            static () => new List<SavedConnection>());
        MigrateLegacyPasswords(connections);
        _initialized = true;
    }

    private void MigrateLegacyPasswords(List<SavedConnection> connections)
    {
        var migratedCount = 0;

        foreach (var connection in connections.Where(connection => !string.IsNullOrEmpty(connection.EncryptedPassword)))
        {
            var password = DecryptLegacyPassword(connection.EncryptedPassword!);
            if (password == null)
                continue;

            try
            {
                _credentialStore.Write(connection.Id, connection.Username, password);
            }
            catch (CredentialStoreException ex)
            {
                // The legacy password remains in JSON, so this is recoverable and
                // migration can be retried on the next launch.
                Log.Warning($"Failed to migrate the password for connection '{connection.Id}'.", ex);
                continue;
            }

            connection.EncryptedPassword = null;
            migratedCount++;
        }

        if (migratedCount == 0)
            return;

        // Atomic save means a failed migration never leaves a truncated document.
        // Its exception is not swallowed; the still-present legacy values allow a
        // retry on the next start.
        SaveDocument(_connectionsFile, "saved connections", connections);
        Log.Info($"Migrated {migratedCount} saved password(s) to Windows Credential Manager.");
    }

    private static string? DecryptLegacyPassword(string encryptedPassword)
    {
        if (!OperatingSystem.IsWindows())
        {
            Log.Warning("A legacy saved password cannot be decrypted outside Windows.");
            return null;
        }

        byte[]? decryptedBytes = null;
        try
        {
            var encryptedBytes = Convert.FromBase64String(encryptedPassword);
            decryptedBytes = System.Security.Cryptography.ProtectedData.Unprotect(
                encryptedBytes,
                Entropy,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (Exception ex)
        {
            Log.Warning("Failed to decrypt a legacy saved password.", ex);
            return null;
        }
        finally
        {
            if (decryptedBytes != null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(decryptedBytes);
        }
    }

    public SavedConnection? GetConnection(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return LoadConnections().FirstOrDefault(c => c.Id == connectionId);
    }

    public void UpdateLastUsed(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_persistenceLock)
        {
            using var persistenceLease = EnterInterprocessLock();
            var connections = LoadConnections();
            var connection = connections.FirstOrDefault(c => c.Id == connectionId);
            if (connection != null)
            {
                connection.LastUsed = DateTime.Now;
                SaveConnections(connections);
            }
        }
    }

    public List<SavedConnection> GetAllConnections()
    {
        return LoadConnections();
    }

    public List<ConnectionGroupSettings> LoadGroups()
    {
        lock (_persistenceLock)
        {
            using var persistenceLease = EnterInterprocessLock();
            EnsureInitialized();
            var groups = LoadDocument(
                _groupsFile,
                "connection groups",
                static () => new List<ConnectionGroupSettings>());

            foreach (var groupName in LoadConnections()
                         .Select(c => c.Group?.Trim())
                         .Where(group => !string.IsNullOrWhiteSpace(group))
                         .Cast<string>()
                         .Distinct(StringComparer.CurrentCultureIgnoreCase))
            {
                if (!groups.Any(group => string.Equals(group.Name, groupName, StringComparison.CurrentCultureIgnoreCase)))
                {
                    groups.Add(new ConnectionGroupSettings { Name = groupName });
                }
            }

            foreach (var group in groups)
            {
                group.Name = group.Name?.Trim() ?? "";
                group.Glyph = string.IsNullOrEmpty(group.Glyph)
                    ? ConnectionAppearanceDefaults.GroupGlyph
                    : group.Glyph;
                group.Color = string.IsNullOrWhiteSpace(group.Color)
                    ? ConnectionAppearanceDefaults.DefaultColor
                    : group.Color;
            }

            return groups
                .Where(group => !string.IsNullOrWhiteSpace(group.Name))
                .OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public void AddOrUpdateGroup(ConnectionGroupSettings group)
    {
        ArgumentNullException.ThrowIfNull(group);

        lock (_persistenceLock)
        {
            using var persistenceLease = EnterInterprocessLock();
            group.Name = group.Name?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(group.Name))
                return;

            group.Glyph = string.IsNullOrEmpty(group.Glyph)
                ? ConnectionAppearanceDefaults.GroupGlyph
                : group.Glyph;
            group.Color = string.IsNullOrWhiteSpace(group.Color)
                ? ConnectionAppearanceDefaults.DefaultColor
                : group.Color;

            var groups = LoadGroups();
            var existingGroup = groups.FirstOrDefault(existing => string.Equals(
                existing.Name,
                group.Name,
                StringComparison.CurrentCultureIgnoreCase));
            if (existingGroup != null)
            {
                // Appearance edits must not reset the user's persisted collapse state.
                group.IsExpanded = existingGroup.IsExpanded;
            }

            groups.RemoveAll(existing => string.Equals(
                existing.Name,
                group.Name,
                StringComparison.CurrentCultureIgnoreCase));
            groups.Add(group);

            SaveGroups(groups);
        }
    }

    public void SetGroupExpandedState(string groupName, bool isExpanded)
    {
        groupName = groupName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(groupName))
            return;

        lock (_persistenceLock)
        {
            using var persistenceLease = EnterInterprocessLock();
            var groups = LoadGroups();
            var group = groups.FirstOrDefault(existing => string.Equals(
                existing.Name,
                groupName,
                StringComparison.CurrentCultureIgnoreCase));
            if (group == null)
            {
                group = new ConnectionGroupSettings { Name = groupName };
                groups.Add(group);
            }

            group.IsExpanded = isExpanded;
            SaveGroups(groups);
        }
    }

    private void SaveGroups(List<ConnectionGroupSettings> groups)
    {
        SaveDocument(_groupsFile, "connection groups", groups);
    }

    private void RestoreCredential(string connectionId, StoredCredential? previousCredential)
    {
        if (previousCredential is null)
        {
            _credentialStore.Delete(connectionId);
            return;
        }

        _credentialStore.Write(connectionId, previousCredential.Username, previousCredential.Password);
    }

    private IDisposable EnterInterprocessLock()
    {
        if (!System.Threading.Monitor.IsEntered(_persistenceLock))
        {
            throw new InvalidOperationException("The in-process persistence lock must be held first.");
        }

        if (_interprocessLockDepth == 0)
        {
            _interprocessLock = InterprocessFileLock.Acquire(
                _storageDirectory,
                InterprocessLockFileName,
                InterprocessLockTimeout);
        }

        _interprocessLockDepth++;
        return new InterprocessLease(this);
    }

    private void ExitInterprocessLock()
    {
        if (!System.Threading.Monitor.IsEntered(_persistenceLock) || _interprocessLockDepth <= 0)
        {
            throw new SynchronizationLockException("The persistence lease is not held by this thread.");
        }

        _interprocessLockDepth--;
        if (_interprocessLockDepth != 0)
            return;

        var interprocessLock = _interprocessLock;
        _interprocessLock = null;
        interprocessLock?.Dispose();
    }

    private sealed class InterprocessLease : IDisposable
    {
        private ConnectionManager? _owner;

        public InterprocessLease(ConnectionManager owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ExitInterprocessLock();
        }
    }

    private static SavedConnection CloneConnection(SavedConnection connection) => new()
    {
        Id = connection.Id,
        Name = connection.Name,
        Hostname = connection.Hostname,
        Port = connection.Port,
        Username = connection.Username,
        AuthenticationMode = connection.AuthenticationMode,
        AuthenticationRevision = connection.AuthenticationRevision,
        PrivateKeyPath = connection.PrivateKeyPath,
        PrivateKeyRequiresPassphrase = connection.PrivateKeyRequiresPassphrase,
        Group = connection.Group,
        Notes = connection.Notes,
        Glyph = connection.Glyph,
        EncryptedPassword = connection.EncryptedPassword,
        CreatedAt = connection.CreatedAt,
        LastUsed = connection.LastUsed,
        Color = connection.Color
    };

    private static T LoadDocument<T>(string path, string description, Func<T> createDefault)
    {
        try
        {
            var result = AtomicJsonFile.Load(path, createDefault, JsonOptions);
            if (result.Source == AtomicJsonLoadSource.Backup)
            {
                Log.Warning($"Recovered {description} from '{AtomicJsonFile.GetBackupPath(path)}'.", result.PrimaryError);
                if (result.RepairError != null)
                {
                    Log.Error($"Recovered {description}, but failed to repair the primary file '{path}'.", result.RepairError);
                }
            }

            return result.Value;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load {description} from '{path}'.", ex);
            throw;
        }
    }

    private static void SaveDocument<T>(string path, string description, T value)
    {
        try
        {
            AtomicJsonFile.Save(path, value, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save {description} to '{path}'.", ex);
            throw;
        }
    }
}
