using System.Security.Cryptography;
using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SupportChat.Api.Guests;
using SupportChat.Core.Options;

namespace SupportChat.Api.Tests.Guests;

internal sealed class ListLogger : ILogger<GuestSigningKeyRefresher>, ILogger<GuestSigningSmokeProbe>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public List<(LogLevel Level, string? EventName, string Message)> Events { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Record(logLevel, eventId, formatter(state, exception));

    private void Record(LogLevel level, EventId eventId, string message)
    {
        Entries.Add((level, message));
        Events.Add((level, eventId.Name, message));
    }

    public bool Has(LogLevel level, string text) => Entries.Any(e => e.Level == level && e.Message.Contains(text, StringComparison.Ordinal));
}

internal sealed class ScriptedKeySource : IGuestKeySource, IDisposable
{
    private readonly Dictionary<string, (GuestKeyVersionInfo Info, ECDsa Key, bool CanSign)> _versions = [];

    public Exception? Failure { get; set; }

    public int ListCalls { get; private set; }

    public void Add(string version, DateTimeOffset created, bool enabled = true, DateTimeOffset? notBefore = null, DateTimeOffset? expires = null, bool canSign = true) =>
        _versions[version] = (new GuestKeyVersionInfo(version, enabled, notBefore, expires, created), ECDsa.Create(ECCurve.NamedCurves.nistP256), canSign);

    public Task<IReadOnlyList<GuestKeyVersionInfo>> ListVersionsAsync(CancellationToken ct)
    {
        ListCalls++;
        return Failure is null
            ? Task.FromResult<IReadOnlyList<GuestKeyVersionInfo>>(_versions.Values.Select(v => v.Info).ToList())
            : Task.FromException<IReadOnlyList<GuestKeyVersionInfo>>(Failure);
    }

    public Task<GuestKeyMaterial?> GetKeyAsync(string version, CancellationToken ct) =>
        Task.FromResult<GuestKeyMaterial?>(new GuestKeyMaterial(version, _versions[version].Key, _versions[version].CanSign));

    public void Dispose()
    {
        foreach (var (_, key, _) in _versions.Values)
        {
            key.Dispose();
        }
    }
}

/// <summary>Key cache, selection and refresher behavior (ADR 0006 section 5.4) with FakeTimeProvider and a scripted source.</summary>
public sealed class GuestKeyTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly ScriptedKeySource _source = new();
    private readonly ListLogger _log = new();
    private readonly GuestOptions _options = new() { Enabled = true, Issuer = "x-guest", SigningKeyUri = "https://v.vault.azure.net/keys/k" };

    public void Dispose() => _source.Dispose();

    private Task<GuestKeySnapshot> LoadAsync() => GuestSigningKeyLoader.LoadAsync(_source, _options, _time, _log, default);

    // ---- selection ----

    [Fact]
    public async Task The_newest_version_old_enough_to_be_on_every_replica_signs_and_all_versions_verify()
    {
        _source.Add("old", Start.AddDays(-100));
        _source.Add("mature", Start.AddDays(-10));
        _source.Add("young", Start.AddMinutes(-10)); // younger than refresh interval + 5 min (65 min)

        var snapshot = await LoadAsync();

        Assert.Equal("mature", snapshot.SigningVersion);
        Assert.Equal(["mature", "old", "young"], snapshot.VerificationKeys.Keys.Order());
        Assert.All(snapshot.VerificationKeys, kv => Assert.Equal(kv.Key, kv.Value.KeyId));
    }

    [Fact]
    public async Task When_only_young_versions_exist_the_newest_eligible_one_signs()
    {
        _source.Add("first", Start.AddMinutes(-10));
        _source.Add("second", Start.AddMinutes(-2));
        Assert.Equal("second", (await LoadAsync()).SigningVersion);
    }

    [Fact]
    public async Task Disabled_unsignable_not_yet_valid_and_soon_expiring_versions_never_sign()
    {
        _source.Add("disabled", Start.AddDays(-5), enabled: false);
        _source.Add("verify-only", Start.AddDays(-4), canSign: false);
        _source.Add("future", Start.AddDays(-3), notBefore: Start.AddHours(1));
        _source.Add("expires-soon", Start.AddDays(-2), expires: Start.AddHours(4) + TimeSpan.FromMinutes(5)); // < now + 4 h + 10 min
        _source.Add("good", Start.AddDays(-1), expires: Start.AddDays(300));

        var snapshot = await LoadAsync();

        Assert.Equal("good", snapshot.SigningVersion);
        Assert.DoesNotContain("disabled", snapshot.VerificationKeys.Keys);
    }

    [Fact]
    public async Task Versions_that_expired_longer_ago_than_the_token_lifetime_are_not_loaded_but_recent_ones_keep_verifying()
    {
        _source.Add("ancient", Start.AddDays(-400), expires: Start.AddHours(-5)); // all its tokens are dead
        _source.Add("just-expired", Start.AddDays(-300), expires: Start.AddHours(-2)); // tokens signed before may live 2 more hours
        _source.Add("current", Start.AddDays(-1));

        var snapshot = await LoadAsync();

        Assert.Equal(["current", "just-expired"], snapshot.VerificationKeys.Keys.Order());
        Assert.Equal("current", snapshot.SigningVersion);
    }

    [Fact]
    public async Task At_most_the_newest_ten_versions_are_loaded()
    {
        for (var i = 0; i < 14; i++)
        {
            _source.Add($"v{i:00}", Start.AddDays(-30 + i));
        }

        var snapshot = await LoadAsync();

        Assert.Equal(GuestSigningKeyLoader.MaxVersions, snapshot.VerificationKeys.Count);
        Assert.Contains("v13", snapshot.VerificationKeys.Keys);
        Assert.DoesNotContain("v00", snapshot.VerificationKeys.Keys);
    }

    [Fact]
    public async Task No_eligible_version_logs_GuestSigningKeyUnavailable_and_the_signer_throws()
    {
        _source.Add("expired", Start.AddDays(-9), expires: Start.AddMinutes(30));
        var cache = new GuestSigningKeyCache(_time);

        var snapshot = await LoadAsync();
        cache.Update(snapshot);

        Assert.Null(snapshot.SigningVersion);
        Assert.True(_log.Has(LogLevel.Error, "GuestSigningKeyUnavailable"));
        var signer = new KeyVaultGuestTokenSigner(cache, Options.Create(_options), new NoCredentialProvider());
        Assert.Throws<GuestSigningUnavailableException>(() => signer.GetSigningKid());
    }

    [Fact]
    public async Task A_signing_version_expiring_within_30_days_logs_GuestSigningKeyNearExpiry_at_every_load()
    {
        _source.Add("rotating", Start.AddDays(-300), expires: Start.AddDays(20));
        await LoadAsync();
        await LoadAsync();

        Assert.Equal(2, _log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("GuestSigningKeyNearExpiry")));
        Assert.Contains(_log.Entries, e => e.Message.Contains("rotating")); // key version only, no secret
    }

    [Fact]
    public async Task A_healthy_key_logs_no_events()
    {
        _source.Add("healthy", Start.AddDays(-20));
        await LoadAsync();
        Assert.Empty(_log.Entries);
    }

    // ---- cache ----

    [Fact]
    public void An_unknown_kid_requests_at_most_one_refresh_per_30_seconds_and_never_returns_a_key()
    {
        var cache = new GuestSigningKeyCache(_time);

        for (var i = 0; i < 50; i++)
        {
            Assert.Null(cache.Resolve($"random-{i}"));
        }

        Assert.Equal(1, cache.AcceptedRefreshRequests);

        _time.Advance(TimeSpan.FromSeconds(29));
        cache.Resolve("another");
        Assert.Equal(1, cache.AcceptedRefreshRequests);

        _time.Advance(TimeSpan.FromSeconds(2));
        cache.Resolve("another");
        Assert.Equal(2, cache.AcceptedRefreshRequests);
    }

    [Fact]
    public async Task A_known_kid_resolves_from_the_cache_without_a_refresh_request()
    {
        _source.Add("k1", Start.AddDays(-20));
        var cache = new GuestSigningKeyCache(_time);
        cache.Update(await LoadAsync());

        Assert.NotNull(cache.Resolve("k1"));
        Assert.Equal(0, cache.AcceptedRefreshRequests);
        Assert.Equal(1, _source.ListCalls); // resolving never calls the source
    }

    // ---- refresher ----

    private GuestSigningKeyRefresher Refresher(GuestSigningKeyCache cache) =>
        new(_source, cache, Options.Create(_options), _time, _log);

    [Fact]
    public async Task A_failed_refresh_keeps_the_last_good_snapshot_and_logs_only_the_exception_type()
    {
        _source.Add("k1", Start.AddDays(-20));
        var cache = new GuestSigningKeyCache(_time);
        var refresher = Refresher(cache);
        Assert.True(await refresher.TryRefreshAsync(default, "refresh"));
        var good = cache.Current;

        _source.Failure = new RequestFailedException(503, "SECRET-ENDPOINT https://vault.example/keys");
        Assert.False(await refresher.TryRefreshAsync(default, "refresh"));

        Assert.Same(good, cache.Current);
        Assert.True(_log.Has(LogLevel.Warning, "keeping the last good keys"));
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("SECRET-ENDPOINT"));
        Assert.Contains(_log.Entries, e => e.Message.Contains("status 503"));
    }

    [Fact]
    public async Task Prefill_loads_the_snapshot_before_start_returns_and_retries_a_transient_failure()
    {
        _source.Add("k1", Start.AddDays(-20));
        _source.Failure = new RequestFailedException(500, "boom");
        var cache = new GuestSigningKeyCache(_time);
        var refresher = Refresher(cache);

        var start = refresher.StartAsync(default);
        await WaitForListCallsAsync(1);
        _source.Failure = null;
        await AdvanceUntilCompletedAsync(start); // the 2 s pause between attempts
        await start;
        await refresher.StopAsync(default);

        Assert.Equal("k1", cache.Current.SigningVersion);
    }

    [Fact]
    public async Task Prefill_that_keeps_failing_logs_an_error_and_lets_the_host_start()
    {
        _source.Failure = new RequestFailedException(403, "forbidden");
        var cache = new GuestSigningKeyCache(_time);
        var refresher = Refresher(cache);

        var start = refresher.StartAsync(default);
        await AdvanceUntilCompletedAsync(start);
        await start; // does not throw
        await refresher.StopAsync(default);

        Assert.Null(cache.Current.SigningVersion);
        Assert.True(_log.Has(LogLevel.Error, "could not be loaded at startup"));
        var alert = Assert.Single(_log.Events, e => e.EventName == "GuestSigningKeyRefreshFailing");
        Assert.Equal(LogLevel.Error, alert.Level);
        Assert.StartsWith("GuestSigningKeyRefreshFailing:", alert.Message, StringComparison.Ordinal);
    }

    private IEnumerable<(LogLevel Level, string? EventName, string Message)> Alerts =>
        _log.Events.Where(e => e.EventName == "GuestSigningKeyRefreshFailing");

    [Fact]
    public async Task Three_consecutive_failed_refreshes_log_one_alert_then_one_every_twelve_failures()
    {
        _source.Add("k1", Start.AddDays(-20));
        var refresher = Refresher(new GuestSigningKeyCache(_time));
        Assert.True(await refresher.TryRefreshAsync(default, "refresh"));
        _source.Failure = new RequestFailedException(403, "SECRET-ENDPOINT https://vault.example/keys");

        for (var i = 1; i <= 2; i++)
        {
            await refresher.TryRefreshAsync(default, "refresh");
        }

        Assert.Empty(Alerts); // 2 failures: warnings only
        await refresher.TryRefreshAsync(default, "refresh");
        var alert = Assert.Single(Alerts);
        Assert.Equal(LogLevel.Error, alert.Level);
        Assert.StartsWith("GuestSigningKeyRefreshFailing:", alert.Message, StringComparison.Ordinal);
        Assert.Contains("status 403", alert.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-ENDPOINT", alert.Message, StringComparison.Ordinal);

        for (var i = 4; i <= 14; i++)
        {
            await refresher.TryRefreshAsync(default, "refresh");
        }

        Assert.Single(Alerts); // not every time
        await refresher.TryRefreshAsync(default, "refresh"); // 15th = 3 + 12
        Assert.Equal(2, Alerts.Count());
    }

    [Fact]
    public async Task A_successful_refresh_resets_the_failure_counter()
    {
        _source.Add("k1", Start.AddDays(-20));
        var refresher = Refresher(new GuestSigningKeyCache(_time));
        _source.Failure = new RequestFailedException(500, "x");
        await refresher.TryRefreshAsync(default, "refresh");
        await refresher.TryRefreshAsync(default, "refresh");
        _source.Failure = null;
        Assert.True(await refresher.TryRefreshAsync(default, "refresh"));

        _source.Failure = new RequestFailedException(500, "x");
        await refresher.TryRefreshAsync(default, "refresh");
        await refresher.TryRefreshAsync(default, "refresh");
        Assert.Empty(Alerts); // 2 + 2 failures, never 3 in a row

        await refresher.TryRefreshAsync(default, "refresh");
        Assert.Single(Alerts);
    }

    [Fact]
    public async Task The_background_loop_alerts_after_three_failed_refreshes_with_backoff()
    {
        _source.Add("k1", Start.AddDays(-20));
        var refresher = Refresher(new GuestSigningKeyCache(_time));
        await refresher.StartAsync(default);
        _source.Failure = new RequestFailedException(403, "forbidden");

        var deadline = Environment.TickCount64 + 10_000;
        while (!Alerts.Any() && Environment.TickCount64 < deadline)
        {
            _time.Advance(TimeSpan.FromMinutes(10));
            await Task.Delay(10);
        }

        await refresher.StopAsync(default);
        Assert.Single(Alerts);
    }

    [Fact]
    public void Replaced_snapshots_dispose_their_keys_one_generation_later_but_never_shared_keys()
    {
        using var shared = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        GuestKeySnapshot Snap(params (string Kid, ECDsa Key)[] keys) => new(
            keys.ToDictionary(k => k.Kid, k => (Microsoft.IdentityModel.Tokens.SecurityKey)new Microsoft.IdentityModel.Tokens.ECDsaSecurityKey(k.Key) { KeyId = k.Kid }),
            null, null, Start);

        var cache = new GuestSigningKeyCache(_time);
        cache.Update(Snap(("s", shared), ("a", first)));
        cache.Update(Snap(("s", shared), ("b", second))); // replaced snapshot (with "a") is retired, not disposed yet
        Assert.NotNull(first.ExportParameters(false).Q.X); // still usable
        cache.Update(Snap(("s", shared)));                 // retired one is disposed now
        Assert.Throws<ObjectDisposedException>(() => first.ExportParameters(false));
        _ = shared.ExportParameters(false);    // shared, still live
        second.Dispose();
    }

    [Fact]
    public async Task The_refresher_is_inert_unless_guests_are_enabled_in_KeyVault_mode()
    {
        foreach (var inert in new[] { new GuestOptions { Enabled = false }, new GuestOptions { Enabled = true, SigningMode = GuestSigningMode.Ephemeral } })
        {
            var refresher = new GuestSigningKeyRefresher(_source, new GuestSigningKeyCache(_time), Options.Create(inert), _time, _log);
            await refresher.StartAsync(default);
            await refresher.StopAsync(default);
        }

        Assert.Equal(0, _source.ListCalls);
    }

    [Fact]
    public async Task An_on_demand_request_triggers_a_background_refresh()
    {
        _source.Add("k1", Start.AddDays(-20));
        var cache = new GuestSigningKeyCache(_time);
        var refresher = Refresher(cache);
        await refresher.StartAsync(default); // prefill: 1 list call
        Assert.Equal(1, _source.ListCalls);

        _source.Add("k2", Start.AddDays(-19));
        Assert.Null(cache.Resolve("k2")); // unknown kid: request, fail this validation
        await WaitForListCallsAsync(2);
        await refresher.StopAsync(default);

        Assert.NotNull(cache.Resolve("k2"));
    }

    private async Task AdvanceUntilCompletedAsync(Task task)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!task.IsCompleted)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("The task did not complete while fake time advanced.");
            }

            _time.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(10);
        }
    }

    private async Task WaitForListCallsAsync(int calls)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (_source.ListCalls < calls)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {calls} list call(s).");
            }

            await Task.Delay(5);
        }
    }

    // ---- smoke probe ----

    private sealed class NoCredentialProvider : SupportChat.Infrastructure.Azure.IAzureCredentialProvider
    {
        public Azure.Core.TokenCredential Credential => throw new InvalidOperationException("No credential expected.");
    }

    private GuestSigningSmokeProbe Probe(GuestOptions options) =>
        new(_source, Options.Create(options), new NoCredentialProvider(), _time, _log);

    [Fact]
    public async Task Smoke_probe_is_a_no_op_when_guests_are_disabled_or_Ephemeral()
    {
        await Probe(new GuestOptions { Enabled = false }).RunAsync(default);
        await Probe(new GuestOptions { Enabled = true, SigningMode = GuestSigningMode.Ephemeral }).RunAsync(default);
        Assert.Equal(0, _source.ListCalls);
    }

    [Fact]
    public async Task Smoke_probe_fails_when_no_version_is_eligible_to_sign()
    {
        _source.Add("expired", Start.AddDays(-9), expires: Start.AddMinutes(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Probe(_options).RunAsync(default));
        Assert.True(_log.Has(LogLevel.Error, "GuestSigningKeyUnavailable"));
    }

    [Fact]
    public async Task Smoke_probe_turns_a_Key_Vault_403_into_a_message_that_names_the_missing_role()
    {
        _source.Failure = new RequestFailedException(403, "Forbidden");
        await Assert.ThrowsAsync<RequestFailedException>(() => Probe(_options).RunAsync(default));
        Assert.True(_log.Has(LogLevel.Error, "Key Vault Crypto User"));
    }

    [Fact]
    public async Task Smoke_probe_warns_about_a_signing_key_expiring_within_30_days()
    {
        _source.Add("rotating", Start.AddDays(-300), expires: Start.AddDays(10));
        // The Key Vault sign step needs a credential (not available here): the load/selection part has run by then.
        await Assert.ThrowsAnyAsync<Exception>(() => Probe(_options).RunAsync(default));
        Assert.True(_log.Has(LogLevel.Warning, "GuestSigningKeyNearExpiry"));
    }
}
