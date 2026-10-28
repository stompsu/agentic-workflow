using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Core.Storage;

namespace AgenticWorkflow.Api.Guests;

/// <summary>
/// Per-replica cache in front of <see cref="IGuestStore.GetAsync"/> (ADR 0006 section 6.2): "guest is active" entries live
/// <c>Guest:ActiveCheckCacheDuration</c> (default 60 s), "guest ended" entries (positive evidence only: a failed write or a completed delete) live until the token's <c>exp</c>, while a plain read miss is cached only as long as a hit, and a
/// "turn quota exhausted" flag (set when a turn start reported GuestQuotaExceeded) lives until <c>exp</c>, so an exhausted
/// guest cannot keep spending global-counter writes. Uses TimeProvider (not IMemoryCache) so tests control time.
/// Never logs or stores anything but the guest id.
/// </summary>
public sealed class GuestActivityCache(IGuestStore store, IOptions<GuestOptions> options, TimeProvider time)
{
    private const int SweepThreshold = 2048;

    private readonly ConcurrentDictionary<string, (bool Active, DateTimeOffset Until)> _activity = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _exhausted = new(StringComparer.Ordinal);

    /// <summary>True while the guest document exists. Cached; <paramref name="tokenExpiresAt"/> bounds negative entries.</summary>
    public async Task<bool> IsActiveAsync(string guestUserId, DateTimeOffset tokenExpiresAt, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (_activity.TryGetValue(guestUserId, out var cached) && cached.Until > now)
        {
            return cached.Active;
        }

        var active = await store.GetAsync(guestUserId, ct) is not null;
        // A plain read miss is only a suspicion (replica lag, a transient miss right after creation): cache it as briefly as a
        // hit. Only positive evidence of deletion (MarkEnded, or Invalidate after a successful delete) lasts until exp.
        Remember(guestUserId, active, now + options.Value.ActiveCheckCacheDuration, now);
        return active;
    }

    /// <summary>Records positive evidence that the guest ended (a write failed because the guest document is missing): cached until exp.</summary>
    public void MarkEnded(string guestUserId, DateTimeOffset tokenExpiresAt)
    {
        var now = time.GetUtcNow();
        Remember(guestUserId, false, MaxTime(tokenExpiresAt, now + TimeSpan.FromMinutes(1)), now);
    }

    /// <summary>
    /// A successful DELETE /api/me/data removed the guest document: positive evidence of deletion, cached until exp.
    /// </summary>
    public void Invalidate(string guestUserId, DateTimeOffset tokenExpiresAt)
    {
        _exhausted.TryRemove(guestUserId, out _);
        MarkEnded(guestUserId, tokenExpiresAt);
    }

    public bool IsTurnQuotaExhausted(string guestUserId) =>
        _exhausted.TryGetValue(guestUserId, out var until) && until > time.GetUtcNow();

    public void MarkTurnQuotaExhausted(string guestUserId, DateTimeOffset tokenExpiresAt)
    {
        var now = time.GetUtcNow();
        _exhausted[guestUserId] = MaxTime(tokenExpiresAt, now + TimeSpan.FromMinutes(1));
        Sweep(now);
    }

    private void Remember(string guestUserId, bool active, DateTimeOffset until, DateTimeOffset now)
    {
        _activity[guestUserId] = (active, until);
        Sweep(now);
    }

    private static DateTimeOffset MaxTime(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private void Sweep(DateTimeOffset now)
    {
        if (_activity.Count + _exhausted.Count < SweepThreshold)
        {
            return;
        }

        foreach (var (key, value) in _activity)
        {
            if (value.Until <= now)
            {
                _activity.TryRemove(key, out _);
            }
        }

        foreach (var (key, until) in _exhausted)
        {
            if (until <= now)
            {
                _exhausted.TryRemove(key, out _);
            }
        }
    }
}
