using System.Diagnostics;
using Renci.SshNet;
using SftpExplorerWinUI.Services;
using SftpExplorerWinUI.Models;

namespace SftpExplorerWinUI.Tests;

public sealed class SshConnectionSessionTests
{
    [Fact]
    public void SessionCreatesFreshAuthenticationStateAndCannotBeUsedAfterDispose()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var factory = new SshClientFactory();
        var session = factory.CreatePasswordSession(" example.test ", 2222, "user", "secret");

        var first = session.CreateConnectionInfo();
        var second = session.CreateConnectionInfo();

        Assert.Equal("example.test", session.Hostname);
        Assert.Equal("example.test:2222", session.EndpointKey);
        Assert.Equal(TimeSpan.FromSeconds(30), first.Timeout);
        Assert.NotSame(first, second);
        Assert.NotSame(first.AuthenticationMethods.Single(), second.AuthenticationMethods.Single());

        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.CreateConnectionInfo());
        session.Dispose();
    }

    [Theory]
    [InlineData("", 22, "user")]
    [InlineData("example.test", 0, "user")]
    [InlineData("example.test", 65536, "user")]
    [InlineData("example.test", 22, " ")]
    public void InvalidEndpointOrUsernameIsRejected(string hostname, int port, string username)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var factory = new SshClientFactory();

        Assert.ThrowsAny<ArgumentException>(() =>
            factory.CreatePasswordSession(hostname, port, username, "secret"));
    }

    [Fact]
    public void PrivateKeySessionRejectsMissingKeyBeforeConnecting()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var factory = new SshClientFactory();
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.key");

        Assert.Throws<FileNotFoundException>(() => factory.CreateSession(
            "example.test",
            22,
            "user",
            SftpAuthenticationMode.PrivateKey,
            "passphrase",
            missingPath));
    }

    [Fact]
    public void PrivateKeySessionAcceptsAnExistingKeyWithAndWithoutPassphrase()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var factory = new SshClientFactory();
        using var keyFile = GenerateRsaPrivateKey();

        // No passphrase: the secret stays empty and only the path is used.
        using (var session = factory.CreateSession(
                   "example.test", 22, "user", SftpAuthenticationMode.PrivateKey, "", keyFile.Path))
        {
            var method = Assert.IsType<PrivateKeyAuthenticationMethod>(
                session.CreateConnectionInfo().AuthenticationMethods.Single());
            Assert.Equal(keyFile.Path, session.PrivateKeyPath);
        }

        // With passphrase: the secret is carried into the authentication method.
        using (var session = factory.CreateSession(
                   "example.test", 22, "user", SftpAuthenticationMode.PrivateKey, "passphrase", keyFile.Path))
        {
            var info = session.CreateConnectionInfo();
            Assert.IsType<PrivateKeyAuthenticationMethod>(info.AuthenticationMethods.Single());
        }
    }

    [Fact]
    public void InvalidAuthenticationModesAreRejected()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var factory = new SshClientFactory();

        Assert.Throws<ArgumentOutOfRangeException>(() => factory.CreateSession(
            "example.test", 22, "user", (SftpAuthenticationMode)99, "secret", null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizePrivateKeyPathReturnsNullForBlankInput(string? path)
    {
        Assert.Null(SshConnectionSession.NormalizePrivateKeyPath(path));
    }

    [Fact]
    public void NormalizePrivateKeyPathExpandsTildeAndEnvironmentVariables()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(
            Path.GetFullPath(profile),
            SshConnectionSession.NormalizePrivateKeyPath("~"));

        var subKey = Path.Combine(profile, "keys", "id_ed25519");
        Assert.Equal(
            Path.GetFullPath(subKey),
            SshConnectionSession.NormalizePrivateKeyPath($"~{Path.DirectorySeparatorChar}keys{Path.DirectorySeparatorChar}id_ed25519"));

        Environment.SetEnvironmentVariable("SFTP_TEST_KEY_DIR", profile);
        try
        {
            Assert.Equal(
                subKey,
                SshConnectionSession.NormalizePrivateKeyPath("%SFTP_TEST_KEY_DIR%\\keys\\id_ed25519"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SFTP_TEST_KEY_DIR", null);
        }
    }

    [Fact]
    public void NormalizePrivateKeyPathTrimsQuotesAndWhitespace()
    {
        var expected = SshConnectionSession.NormalizePrivateKeyPath("/tmp/id_ed25519");
        Assert.Equal(expected, SshConnectionSession.NormalizePrivateKeyPath("  \"/tmp/id_ed25519\"  "));
    }

    [Fact]
    public void HostKeyPromptDisplaysFingerprintWithSha256Prefix()
    {
        var prompt = new HostKeyPrompt(
            "example.test", 22, "ssh-ed25519", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        Assert.Equal("SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", prompt.DisplayFingerprint);
    }

    private static GeneratedKeyFile GenerateRsaPrivateKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sftp-key-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var keyPath = Path.Combine(directory, "id_rsa");
        using (var process = Process.Start(new ProcessStartInfo("ssh-keygen")
               {
                   ArgumentList = { "-q", "-t", "rsa", "-b", "2048", "-N", "", "-f", keyPath },
                   UseShellExecute = false,
                   RedirectStandardError = true
               }))
        {
            if (process == null || !process.WaitForExit(30_000) || process.ExitCode != 0)
                throw new InvalidOperationException("ssh-keygen failed to generate the test key.");
        }

        return new GeneratedKeyFile(keyPath, directory);
    }

    private sealed class GeneratedKeyFile(string path, string directory) : IDisposable
    {
        public string Path => path;

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
