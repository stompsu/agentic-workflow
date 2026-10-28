using Microsoft.Extensions.Options;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Core.Storage;

namespace AgenticWorkflow.Api.Guests;

/// <summary>
/// Global guest caps (ADR 0006 sections 8, 9.2): creations per hour, guest turns per hour and per day, across all guests
/// and replicas (Cosmos counters). Order: in-memory "closed until" flag first (no Cosmos call while closed), then the
/// counter increment; a count above its cap closes that window on this replica until the window ends. Counters count
/// attempts, so they may exceed a cap by about (replicas x concurrent requests); that errs on the side of lower spend.
/// Returned value = Retry-After (seconds until the window ends, minimum 1 applied by the caller), null = admitted.
/// </summary>
public sealed class GuestGlobalLimits(IGuestStore store, IOptions<GuestOptions> options, TimeProvider time)
{
    private long _createClosedUntil;
    private long _turnHourClosedUntil;
    private long _turnDayClosedUntil;

    /// <summary>Steps 3-4 of the POST /api/guest/token guard order.</summary>
    public async Task<TimeSpan?> TryAdmitCreationAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (ClosedFor(ref _createClosedUntil, now) is { } closed)
        {
            return closed;
        }

        var count = await store.IncrementCounterAsync(GuestCounter.CreationsPerHour, now, ct);
        if (count > options.Value.MaxCreationsPerHour)
        {
            var until = NextHour(now);
            Volatile.Write(ref _createClosedUntil, until.UtcTicks);
            return until - now;
        }

        return null;
    }

    /// <summary>Steps 4-5 of the POST messages guard order (hour and day caps).</summary>
    public async Task<TimeSpan?> TryAdmitTurnAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var hourClosed = ClosedFor(ref _turnHourClosedUntil, now);
        var dayClosed = ClosedFor(ref _turnDayClosedUntil, now);
        if (hourClosed is not null || dayClosed is not null)
        {
            return Later(hourClosed, dayClosed);
        }

        var hourCount = await store.IncrementCounterAsync(GuestCounter.TurnsPerHour, now, ct);
        var dayCount = await store.IncrementCounterAsync(GuestCounter.TurnsPerDay, now, ct);

        TimeSpan? hourRetry = null;
        TimeSpan? dayRetry = null;
        if (hourCount > options.Value.MaxTurnsPerHour)
        {
            var until = NextHour(now);
            Volatile.Write(ref _turnHourClosedUntil, until.UtcTicks);
            hourRetry = until - now;
        }

        if (dayCount > options.Value.MaxTurnsPerDay)
        {
            var until = NextDay(now);
            Volatile.Write(ref _turnDayClosedUntil, until.UtcTicks);
            dayRetry = until - now;
        }

        return hourRetry is null && dayRetry is null ? null : Later(hourRetry, dayRetry);
    }

    private static TimeSpan? Later(TimeSpan? a, TimeSpan? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    private static TimeSpan? ClosedFor(ref long closedUntilTicks, DateTimeOffset now)
    {
        var until = Volatile.Read(ref closedUntilTicks);
        return until > now.UtcTicks ? new TimeSpan(until - now.UtcTicks) : null;
    }

    private static DateTimeOffset NextHour(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        return new DateTimeOffset(new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc)).AddHours(1);
    }

    private static DateTimeOffset NextDay(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        return new DateTimeOffset(new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc)).AddDays(1);
    }
}

/// <summary>Guest workflow filter (ADR 0006 section 9.1 item 3): the registry stays identity-agnostic, the API filters per caller.</summary>
public sealed class GuestWorkflowPolicy(IOptions<GuestOptions> options)
{
    public bool IsAllowed(string workflowId) => options.Value.EffectiveWorkflows.Contains(workflowId, StringComparer.Ordinal);
}
