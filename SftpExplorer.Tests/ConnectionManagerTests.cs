using SftpExplorerWinUI.Models;
using SftpExplorerWinUI.Services;

namespace SftpExplorerWinUI.Tests;

public sealed class ConnectionManagerTests
{
    [Fact]
    public void ConnectionCrudPersistsProfileAndCredential()
    {
        using var temp = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var manager = new ConnectionManager(temp.Path, credentials);
        var connection = CreateConnection("connection-1", "initial");

        manager.AddOrUpdateConnection(connection, "secret");

        var reloadedManager = new ConnectionManager(temp.Path, credentials);
        var reloaded = Assert.Single(reloadedManager.LoadConnections());
        Assert.Equal("connection-1", reloaded.Id);
        Assert.Equal("initial", reloaded.Name);
        Assert.Equal("secret", reloadedManager.GetPassword(reloaded.Id));

        reloaded.Name = "updated";
        reloadedManager.AddOrUpdateConnection(reloaded);
        Assert.Equal("updated", Assert.Single(reloadedManager.LoadConnections()).Name);
        Assert.Equal("secret", reloadedManager.GetPassword(reloaded.Id));

        reloadedManager.DeleteConnection(reloaded.Id);
        Assert.Empty(reloadedManager.LoadConnections());
        Assert.Null(reloadedManager.GetPassword(reloaded.Id));
    }

    [Fact]
    public void CredentialWriteFailureDoesNotPersistProfileAndReachesCaller()
    {
        using var temp = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore { FailWrites = true };
        var manager = new ConnectionManager(temp.Path, credentials);

        Assert.Throws<CredentialStoreException>(() =>
            manager.AddOrUpdateConnection(CreateConnection("connection-1", "new"), "secret"));
        Assert.Empty(manager.LoadConnections());
    }

    [Fact]
    public void JsonWriteFailureRestoresPreviousCredential()
    {
        using var temp = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var manager = new ConnectionManager(temp.Path, credentials);
        manager.AddOrUpdateConnection(CreateConnection("connection-1", "old"), "old-secret");

        var connectionsPath = System.IO.Path.Combine(temp.Path, "connections.json");
        File.Delete(connectionsPath);
        Directory.CreateDirectory(connectionsPath);

        Assert.ThrowsAny<IOException>(() =>
            manager.AddOrUpdateConnection(CreateConnection("connection-1", "new"), "new-secret"));
        Assert.Equal("old-secret", credentials.Read("connection-1")?.Password);
    }

    [Fact]
    public void FailedSaveDoesNotMutateCallersConnectionObject()
    {
        using var temp = new TemporaryDirectory();
        var manager = new ConnectionManager(temp.Path, new MemoryCredentialStore());
        var connectionsPath = System.IO.Path.Combine(temp.Path, "connections.json");
        Directory.CreateDirectory(connectionsPath);
        var originalLastUsed = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Local);
        var connection = CreateConnection("connection-1", "unchanged");
        connection.Group = "  group with spaces  ";
        connection.LastUsed = originalLastUsed;

        Assert.ThrowsAny<IOException>(() => manager.AddOrUpdateConnection(connection));

        Assert.Equal("  group with spaces  ", connection.Group);
        Assert.Equal(originalLastUsed, connection.LastUsed);
    }

    [Fact]
    public void CredentialDeleteFailureRestoresPersistedProfileAndReachesCaller()
    {
        using var temp = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var manager = new ConnectionManager(temp.Path, credentials);
        manager.AddOrUpdateConnection(CreateConnection("connection-1", "keep-me"), "secret");
        credentials.FailDeletes = true;

        Assert.Throws<CredentialStoreException>(() => manager.DeleteConnection("connection-1"));

        Assert.Equal("keep-me", Assert.Single(manager.LoadConnections()).Name);
        Assert.Equal("secret", credentials.Read("connection-1")?.Password);
    }

    [Fact]
    public void GroupStateIsPersistedAcrossManagerInstances()
    {
        using var temp = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var manager = new ConnectionManager(temp.Path, credentials);
        manager.AddOrUpdateGroup(new ConnectionGroupSettings { Name = "Production" });
        manager.SetGroupExpandedState("Production", false);

        var reloaded = new ConnectionManager(temp.Path, credentials);
        var group = Assert.Single(reloaded.LoadGroups());
        Assert.Equal("Production", group.Name);
        Assert.False(group.IsExpanded);
    }

    [Fact]
    public async Task ConcurrentManagerWritesDoNotLoseConnections()
    {
        using var temp = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var firstManager = new ConnectionManager(temp.Path, credentials);
        var secondManager = new ConnectionManager(temp.Path, credentials);

        await Task.WhenAll(
            Task.Run(() => firstManager.AddOrUpdateConnection(CreateConnection("connection-1", "first"))),
            Task.Run(() => secondManager.AddOrUpdateConnection(CreateConnection("connection-2", "second"))));

        var reloaded = new ConnectionManager(temp.Path, credentials).LoadConnections();
        Assert.Equal(2, reloaded.Count);
        Assert.Contains(reloaded, connection => connection.Id == "connection-1");
        Assert.Contains(reloaded, connection => connection.Id == "connection-2");
    }

    [Fact]
    public void CorruptPrimaryRecoversPreviousConnectionSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var manager = new ConnectionManager(temp.Path, new MemoryCredentialStore());
        manager.AddOrUpdateConnection(CreateConnection("connection-1", "known-good"));
        manager.AddOrUpdateConnection(CreateConnection("connection-2", "newer"));
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "connections.json"), "corrupt primary");

        var recovered = new ConnectionManager(temp.Path, new MemoryCredentialStore()).LoadConnections();

        var connection = Assert.Single(recovered);
        Assert.Equal("connection-1", connection.Id);
        Assert.Equal("known-good", connection.Name);
    }

    [Fact]
    public void UpdatingGroupAppearancePreservesCollapsedState()
    {
        using var temp = new TemporaryDirectory();
        var manager = new ConnectionManager(temp.Path, new MemoryCredentialStore());
        manager.AddOrUpdateGroup(new ConnectionGroupSettings { Name = "Production" });
        manager.SetGroupExpandedState("Production", false);

        manager.AddOrUpdateGroup(new ConnectionGroupSettings
        {
            Name = " production ",
            Glyph = "\uE8B7",
            Color = "#FF336699",
            IsExpanded = true
        });

        var group = Assert.Single(new ConnectionManager(temp.Path, new MemoryCredentialStore()).LoadGroups());
        Assert.Equal("production", group.Name);
        Assert.Equal("\uE8B7", group.Glyph);
        Assert.Equal("#FF336699", group.Color);
        Assert.False(group.IsExpanded);
    }

    [Fact]
    public void ConnectionGroupsAreDiscoveredDeduplicatedAndSorted()
    {
        using var temp = new TemporaryDirectory();
        var manager = new ConnectionManager(temp.Path, new MemoryCredentialStore());
        var first = CreateConnection("connection-1", "first");
        first.Group = " Zebra ";
        var second = CreateConnection("connection-2", "second");
        second.Group = "alpha";
        var third = CreateConnection("connection-3", "third");
        third.Group = "zebra";
        manager.AddOrUpdateConnection(first);
        manager.AddOrUpdateConnection(second);
        manager.AddOrUpdateConnection(third);

        var groups = manager.LoadGroups();

        Assert.Equal(new[] { "alpha", "Zebra" }, groups.Select(group => group.Name));
    }

    [Fact]
    public void LegacyPasswordRemainsUsableWhenCredentialMigrationFails()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        var passwordBytes = System.Text.Encoding.UTF8.GetBytes("legacy-secret");
        byte[]? encryptedBytes = null;
        try
        {
            encryptedBytes = System.Security.Cryptography.ProtectedData.Protect(
                passwordBytes,
                System.Text.Encoding.UTF8.GetBytes("SftpExplorer_v1_Salt"),
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var connection = CreateConnection("legacy-connection", "legacy");
            connection.EncryptedPassword = Convert.ToBase64String(encryptedBytes);
            File.WriteAllText(
                System.IO.Path.Combine(temp.Path, "connections.json"),
                System.Text.Json.JsonSerializer.Serialize(new[] { connection }));

            var credentials = new MemoryCredentialStore { FailWrites = true };
            var manager = new ConnectionManager(temp.Path, credentials);

            Assert.Equal("legacy-secret", manager.GetPassword(connection.Id));
            Assert.NotNull(Assert.Single(manager.LoadConnections()).EncryptedPassword);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordBytes);
            if (encryptedBytes != null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(encryptedBytes);
        }
    }

    [Fact]
    public void GetConnectionReturnsMatchingProfileAndNullForUnknownId()
    {
        using var temp = new TemporaryDirectory();
        var manager = new ConnectionManager(temp.Path, new MemoryCredentialStore());
        manager.AddOrUpdateConnection(CreateConnection("connection-1", "known"));

        Assert.Equal("known", manager.GetConnection("connection-1")?.Name);
        Assert.Null(manager.GetConnection("missing-id"));
        Assert.Throws<ArgumentException>(() => manager.GetConnection("  "));
    }

    [Fact]
    public void UpdateLastUsedPersistsTimestampOnlyForExistingConnections()
    {
        using var temp = new TemporaryDirectory();
        var manager = new ConnectionManager(temp.Path, new MemoryCredentialStore());
        var connection = CreateConnection("connection-1", "known");
        connection.LastUsed = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Local);
        manager.AddOrUpdateConnection(connection);

        Thread.Sleep(20);
        manager.UpdateLastUsed("connection-1");
        manager.UpdateLastUsed("missing-id"); // Must be a no-op.

        var reloaded = new ConnectionManager(temp.Path, new MemoryCredentialStore())
            .GetConnection("connection-1");
        Assert.True(reloaded!.LastUsed > connection.LastUsed);
    }

    [Fact]
    public void SaveConnectionsReplacesThePersistedList()
    {
        using var temp = new TemporaryDirectory();
        var manager = new ConnectionManager(temp.Path, new MemoryCredentialStore());
        manager.AddOrUpdateConnection(CreateConnection("connection-1", "first"));

        manager.SaveConnections(new List<SavedConnection>
        {
            CreateConnection("connection-2", "second")
        });

        var reloaded = new ConnectionManager(temp.Path, new MemoryCredentialStore()).LoadConnections();
        Assert.Equal("connection-2", Assert.Single(reloaded).Id);
        Assert.Throws<ArgumentNullException>(() => manager.SaveConnections(null!));
    }

    [Fact]
    public void InvalidAuthenticationSettingsAreRejectedBeforePersistence()
    {
        using var temp = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var manager = new ConnectionManager(temp.Path, credentials);

        var unknownMode = CreateConnection("connection-1", "bad-mode");
        unknownMode.AuthenticationMode = (SftpAuthenticationMode)99;
        Assert.Throws<InvalidOperationException>(() => manager.AddOrUpdateConnection(unknownMode));

        var missingKeyPath = CreateConnection("connection-2", "missing-key");
        missingKeyPath.AuthenticationMode = SftpAuthenticationMode.PrivateKey;
        Assert.Throws<InvalidOperationException>(() => manager.AddOrUpdateConnection(missingKeyPath));

        Assert.Empty(manager.LoadConnections());
    }

    [Fact]
    public void AuthenticationRevisionBumpsOnlyWhenTheContractOrPasswordChanges()
    {
        using var temp = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var manager = new ConnectionManager(temp.Path, credentials);
        var connection = CreateConnection("connection-1", "known");

        manager.AddOrUpdateConnection(connection, "secret");
        Assert.Equal(0, manager.GetConnection("connection-1")!.AuthenticationRevision);

        // Same contract and no password: revision is preserved.
        manager.AddOrUpdateConnection(manager.GetConnection("connection-1")!);
        Assert.Equal(0, manager.GetConnection("connection-1")!.AuthenticationRevision);

        // A new password bumps the revision even when nothing else changed.
        manager.AddOrUpdateConnection(manager.GetConnection("connection-1")!, "other-secret");
        Assert.Equal(1, manager.GetConnection("connection-1")!.AuthenticationRevision);

        // Changing the endpoint contract bumps it again.
        var moved = manager.GetConnection("connection-1")!;
        moved.Hostname = "moved.example.test";
        manager.AddOrUpdateConnection(moved);
        Assert.Equal(2, manager.GetConnection("connection-1")!.AuthenticationRevision);

        // Switching the authentication mode bumps it as well.
        var switched = manager.GetConnection("connection-1")!;
        switched.AuthenticationMode = SftpAuthenticationMode.PrivateKey;
        switched.PrivateKeyPath = Path.Combine(temp.Path, "id_ed25519");
        File.WriteAllText(switched.PrivateKeyPath, "placeholder-key");
        manager.AddOrUpdateConnection(switched);
        Assert.Equal(3, manager.GetConnection("connection-1")!.AuthenticationRevision);
    }

    [Fact]
    public void AuthenticationRevisionWrapsFromMaxValueToMinValue()
    {
        using var temp = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var manager = new ConnectionManager(temp.Path, credentials);
        var connection = CreateConnection("connection-1", "known");
        connection.AuthenticationRevision = long.MaxValue;
        File.WriteAllText(
            Path.Combine(temp.Path, "connections.json"),
            System.Text.Json.JsonSerializer.Serialize(new[] { connection }));

        var moved = CreateConnection("connection-1", "moved");
        moved.Hostname = "moved.example.test";
        manager.AddOrUpdateConnection(moved);

        Assert.Equal(long.MinValue, manager.GetConnection("connection-1")!.AuthenticationRevision);
    }

    [Fact]
    public void FailedPasswordUpdateRestoresThePreviousCredential()
    {
        using var temp = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var manager = new ConnectionManager(temp.Path, credentials);
        manager.AddOrUpdateConnection(CreateConnection("connection-1", "known"), "old-secret");

        credentials.FailWrites = true;
        Assert.Throws<CredentialStoreException>(() =>
            manager.AddOrUpdateConnection(manager.GetConnection("connection-1")!, "new-secret"));

        Assert.Equal("old-secret", credentials.Read("connection-1")?.Password);
        Assert.Equal("known", manager.LoadConnections().Single().Name);
    }

    [Fact]
    public void DeleteConnectionValidatesIdAndIgnoresUnknownIds()
    {
        using var temp = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var manager = new ConnectionManager(temp.Path, credentials);
        manager.AddOrUpdateConnection(CreateConnection("connection-1", "known"), "secret");

        Assert.Throws<ArgumentException>(() => manager.DeleteConnection(""));
        manager.DeleteConnection("missing-id"); // No-op.

        Assert.Single(manager.LoadConnections());
    }

    [Fact]
    public void CorruptGroupsFileRecoversPreviousGroupSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var credentials = new MemoryCredentialStore();
        var manager = new ConnectionManager(temp.Path, credentials);
        manager.AddOrUpdateGroup(new ConnectionGroupSettings { Name = "Production" });
        manager.AddOrUpdateGroup(new ConnectionGroupSettings { Name = "Staging" });

        File.WriteAllText(Path.Combine(temp.Path, "groups.json"), "{ corrupt");

        var recovered = new ConnectionManager(temp.Path, credentials).LoadGroups();
        Assert.Equal("Production", Assert.Single(recovered).Name);
    }

    [Fact]
    public void BlankGroupNamesAreIgnoredWithoutTouchingStorage()
    {
        using var temp = new TemporaryDirectory();
        var manager = new ConnectionManager(temp.Path, new MemoryCredentialStore());

        manager.AddOrUpdateGroup(new ConnectionGroupSettings { Name = "   " });
        manager.SetGroupExpandedState("  ", false);

        Assert.Empty(manager.LoadGroups());
    }

    [Fact]
    public void LegacyPasswordMigratesToCredentialStoreOnFirstUse()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        var passwordBytes = System.Text.Encoding.UTF8.GetBytes("legacy-secret");
        byte[]? encryptedBytes = null;
        try
        {
            encryptedBytes = System.Security.Cryptography.ProtectedData.Protect(
                passwordBytes,
                System.Text.Encoding.UTF8.GetBytes("SftpExplorer_v1_Salt"),
                System.Security.Cryptography.DataProtectionScope.CurrentUser);

            var connection = CreateConnection("legacy-connection", "legacy");
            connection.EncryptedPassword = Convert.ToBase64String(encryptedBytes);
            File.WriteAllText(
                Path.Combine(temp.Path, "connections.json"),
                System.Text.Json.JsonSerializer.Serialize(new[] { connection }));

            var credentials = new MemoryCredentialStore();
            var manager = new ConnectionManager(temp.Path, credentials);
            var loaded = Assert.Single(manager.LoadConnections());

            // Migration moved the secret into the credential store and cleared it from JSON.
            Assert.Null(loaded.EncryptedPassword);
            Assert.Equal("legacy-secret", credentials.Read("legacy-connection")?.Password);
            Assert.Equal("legacy-secret", manager.GetPassword("legacy-connection"));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(passwordBytes);
            if (encryptedBytes != null)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(encryptedBytes);
        }
    }

    [Fact]
    public void GetPasswordReturnsNullForUnknownConnections()
    {
        using var temp = new TemporaryDirectory();
        var manager = new ConnectionManager(temp.Path, new MemoryCredentialStore());

        Assert.Null(manager.GetPassword("missing-id"));
        Assert.Throws<ArgumentException>(() => manager.GetPassword(" "));
    }

    private static SavedConnection CreateConnection(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Hostname = "example.test",
        Username = "test"
    };

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, StoredCredential> _credentials = new(StringComparer.Ordinal);

        public bool FailWrites { get; set; }

        public bool FailDeletes { get; set; }

        public StoredCredential? Read(string connectionId) =>
            _credentials.TryGetValue(connectionId, out var credential) ? credential : null;

        public void Write(string connectionId, string username, string password)
        {
            if (FailWrites)
                throw new CredentialStoreException(connectionId, "write");
            _credentials[connectionId] = new StoredCredential(username, password);
        }

        public void Delete(string connectionId)
        {
            if (FailDeletes)
                throw new CredentialStoreException(connectionId, "delete");
            _credentials.Remove(connectionId);
        }
    }
}
