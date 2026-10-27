using System.Globalization;
using Microsoft.Extensions.Options;
using SupportChat.Core.Options;
using SupportChat.LocalHost.Guards;

namespace SupportChat.LocalHost;

/// <summary>
/// Startup validation of the local mode settings (ADR 0005): G1-G4, AllowedHosts, Auth, Models, TokenLifetime, TokenDelay,
/// knowledge path and the optional Entra / Foundry settings. Messages name configuration keys only, never values.
/// </summary>
public sealed class LocalModeValidator(IConfiguration configuration, IHostEnvironment environment) : IValidateOptions<LocalModeOptions>
{
    public static readonly TimeSpan MinTokenLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan MaxTokenDelay = TimeSpan.FromSeconds(1);

    public ValidateOptionsResult Validate(string? name, LocalModeOptions options)
    {
        var failures = Validate(configuration, environment.EnvironmentName).ToList();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public static IReadOnlyList<string> Validate(IConfiguration configuration, string environmentName)
    {
        var failures = new List<string>();
        failures.AddRange(LocalModeGuards.CheckEnvironment(configuration, environmentName));
        failures.AddRange(LocalModeGuards.CheckUrls(configuration));
        failures.AddRange(AllowedHosts(configuration));

        var auth = LocalAuthMode.Local;
        if (configuration["LocalMode:Auth"] is { } authRaw && !string.IsNullOrWhiteSpace(authRaw)
            && !LocalModeOptions.TryParseName(authRaw, out auth))
        {
            failures.Add("LocalMode:Auth must be Local or Entra.");
        }

        var models = LocalModelsMode.Scripted;
        if (configuration["LocalMode:Models"] is { } modelsRaw && !string.IsNullOrWhiteSpace(modelsRaw)
            && !LocalModeOptions.TryParseName(modelsRaw, out models))
        {
            failures.Add("LocalMode:Models must be Scripted or Foundry.");
        }

        if (configuration["LocalMode:LocalAuth:TokenLifetime"] is { } lifetimeRaw && !string.IsNullOrWhiteSpace(lifetimeRaw)
            && (!TimeSpan.TryParse(lifetimeRaw.Trim(), CultureInfo.InvariantCulture, out var lifetime) || lifetime < MinTokenLifetime || lifetime > MaxTokenLifetime))
        {
            failures.Add("LocalMode:LocalAuth:TokenLifetime must be a time span between 00:05:00 and 08:00:00.");
        }

        if (configuration["LocalMode:Scripted:TokenDelay"] is { } delayRaw && !string.IsNullOrWhiteSpace(delayRaw)
            && (!TimeSpan.TryParse(delayRaw.Trim(), CultureInfo.InvariantCulture, out var delay) || delay < TimeSpan.Zero || delay > MaxTokenDelay))
        {
            failures.Add("LocalMode:Scripted:TokenDelay must be a time span between 00:00:00 and 00:00:01.");
        }

        var pathRaw = configuration["LocalMode:Knowledge:SourcePath"];
        if (pathRaw is not null && string.IsNullOrWhiteSpace(pathRaw))
        {
            failures.Add("LocalMode:Knowledge:SourcePath must not be blank.");
        }
        else if (!Directory.Exists(LocalModeOptions.From(configuration).ResolveKnowledgePath()))
        {
            failures.Add("LocalMode:Knowledge:SourcePath does not exist.");
        }

        if (auth == LocalAuthMode.Entra)
        {
            foreach (var key in new[] { "AzureAd:Instance", "AzureAd:TenantId", "AzureAd:ClientId" })
            {
                if (string.IsNullOrWhiteSpace(configuration[key]) || ConfigPlaceholders.HasPlaceholder(configuration[key]))
                {
                    failures.Add($"{key} is required for LocalMode:Auth=Entra and must not be a placeholder (use user-secrets).");
                }
            }
        }

        if (models == LocalModelsMode.Foundry)
        {
            var endpoint = configuration["Foundry:ProjectEndpoint"];
            if (string.IsNullOrWhiteSpace(endpoint) || ConfigPlaceholders.HasPlaceholder(endpoint)
                || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                failures.Add("Foundry:ProjectEndpoint is required for LocalMode:Models=Foundry and must be an https URI without placeholders (use user-secrets).");
            }
        }

        return failures;
    }

    private static IEnumerable<string> AllowedHosts(IConfiguration configuration)
    {
        var raw = configuration["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield return "AllowedHosts must be set to loopback hosts (localhost;127.0.0.1;[::1]).";
            yield break;
        }

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!LocalModeGuards.IsLoopbackHost(StripPort(entry)))
            {
                yield return "AllowedHosts must contain only loopback hosts (localhost, 127.0.0.1, [::1]); no wildcards.";
                yield break;
            }
        }
    }

    private static string StripPort(string entry)
    {
        if (entry.StartsWith('['))
        {
            var end = entry.IndexOf(']', StringComparison.Ordinal);
            return end > 0 ? entry[..(end + 1)] : entry;
        }

        var colon = entry.LastIndexOf(':');
        return colon > 0 && entry.IndexOf(':', StringComparison.Ordinal) == colon ? entry[..colon] : entry;
    }
}
