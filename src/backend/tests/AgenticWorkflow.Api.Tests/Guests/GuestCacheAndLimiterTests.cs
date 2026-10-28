using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using AgenticWorkflow.Api.Guests;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Core.Storage;

namespace AgenticWorkflow.Api.Tests.Guests;

public sealed class GuestActivityCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeGuests : IGuestStore
    {
        public bool Exists { get; set; }

        public int Reads { get; private set; }

        public Task CreateAsync(GuestRecord guest, CancellationToken ct) => Task.CompletedTask;

        public Task<GuestRecord?> GetAsync(string guestUserId, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<GuestRecord?>(Exists ? new GuestRecord(guestUserId, Start, Start.AddHours(4), 0, 30, 0, 5) : null);
        }

        public Task<long> IncrementCounterAsync(GuestCounter counter, DateTimeOffset now, CancellationToken ct) => Task.FromResult(1L);
    }

    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeGuests _store = new();

    private GuestActivityCache Cache() => new(_store, Options.Create(new GuestOptions()), _time);

    [Fact]
    public async Task A_plain_read_miss_is_cached_only_for_the_active_check_duration()
    {
        var cache = Cache();
        var exp = Start.AddHours(4);

        Assert.False(await cache.IsActiveAsync("g", exp, default)); // transient miss
        _store.Exists = true;
        Assert.False(await cache.IsActiveAsync("g", exp, default)); // still cached
        Assert.Equal(1, _store.Reads);

        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(await cache.IsActiveAsync("g", exp, default)); // re-read: the guest is back
        Assert.Equal(2, _store.Reads);
    }

    [Fact]
    public async Task Positive_evidence_of_deletion_is_cached_until_the_token_expires()
    {
        var cache = Cache();
        var exp = Start.AddHours(4);
        _store.Exists = true;
        Assert.True(await cache.IsActiveAsync("g", exp, default));

        cache.MarkEnded("g", exp); // a write batch reported the guest document missing
        _time.Advance(TimeSpan.FromHours(3));
        Assert.False(await cache.IsActiveAsync("g", exp, default));
        Assert.Equal(1, _store.Reads); // never re-read before exp

        _time.Advance(TimeSpan.FromHours(2));
        Assert.True(await cache.IsActiveAsync("g", exp, default)); // after exp the entry is gone
    }

    [Fact]
    public async Task A_successful_delete_of_my_data_is_cached_until_the_token_expires()
    {
        var cache = Cache();
        var exp = Start.AddHours(4);
        _store.Exists = true;
        Assert.True(await cache.IsActiveAsync("g", exp, default));

        cache.Invalidate("g", exp);
        _store.Exists = false;
        _time.Advance(TimeSpan.FromHours(3));
        Assert.False(await cache.IsActiveAsync("g", exp, default));
        Assert.Equal(1, _store.Reads);
    }
}

public sealed class GuestClientLimiterTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly ListLogger2 _log = new();

    private GuestClientLimiter Limiter(int perHour = 2) =>
        new(Options.Create(new GuestOptions { CreationsPerClientPerHour = perHour }), _time, _log);

    private sealed class ListLogger2 : Microsoft.Extensions.Logging.ILogger<GuestClientLimiter>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public void Limits_per_client_and_resets_with_the_window()
    {
        var limiter = Limiter();
        Assert.Null(limiter.TryAcquire("a"));
        Assert.Null(limiter.TryAcquire("a"));
        Assert.NotNull(limiter.TryAcquire("a"));
        Assert.Null(limiter.TryAcquire("b"));
        Assert.Null(limiter.TryAcquire(null));
        _time.Advance(GuestClientLimiter.Window);
        Assert.Null(limiter.TryAcquire("a"));
    }

    [Fact]
    public void The_dictionary_is_capped_and_new_clients_fail_open_with_one_warning_per_interval()
    {
        var limiter = Limiter(perHour: 1);
        for (var i = 0; i < GuestClientLimiter.MaxBuckets; i++)
        {
            Assert.Null(limiter.TryAcquire("c" + i));
        }

        Assert.Equal(GuestClientLimiter.MaxBuckets, limiter.BucketCount);

        // Full: a new client is admitted without a bucket, repeatedly; the dictionary does not grow.
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(limiter.TryAcquire("new"));
        }

        Assert.Equal(GuestClientLimiter.MaxBuckets, limiter.BucketCount);
        Assert.Single(_log.Warnings);

        // A known client is still limited.
        Assert.NotNull(limiter.TryAcquire("c0"));

        _time.Advance(GuestClientLimiter.SweepInterval + TimeSpan.FromSeconds(1));
        Assert.Null(limiter.TryAcquire("new2"));
        Assert.Equal(2, _log.Warnings.Count);
    }

    [Fact]
    public void Expired_buckets_are_swept_at_most_once_per_interval()
    {
        var limiter = Limiter();
        for (var i = 0; i < 100; i++)
        {
            limiter.TryAcquire("c" + i);
        }

        _time.Advance(GuestClientLimiter.Window + TimeSpan.FromSeconds(1)); // all expired; first new key sweeps
        limiter.TryAcquire("fresh");
        Assert.Equal(1, limiter.BucketCount);

        // Within the sweep interval no sweep runs even though a bucket expired again.
        _time.Advance(GuestClientLimiter.Window + TimeSpan.FromSeconds(1));
        limiter.TryAcquire("x"); // sweep is due (> interval since): removes "fresh"
        Assert.Equal(1, limiter.BucketCount);
        limiter.TryAcquire("y");
        limiter.TryAcquire("z");
        Assert.Equal(3, limiter.BucketCount);
    }

    [Fact]
    public void Full_and_expired_buckets_are_reclaimed_by_the_sweep_so_the_limiter_recovers()
    {
        var limiter = Limiter(perHour: 1);
        for (var i = 0; i < GuestClientLimiter.MaxBuckets; i++)
        {
            limiter.TryAcquire("c" + i);
        }

        _time.Advance(GuestClientLimiter.Window + TimeSpan.FromSeconds(1));
        Assert.Null(limiter.TryAcquire("n1"));
        Assert.NotNull(limiter.TryAcquire("n1")); // it got a real bucket after the sweep: limited at perHour 1
        Assert.True(limiter.BucketCount < GuestClientLimiter.MaxBuckets);
    }
}
