using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SupportChat.Core.Options;
using SupportChat.Core.Workflows;

namespace SupportChat.Infrastructure.Azure;

/// <summary>
/// Fail-fast startup rules (ADR 0003 section 4). Environment names and AuthMode values are compared
/// case-insensitively. Messages name configuration keys only, never values.
/// </summary>
public static class AzureStartupValidator
{
    public const string EchoWorkflowId = "echo";

    private static readonly string[] NonProductionEnvironments = ["Development", "Dev", "Test"];

    public static IReadOnlyList<string> Validate(
        IConfiguration configuration, string environmentName, IEnumerable<WorkflowDescriptor> workflows)
    {
        var failures = new List<string>();
        var isProduction = Is(environmentName, "Production");
        var isDevelopment = Is(environmentName, "Development");
        var keyAllowedEnvironment = NonProductionEnvironments.Any(e => Is(environmentName, e));

        var authModeRaw = configuration["Azure:AuthMode"];
        AzureAuthMode? mode = null;
        if (string.IsNullOrWhiteSpace(authModeRaw))
        {
            failures.Add("Azure:AuthMode is missing (expected Key or ManagedIdentity).");
        }
        else if (Enum.TryParse<AzureAuthMode>(authModeRaw.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            mode = parsed;
        }
        else
        {
            failures.Add("Azure:AuthMode must be Key or ManagedIdentity.");
        }

        var cosmosKeyPresent = !string.IsNullOrEmpty(configuration["Cosmos:Key"]);
        var searchKeyPresent = !string.IsNullOrEmpty(configuration["Search:Key"]);

        if (isProduction && mode != AzureAuthMode.ManagedIdentity)
        {
            failures.Add("Azure:AuthMode must be ManagedIdentity in Production.");
        }

        if (mode == AzureAuthMode.Key && !keyAllowedEnvironment)
        {
            failures.Add("Azure:AuthMode=Key is allowed only in Development, Dev and Test.");
        }

        if (mode == AzureAuthMode.Key)
        {
            if (!cosmosKeyPresent)
            {
                failures.Add("Cosmos:Key is required when Azure:AuthMode=Key.");
            }

            if (!searchKeyPresent)
            {
                failures.Add("Search:Key is required when Azure:AuthMode=Key.");
            }
        }

        if (isProduction)
        {
            if (cosmosKeyPresent)
            {
                failures.Add("Cosmos:Key must not be configured in Production.");
            }

            if (searchKeyPresent)
            {
                failures.Add("Search:Key must not be configured in Production.");
            }
        }

        if (!isDevelopment && string.IsNullOrWhiteSpace(configuration["AZURE_CLIENT_ID"]))
        {
            failures.Add("AZURE_CLIENT_ID is required outside Development.");
        }

        foreach (var key in new[] { "AzureAd:TenantId", "AzureAd:ClientId" })
        {
            if (!IsRealGuid(configuration[key]))
            {
                failures.Add($"{key} must be a valid, non-empty GUID (not a placeholder).");
            }
        }

        foreach (var key in new[] { "AzureAd:Instance", "Cosmos:Endpoint", "Search:Endpoint" })
        {
            if (!IsRealHttpsUri(configuration[key], keyAllowedEnvironment && key == "Cosmos:Endpoint"))
            {
                failures.Add($"{key} must be an absolute https URI without placeholders.");
            }
        }

        var registered = workflows.ToList();
        if (registered.Any(w => !string.Equals(w.Id, EchoWorkflowId, StringComparison.Ordinal))
            && !IsRealHttpsUri(configuration["Foundry:ProjectEndpoint"], allowLocalhost: false))
        {
            failures.Add("Foundry:ProjectEndpoint must be an absolute https URI without placeholders when a workflow other than 'echo' is registered.");
        }

        var echoEnabled = configuration.GetValue("Workflows:Echo:Enabled", false);
        if (echoEnabled && !keyAllowedEnvironment)
        {
            failures.Add("Workflows:Echo:Enabled=true is allowed only in Development, Dev and Test.");
        }

        return failures;
    }

    private static bool IsRealGuid(string? value) =>
        Guid.TryParse(value?.Trim(), out var guid) && guid != Guid.Empty && !HasPlaceholder(value);

    /// <summary>
    /// Absolute https URI with no placeholder markers. Localhost is accepted only when <paramref name="allowLocalhost"/>
    /// (the Cosmos emulator in Development, Dev and Test, https://localhost:8081); in any other case a loopback host is rejected.
    /// </summary>
    private static bool IsRealHttpsUri(string? value, bool allowLocalhost)
    {
        if (string.IsNullOrWhiteSpace(value) || HasPlaceholder(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return allowLocalhost || !uri.IsLoopback;
    }

    private static bool HasPlaceholder(string? value) =>
        value is not null
        && (value.Contains("REPLACE_ME", StringComparison.OrdinalIgnoreCase) || value.Contains('<') || value.Contains('>'));

    private static bool Is(string actual, string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Runs <see cref="AzureStartupValidator"/> through ValidateOnStart, after workflows are registered.</summary>
public sealed class AzureStartupOptionsValidator(
    IConfiguration configuration, IHostEnvironment environment, IWorkflowRegistry registry) : IValidateOptions<AzureOptions>
{
    public ValidateOptionsResult Validate(string? name, AzureOptions options)
    {
        var failures = AzureStartupValidator.Validate(configuration, environment.EnvironmentName, registry.List());
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
