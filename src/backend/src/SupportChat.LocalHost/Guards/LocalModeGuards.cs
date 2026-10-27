using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace SupportChat.LocalHost.Guards;

/// <summary>Result of the post-start binding check (G4b).</summary>
public sealed class BindingGuardState
{
    public volatile bool Violated;
}

/// <summary>
/// G1-G4 of ADR 0005 section 1. Pure functions over configuration / environment so each one has a failing test. Messages
/// name configuration keys only, never values.
/// </summary>
public static partial class LocalModeGuards
{
    /// <summary>G3: any of these set (non-empty) means a hosted or containerised environment.</summary>
    public static readonly string[] HostedEnvironmentVariables =
    [
        "CONTAINER_APP_NAME",
        "CONTAINER_APP_REVISION",
        "CONTAINER_APP_JOB_NAME",
        "CONTAINER_APP_ENV_DNS_SUFFIX",
        "WEBSITE_SITE_NAME",
        "KUBERNETES_SERVICE_HOST",
        "DOTNET_RUNNING_IN_CONTAINER",
    ];

    /// <summary>G1 (Development only), G2 (LocalMode:Enabled = true), G3 (not hosted / containerised).</summary>
    public static IReadOnlyList<string> CheckEnvironment(IConfiguration configuration, string environmentName)
    {
        var failures = new List<string>();

        if (!string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("The LocalHost runs only in the Development environment (ASPNETCORE_ENVIRONMENT).");
        }

        if (!bool.TryParse(configuration["LocalMode:Enabled"]?.Trim(), out var enabled) || !enabled)
        {
            failures.Add("LocalMode:Enabled must be true.");
        }

        foreach (var name in HostedEnvironmentVariables)
        {
            // Configuration first (tests inject values; an empty value counts as unset), then the process environment.
            var value = configuration[name] ?? Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                failures.Add($"The LocalHost refuses to start in a hosted or containerised environment ({name} is set).");
            }
        }

        return failures;
    }

    /// <summary>G4(a): every configured URL / port setting must be loopback only. Nothing configured = Kestrel's default (localhost).</summary>
    public static IReadOnlyList<string> CheckUrls(IConfiguration configuration)
    {
        var failures = new List<string>();

        foreach (var key in new[] { "urls", "ASPNETCORE_URLS", "DOTNET_URLS" })
        {
            if (configuration[key] is { Length: > 0 } urls)
            {
                foreach (var url in urls.Split([';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!IsLoopbackUrl(url))
                    {
                        failures.Add($"{key} must contain only loopback URLs (localhost, 127.0.0.1, [::1]).");
                        break;
                    }
                }
            }
        }

        foreach (var key in new[] { "http_ports", "https_ports", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS" })
        {
            // *_PORTS bind every interface, so any non-empty value is rejected.
            if (!string.IsNullOrWhiteSpace(configuration[key]))
            {
                failures.Add($"{key} binds all interfaces and is not allowed in local mode; use the urls setting with a loopback host.");
            }
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            if (endpoint["Url"] is { Length: > 0 } url && !IsLoopbackUrl(url.Trim()))
            {
                failures.Add($"Kestrel:Endpoints:{endpoint.Key}:Url must be a loopback URL (localhost, 127.0.0.1, [::1]).");
            }
        }

        return failures;
    }

    /// <summary>G4(b): the addresses Kestrel actually bound.</summary>
    public static IReadOnlyList<string> CheckAddresses(IEnumerable<string> addresses) =>
        addresses.Any(a => !IsLoopbackUrl(a)) ? ["The host is bound to a non-loopback address."] : [];

    public static bool IsLoopbackHost(string host)
    {
        var trimmed = host.Trim();
        if (trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']')
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(trimmed, out var ip) && IPAddress.IsLoopback(ip));
    }

    public static bool IsLoopbackUrl(string url)
    {
        var match = UrlPattern().Match(url.Trim());
        return match.Success && IsLoopbackHost(match.Groups["host"].Value);
    }

    /// <summary>On ApplicationStarted, stops the host (critical log, exit code 1 via the returned state) if it listens on a non-loopback address.</summary>
    public static BindingGuardState AttachBindingGuard(WebApplication app)
    {
        var state = new BindingGuardState();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LocalHost.Guards");
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
            if (CheckAddresses(addresses).Count > 0)
            {
                logger.LogCritical("The LocalHost is bound to a non-loopback address; stopping.");
                state.Violated = true;
                app.Lifetime.StopApplication();
            }
        });
        return state;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*://(?<host>\[[^\]]+\]|[^:/\[\]]+)(:\d{1,5})?(/.*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();
}
