using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SftpExplorerWinUI.Tests;

/// <summary>
/// Starts the same pinned disposable SFTP container that CI uses so factory
/// connection tests can exercise a real SSH handshake. Returns false (and the
/// test skips) when Docker or ssh-keygen are unavailable on the host.
/// </summary>
internal sealed class DisposableSftpServer : IDisposable
{
    private const string PinnedImage =
        "atmoz/sftp@sha256:81fa92512bf8ead4849f33c1c153907b86d32d77704d1c62a9c70b4316ae9e50";

    private readonly string _containerName;
    private readonly string? _keyDirectory;
    private bool _started;

    public int Port { get; }

    public string Username => "test";

    public string Password => "password";

    private DisposableSftpServer(int port, string containerName, string keyDirectory)
    {
        Port = port;
        _containerName = containerName;
        _keyDirectory = keyDirectory;
    }

    public static bool TryStart(out DisposableSftpServer? server)
    {
        server = null;
        if (!CommandSucceeds("docker", "version --format {{.Server.Version}}"))
            return false;

        var keyDirectory = Path.Combine(Path.GetTempPath(), "sftp-factory-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(keyDirectory);
        try
        {
            if (!GenerateHostKey(keyDirectory))
                return false;

            var port = GetFreePort();
            var containerName = "sftp-explorer-factory-" + Guid.NewGuid().ToString("N");
            var keyPath = Path.Combine(keyDirectory, "ssh_host_ed25519_key");

            if (!CommandSucceeds(
                    "docker",
                    $"run --detach --name {containerName} " +
                    $"-p 127.0.0.1:{port}:22 " +
                    $"-v \"{keyPath}:/etc/ssh/ssh_host_ed25519_key:ro\" " +
                    $"-v \"{keyPath}.pub:/etc/ssh/ssh_host_ed25519_key.pub:ro\" " +
                    $"{PinnedImage} test:password:::upload"))
            {
                return false;
            }

            if (!WaitForPort(port, TimeSpan.FromSeconds(60)))
            {
                StopContainer(containerName);
                return false;
            }

            server = new DisposableSftpServer(port, containerName, keyDirectory) { _started = true };
            return true;
        }
        catch
        {
            try
            {
                Directory.Delete(keyDirectory, recursive: true);
            }
            catch
            {
                // Best effort.
            }

            return false;
        }
    }

    public void Dispose()
    {
        if (!_started)
            return;

        StopContainer(_containerName);
        try
        {
            if (_keyDirectory != null && Directory.Exists(_keyDirectory))
                Directory.Delete(_keyDirectory, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private static bool GenerateHostKey(string keyDirectory)
    {
        var keyPath = Path.Combine(keyDirectory, "ssh_host_ed25519_key");
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ssh-keygen")
            {
                ArgumentList = { "-q", "-t", "ed25519", "-N", "", "-f", keyPath },
                UseShellExecute = false,
                RedirectStandardError = true
            });
            return process != null && process.WaitForExit(30_000) && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool WaitForPort(int port, TimeSpan timeout)
    {
        // Docker's port proxy accepts TCP connections before sshd inside the
        // container is ready (it generates a host key on first start). A bare
        // connect probe would therefore succeed too early and the SSH handshake
        // would fail with "closed before a valid identification string". Wait
        // for the real banner instead.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient();
                if (!probe.ConnectAsync(IPAddress.Loopback, port).Wait(500))
                    continue;

                using var stream = probe.GetStream();
                stream.ReadTimeout = 2_000;
                var buffer = new byte[64];
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read > 0 &&
                    System.Text.Encoding.ASCII.GetString(buffer, 0, read)
                        .StartsWith("SSH-", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch
            {
                // Not ready yet.
            }

            Thread.Sleep(250);
        }

        return false;
    }

    private static void StopContainer(string containerName)
    {
        try
        {
            CommandSucceeds("docker", $"rm --force {containerName}");
        }
        catch
        {
            // Best effort.
        }
    }

    private static bool CommandSucceeds(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process == null)
            return false;

        return process.WaitForExit(60_000) && process.ExitCode == 0;
    }
}
