using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;

namespace SftpExplorerWinUI.Services;

public sealed record HostKeyPrompt(
    string Hostname,
    int Port,
    string Algorithm,
    string Sha256Fingerprint)
{
    public string DisplayFingerprint => $"SHA256:{Sha256Fingerprint}";
}

public delegate Task<bool> HostKeyConfirmationAsync(
    HostKeyPrompt prompt,
    CancellationToken cancellationToken);

public class SshConnectionSecurityException : Exception
{
    public SshConnectionSecurityException(string message)
        : base(message)
    {
    }

    public SshConnectionSecurityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class HostKeyChangedException : SshConnectionSecurityException
{
    public HostKeyChangedException(
        string hostname,
        int port,
        string expectedAlgorithm,
        string expectedFingerprint,
        string receivedAlgorithm,
        string receivedFingerprint)
        : base(
            $"SECURITY WARNING: the SSH host key for {hostname}:{port} has changed. " +
            "The connection was blocked because this can indicate a server replacement or a man-in-the-middle attack. " +
            $"Expected {expectedAlgorithm} SHA256:{expectedFingerprint}; " +
            $"received {receivedAlgorithm} SHA256:{receivedFingerprint}. " +
            "Verify the new fingerprint with the server administrator before removing the saved host key.")
    {
        Hostname = hostname;
        Port = port;
        ExpectedAlgorithm = expectedAlgorithm;
        ExpectedFingerprint = expectedFingerprint;
        ReceivedAlgorithm = receivedAlgorithm;
        ReceivedFingerprint = receivedFingerprint;
    }

    public string Hostname { get; }

    public int Port { get; }

    public string ExpectedAlgorithm { get; }

    public string ExpectedFingerprint { get; }

    public string ReceivedAlgorithm { get; }

    public string ReceivedFingerprint { get; }
}

public sealed class HostKeyRejectedException : SshConnectionSecurityException
{
    public HostKeyRejectedException(string hostname, int port)
        : base($"The SSH host key for {hostname}:{port} was not trusted. The connection was cancelled.")
    {
    }
}

public sealed class HostKeyStoreException : SshConnectionSecurityException
{
    public HostKeyStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Immutable SSH endpoint plus a protected recipe for creating fresh password or
/// private-key authentication methods. The plaintext secret is never exposed.
/// </summary>
public sealed class SshConnectionSession : IDisposable
{
    private static readonly byte[] PasswordEntropy =
        Encoding.UTF8.GetBytes("SftpExplorerWinUI.SshConnectionSession.v1");

    private readonly object _credentialLock = new();
    private byte[]? _protectedPassword;

    internal SshConnectionSession(
        string hostname,
        int port,
        string username,
        string secret,
        Models.SftpAuthenticationMode authenticationMode = Models.SftpAuthenticationMode.Password,
        string? privateKeyPath = null,
        long authenticationRevision = 0)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SFTP Explorer stores SSH credentials with Windows DPAPI.");
        }

        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new ArgumentException("An SSH hostname is required.", nameof(hostname));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "The SSH port must be between 1 and 65535.");
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("An SSH username is required.", nameof(username));
        }

        Hostname = hostname.Trim();
        Port = port;
        Username = username;
        if (!Enum.IsDefined(authenticationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(authenticationMode));
        }
        AuthenticationMode = authenticationMode;
        PrivateKeyPath = NormalizePrivateKeyPath(privateKeyPath);
        AuthenticationRevision = authenticationRevision;
        if (authenticationMode == Models.SftpAuthenticationMode.PrivateKey &&
            (string.IsNullOrWhiteSpace(PrivateKeyPath) || !File.Exists(PrivateKeyPath)))
        {
            throw new FileNotFoundException("The private key file was not found.", PrivateKeyPath);
        }

        var clearPassword = Encoding.UTF8.GetBytes(secret ?? string.Empty);
        try
        {
            _protectedPassword = ProtectedData.Protect(
                clearPassword,
                PasswordEntropy,
                DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clearPassword);
        }
    }

    public string Hostname { get; }

    public int Port { get; }

    public string Username { get; }
    public Models.SftpAuthenticationMode AuthenticationMode { get; }
    public string? PrivateKeyPath { get; }
    public long AuthenticationRevision { get; }

    public string EndpointKey => KnownHostStore.GetEndpointKey(Hostname, Port);

    internal ConnectionInfo CreateConnectionInfo()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SFTP Explorer stores SSH credentials with Windows DPAPI.");
        }

        byte[] protectedPassword;
        lock (_credentialLock)
        {
            if (_protectedPassword == null)
            {
                throw new ObjectDisposedException(nameof(SshConnectionSession));
            }

            protectedPassword = _protectedPassword.ToArray();
        }

        byte[]? clearPassword = null;
        try
        {
            clearPassword = ProtectedData.Unprotect(
                protectedPassword,
                PasswordEntropy,
                DataProtectionScope.CurrentUser);
            var secret = Encoding.UTF8.GetString(clearPassword);
            AuthenticationMethod authenticationMethod = AuthenticationMode switch
            {
                Models.SftpAuthenticationMode.Password =>
                    new PasswordAuthenticationMethod(Username, secret),
                Models.SftpAuthenticationMode.PrivateKey =>
                    new PrivateKeyAuthenticationMethod(
                        Username,
                        string.IsNullOrEmpty(secret)
                            ? new PrivateKeyFile(PrivateKeyPath!)
                            : new PrivateKeyFile(PrivateKeyPath!, secret)),
                _ => throw new InvalidOperationException("Unsupported SFTP authentication mode.")
            };
            return new ConnectionInfo(
                Hostname,
                Port,
                Username,
                authenticationMethod)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedPassword);
            if (clearPassword != null)
            {
                CryptographicOperations.ZeroMemory(clearPassword);
            }
        }
    }

    public static string? NormalizePrivateKeyPath(string? path)
    {
        var value = Environment.ExpandEnvironmentVariables(path?.Trim().Trim('"') ?? "");
        if (value == "~")
        {
            value = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (value.Length > 1 && value[0] == '~' && IsDirectorySeparator(value[1]))
        {
            value = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                NormalizeDirectorySeparators(value[2..]));
        }

        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(NormalizeDirectorySeparators(value)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return value;
        }
    }

    private static bool IsDirectorySeparator(char value) => value is '/' or '\\';

    private static string NormalizeDirectorySeparators(string value) =>
        value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    public void Dispose()
    {
        lock (_credentialLock)
        {
            if (_protectedPassword == null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_protectedPassword);
            _protectedPassword = null;
        }
    }
}
