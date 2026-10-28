using System.Diagnostics.Metrics;
using System.Net;
using AgenticWorkflow.Api.Guests;
using AgenticWorkflow.Api.Tests.Chat;

namespace AgenticWorkflow.Api.Tests.Guests;

/// <summary>Per-client limiter, global caps and check order (ADR 0006 sections 8, 9.2, 9.4) with a fake clock.</summary>
public sealed class GuestLimitsTests
{
    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.ReadJsonAsync())["code"]!.GetValue<string>());
    }

    private static int RetryAfter(HttpResponseMessage response) => int.Parse(Assert.Single(response.Headers.GetValues("Retry-After")));

    private static async Task<HttpResponseMessage> PostTurnAsync(HttpClient client, string sessionId)
    {
        var response = await client.PostAsync($"/api/sessions/{sessionId}/messages", Helpers.Json("""{"content":"hi"}"""));
        await response.Content.ReadAsStringAsync();
        return response;
    }

    private static void Wide(Dictionary<string, string?> c, params (string Key, string Value)[] overrides)
    {
        c["Guest:TurnsPerMinute"] = "1000";
        c["Guest:MaxTurns"] = "1000";
        c["Guest:MaxSessions"] = "100";
        foreach (var (key, value) in overrides)
        {
            c[key] = value;
        }
    }

    // ---- global turn caps ----

    [Fact]
    public async Task Hourly_turn_cap_closes_with_Retry_After_to_the_next_hour_writes_nothing_while_closed_and_reopens()
    {
        using var host = new GuestHost(c => Wide(c, ("Guest:MaxTurnsPerHour", "2"), ("Guest:MaxTurnsPerDay", "10")));
        var store = host.Factory.Store;
        var (_, guestId, guest) = await host.NewGuestAsync();
        var session = await host.Host.NewSessionAsync(guest);

        Assert.Equal(HttpStatusCode.OK, (await PostTurnAsync(guest, session)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostTurnAsync(guest, session)).StatusCode);
        host.Factory.Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(30)); // 10:59:30

        var capped = await PostTurnAsync(guest, session);
        await AssertProblemAsync(capped, (HttpStatusCode)429, "rate_limited");
        Assert.Equal(30, RetryAfter(capped));
        Assert.Equal(2, store.GuestDocument(guestId)!.TurnCount); // the rejected turn consumed no guest quota

        // Closed: the in-memory flag answers, with no Cosmos (counter) write and no guest quota.
        var calls = store.CounterIncrementCalls;
        var again = await PostTurnAsync(guest, session);
        await AssertProblemAsync(again, (HttpStatusCode)429, "rate_limited");
        Assert.Equal(calls, store.CounterIncrementCalls);
        Assert.Equal(2, store.GuestDocument(guestId)!.TurnCount);

        host.Factory.Time.Advance(TimeSpan.FromSeconds(31)); // 11:00:01
        Assert.Equal(HttpStatusCode.OK, (await PostTurnAsync(guest, session)).StatusCode);
    }

    [Fact]
    public async Task Daily_turn_cap_has_Retry_After_to_UTC_midnight_and_stays_closed_across_hours()
    {
        using var host = new GuestHost(c => Wide(c, ("Guest:MaxTurnsPerHour", "100"), ("Guest:MaxTurnsPerDay", "3")));
        var store = host.Factory.Store;
        var (_, _, guest) = await host.NewGuestAsync();
        var session = await host.Host.NewSessionAsync(guest);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostTurnAsync(guest, session)).StatusCode);
        }

        var capped = await PostTurnAsync(guest, session); // fake clock: 10:00:00 UTC
        await AssertProblemAsync(capped, (HttpStatusCode)429, "rate_limited");
        Assert.Equal(14 * 3600, RetryAfter(capped));

        host.Factory.Time.Advance(TimeSpan.FromHours(2));
        var calls = store.CounterIncrementCalls;
        var later = await PostTurnAsync(guest, session);
        Assert.Equal(12 * 3600, RetryAfter(later));
        Assert.Equal(calls, store.CounterIncrementCalls);

        // Past midnight the (4 h) guest is gone, but a new guest gets turns again: the daily window rolled over.
        host.Factory.Time.Advance(TimeSpan.FromHours(12));
        var (_, _, fresh) = await host.NewGuestAsync();
        var freshSession = await host.Host.NewSessionAsync(fresh);
        Assert.Equal(HttpStatusCode.OK, (await PostTurnAsync(fresh, freshSession)).StatusCode);
    }

    [Fact]
    public async Task When_both_turn_caps_are_closed_Retry_After_is_the_later_one()
    {
        using var host = new GuestHost(c => Wide(c, ("Guest:MaxTurnsPerHour", "1"), ("Guest:MaxTurnsPerDay", "1")));
        var (_, _, guest) = await host.NewGuestAsync();
        var session = await host.Host.NewSessionAsync(guest);

        Assert.Equal(HttpStatusCode.OK, (await PostTurnAsync(guest, session)).StatusCode);
        var capped = await PostTurnAsync(guest, session);
        Assert.Equal(14 * 3600, RetryAfter(capped)); // day (14 h), not hour (1 h)
    }

    [Fact]
    public async Task Turn_caps_do_not_apply_to_signed_in_users()
    {
        using var host = new GuestHost(c => Wide(c, ("Guest:MaxTurnsPerHour", "1"), ("Guest:MaxTurnsPerDay", "1")));
        var (_, _, guest) = await host.NewGuestAsync();
        var guestSession = await host.Host.NewSessionAsync(guest);
        Assert.Equal(HttpStatusCode.OK, (await PostTurnAsync(guest, guestSession)).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await PostTurnAsync(guest, guestSession)).StatusCode);

        var userSession = await host.Host.NewSessionAsync();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostTurnAsync(host.Host.Client, userSession)).StatusCode);
        }
    }

    // ---- creation caps ----

    [Fact]
    public async Task Creation_cap_is_429_with_Retry_After_and_writes_nothing_while_closed()
    {
        using var host = new GuestHost(c => c["Guest:MaxCreationsPerHour"] = "2");
        var store = host.Factory.Store;
        Assert.Equal(HttpStatusCode.OK, (await host.RequestTokenAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.RequestTokenAsync()).StatusCode);

        var capped = await host.RequestTokenAsync();
        await AssertProblemAsync(capped, (HttpStatusCode)429, "rate_limited");
        Assert.Equal(3600, RetryAfter(capped));

        var calls = store.CounterIncrementCalls;
        Assert.Equal((HttpStatusCode)429, (await host.RequestTokenAsync()).StatusCode);
        Assert.Equal(calls, store.CounterIncrementCalls); // closed flag: no Cosmos write

        host.Factory.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.OK, (await host.RequestTokenAsync()).StatusCode);
    }

    // ---- per-client limiter and check order ----

    private static Action<HttpRequestMessage> Ip(string? clientIp, string? forwardedFor = null) => r =>
    {
        if (clientIp is not null)
        {
            r.Headers.TryAddWithoutValidation("X-Client-Ip", clientIp);
        }

        if (forwardedFor is not null)
        {
            r.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        }
    };

    [Fact]
    public async Task Per_client_limit_is_checked_in_memory_before_Cosmos_and_is_keyed_by_X_Client_Ip_only()
    {
        using var host = new GuestHost(c =>
        {
            c["Guest:TrustClientIpHeader"] = "true";
            c["Guest:CreationsPerClientPerHour"] = "1";
        });
        var store = host.Factory.Store;

        Assert.Equal(HttpStatusCode.OK, (await host.RequestTokenAsync(Ip("203.0.113.5"))).StatusCode);
        var calls = store.CounterIncrementCalls;

        var limited = await host.RequestTokenAsync(Ip("203.0.113.5"));
        await AssertProblemAsync(limited, (HttpStatusCode)429, "rate_limited");
        Assert.InRange(RetryAfter(limited), 1, 3600);
        Assert.Equal(calls, store.CounterIncrementCalls); // rejected before any Cosmos call

        // Spoofed X-Forwarded-For is ignored entirely: same X-Client-Ip stays limited, a new X-Client-Ip is a new client.
        Assert.Equal((HttpStatusCode)429, (await host.RequestTokenAsync(Ip("203.0.113.5", forwardedFor: "9.9.9.9"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.RequestTokenAsync(Ip("203.0.113.6", forwardedFor: "203.0.113.5"))).StatusCode);

        // The window is one hour.
        host.Factory.Time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.OK, (await host.RequestTokenAsync(Ip("203.0.113.5"))).StatusCode);
    }

    [Theory]
    [InlineData("203.0.113.9:5555", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("[2001:db8:1:2::9]:443", "2001:db8:1:2:ffff::1")]
    [InlineData("2001:db8:1:2::9", "2001:db8:1:2:aaaa::2")]
    public async Task Equivalent_spellings_of_one_client_share_a_bucket(string first, string second)
    {
        using var host = new GuestHost(c =>
        {
            c["Guest:TrustClientIpHeader"] = "true";
            c["Guest:CreationsPerClientPerHour"] = "1";
        });
        Assert.Equal(HttpStatusCode.OK, (await host.RequestTokenAsync(Ip(first))).StatusCode);
        Assert.Equal((HttpStatusCode)429, (await host.RequestTokenAsync(Ip(second))).StatusCode);
    }

    [Fact]
    public async Task A_missing_or_unparsable_client_address_gets_no_bucket_and_is_counted_in_a_metric()
    {
        var unparsed = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == GuestMetrics.MeterName && instrument.Name == GuestMetrics.ClientIpUnparsed)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref unparsed, value));
        listener.Start();

        using var host = new GuestHost(c =>
        {
            c["Guest:TrustClientIpHeader"] = "true";
            c["Guest:CreationsPerClientPerHour"] = "1";
        });

        // Not one shared "unknown" bucket that one abuser could fill for everyone.
        foreach (var header in new string?[] { null, "garbage", "999.1.1.1", "1.2.3.4,5.6.7.8", string.Empty, "1.2.3.4:99999" })
        {
            Assert.Equal(HttpStatusCode.OK, (await host.RequestTokenAsync(Ip(header))).StatusCode);
        }

        Assert.True(Interlocked.Read(ref unparsed) >= 6);
    }

    [Fact]
    public async Task Without_TrustClientIpHeader_the_header_is_ignored()
    {
        using var host = new GuestHost(c => c["Guest:CreationsPerClientPerHour"] = "1");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.RequestTokenAsync(Ip("203.0.113.5"))).StatusCode);
        }
    }

    [Theory]
    [InlineData("203.0.113.9", "v4:203.0.113.9")]
    [InlineData("  203.0.113.9  ", "v4:203.0.113.9")]
    [InlineData("203.0.113.9:8080", "v4:203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "v4:203.0.113.9")]
    [InlineData("[::ffff:203.0.113.9]:80", "v4:203.0.113.9")]
    [InlineData("2001:db8:1:2:3:4:5:6", "v6:20010db800010002")]
    [InlineData("[2001:db8:1:2:3:4:5:6]:443", "v6:20010db800010002")]
    [InlineData("[2001:db8:1:2:3:4:5:6]", "v6:20010db800010002")]
    [InlineData("2001:db8:1:2::", "v6:20010db800010002")]
    [InlineData("::1", "v6:0000000000000000")]
    public void Parsing_strips_ports_and_brackets_maps_IPv4_mapped_and_keys_IPv6_by_its_64_prefix(string raw, string expected) =>
        Assert.Equal(expected, GuestClientAddress.Parse(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("1.2.3.4, 5.6.7.8")]
    [InlineData("1.2.3.4:abc")]
    [InlineData("1.2.3.4:70000")]
    [InlineData("[2001:db8::1")]
    [InlineData("[2001:db8::1]x")]
    [InlineData("999.1.1.1")]
    [InlineData("1.2.3")]
    public void Unparsable_values_yield_no_key(string? raw) => Assert.Null(GuestClientAddress.Parse(raw));
}
