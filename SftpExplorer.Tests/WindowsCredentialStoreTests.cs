using SftpExplorerWinUI.Services;

namespace SftpExplorerWinUI.Tests;

public sealed class WindowsCredentialStoreTests : IDisposable
{
    private readonly List<string> _createdTargets = new();

    [Fact]
    public void WriteReadDeleteRoundTripsTheCredential()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var store = new WindowsCredentialStore();
        var connectionId = Guid.NewGuid().ToString("N");
        _createdTargets.Add(connectionId);

        try
        {
            store.Write(connectionId, "test-user", "s3cret-password");
            var credential = store.Read(connectionId);

            Assert.NotNull(credential);
            Assert.Equal("test-user", credential!.Username);
            Assert.Equal("s3cret-password", credential.Password);

            // Overwriting replaces the stored value.
            store.Write(connectionId, "other-user", "new-secret");
            var updated = store.Read(connectionId);
            Assert.Equal("other-user", updated!.Username);
            Assert.Equal("new-secret", updated.Password);

            store.Delete(connectionId);
            Assert.Null(store.Read(connectionId));
        }
        finally
        {
            Cleanup(store, connectionId);
        }
    }

    [Fact]
    public void ReadMissingCredentialReturnsNull()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var store = new WindowsCredentialStore();
        Assert.Null(store.Read(Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void DeleteMissingCredentialDoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var store = new WindowsCredentialStore();
        store.Delete(Guid.NewGuid().ToString("N")); // ERROR_NOTFOUND is swallowed.
    }

    [Fact]
    public void EmptyPasswordRoundTripsAsEmptyString()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var store = new WindowsCredentialStore();
        var connectionId = Guid.NewGuid().ToString("N");
        _createdTargets.Add(connectionId);

        try
        {
            store.Write(connectionId, "test-user", "");
            var credential = store.Read(connectionId);

            Assert.NotNull(credential);
            Assert.Equal(string.Empty, credential!.Password);
        }
        finally
        {
            Cleanup(store, connectionId);
        }
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var store = new WindowsCredentialStore();
        foreach (var target in _createdTargets)
            Cleanup(store, target);
    }

    private static void Cleanup(WindowsCredentialStore store, string connectionId)
    {
        try
        {
            store.Delete(connectionId);
        }
        catch
        {
            // Best effort: the test must not fail because of cleanup.
        }
    }
}
