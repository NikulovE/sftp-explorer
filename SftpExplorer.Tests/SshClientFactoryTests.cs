using System.Diagnostics;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;
using SftpExplorerWinUI.Models;
using SftpExplorerWinUI.Services;

namespace SftpExplorerWinUI.Tests;

public sealed class SshClientFactoryTests
{
    [Fact]
    public void CreatePasswordSessionExposesEndpointAndCredentials()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var factory = new SshClientFactory();
        using var session = factory.CreatePasswordSession("example.test", 22, "user", "secret");

        Assert.Equal("example.test", session.Hostname);
        Assert.Equal(22, session.Port);
        Assert.Equal("user", session.Username);
        Assert.Equal(SftpAuthenticationMode.Password, session.AuthenticationMode);
        Assert.Null(session.PrivateKeyPath);
    }

    [Fact]
    public void FactoryExposesItsKnownHostStore()
    {
        using var temp = new TemporaryDirectory();
        var store = new KnownHostStore(Path.Combine(temp.Path, "known-hosts.json"));
        var factory = new SshClientFactory(store);

        Assert.Same(store, factory.KnownHosts);
        // The parameterless constructor creates its own default store.
        Assert.NotNull(new SshClientFactory().KnownHosts);
    }

    [Fact]
    public void CreateClientsConfigureKeepAliveAndAttachValidation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        var factory = new SshClientFactory(new KnownHostStore(Path.Combine(temp.Path, "known-hosts.json")));
        HostKeyConfirmationAsync confirmation = static (_, _) => Task.FromResult(true);

        // Two distinct endpoints so each TOFU decision is independent.
        using (var sftpSession = factory.CreatePasswordSession("example-1.test", 22, "user", "secret"))
        using (var sftpClient = factory.CreateSftpClient(sftpSession, confirmation))
        {
            Assert.Equal(TimeSpan.FromSeconds(30), sftpClient.KeepAliveInterval);
            RaiseHostKeyReceived(sftpClient, "ssh-ed25519");
        }

        using (var sshSession = factory.CreatePasswordSession("example-2.test", 22, "user", "secret"))
        using (var sshClient = factory.CreateSshClient(sshSession, confirmation))
        {
            Assert.Equal(TimeSpan.FromSeconds(30), sshClient.KeepAliveInterval);
            RaiseHostKeyReceived(sshClient, "rsa-sha2-512");
        }

        // Both host keys were trusted through the TOFU flow and persisted.
        var document = File.ReadAllText(Path.Combine(temp.Path, "known-hosts.json"));
        Assert.Contains("example-1.test:22", document);
        Assert.Contains("example-2.test:22", document);
    }

    [Fact]
    public void CreateClientsRejectNullArguments()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var factory = new SshClientFactory();
        using var session = factory.CreatePasswordSession("example.test", 22, "user", "secret");

        Assert.Throws<ArgumentNullException>(() => factory.CreateSftpClient(null!, static (_, _) => Task.FromResult(true)));
        Assert.Throws<ArgumentNullException>(() => factory.CreateSshClient(null!, static (_, _) => Task.FromResult(true)));
        Assert.Throws<ArgumentNullException>(() => factory.CreateSftpClient(session, null!));

        // The async overloads validate their arguments synchronously.
        Assert.Throws<ArgumentNullException>(() => factory.ConnectSftpAsync(
            null!, static (_, _) => Task.FromResult(true)).GetAwaiter().GetResult());
        Assert.Throws<ArgumentNullException>(() => factory.ConnectSshAsync(
            null!, static (_, _) => Task.FromResult(true)).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task UnreachableServerSurfacesTheConnectionError()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var temp = new TemporaryDirectory();
        var factory = new SshClientFactory(new KnownHostStore(Path.Combine(temp.Path, "known-hosts.json")));
        // Port 9 (discard) on loopback is closed: the handshake cannot start.
        using var session = factory.CreatePasswordSession("127.0.0.1", 9, "user", "secret");

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await factory.ConnectSftpAsync(session, static (_, _) => Task.FromResult(true)));
    }

    [Fact]
    public async Task ConnectSftpAsyncTrustsTheDisposableServerThroughTofu()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (!DisposableSftpServer.TryStart(out var server) || server == null)
            return; // Docker or ssh-keygen is unavailable on this host.

        try
        {
            using var temp = new TemporaryDirectory();
            var factory = new SshClientFactory(new KnownHostStore(Path.Combine(temp.Path, "known-hosts.json")));
            using var session = factory.CreatePasswordSession("127.0.0.1", server.Port, server.Username, server.Password);
            var confirmationCount = 0;

            using (var client = await factory.ConnectSftpAsync(
                       session,
                       (_, _) =>
                       {
                           Interlocked.Increment(ref confirmationCount);
                           return Task.FromResult(true);
                       }))
            {
                Assert.True(client.IsConnected);
                Assert.Equal(server.Username, client.ConnectionInfo.Username);

                // The TOFU decision was persisted for the next connection.
                var document = File.ReadAllText(Path.Combine(temp.Path, "known-hosts.json"));
                Assert.Contains($"127.0.0.1:{server.Port}", document);
            }

            Assert.True(confirmationCount >= 1);

            // A second connection reuses the stored key and never prompts again.
            using (var client = await factory.ConnectSftpAsync(
                       session,
                       (_, _) => throw new InvalidOperationException("The host key must already be trusted.")))
            {
                Assert.True(client.IsConnected);
            }
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task ConnectSshAsyncTrustsTheDisposableServerThroughTofu()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (!DisposableSftpServer.TryStart(out var server) || server == null)
            return; // Docker or ssh-keygen is unavailable on this host.

        try
        {
            using var temp = new TemporaryDirectory();
            var factory = new SshClientFactory(new KnownHostStore(Path.Combine(temp.Path, "known-hosts.json")));
            using var session = factory.CreatePasswordSession("127.0.0.1", server.Port, server.Username, server.Password);

            using (var client = await factory.ConnectSshAsync(
                       session, static (_, _) => Task.FromResult(true)))
            {
                Assert.True(client.IsConnected);
            }
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task ConnectSftpAsyncRejectsTheDisposableServerWhenConfirmationDeclines()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (!DisposableSftpServer.TryStart(out var server) || server == null)
            return; // Docker or ssh-keygen is unavailable on this host.

        try
        {
            using var temp = new TemporaryDirectory();
            var factory = new SshClientFactory(new KnownHostStore(Path.Combine(temp.Path, "known-hosts.json")));
            using var session = factory.CreatePasswordSession("127.0.0.1", server.Port, server.Username, server.Password);

            await Assert.ThrowsAsync<HostKeyRejectedException>(() => factory.ConnectSftpAsync(
                session, static (_, _) => Task.FromResult(false)));

            // The rejected key must not have been persisted.
            Assert.False(File.Exists(Path.Combine(temp.Path, "known-hosts.json")));
        }
        finally
        {
            server.Dispose();
        }
    }

    private static void RaiseHostKeyReceived(BaseClient client, string algorithm)
    {
        var key = GenerateEd25519Key() ?? throw new InvalidOperationException("ssh-keygen is unavailable.");
        try
        {
            // The production handler reads the SHA-256 fingerprint from the event;
            // a real generated key keeps that value valid for KnownHostStore.
            var args = new HostKeyEventArgs(new KeyHostAlgorithm(algorithm, key.Key));

            var handler = (EventHandler<HostKeyEventArgs>)typeof(BaseClient)
                .GetField("HostKeyReceived", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(client)!;
            handler.Invoke(client, args);

            Assert.True(args.CanTrust);
        }
        finally
        {
            key.Dispose();
        }
    }

    private static GeneratedEd25519Key? GenerateEd25519Key()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sftp-hostkey-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, "host_key");
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ssh-keygen")
            {
                ArgumentList = { "-q", "-t", "ed25519", "-N", "", "-f", keyPath },
                UseShellExecute = false,
                RedirectStandardError = true
            });
            if (process == null || !process.WaitForExit(30_000) || process.ExitCode != 0)
                return null;

            return new GeneratedEd25519Key(new PrivateKeyFile(keyPath).Key, directory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class GeneratedEd25519Key(Renci.SshNet.Security.Key key, string directory) : IDisposable
    {
        public Renci.SshNet.Security.Key Key => key;

        public void Dispose()
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup.
            }
        }
    }
}
