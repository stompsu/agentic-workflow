using SupportChat.Core.Serialization;
using SupportChat.Infrastructure.Storage;

namespace SupportChat.Infrastructure.Tests.Storage;

public class TurnRulesTests
{
    [Fact]
    public void Auto_title_is_trimmed_and_limited_to_60_characters()
    {
        Assert.Equal("hello", TurnRules.AutoTitle("  hello  "));
        Assert.Equal(60, TurnRules.AutoTitle(new string('a', 200)).Length);
    }

    [Fact]
    public void Retention_days_map_to_ttl_seconds_and_unset_means_no_ttl()
    {
        Assert.Null(TurnRules.TtlSeconds(null));
        Assert.Equal(90 * 86400, TurnRules.TtlSeconds(90));
    }

    [Fact]
    public void Continuation_tokens_roundtrip_and_reject_garbage()
    {
        var token = ContinuationTokens.Encode("{\"token\":\"abc\"}");
        Assert.Equal("{\"token\":\"abc\"}", ContinuationTokens.Decode(token));
        Assert.Throws<SupportChat.Core.Storage.InvalidContinuationTokenException>(() => ContinuationTokens.Decode("!!"));
        Assert.Throws<SupportChat.Core.Storage.InvalidContinuationTokenException>(() => ContinuationTokens.Decode(new string('A', 9000)));
    }

    [Fact]
    public void Timestamps_serialize_as_fixed_width_utc_milliseconds_and_enums_as_camelCase()
    {
        var options = JsonDefaults.Create();
        var at = new DateTimeOffset(2026, 10, 25, 10, 15, 30, 100, TimeSpan.FromHours(2));
        Assert.Equal("\"2026-10-25T08:15:30.100Z\"", System.Text.Json.JsonSerializer.Serialize(at, options));
        Assert.Equal("\"healthAssessments\"", System.Text.Json.JsonSerializer.Serialize(Sample.HealthAssessments, options));
        Assert.Equal(at.ToUniversalTime(), System.Text.Json.JsonSerializer.Deserialize<DateTimeOffset>("\"2026-10-25T08:15:30.100Z\"", options));
    }

    private enum Sample
    {
        HealthAssessments,
    }
}
