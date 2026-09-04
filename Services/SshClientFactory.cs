using Renci.SshNet;
using Renci.SshNet.Common;

namespace SftpExplorerWinUI.Services;

/// <summary>
/// Creates independent SSH.NET clients with fresh authentication methods and
/// mandatory persistent host-key validation.
/// </summary>
public sealed class SshClientFactory
{
    private readonly KnownHostStore _knownHosts;

    public SshClientFactory(KnownHostStore? knownHosts = null)
    {
        _knownHosts = knownHosts ?? new KnownHostStore();
    }

    public KnownHostStore KnownHosts => _knownHosts;

    public SshConnectionSession CreatePasswordSession(
        string hostname,
        int port,
        string username,
        string password)
    {
        return new SshConnectionSession(hostname, port, username, password);
    }

    public SshConnectionSession CreateSession(
        string hostname,
        int port,
        string username,
        Models.SftpAuthenticationMode authenticationMode,
        string secret,
        string? privateKeyPath,
        long authenticationRevision = 0) =>
        new(hostname, port, username, secret, authenticationMode, privateKeyPath, authenticationRevision);

    public SftpClient CreateSftpClient(
        SshConnectionSession session,
        HostKeyConfirmationAsync confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var client = new SftpClient(session.CreateConnectionInfo());
        ConfigureClient(client);
        AttachHostKeyValidation(client, session, confirmation, cancellationToken);
        return client;
    }

    public SshClient CreateSshClient(
        SshConnectionSession session,
        HostKeyConfirmationAsync confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var client = new SshClient(session.CreateConnectionInfo());
        ConfigureClient(client);
        AttachHostKeyValidation(client, session, confirmation, cancellationToken);
        return client;
    }

    public async Task<SftpClient> ConnectSftpAsync(
        SshConnectionSession session,
        HostKeyConfirmationAsync confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var client = new SftpClient(session.CreateConnectionInfo());
        ConfigureClient(client);
        var validation = AttachHostKeyValidation(
            client,
            session,
            confirmation,
            cancellationToken);
        try
        {
            // Start SSH.NET from a worker even when the caller is the WinUI
            // thread. HostKeyReceived is synchronous; running it on the UI
            // thread while it awaits an inline confirmation would deadlock.
            await Task.Run(
                async () => await client.ConnectAsync(cancellationToken).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
            return client;
        }
        catch (Exception connectionError)
        {
            TryDispose(client);
            var validationFailure = validation.Failure;
            if (validationFailure != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(validationFailure)
                    .Throw();
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(connectionError)
                .Throw();
            throw;
        }
    }

    public async Task<SshClient> ConnectSshAsync(
        SshConnectionSession session,
        HostKeyConfirmationAsync confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var client = new SshClient(session.CreateConnectionInfo());
        ConfigureClient(client);
        var validation = AttachHostKeyValidation(
            client,
            session,
            confirmation,
            cancellationToken);
        try
        {
            await Task.Run(
                async () => await client.ConnectAsync(cancellationToken).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
            return client;
        }
        catch (Exception connectionError)
        {
            TryDispose(client);
            var validationFailure = validation.Failure;
            if (validationFailure != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(validationFailure)
                    .Throw();
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(connectionError)
                .Throw();
            throw;
        }
    }

    private HostKeyValidationState AttachHostKeyValidation(
        BaseClient client,
        SshConnectionSession session,
        HostKeyConfirmationAsync confirmation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        var validation = new HostKeyValidationState();

        client.HostKeyReceived += (_, eventArgs) =>
        {
            // SSH.NET raises this callback from its connection worker. It must
            // make the trust decision synchronously, so the UI callback posts
            // to the dispatcher and this worker waits without blocking the UI.
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prompt = CreatePrompt(session, eventArgs);
                _knownHosts
                    .VerifyAsync(prompt, confirmation, cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                eventArgs.CanTrust = true;
            }
            catch (Exception ex)
            {
                validation.Failure = ex;
                eventArgs.CanTrust = false;
            }
        };

        return validation;
    }

    private static HostKeyPrompt CreatePrompt(
        SshConnectionSession session,
        HostKeyEventArgs eventArgs)
    {
        return new HostKeyPrompt(
            session.Hostname,
            session.Port,
            eventArgs.HostKeyName ?? string.Empty,
            NormalizeSha256Fingerprint(eventArgs.FingerPrintSHA256));
    }

    private static void ConfigureClient(BaseClient client)
    {
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);
        if (client is SftpClient sftpClient)
        {
            // The SSH.NET default is conservative. A larger packet substantially
            // reduces request/response overhead for high-latency folder transfers
            // without changing transfer semantics or opening unbounded buffers.
            sftpClient.BufferSize = 256 * 1024;
        }
    }

    private static string NormalizeSha256Fingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return string.Empty;
        }

        var normalized = fingerprint.Trim();
        const string prefix = "SHA256:";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : normalized;
    }

    private static void TryDispose(IDisposable client)
    {
        try
        {
            client.Dispose();
        }
        catch
        {
            // Preserve the connection or host-key validation exception.
        }
    }

    private sealed class HostKeyValidationState
    {
        private Exception? _failure;

        public Exception? Failure
        {
            get => Volatile.Read(ref _failure);
            set => Volatile.Write(ref _failure, value);
        }
    }
}
