using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SftpExplorerWinUI.Services;

/// <summary>
/// Persistent trust-on-first-use store for SSH server keys. A malformed store
/// fails closed instead of silently forgetting trusted keys.
/// </summary>
public sealed class KnownHostStore
{
    private const int CurrentFormatVersion = 1;
    private const string InterprocessLockFileName = ".sftpexplorer-known-hosts.lock";
    private static readonly TimeSpan InterprocessLockTimeout = TimeSpan.FromSeconds(10);
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public KnownHostStore(string? filePath = null)
    {
        _filePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SftpExplorer",
            "known-hosts.json"));
    }

    public string FilePath => _filePath;

    public static string GetEndpointKey(string hostname, int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var normalizedHostname = NormalizeHostname(hostname);
        var formattedHostname = normalizedHostname.Contains(':', StringComparison.Ordinal)
            ? $"[{normalizedHostname}]"
            : normalizedHostname;
        return $"{formattedHostname}:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    public async Task VerifyAsync(
        HostKeyPrompt presentedKey,
        HostKeyConfirmationAsync confirmation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentedKey);
        ArgumentNullException.ThrowIfNull(confirmation);

        if (!IsValidAlgorithm(presentedKey.Algorithm) ||
            !IsValidSha256Fingerprint(presentedKey.Sha256Fingerprint))
        {
            throw new SshConnectionSecurityException(
                $"The SSH server {presentedKey.Hostname}:{presentedKey.Port} supplied an invalid host key.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var endpointKey = GetEndpointKey(presentedKey.Hostname, presentedKey.Port);
            await using (var interprocessLock = await AcquireInterprocessLockAsync(cancellationToken)
                             .ConfigureAwait(false))
            {
                var document = LoadFresh(cancellationToken);
                var knownKey = FindKnownKey(document, endpointKey);
                if (knownKey != null)
                {
                    VerifyMatchingKey(presentedKey, knownKey);
                    return;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var trusted = await confirmation(presentedKey, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!trusted)
            {
                throw new HostKeyRejectedException(presentedKey.Hostname, presentedKey.Port);
            }

            // The user prompt runs without a cross-process lease. Reload after
            // confirmation so a key saved by another app instance is never lost
            // or silently replaced by this stale snapshot.
            await using var saveLock = await AcquireInterprocessLockAsync(cancellationToken)
                .ConfigureAwait(false);
            var latestDocument = LoadFresh(cancellationToken);
            var concurrentlyKnownKey = FindKnownKey(latestDocument, endpointKey);
            if (concurrentlyKnownKey != null)
            {
                VerifyMatchingKey(presentedKey, concurrentlyKnownKey);
                return;
            }

            var updatedDocument = latestDocument.Clone();
            updatedDocument.Hosts.Add(new KnownHostEntry
            {
                Endpoint = endpointKey,
                Hostname = NormalizeHostname(presentedKey.Hostname),
                Port = presentedKey.Port,
                Algorithm = presentedKey.Algorithm,
                Sha256Fingerprint = presentedKey.Sha256Fingerprint,
                FirstSeenUtc = DateTimeOffset.UtcNow
            });
            updatedDocument.Hosts.Sort((left, right) =>
                string.CompareOrdinal(left.Endpoint, right.Endpoint));
            Save(updatedDocument, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string hostname, int port, CancellationToken cancellationToken = default)
    {
        var endpointKey = GetEndpointKey(hostname, port);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var interprocessLock = await AcquireInterprocessLockAsync(cancellationToken)
                .ConfigureAwait(false);
            var document = LoadFresh(cancellationToken);
            var updatedDocument = document.Clone();
            updatedDocument.Hosts.RemoveAll(entry =>
                string.Equals(entry.Endpoint, endpointKey, StringComparison.Ordinal));

            // File.Replace deliberately keeps the old primary as .bak. For a
            // host-key revocation that old document must not remain recoverable:
            // a later corrupt primary could otherwise silently trust the key
            // that the user explicitly removed. The second durable write makes
            // both primary and backup contain the post-revocation document.
            Save(updatedDocument, cancellationToken);
            Save(updatedDocument, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<InterprocessFileLock> AcquireInterprocessLockAsync(
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The SSH known-hosts file has no parent directory.");
        try
        {
            return await InterprocessFileLock.AcquireAsync(
                    directory,
                    InterprocessLockFileName,
                    InterprocessLockTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new HostKeyStoreException(
                "The SSH known-hosts file is busy or could not be locked safely. No connection was attempted.",
                ex);
        }
    }

    private KnownHostDocument LoadFresh(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = AtomicJsonFile.Load(
                _filePath,
                static () => new KnownHostDocument());
            var document = result.Value;
            if (document.Version != CurrentFormatVersion || document.Hosts == null)
            {
                throw new InvalidDataException("The SSH known-hosts file has an unsupported or invalid format.");
            }

            ValidateEntries(document);
            if (result.RepairError != null)
            {
                throw new IOException(
                    "A valid SSH known-hosts backup was loaded, but the primary file could not be repaired.",
                    result.RepairError);
            }

            return document;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException or ArgumentException)
        {
            throw new HostKeyStoreException(
                "The SSH known-hosts file could not be read safely. No connection was attempted.",
                ex);
        }
    }

    private void Save(KnownHostDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            AtomicJsonFile.Save(
                _filePath,
                document,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new HostKeyStoreException(
                "The trusted SSH host key could not be saved. The connection was blocked.",
                ex);
        }
    }

    private static KnownHostEntry? FindKnownKey(KnownHostDocument document, string endpointKey) =>
        document.Hosts.FirstOrDefault(entry =>
            string.Equals(entry.Endpoint, endpointKey, StringComparison.Ordinal));

    private static void VerifyMatchingKey(HostKeyPrompt presentedKey, KnownHostEntry knownKey)
    {
        if (FixedTimeEquals(knownKey.Algorithm, presentedKey.Algorithm) &&
            FixedTimeEquals(knownKey.Sha256Fingerprint, presentedKey.Sha256Fingerprint))
        {
            return;
        }

        throw new HostKeyChangedException(
            presentedKey.Hostname,
            presentedKey.Port,
            knownKey.Algorithm,
            knownKey.Sha256Fingerprint,
            presentedKey.Algorithm,
            presentedKey.Sha256Fingerprint);
    }

    private static void ValidateEntries(KnownHostDocument document)
    {
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Hosts)
        {
            if (string.IsNullOrWhiteSpace(entry.Endpoint) ||
                string.IsNullOrWhiteSpace(entry.Hostname) ||
                entry.Port is < 1 or > 65535 ||
                !IsValidAlgorithm(entry.Algorithm) ||
                !IsValidSha256Fingerprint(entry.Sha256Fingerprint) ||
                !string.Equals(
                    entry.Endpoint,
                    GetEndpointKey(entry.Hostname, entry.Port),
                    StringComparison.Ordinal) ||
                !endpoints.Add(entry.Endpoint))
            {
                throw new InvalidDataException("The SSH known-hosts file contains an invalid entry.");
            }
        }
    }

    private static string NormalizeHostname(string hostname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        var value = hostname.Trim().TrimEnd('.');
        if (value.Length == 0)
        {
            throw new ArgumentException("The SSH hostname is invalid.", nameof(hostname));
        }
        if (IPAddress.TryParse(value, out var address))
        {
            return address.ToString().ToLowerInvariant();
        }

        try
        {
            return new IdnMapping().GetAscii(value).ToLowerInvariant();
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("The SSH hostname is invalid.", nameof(hostname), ex);
        }
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsValidAlgorithm(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length <= 128 &&
               !value.Any(char.IsControl);
    }

    private static bool IsValidSha256Fingerprint(string? value)
    {
        if (value == null || value.Length != 43 || value.Any(char.IsWhiteSpace) || value.Contains('='))
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(value + "=").Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed class KnownHostDocument
    {
        public int Version { get; set; } = CurrentFormatVersion;

        public List<KnownHostEntry> Hosts { get; set; } = new();

        public KnownHostDocument Clone()
        {
            return new KnownHostDocument
            {
                Version = Version,
                Hosts = Hosts.Select(static entry => entry.Clone()).ToList()
            };
        }
    }

    private sealed class KnownHostEntry
    {
        public string Endpoint { get; set; } = string.Empty;

        public string Hostname { get; set; } = string.Empty;

        public int Port { get; set; }

        public string Algorithm { get; set; } = string.Empty;

        public string Sha256Fingerprint { get; set; } = string.Empty;

        public DateTimeOffset FirstSeenUtc { get; set; }

        public KnownHostEntry Clone()
        {
            return new KnownHostEntry
            {
                Endpoint = Endpoint,
                Hostname = Hostname,
                Port = Port,
                Algorithm = Algorithm,
                Sha256Fingerprint = Sha256Fingerprint,
                FirstSeenUtc = FirstSeenUtc
            };
        }
    }
}
