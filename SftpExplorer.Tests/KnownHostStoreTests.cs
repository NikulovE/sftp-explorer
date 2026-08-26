using SftpExplorerWinUI.Services;

namespace SftpExplorerWinUI.Tests;

public sealed class KnownHostStoreTests
{
    private static readonly HostKeyPrompt FirstKey = new(
        "example.test",
        22,
        "ssh-ed25519",
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

    [Fact]
    public async Task FirstUsePersistsAndMatchingKeyDoesNotPromptAgain()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        var confirmationCount = 0;
        var firstStore = new KnownHostStore(path);

        await firstStore.VerifyAsync(FirstKey, Confirm, CancellationToken.None);
        Assert.True(File.Exists(path));

        var reloadedStore = new KnownHostStore(path);
        await reloadedStore.VerifyAsync(FirstKey, Confirm, CancellationToken.None);

        Assert.Equal(1, confirmationCount);
        return;

        Task<bool> Confirm(HostKeyPrompt _, CancellationToken __)
        {
            confirmationCount++;
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task ChangedKeyFailsClosedWithoutAskingForTofuConfirmation()
    {
        using var temp = new TemporaryDirectory();
        var store = new KnownHostStore(System.IO.Path.Combine(temp.Path, "known-hosts.json"));
        await store.VerifyAsync(
            FirstKey,
            static (_, _) => Task.FromResult(true),
            CancellationToken.None);

        var changed = FirstKey with
        {
            Sha256Fingerprint = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"
        };
        var confirmationWasCalled = false;

        await Assert.ThrowsAsync<HostKeyChangedException>(() => store.VerifyAsync(
            changed,
            (_, _) =>
            {
                confirmationWasCalled = true;
                return Task.FromResult(true);
            },
            CancellationToken.None));

        Assert.False(confirmationWasCalled);
    }

    [Fact]
    public async Task RejectedFirstKeyIsNotPersistedOrCached()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        var store = new KnownHostStore(path);
        var confirmationCount = 0;

        await Assert.ThrowsAsync<HostKeyRejectedException>(() => store.VerifyAsync(
            FirstKey,
            (_, _) =>
            {
                confirmationCount++;
                return Task.FromResult(false);
            },
            CancellationToken.None));

        Assert.False(File.Exists(path));
        await store.VerifyAsync(
            FirstKey,
            (_, _) =>
            {
                confirmationCount++;
                return Task.FromResult(true);
            },
            CancellationToken.None);
        Assert.Equal(2, confirmationCount);
    }

    [Fact]
    public async Task CorruptPrimaryRecoversKnownKeyFromBackup()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        var store = new KnownHostStore(path);
        await store.VerifyAsync(FirstKey, static (_, _) => Task.FromResult(true), CancellationToken.None);
        await store.VerifyAsync(
            FirstKey with { Hostname = "second.example.test" },
            static (_, _) => Task.FromResult(true),
            CancellationToken.None);
        File.WriteAllText(path, "corrupt primary");

        var confirmationWasCalled = false;
        var recoveredStore = new KnownHostStore(path);
        await recoveredStore.VerifyAsync(
            FirstKey,
            (_, _) =>
            {
                confirmationWasCalled = true;
                return Task.FromResult(false);
            },
            CancellationToken.None);

        Assert.False(confirmationWasCalled);
    }

    [Fact]
    public async Task RemoveThenCorruptPrimaryDoesNotRestoreRemovedKey()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        var store = new KnownHostStore(path);
        await store.VerifyAsync(FirstKey, static (_, _) => Task.FromResult(true), CancellationToken.None);
        await store.VerifyAsync(
            FirstKey with { Hostname = "second.example.test" },
            static (_, _) => Task.FromResult(true),
            CancellationToken.None);

        await store.RemoveAsync(FirstKey.Hostname, FirstKey.Port, CancellationToken.None);
        File.WriteAllText(path, "corrupt primary");

        var confirmationWasCalled = false;
        await Assert.ThrowsAsync<HostKeyRejectedException>(() => new KnownHostStore(path).VerifyAsync(
            FirstKey,
            (_, _) =>
            {
                confirmationWasCalled = true;
                return Task.FromResult(false);
            },
            CancellationToken.None));

        Assert.True(confirmationWasCalled);
    }

    [Fact]
    public async Task CorruptPrimaryAndBackupFailClosedBeforeConfirmation()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        var store = new KnownHostStore(path);
        await store.VerifyAsync(FirstKey, static (_, _) => Task.FromResult(true), CancellationToken.None);
        await store.VerifyAsync(
            FirstKey with { Hostname = "second.example.test" },
            static (_, _) => Task.FromResult(true),
            CancellationToken.None);
        File.WriteAllText(path, "corrupt primary");
        File.WriteAllText(AtomicJsonFile.GetBackupPath(path), "corrupt backup");
        var confirmationWasCalled = false;

        await Assert.ThrowsAsync<HostKeyStoreException>(() => new KnownHostStore(path).VerifyAsync(
            FirstKey,
            (_, _) =>
            {
                confirmationWasCalled = true;
                return Task.FromResult(true);
            },
            CancellationToken.None));

        Assert.False(confirmationWasCalled);
    }

    [Fact]
    public void Ipv6EndpointUsesCanonicalBracketedForm()
    {
        Assert.Equal(
            "[2001:db8::1]:2222",
            KnownHostStore.GetEndpointKey("2001:0db8:0:0:0:0:0:1", 2222));
    }

    [Fact]
    public void Ipv4EndpointIsCanonicalizedWithoutBrackets()
    {
        Assert.Equal("192.168.0.10:22", KnownHostStore.GetEndpointKey("192.168.0.10", 22));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void EndpointKeyRejectsInvalidPorts(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KnownHostStore.GetEndpointKey("example.test", port));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    public void EndpointKeyRejectsBlankOrDotOnlyHostnames(string hostname)
    {
        Assert.Throws<ArgumentException>(() => KnownHostStore.GetEndpointKey(hostname, 22));
    }

    [Fact]
    public async Task RemoveAsyncRevokesTrustAndPurgesTheBackupCopy()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        var store = new KnownHostStore(path);
        await store.VerifyAsync(FirstKey, static (_, _) => Task.FromResult(true), CancellationToken.None);

        await store.RemoveAsync("example.test", 22);

        // The key is gone from both primary and backup: a later corrupt primary
        // must not resurrect the revoked trust decision.
        var document = System.Text.Json.JsonSerializer.Deserialize<RemoveDocument>(File.ReadAllText(path));
        Assert.Empty(document!.Hosts);
        var backup = System.Text.Json.JsonSerializer.Deserialize<RemoveDocument>(
            File.ReadAllText(AtomicJsonFile.GetBackupPath(path)));
        Assert.Empty(backup!.Hosts);

        // Reconnecting to the same endpoint asks for confirmation again.
        var confirmationCount = 0;
        await store.VerifyAsync(FirstKey, (_, _) =>
        {
            confirmationCount++;
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.Equal(1, confirmationCount);
    }

    [Fact]
    public async Task RemoveUnknownEndpointIsANoOp()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        var store = new KnownHostStore(path);
        await store.VerifyAsync(FirstKey, static (_, _) => Task.FromResult(true), CancellationToken.None);

        await store.RemoveAsync("unknown.example.test", 22);

        var confirmationWasCalled = false;
        await store.VerifyAsync(FirstKey, (_, _) =>
        {
            confirmationWasCalled = true;
            return Task.FromResult(false);
        }, CancellationToken.None);
        Assert.False(confirmationWasCalled);
    }

    [Fact]
    public async Task UnsupportedFormatVersionFailsClosed()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        File.WriteAllText(path, """{"Version":99,"Hosts":[]}""");

        await Assert.ThrowsAsync<HostKeyStoreException>(() => new KnownHostStore(path).VerifyAsync(
            FirstKey,
            static (_, _) => Task.FromResult(true),
            CancellationToken.None));
    }

    [Fact]
    public async Task InvalidPersistedEntryFailsClosed()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        // The endpoint does not match the canonical form of its hostname/port.
        File.WriteAllText(path, """
            {"Version":1,"Hosts":[{
                "Endpoint":"example.test:23",
                "Hostname":"example.test",
                "Port":22,
                "Algorithm":"ssh-ed25519",
                "Sha256Fingerprint":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                "FirstSeenUtc":"2026-01-01T00:00:00Z"
            }]}
        """);

        await Assert.ThrowsAsync<HostKeyStoreException>(() => new KnownHostStore(path).VerifyAsync(
            FirstKey,
            static (_, _) => Task.FromResult(true),
            CancellationToken.None));
    }

    [Fact]
    public async Task DuplicatePersistedEndpointsFailClosed()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        File.WriteAllText(path, """
            {"Version":1,"Hosts":[
                {"Endpoint":"example.test:22","Hostname":"example.test","Port":22,
                 "Algorithm":"ssh-ed25519",
                 "Sha256Fingerprint":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                 "FirstSeenUtc":"2026-01-01T00:00:00Z"},
                {"Endpoint":"example.test:22","Hostname":"example.test","Port":22,
                 "Algorithm":"ssh-ed25519",
                 "Sha256Fingerprint":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                 "FirstSeenUtc":"2026-01-02T00:00:00Z"}
            ]}
        """);

        await Assert.ThrowsAsync<HostKeyStoreException>(() => new KnownHostStore(path).VerifyAsync(
            FirstKey,
            static (_, _) => Task.FromResult(true),
            CancellationToken.None));
    }

    [Fact]
    public async Task AlgorithmChangeIsReportedAsChangedKey()
    {
        using var temp = new TemporaryDirectory();
        var store = new KnownHostStore(System.IO.Path.Combine(temp.Path, "known-hosts.json"));
        await store.VerifyAsync(FirstKey, static (_, _) => Task.FromResult(true), CancellationToken.None);

        var error = await Assert.ThrowsAsync<HostKeyChangedException>(() => store.VerifyAsync(
            FirstKey with { Algorithm = "rsa-sha2-512" },
            static (_, _) => Task.FromResult(false),
            CancellationToken.None));

        Assert.Equal("ssh-ed25519", error.ExpectedAlgorithm);
        Assert.Equal("rsa-sha2-512", error.ReceivedAlgorithm);
    }

    [Fact]
    public async Task ConcurrentVerificationsPersistBothKeys()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        var store = new KnownHostStore(path);
        var secondKey = FirstKey with { Hostname = "second.example.test" };

        await Task.WhenAll(
            store.VerifyAsync(FirstKey, static (_, _) => Task.FromResult(true), CancellationToken.None),
            store.VerifyAsync(secondKey, static (_, _) => Task.FromResult(true), CancellationToken.None));

        var document = System.Text.Json.JsonSerializer.Deserialize<RemoveDocument>(File.ReadAllText(path));
        Assert.Equal(2, document!.Hosts.Count);
    }

    private sealed class RemoveDocument
    {
        public int Version { get; set; }

        public List<RemovedEntry> Hosts { get; set; } = new();
    }

    private sealed class RemovedEntry
    {
        public string Endpoint { get; set; } = "";
    }

    [Fact]
    public async Task FailedSaveDoesNotLeaveUnpersistedKeyTrustedInMemory()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        var store = new KnownHostStore(path);
        var confirmationCount = 0;

        await Assert.ThrowsAsync<HostKeyStoreException>(() => store.VerifyAsync(
            FirstKey,
            (_, _) =>
            {
                confirmationCount++;
                Directory.CreateDirectory(path);
                return Task.FromResult(true);
            },
            CancellationToken.None));

        Directory.Delete(path);
        await store.VerifyAsync(
            FirstKey,
            (_, _) =>
            {
                confirmationCount++;
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.Equal(2, confirmationCount);
    }

    [Fact]
    public async Task SeparateStoreInstancesReloadKeysWrittenByEachOther()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        var firstStore = new KnownHostStore(path);
        var secondStore = new KnownHostStore(path);
        var secondKey = FirstKey with { Hostname = "second.example.test" };

        await firstStore.VerifyAsync(
            FirstKey,
            static (_, _) => Task.FromResult(true),
            CancellationToken.None);
        await secondStore.VerifyAsync(
            secondKey,
            static (_, _) => Task.FromResult(true),
            CancellationToken.None);

        var confirmationWasCalled = false;
        await firstStore.VerifyAsync(
            secondKey,
            (_, _) =>
            {
                confirmationWasCalled = true;
                return Task.FromResult(false);
            },
            CancellationToken.None);

        Assert.False(confirmationWasCalled);
    }

    [Fact]
    public async Task EquivalentHostnameFormsReuseThePersistedTrustDecision()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        var store = new KnownHostStore(path);
        var confirmationCount = 0;

        await store.VerifyAsync(
            FirstKey with { Hostname = " EXAMPLE.TEST. " },
            Confirm,
            CancellationToken.None);
        await new KnownHostStore(path).VerifyAsync(
            FirstKey with { Hostname = "example.test" },
            Confirm,
            CancellationToken.None);

        Assert.Equal(1, confirmationCount);
        return;

        Task<bool> Confirm(HostKeyPrompt _, CancellationToken __)
        {
            confirmationCount++;
            return Task.FromResult(true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA ")]
    public async Task InvalidFingerprintFailsClosedBeforeConfirmation(string fingerprint)
    {
        using var temp = new TemporaryDirectory();
        var confirmationWasCalled = false;

        await Assert.ThrowsAsync<SshConnectionSecurityException>(() =>
            new KnownHostStore(System.IO.Path.Combine(temp.Path, "known-hosts.json")).VerifyAsync(
                FirstKey with { Sha256Fingerprint = fingerprint },
                (_, _) =>
                {
                    confirmationWasCalled = true;
                    return Task.FromResult(true);
                },
                CancellationToken.None));

        Assert.False(confirmationWasCalled);
    }

    [Fact]
    public async Task CancellationAfterConfirmationDoesNotPersistTrust()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "known-hosts.json");
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new KnownHostStore(path).VerifyAsync(
            FirstKey,
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(true);
            },
            cancellation.Token));

        Assert.False(File.Exists(path));
    }
}
