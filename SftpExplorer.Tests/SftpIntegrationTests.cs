using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;

namespace SftpExplorerWinUI.Tests;

public sealed class SftpIntegrationTests
{
    [Fact]
    public async Task DisposableServerSupportsBasicFileAndDirectoryLifecycle()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SFTP_TEST_ENABLED"),
                "1",
                StringComparison.Ordinal))
        {
            // Local unit-test runs do not require Docker. CI explicitly enables
            // this test and treats missing configuration as a failure below.
            return;
        }

        var host = GetRequiredEnvironmentVariable("SFTP_TEST_HOST");
        var port = int.Parse(GetRequiredEnvironmentVariable("SFTP_TEST_PORT"));
        var username = GetRequiredEnvironmentVariable("SFTP_TEST_USERNAME");
        var password = GetRequiredEnvironmentVariable("SFTP_TEST_PASSWORD");
        var writableRoot = GetRequiredEnvironmentVariable("SFTP_TEST_WRITABLE_PATH").TrimEnd('/');
        var expectedFingerprint = NormalizeFingerprint(
            GetRequiredEnvironmentVariable("SFTP_TEST_HOST_KEY_SHA256"));

        var connectionInfo = new PasswordConnectionInfo(host, port, username, password);
        const string pinnedHostKeyAlgorithm = "ssh-ed25519";
        Assert.Contains(pinnedHostKeyAlgorithm, connectionInfo.HostKeyAlgorithms.Keys);
        foreach (var algorithm in connectionInfo.HostKeyAlgorithms.Keys
                     .Where(name => !string.Equals(name, pinnedHostKeyAlgorithm, StringComparison.Ordinal))
                     .ToArray())
        {
            connectionInfo.HostKeyAlgorithms.Remove(algorithm);
        }

        using var client = new SftpClient(connectionInfo);
        var hostKeyWasValidated = false;
        client.HostKeyReceived += (_, eventArgs) =>
        {
            hostKeyWasValidated = true;
            var actualFingerprint = NormalizeFingerprint(eventArgs.FingerPrintSHA256);
            eventArgs.CanTrust = FixedTimeEquals(expectedFingerprint, actualFingerprint);
        };

        client.Connect();
        Assert.True(hostKeyWasValidated);

        var testToken = Guid.NewGuid().ToString("N");
        var originalRoot = $"{writableRoot}/ci-{testToken}-original";
        var renamedRoot = $"{writableRoot}/ci-{testToken}-renamed";
        var originalFile = $"{renamedRoot}/original.txt";
        var renamedFile = $"{renamedRoot}/renamed.txt";
        try
        {
            client.CreateDirectory(originalRoot);
            Assert.True(client.Exists(originalRoot));

            client.RenameFile(originalRoot, renamedRoot);
            Assert.False(client.Exists(originalRoot));
            Assert.True(client.Exists(renamedRoot));

            var expectedContent = Encoding.UTF8.GetBytes("SFTP Explorer integration smoke test");
            ulong uploadedBytes = 0;
            using (var upload = new MemoryStream(expectedContent, writable: false))
            {
                await client.UploadFileAsync(
                    upload,
                    originalFile,
                    canOverride: false,
                    new InlineProgress<UploadFileProgressReport>(report =>
                        uploadedBytes = report.TotalBytesUploaded),
                    CancellationToken.None);
            }
            Assert.Equal((ulong)expectedContent.Length, uploadedBytes);
            Assert.True(client.Exists(originalFile));

            client.RenameFile(originalFile, renamedFile);
            Assert.False(client.Exists(originalFile));
            Assert.True(client.Exists(renamedFile));

            ulong downloadedBytes = 0;
            using var download = new MemoryStream();
            await client.DownloadFileAsync(
                renamedFile,
                download,
                new InlineProgress<DownloadFileProgressReport>(report =>
                    downloadedBytes = report.TotalBytesDownloaded),
                CancellationToken.None);
            Assert.Equal((ulong)expectedContent.Length, downloadedBytes);
            Assert.Equal(expectedContent, download.ToArray());

            client.DeleteFile(renamedFile);
            Assert.False(client.Exists(renamedFile));
            client.DeleteDirectory(renamedRoot);
            Assert.False(client.Exists(renamedRoot));
        }
        finally
        {
            if (client.Exists(originalFile))
                client.DeleteFile(originalFile);
            if (client.Exists(renamedFile))
                client.DeleteFile(renamedFile);
            if (client.Exists(originalRoot))
                client.DeleteDirectory(originalRoot);
            if (client.Exists(renamedRoot))
                client.DeleteDirectory(renamedRoot);
            client.Disconnect();
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static string GetRequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required integration-test variable '{name}' is missing.");

    private static string NormalizeFingerprint(string fingerprint) =>
        fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? fingerprint[7..]
            : fingerprint;

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
