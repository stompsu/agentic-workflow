using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using AgenticWorkflow.Core.Options;

namespace AgenticWorkflow.Api.Guests;

/// <summary>One key version as listed by the vault (before its key material is fetched).</summary>
public sealed record GuestKeyVersionInfo(string Version, bool Enabled, DateTimeOffset? NotBefore, DateTimeOffset? ExpiresOn, DateTimeOffset CreatedOn);

/// <summary>Public key material of one version. <see cref="CanSign"/>: the key allows the sign operation.</summary>
public sealed record GuestKeyMaterial(string Version, ECDsa PublicKey, bool CanSign);

/// <summary>Source of the key versions (Key Vault in production, a fake in tests).</summary>
public interface IGuestKeySource
{
    Task<IReadOnlyList<GuestKeyVersionInfo>> ListVersionsAsync(CancellationToken ct);

    /// <summary>Null when the version is not usable (not EC P-256).</summary>
    Task<GuestKeyMaterial?> GetKeyAsync(string version, CancellationToken ct);
}

/// <summary>Immutable snapshot swapped atomically (ADR 0006 section 5.4).</summary>
public sealed record GuestKeySnapshot(
    IReadOnlyDictionary<string, SecurityKey> VerificationKeys,
    string? SigningVersion,
    DateTimeOffset? SigningExpiresOn,
    DateTimeOffset LoadedAt)
{
    public static readonly GuestKeySnapshot Empty = new(new Dictionary<string, SecurityKey>(), null, null, DateTimeOffset.MinValue);
}

/// <summary>
/// Singleton cache behind <c>IGuestTokenSigner.ResolveValidationKey</c>. The resolver is synchronous, so it never calls
/// Key Vault: an unknown kid only requests a (throttled, coalesced) background refresh and fails that request.
/// </summary>
public sealed class GuestSigningKeyCache(TimeProvider time)
{
    public static readonly TimeSpan OnDemandRefreshThrottle = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _signal = new(0, 1);
    private GuestKeySnapshot _current = GuestKeySnapshot.Empty;
    private readonly object _retireGate = new();
    private GuestKeySnapshot? _retired;
    private long _lastRequestTicks = long.MinValue;

    public GuestKeySnapshot Current => Volatile.Read(ref _current);

    /// <summary>Number of on-demand refresh requests that passed the throttle (diagnostics and tests).</summary>
    public int AcceptedRefreshRequests => Volatile.Read(ref _acceptedRefreshRequests);

    private int _acceptedRefreshRequests;

    /// <summary>
    /// Swaps the snapshot. A replaced snapshot may still be in use by a validation that resolved a key just before the swap,
    /// so its ECDsa keys are disposed one generation later (a refresh interval later), and never when the same instance is
    /// still part of the current or the retired snapshot.
    /// </summary>
    public void Update(GuestKeySnapshot snapshot)
    {
        List<ECDsa> toDispose = [];
        lock (_retireGate)
        {
            var replaced = Interlocked.Exchange(ref _current, snapshot);
            var live = new HashSet<ECDsa>(ReferenceEqualityComparer.Instance);
            foreach (var key in Keys(snapshot).Concat(Keys(replaced)))
            {
                live.Add(key);
            }

            if (_retired is not null)
            {
                toDispose.AddRange(Keys(_retired).Where(k => !live.Contains(k)));
            }

            _retired = ReferenceEquals(replaced, snapshot) ? null : replaced;
        }

        foreach (var key in toDispose.Distinct())
        {
            key.Dispose();
        }

        static IEnumerable<ECDsa> Keys(GuestKeySnapshot s) =>
            s.VerificationKeys.Values.OfType<ECDsaSecurityKey>().Select(k => k.ECDsa);
    }

    public SecurityKey? Resolve(string kid)
    {
        if (Current.VerificationKeys.TryGetValue(kid, out var key))
        {
            return key;
        }

        RequestRefresh();
        return null;
    }

    /// <summary>Non-blocking; at most one on-demand refetch per 30 s per replica.</summary>
    public void RequestRefresh()
    {
        var now = time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastRequestTicks);
        if (last != long.MinValue && now - last < OnDemandRefreshThrottle.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastRequestTicks, now, last) != last)
        {
            return;
        }

        Interlocked.Increment(ref _acceptedRefreshRequests);
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A refresh is already pending.
        }
    }

    public Task WaitForRefreshRequestAsync(CancellationToken ct) => _signal.WaitAsync(ct);
}

/// <summary>Pure load + selection logic (ADR 0006 section 5.4); testable with a fake source and FakeTimeProvider.</summary>
public static class GuestSigningKeyLoader
{
    public const int MaxVersions = 10;
    public static readonly TimeSpan NotBeforeTolerance = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan NearExpiryWarning = TimeSpan.FromDays(30);

    public static async Task<GuestKeySnapshot> LoadAsync(
        IGuestKeySource source, GuestOptions options, TimeProvider time, ILogger logger, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var versions = await source.ListVersionsAsync(ct);

        // Verification set: enabled, not-before within 5 min, not expired longer than MaxLifetime + skew ago, newest 10.
        var candidates = versions
            .Where(v => v.Enabled
                        && (v.NotBefore is null || v.NotBefore <= now + NotBeforeTolerance)
                        && (v.ExpiresOn is null || v.ExpiresOn > now - options.MaxLifetime - ClockSkew))
            .OrderByDescending(v => v.CreatedOn)
            .Take(MaxVersions)
            .ToList();

        var keys = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
        var signable = new List<(GuestKeyVersionInfo Info, bool CanSign)>();
        foreach (var info in candidates)
        {
            var material = await source.GetKeyAsync(info.Version, ct);
            if (material is null)
            {
                continue;
            }

            keys[info.Version] = new ECDsaSecurityKey(material.PublicKey) { KeyId = info.Version };
            signable.Add((info, material.CanSign));
        }

        // Signing version: newest that is enabled, may sign, started, and outlives MaxLifetime + 10 min; prefer one
        // older than a refresh interval + 5 min (every replica has loaded it), else the newest eligible.
        var eligible = signable
            .Where(x => x.CanSign
                        && (x.Info.NotBefore is null || x.Info.NotBefore <= now)
                        && (x.Info.ExpiresOn is null || x.Info.ExpiresOn > now + options.MaxLifetime + TimeSpan.FromMinutes(10)))
            .Select(x => x.Info)
            .OrderByDescending(v => v.CreatedOn)
            .ToList();
        var matureCutoff = now - (options.SigningKeyRefreshInterval + TimeSpan.FromMinutes(5));
        var signing = eligible.FirstOrDefault(v => v.CreatedOn <= matureCutoff) ?? eligible.FirstOrDefault();

        if (signing is null)
        {
            logger.LogError(
                "GuestSigningKeyUnavailable: no enabled key version may sign guest tokens (versions listed: {Listed}, loaded for verification: {Verifiable})",
                versions.Count, keys.Count);
        }
        else if (signing.ExpiresOn is { } expires && expires < now + NearExpiryWarning)
        {
            logger.LogWarning(
                "GuestSigningKeyNearExpiry: signing key version {Version} expires at {ExpiresOn:O}; rotate the key", signing.Version, expires);
        }

        return new GuestKeySnapshot(keys, signing?.Version, signing?.ExpiresOn, now);
    }
}
