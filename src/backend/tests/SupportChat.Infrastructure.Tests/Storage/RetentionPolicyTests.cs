using Microsoft.Extensions.Options;
using SupportChat.Core.Options;
using SupportChat.Infrastructure.Storage;

namespace SupportChat.Infrastructure.Tests.Storage;

/// <summary>ttl by user (data-model 1.3, ADR 0006 section 6.3). Replaces the old retention-days test of TurnRulesTests.</summary>
public class RetentionPolicyTests
{
    private static readonly TimeSpan GuestTtl = TimeSpan.FromMinutes(255);
    private static readonly string GuestId = "guest-" + Guid.NewGuid();
    private static readonly string SocialId = Guid.NewGuid().ToString();

    [Fact]
    public void Retention_days_map_to_ttl_seconds_and_unset_means_no_ttl_for_signed_in_users()
    {
        Assert.Null(new RetentionPolicy(null, GuestTtl).TtlSeconds(SocialId));
        Assert.Null(new RetentionPolicy(0, GuestTtl).TtlSeconds(SocialId));
        Assert.Equal(90 * 86400, new RetentionPolicy(90, GuestTtl).TtlSeconds(SocialId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(30)]
    [InlineData(3650)]
    public void A_guest_always_gets_the_guest_data_ttl_with_or_without_retention_days(int? retentionDays)
    {
        Assert.Equal(15300, new RetentionPolicy(retentionDays, GuestTtl).TtlSeconds(GuestId));
        Assert.Equal(15300, new RetentionPolicy(retentionDays, GuestTtl).GuestTtlSeconds);
    }

    [Fact]
    public void Guest_ttl_applies_even_when_guest_access_is_disabled_and_options_are_defaults()
    {
        var policy = new RetentionPolicy(
            Options.Create(new ChatLimitsOptions { RetentionDays = null }),
            Options.Create(new GuestOptions { Enabled = false }));
        Assert.Equal(15300, policy.TtlSeconds(GuestId));
        Assert.Null(policy.TtlSeconds(SocialId));
    }

    [Fact]
    public void The_prefix_must_be_exact_so_other_ids_follow_the_normal_rule()
    {
        var policy = new RetentionPolicy(30, GuestTtl);
        Assert.Equal(30 * 86400, policy.TtlSeconds("Guest-abc")); // case-sensitive
        Assert.Equal(30 * 86400, policy.TtlSeconds("xguest-abc"));
        Assert.Equal(30 * 86400, policy.TtlSeconds("system-guest-counter"));
    }

    [Fact]
    public void A_configured_guest_data_ttl_is_used()
    {
        Assert.Equal(3600 * 6, new RetentionPolicy(null, TimeSpan.FromHours(6)).TtlSeconds(GuestId));
    }
}
