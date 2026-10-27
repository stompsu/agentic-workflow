using SupportChat.Core.Workflows;
using SupportChat.Infrastructure.Azure;

namespace SupportChat.Infrastructure.Tests.Azure;

/// <summary>ADR 0003 section 4 rules 12-17 (ADR 0006 section 11): each rule has a failing test.</summary>
public class GuestStartupValidatorTests
{
    private static readonly WorkflowDescriptor SupportTicket = new("support-ticket", "Support ticket", "x");

    private static Dictionary<string, string?> Valid()
    {
        var c = TestConfig.Production();
        c["Guest:Enabled"] = "true";
        c["Guest:Issuer"] = "supportchat-guest-dev";
        c["Guest:SigningKeyUri"] = "https://kv-dev.vault.azure.net/keys/guest-token-signing";
        return c;
    }

    private static IReadOnlyList<string> Run(Dictionary<string, string?> config, string env = "Production", params WorkflowDescriptor[] workflows) =>
        GuestStartupValidator.Validate(TestConfig.Build(config), env, workflows.Length == 0 ? [SupportTicket] : workflows);

    private static void AssertFails(Dictionary<string, string?> config, string expectedKey, string env = "Production", params WorkflowDescriptor[] workflows) =>
        Assert.Contains(Run(config, env, workflows), f => f.Contains(expectedKey));

    [Fact]
    public void A_valid_enabled_configuration_has_no_failures() => Assert.Empty(Run(Valid()));

    [Fact]
    public void A_valid_ephemeral_configuration_in_Development_has_no_failures()
    {
        var c = Valid();
        c["Guest:SigningMode"] = "Ephemeral";
        c.Remove("Guest:SigningKeyUri");
        Assert.Empty(Run(c, "Development"));
    }

    [Fact]
    public void Disabled_guests_ignore_every_key_except_DataTtl()
    {
        var c = TestConfig.Production();
        c["Guest:Enabled"] = "false";
        c["Guest:Issuer"] = "https://bad";
        c["Guest:SigningMode"] = "nonsense";
        c["Guest:MaxTurns"] = "-5";
        c["Guest:MaxLifetime"] = "garbage";
        Assert.Empty(Run(c));
        Assert.Empty(Run(TestConfig.Production())); // nothing configured at all
    }

    // Rule 12
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("Has-Upper")]
    [InlineData("https://tenant.ciamlogin.com/tid/v2.0")]
    [InlineData("under_score")]
    public void Rule_12_issuer_is_required_and_must_match_the_pattern(string? issuer)
    {
        var c = Valid();
        c["Guest:Issuer"] = issuer;
        AssertFails(c, "Guest:Issuer");
    }

    [Fact]
    public void Rule_12_issuer_must_not_equal_an_issuer_the_Entra_scheme_accepts()
    {
        var c = Valid();
        c["AzureAd:ValidIssuers:0"] = "supportchat-guest-dev";
        AssertFails(c, "Guest:Issuer");

        var explicitIssuer = Valid();
        explicitIssuer["AzureAd:ValidIssuer"] = "supportchat-guest-dev";
        AssertFails(explicitIssuer, "Guest:Issuer");
    }

    // Rule 13
    [Theory]
    [InlineData("Not Valid")]
    [InlineData("ab")]
    [InlineData("00000000-0000-0000-0000-000000000002")] // the AzureAd:ClientId of TestConfig
    public void Rule_13_audience_must_match_the_pattern_and_differ_from_the_Entra_audiences(string audience)
    {
        var c = Valid();
        c["Guest:Audience"] = audience;
        AssertFails(c, "Guest:Audience");
    }

    [Theory]
    [InlineData("AzureAd:Audience", "supportchat-api-guest")]
    [InlineData("AzureAd:Audiences:0", "supportchat-api-guest")]
    public void Rule_13_a_configured_Entra_audience_may_not_equal_the_guest_audience(string key, string value)
    {
        var c = Valid();
        c[key] = value;
        AssertFails(c, "Guest:Audience");
    }

    [Fact]
    public void Rule_13_the_api_client_id_form_is_also_rejected()
    {
        var c = Valid();
        c["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000002";
        c["Guest:Audience"] = "api://00000000-0000-0000-0000-000000000002".ToLowerInvariant();
        // "api://..." does not match the audience pattern either way: both rules reject it.
        AssertFails(c, "Guest:Audience");
    }

    // Rule 14
    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData("Dev")]
    [InlineData("Test")]
    public void Rule_14_ephemeral_signing_is_only_allowed_in_Development(string env)
    {
        var c = Valid();
        c["Guest:SigningMode"] = "Ephemeral";
        AssertFails(c, "Guest:SigningMode", env);
    }

    [Fact]
    public void Rule_14_unknown_signing_mode_fails()
    {
        var c = Valid();
        c["Guest:SigningMode"] = "Certificate";
        AssertFails(c, "Guest:SigningMode");
    }

    // Rule 15
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://kv.vault.azure.net/keys/guest-token-signing")]
    [InlineData("https://kv.example.com/keys/guest-token-signing")]
    [InlineData("https://.vault.azure.net/keys/guest-token-signing")]
    [InlineData("https://kv.vault.azure.net/keys/guest-token-signing/0123456789abcdef0123456789abcdef")]
    [InlineData("https://kv.vault.azure.net/secrets/guest-token-signing")]
    [InlineData("https://kv.vault.azure.net/keys/")]
    [InlineData("https://kv.vault.azure.net/")]
    [InlineData("https://<vault>.vault.azure.net/keys/guest-token-signing")]
    public void Rule_15_KeyVault_mode_needs_a_versionless_https_vault_key_uri(string? uri)
    {
        var c = Valid();
        c["Guest:SigningKeyUri"] = uri;
        AssertFails(c, "Guest:SigningKeyUri");
    }

    [Fact]
    public void Rule_15_a_sovereign_vault_host_must_still_end_with_vault_azure_net()
    {
        var c = Valid();
        c["Guest:SigningKeyUri"] = "https://kv.vault.azure.net/keys/guest-token-signing";
        Assert.Empty(Run(c));
    }

    // Rule 16
    [Theory]
    [InlineData("Guest:SigningKeyRefreshInterval", "00:04:59")]
    [InlineData("Guest:SigningKeyRefreshInterval", "01:00:01")]
    [InlineData("Guest:MaxLifetime", "00:29:59")]
    [InlineData("Guest:MaxLifetime", "12:00:01")]
    [InlineData("Guest:ActiveCheckCacheDuration", "00:00:09")]
    [InlineData("Guest:ActiveCheckCacheDuration", "00:05:01")]
    [InlineData("Guest:MaxTurns", "0")]
    [InlineData("Guest:MaxTurns", "1001")]
    [InlineData("Guest:MaxSessions", "0")]
    [InlineData("Guest:MaxSessions", "101")]
    [InlineData("Guest:TurnsPerMinute", "0")]
    [InlineData("Guest:CreationsPerClientPerHour", "0")]
    [InlineData("Guest:MaxCreationsPerHour", "0")]
    [InlineData("Guest:MaxTurnsPerHour", "0")]
    [InlineData("Guest:MaxTurnsPerDay", "0")]
    [InlineData("Guest:MaxTurns", "many")]
    [InlineData("Guest:MaxLifetime", "four hours")]
    public void Rule_16_values_outside_their_range_fail(string key, string value)
    {
        var c = Valid();
        c[key] = value;
        AssertFails(c, key);
    }

    [Theory]
    [InlineData("Guest:SigningKeyRefreshInterval", "00:05:00")]
    [InlineData("Guest:SigningKeyRefreshInterval", "01:00:00")]
    [InlineData("Guest:MaxLifetime", "00:30:00")]
    [InlineData("Guest:ActiveCheckCacheDuration", "00:00:10")]
    [InlineData("Guest:MaxTurns", "1000")]
    [InlineData("Guest:MaxSessions", "100")]
    public void Rule_16_boundary_values_pass(string key, string value)
    {
        var c = Valid();
        c[key] = value;
        if (key == "Guest:MaxLifetime")
        {
            c["Guest:DataTtl"] = "00:35:00";
        }

        Assert.Empty(Run(c));
    }

    [Theory]
    [InlineData("04:04:59", false)]
    [InlineData("04:05:00", true)] // MaxLifetime default 04:00:00 + 5 min
    [InlineData("7.00:00:00", true)]
    [InlineData("7.00:00:01", false)]
    public void Rule_16_DataTtl_is_at_least_MaxLifetime_plus_5_minutes_and_at_most_7_days(string ttl, bool valid)
    {
        var c = Valid();
        c["Guest:MaxLifetime"] = "04:00:00";
        c["Guest:DataTtl"] = ttl;
        Assert.Equal(valid, Run(c).Count == 0);
        if (!valid)
        {
            AssertFails(c, "Guest:DataTtl");
        }
    }

    [Fact]
    public void Rule_16_a_longer_MaxLifetime_raises_the_DataTtl_floor()
    {
        var c = Valid();
        c["Guest:MaxLifetime"] = "12:00:00";
        AssertFails(c, "Guest:DataTtl"); // default 04:15:00 is now too short
        c["Guest:DataTtl"] = "12:05:00";
        Assert.Empty(Run(c));
    }

    [Theory]
    [InlineData("100", "2400", true)]
    [InlineData("100", "2401", false)]
    [InlineData("1", "24", true)]
    [InlineData("1", "25", false)]
    public void Rule_16_MaxTurnsPerDay_is_at_most_24_times_MaxTurnsPerHour(string perHour, string perDay, bool valid)
    {
        var c = Valid();
        c["Guest:MaxTurnsPerHour"] = perHour;
        c["Guest:MaxTurnsPerDay"] = perDay;
        Assert.Equal(valid, Run(c).Count == 0);
    }

    // The architect's open point: the DataTtl range is validated ALWAYS, even with guests disabled.
    [Theory]
    [InlineData("false", "00:34:59")]
    [InlineData("false", "7.00:00:01")]
    [InlineData("false", "soon")]
    [InlineData("true", "00:34:59")]
    public void DataTtl_range_is_validated_even_when_guests_are_disabled(string enabled, string ttl)
    {
        var c = Valid();
        c["Guest:Enabled"] = enabled;
        c["Guest:DataTtl"] = ttl;
        AssertFails(c, "Guest:DataTtl");
    }

    [Theory]
    [InlineData("00:35:00")]
    [InlineData("04:15:00")]
    [InlineData("7.00:00:00")]
    public void A_valid_DataTtl_passes_while_guests_are_disabled(string ttl)
    {
        var c = TestConfig.Production();
        c["Guest:DataTtl"] = ttl;
        Assert.Empty(Run(c));
    }

    // Rule 17
    [Fact]
    public void Rule_17_at_least_one_guest_workflow_must_be_registered()
    {
        var c = Valid();
        c["Guest:Workflows:0"] = "does-not-exist";
        AssertFails(c, "Guest:Workflows");

        c["Guest:Workflows:1"] = "support-ticket";
        Assert.Empty(Run(c));

        Assert.Contains(Run(Valid(), workflows: new WorkflowDescriptor("echo", "Echo", "x")), f => f.Contains("Guest:Workflows")); // default list, only echo registered
    }

    [Fact]
    public void Failure_messages_name_keys_only_never_values()
    {
        var c = Valid();
        c["Guest:Issuer"] = "https://secret-tenant.example";
        c["Guest:SigningKeyUri"] = "https://secret-vault.example.com/keys/secret-key";
        c["Guest:MaxTurns"] = "123456789";
        var failures = string.Join('\n', Run(c));
        Assert.DoesNotContain("secret-tenant", failures);
        Assert.DoesNotContain("secret-vault", failures);
        Assert.DoesNotContain("123456789", failures);
    }
}
