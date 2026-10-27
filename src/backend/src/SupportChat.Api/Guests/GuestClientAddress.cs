using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using SupportChat.Core.Options;

namespace SupportChat.Api.Guests;

/// <summary>Meter of the guest controls. Never carries a value that identifies a client.</summary>
public static class GuestMetrics
{
    public const string MeterName = "SupportChat.Guest";
    public const string ClientIpUnparsed = "guest.client_ip_unparsed";

    public const string ClientLimiterFull = "guest.client_limiter_full";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> ClientIpUnparsedCounter = Meter.CreateCounter<long>(ClientIpUnparsed);

    /// <summary>A new client was admitted without a per-client bucket because the limiter was full.</summary>
    public static readonly Counter<long> ClientLimiterFullCounter = Meter.CreateCounter<long>(ClientLimiterFull);
}

/// <summary>
/// Client address for the per-client creation limiter (ADR 0006 section 9.4). Source: header <c>X-Client-Ip</c> set by
/// nginx (rightmost X-Forwarded-For entry, overwriting any client value) ONLY when <c>Guest:TrustClientIpHeader</c>;
/// otherwise the connection address. <c>X-Forwarded-For</c> is never read. The result is a limiter partition key held in
/// memory for at most one window; it is never logged or persisted.
/// </summary>
public static class GuestClientAddress
{
    public const string HeaderName = "X-Client-Ip";

    /// <summary>Key for the limiter (IPv4: the address; IPv6: its /64 prefix), or null when missing/unparsable.</summary>
    public static string? KeyFor(HttpContext http, GuestOptions options)
    {
        string? key;
        if (options.TrustClientIpHeader)
        {
            key = Parse(http.Request.Headers[HeaderName].Count == 1 ? http.Request.Headers[HeaderName].ToString() : null);
        }
        else
        {
            key = http.Connection.RemoteIpAddress is { } address ? KeyOf(address) : null;
        }

        if (key is null && options.TrustClientIpHeader)
        {
            GuestMetrics.ClientIpUnparsedCounter.Add(1);
        }

        return key;
    }

    /// <summary>
    /// trim; strip a port from "a.b.c.d:port"; strip brackets and port from "[v6]:port"; a bare IPv6 address is taken whole;
    /// IPAddress.TryParse; IPv4-mapped IPv6 becomes IPv4; IPv6 keys use the /64 prefix.
    /// </summary>
    public static string? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (value.Length > 64 || value.Contains(','))
        {
            return null;
        }

        string host;
        if (value[0] == '[')
        {
            var close = value.IndexOf(']');
            if (close < 0)
            {
                return null;
            }

            host = value[1..close];
            var rest = value[(close + 1)..];
            if (rest.Length > 0 && !IsPort(rest))
            {
                return null;
            }
        }
        else if (value.Count(c => c == ':') == 1 && value.Contains('.'))
        {
            var colon = value.IndexOf(':');
            if (!IsPort(value[colon..]))
            {
                return null;
            }

            host = value[..colon];
        }
        else
        {
            host = value;
        }

        // No legacy shorthand ("1.2.3", "16909060", hex/octal parts): an IPv4 form must be a dotted quad of decimals.
        if (!host.Contains(':') && !System.Text.RegularExpressions.Regex.IsMatch(host, @"^[0-9]{1,3}(\.[0-9]{1,3}){3}$"))
        {
            return null;
        }

        return IPAddress.TryParse(host, out var address) ? KeyOf(address) : null;
    }

    private static bool IsPort(string withColon) =>
        withColon.Length is > 1 and <= 6 && withColon[0] == ':' && withColon[1..].All(char.IsAsciiDigit)
        && int.Parse(withColon[1..], System.Globalization.CultureInfo.InvariantCulture) <= 65535;

    private static string KeyOf(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return "v4:" + address;
        }

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        return "v6:" + Convert.ToHexStringLower(bytes[..8]);
    }
}

/// <summary>
/// Per-client fixed-window limiter for guest creation (Guest:CreationsPerClientPerHour per replica, 1 h window), in memory.
/// Own implementation on TimeProvider (the BCL limiters use Stopwatch) so windows are testable. No key -> no bucket.
/// Bounded: expired buckets are swept at most once per <see cref="SweepInterval"/>, and at most <see cref="MaxBuckets"/>
/// live buckets exist; when full a client without a bucket fails OPEN for this limiter (the global caps still apply), with a
/// metric and at most one warning per interval.
/// </summary>
public sealed class GuestClientLimiter(IOptions<GuestOptions> options, TimeProvider time, ILogger<GuestClientLimiter> logger)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);
    public const int MaxBuckets = 10_000;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private long _nextSweepTicks;
    private long _nextWarnTicks;

    public int BucketCount => _buckets.Count;

    /// <summary>Null = admitted; otherwise the time until the window ends (Retry-After).</summary>
    public TimeSpan? TryAcquire(string? key)
    {
        if (key is null)
        {
            return null;
        }

        var now = time.GetUtcNow();
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            SweepIfDue(now);
            if (_buckets.Count >= MaxBuckets)
            {
                GuestMetrics.ClientLimiterFullCounter.Add(1);
                WarnFullOncePerInterval(now);
                return null;
            }

            bucket = _buckets.GetOrAdd(key, _ => new Bucket());
        }

        lock (bucket)
        {
            if (now >= bucket.WindowStart + Window)
            {
                bucket.WindowStart = now;
                bucket.Count = 0;
            }

            if (bucket.Count >= Math.Max(1, options.Value.CreationsPerClientPerHour))
            {
                return bucket.WindowStart + Window - now;
            }

            bucket.Count++;
        }

        return null;
    }

    private void SweepIfDue(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < due || Interlocked.CompareExchange(ref _nextSweepTicks, (now + SweepInterval).UtcTicks, due) != due)
        {
            return;
        }

        foreach (var (k, b) in _buckets)
        {
            bool expired;
            lock (b)
            {
                expired = now >= b.WindowStart + Window;
            }

            if (expired)
            {
                _buckets.TryRemove(k, out _);
            }
        }
    }

    private void WarnFullOncePerInterval(DateTimeOffset now)
    {
        var due = Interlocked.Read(ref _nextWarnTicks);
        if (now.UtcTicks >= due && Interlocked.CompareExchange(ref _nextWarnTicks, (now + SweepInterval).UtcTicks, due) == due)
        {
            logger.LogWarning(
                "Guest per-client limiter is full ({Max} live buckets); failing open for new clients, global caps still apply", MaxBuckets);
        }
    }

    private sealed class Bucket
    {
        public DateTimeOffset WindowStart { get; set; } = DateTimeOffset.MinValue;

        public int Count { get; set; }
    }
}
