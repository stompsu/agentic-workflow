using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SupportChat.Core.Options;
using SupportChat.Core.Workflows;

namespace SupportChat.Infrastructure.Azure;

/// <summary>
/// Fail-fast guest rules (ADR 0003 section 4 rules 12-17, ADR 0006 section 11). Reads the raw configuration so that
/// invalid values are reported by key name only, never by value. When Guest:Enabled is false every key is ignored
/// EXCEPT Guest:DataTtl, which is always range-checked because RetentionPolicy applies it to leftover guest data.
/// </summary>
public static partial class GuestStartupValidator
{
    /// <summary>Smallest DataTtl any valid MaxLifetime (min 00:30:00) allows (rule 16: DataTtl &gt;= MaxLifetime + 00:05:00).</summary>
    public static readonly TimeSpan MinDataTtl = TimeSpan.FromMinutes(35);

    public static readonly TimeSpan MaxDataTtl = TimeSpan.FromDays(7);
    public static readonly TimeSpan DataTtlMargin = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<string> Validate(
        IConfiguration configuration, string environmentName, IEnumerable<WorkflowDescriptor> workflows)
    {
        var failures = new List<string>();
        var guest = configuration.GetSection(GuestOptions.SectionName);

        // Always: DataTtl range (RetentionPolicy uses it even while guests are disabled).
        var dataTtl = ParseSpan(guest["DataTtl"], new GuestOptions().DataTtl, "Guest:DataTtl", failures);
        if (dataTtl is { } ttl && (ttl < MinDataTtl || ttl > MaxDataTtl))
        {
            failures.Add("Guest:DataTtl must be between 00:35:00 and 7.00:00:00.");
            dataTtl = null;
        }

        if (!TryParseBool(guest["Enabled"], out var enabled))
        {
            failures.Add("Guest:Enabled must be true or false.");
            return failures;
        }

        if (!enabled)
        {
            return failures;
        }

        var defaults = new GuestOptions();

        // Rule 12: issuer.
        var issuer = guest["Issuer"]?.Trim();
        if (string.IsNullOrEmpty(issuer) || !IdPattern().IsMatch(issuer))
        {
            failures.Add("Guest:Issuer is required and must match ^[a-z0-9-]{3,64}$ (never an https issuer URL).");
        }
        else if (EntraIssuers(configuration).Contains(issuer, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add("Guest:Issuer must differ from every issuer the Entra scheme accepts.");
        }

        // Rule 13: audience.
        var audience = guest["Audience"]?.Trim() is { Length: > 0 } a ? a : defaults.Audience;
        if (!IdPattern().IsMatch(audience))
        {
            failures.Add("Guest:Audience must match ^[a-z0-9-]{3,64}$.");
        }
        else if (EntraAudiences(configuration).Contains(audience, StringComparer.OrdinalIgnoreCase))
        {
            failures.Add("Guest:Audience must not equal an audience of the Entra scheme (AzureAd:ClientId, api://<ClientId>, AzureAd:Audience(s)).");
        }

        // Rule 14: signing mode. Rule 15: key URI.
        var modeRaw = guest["SigningMode"]?.Trim();
        GuestSigningMode mode = defaults.SigningMode;
        if (!string.IsNullOrEmpty(modeRaw))
        {
            if (!Enum.TryParse(modeRaw, ignoreCase: true, out mode) || !Enum.IsDefined(mode))
            {
                failures.Add("Guest:SigningMode must be KeyVault or Ephemeral.");
                mode = defaults.SigningMode;
            }
        }

        if (mode == GuestSigningMode.Ephemeral && !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Guest:SigningMode=Ephemeral is allowed only in Development.");
        }

        if (mode == GuestSigningMode.KeyVault && !IsVersionlessKeyUri(guest["SigningKeyUri"]))
        {
            failures.Add("Guest:SigningKeyUri must be https://<vault>.vault.azure.net/keys/<name> (versionless) when Guest:SigningMode=KeyVault.");
        }

        // Rule 16: ranges.
        var refresh = ParseSpan(guest["SigningKeyRefreshInterval"], defaults.SigningKeyRefreshInterval, "Guest:SigningKeyRefreshInterval", failures);
        InRange(refresh, TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), "Guest:SigningKeyRefreshInterval", "00:05:00", "01:00:00", failures);
        var lifetime = ParseSpan(guest["MaxLifetime"], defaults.MaxLifetime, "Guest:MaxLifetime", failures);
        InRange(lifetime, TimeSpan.FromMinutes(30), TimeSpan.FromHours(12), "Guest:MaxLifetime", "00:30:00", "12:00:00", failures);
        if (dataTtl is { } d && lifetime is { } l && d < l + DataTtlMargin)
        {
            failures.Add("Guest:DataTtl must be at least Guest:MaxLifetime + 00:05:00.");
        }

        var cache = ParseSpan(guest["ActiveCheckCacheDuration"], defaults.ActiveCheckCacheDuration, "Guest:ActiveCheckCacheDuration", failures);
        InRange(cache, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), "Guest:ActiveCheckCacheDuration", "00:00:10", "00:05:00", failures);

        var maxTurns = ParseInt(guest["MaxTurns"], defaults.MaxTurns, "Guest:MaxTurns", failures);
        InRange(maxTurns, 1, 1000, "Guest:MaxTurns", failures);
        var maxSessions = ParseInt(guest["MaxSessions"], defaults.MaxSessions, "Guest:MaxSessions", failures);
        InRange(maxSessions, 1, 100, "Guest:MaxSessions", failures);
        foreach (var (key, fallback) in new[]
                 {
                     ("TurnsPerMinute", defaults.TurnsPerMinute),
                     ("CreationsPerClientPerHour", defaults.CreationsPerClientPerHour),
                     ("MaxCreationsPerHour", defaults.MaxCreationsPerHour),
                 })
        {
            if (ParseInt(guest[key], fallback, "Guest:" + key, failures) is < 1)
            {
                failures.Add($"Guest:{key} must be an integer >= 1.");
            }
        }

        var perHour = ParseInt(guest["MaxTurnsPerHour"], defaults.MaxTurnsPerHour, "Guest:MaxTurnsPerHour", failures);
        var perDay = ParseInt(guest["MaxTurnsPerDay"], defaults.MaxTurnsPerDay, "Guest:MaxTurnsPerDay", failures);
        if (perHour is < 1)
        {
            failures.Add("Guest:MaxTurnsPerHour must be an integer >= 1.");
        }

        if (perDay is < 1)
        {
            failures.Add("Guest:MaxTurnsPerDay must be an integer >= 1.");
        }
        else if (perHour is >= 1 && perDay > 24L * perHour)
        {
            failures.Add("Guest:MaxTurnsPerDay must not exceed 24 x Guest:MaxTurnsPerHour.");
        }

        if (!string.IsNullOrWhiteSpace(guest["TrustClientIpHeader"]) && !TryParseBool(guest["TrustClientIpHeader"], out _))
        {
            failures.Add("Guest:TrustClientIpHeader must be true or false.");
        }

        // Rule 17: at least one registered guest workflow.
        var guestWorkflows = guest.GetSection("Workflows").Get<string[]>() is { Length: > 0 } configured ? configured : GuestOptions.DefaultWorkflows;
        var registered = workflows.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        if (!guestWorkflows.Any(registered.Contains))
        {
            failures.Add("None of the Guest:Workflows ids is a registered workflow.");
        }

        return failures;
    }

    private static IEnumerable<string> EntraIssuers(IConfiguration configuration)
    {
        var azureAd = configuration.GetSection("AzureAd");
        foreach (var value in new[] { azureAd["Instance"], azureAd["Authority"], azureAd["TenantId"], azureAd["ValidIssuer"] })
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value.Trim().TrimEnd('/');
                yield return value.Trim();
            }
        }

        foreach (var value in azureAd.GetSection("ValidIssuers").Get<string[]>() ?? [])
        {
            yield return value.Trim();
        }
    }

    private static IEnumerable<string> EntraAudiences(IConfiguration configuration)
    {
        var azureAd = configuration.GetSection("AzureAd");
        var clientId = azureAd["ClientId"]?.Trim();
        if (!string.IsNullOrEmpty(clientId))
        {
            yield return clientId;
            yield return "api://" + clientId;
        }

        if (!string.IsNullOrWhiteSpace(azureAd["Audience"]))
        {
            yield return azureAd["Audience"]!.Trim();
        }

        foreach (var value in azureAd.GetSection("Audiences").Get<string[]>() ?? [])
        {
            yield return value.Trim();
        }
    }

    private static bool IsVersionlessKeyUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || ConfigPlaceholders.HasPlaceholder(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (!uri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase) || uri.Host.Length <= ".vault.azure.net".Length)
        {
            return false;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        return segments.Length == 2 && segments[0] == "keys" && segments[1].Length > 0 && uri.Query.Length == 0;
    }

    private static TimeSpan? ParseSpan(string? raw, TimeSpan fallback, string key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (TimeSpan.TryParse(raw.Trim(), CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        failures.Add($"{key} must be a time span (d.hh:mm:ss).");
        return null;
    }

    private static int? ParseInt(string? raw, int fallback, string key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        failures.Add($"{key} must be an integer.");
        return null;
    }

    private static void InRange(TimeSpan? value, TimeSpan min, TimeSpan max, string key, string minText, string maxText, List<string> failures)
    {
        if (value is { } v && (v < min || v > max))
        {
            failures.Add($"{key} must be between {minText} and {maxText}.");
        }
    }

    private static void InRange(int? value, int min, int max, string key, List<string> failures)
    {
        if (value is { } v && (v < min || v > max))
        {
            failures.Add($"{key} must be between {min} and {max}.");
        }
    }

    private static bool TryParseBool(string? raw, out bool value)
    {
        value = false;
        return string.IsNullOrWhiteSpace(raw) || bool.TryParse(raw.Trim(), out value);
    }

    [GeneratedRegex("^[a-z0-9-]{3,64}$")]
    private static partial Regex IdPattern();
}

/// <summary>Runs <see cref="GuestStartupValidator"/> through ValidateOnStart (second validator of <see cref="AzureOptions"/>).</summary>
public sealed class GuestStartupOptionsValidator(
    IConfiguration configuration, IHostEnvironment environment, IWorkflowRegistry registry) : IValidateOptions<AzureOptions>
{
    public ValidateOptionsResult Validate(string? name, AzureOptions options)
    {
        var failures = GuestStartupValidator.Validate(configuration, environment.EnvironmentName, registry.List());
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
