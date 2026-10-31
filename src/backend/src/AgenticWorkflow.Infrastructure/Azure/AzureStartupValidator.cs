using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Core.Workflows;
using AgenticWorkflow.Infrastructure.Telemetry;

namespace AgenticWorkflow.Infrastructure.Azure;

/// <summary>
/// Fail-fast startup rules (ADR 0003 section 4). Environment names and AuthMode values are compared
/// case-insensitively. Messages name configuration keys only, never values.
/// </summary>
public static class AzureStartupValidator
{
    public const string EchoWorkflowId = "echo";
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 3650;
    public static readonly TimeSpan MinDeleteBudget = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxDeleteBudget = TimeSpan.FromMinutes(3);

    private static readonly string[] NonProductionEnvironments = ["Development", "Dev", "Test"];

    /// <summary>True for the allow-listed environments (Development, Dev, Test; case-insensitive) where key auth may run.</summary>
    public static bool IsKeyAllowedEnvironment(string environmentName) =>
        NonProductionEnvironments.Any(e => Is(environmentName, e));

    public static IReadOnlyList<string> Validate(
        IConfiguration configuration, string environmentName, IEnumerable<WorkflowDescriptor> workflows)
    {
        var failures = new List<string>();
        var isProduction = Is(environmentName, "Production");
        var isDevelopment = Is(environmentName, "Development");
        var keyAllowedEnvironment = IsKeyAllowedEnvironment(environmentName);

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

        // plan.md D14: LLM content telemetry holds prompts and user text, so it never runs in Production.
        if (configuration.GetValue("Telemetry:LlmContentEnabled", false))
        {
            if (!keyAllowedEnvironment)
            {
                failures.Add("Telemetry:LlmContentEnabled=true is allowed only in Development, Dev and Test.");
            }

            if (string.IsNullOrWhiteSpace(configuration[LlmContentTelemetry.ConnectionStringKey]))
            {
                failures.Add($"{LlmContentTelemetry.ConnectionStringKey} is required when Telemetry:LlmContentEnabled=true.");
            }
        }

        // Rule 9 (D7): retention is required outside Development; when set it must be an integer in 1..3650.
        var retentionRaw = configuration["Chat:RetentionDays"];
        if (string.IsNullOrWhiteSpace(retentionRaw))
        {
            if (!isDevelopment)
            {
                failures.Add("Chat:RetentionDays is required outside Development (integer 1..3650).");
            }
        }
        else if (!int.TryParse(retentionRaw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days)
                 || days is < MinRetentionDays or > MaxRetentionDays)
        {
            failures.Add($"Chat:RetentionDays must be an integer between {MinRetentionDays} and {MaxRetentionDays}.");
        }

        // Rule 10 (D7): delete-my-data budget stays below the 240 s ingress timeout.
        var budgetRaw = configuration["Chat:DeleteUserDataBudget"];
        if (!string.IsNullOrWhiteSpace(budgetRaw)
            && (!TimeSpan.TryParse(budgetRaw.Trim(), CultureInfo.InvariantCulture, out var budget)
                || budget < MinDeleteBudget || budget > MaxDeleteBudget))
        {
            failures.Add("Chat:DeleteUserDataBudget must be a time span between 00:00:05 and 00:03:00.");
        }

        return failures;
    }

    /// <summary>
    /// Extra rules for <c>--smoke-check</c> (ADR 0003 section 1a): its purpose is to exercise the managed-identity path,
    /// so it refuses Key mode and any configured Cosmos/Search key. Messages name configuration keys only.
    /// </summary>
    public static IReadOnlyList<string> ValidateSmokeCheck(IConfiguration configuration)
    {
        var failures = new List<string>();
        if (!Enum.TryParse<AzureAuthMode>(configuration["Azure:AuthMode"]?.Trim(), ignoreCase: true, out var mode)
            || mode != AzureAuthMode.ManagedIdentity)
        {
            failures.Add("--smoke-check requires Azure:AuthMode=ManagedIdentity.");
        }

        if (!string.IsNullOrEmpty(configuration["Cosmos:Key"]))
        {
            failures.Add("--smoke-check requires Cosmos:Key to be unset.");
        }

        if (!string.IsNullOrEmpty(configuration["Search:Key"]))
        {
            failures.Add("--smoke-check requires Search:Key to be unset.");
        }

        return failures;
    }

    private static bool IsRealGuid(string? value) =>
        Guid.TryParse(value?.Trim(), out var guid) && guid != Guid.Empty && !ConfigPlaceholders.HasPlaceholder(value);

    /// <summary>
    /// Absolute https URI with no placeholder markers. Localhost is accepted only when <paramref name="allowLocalhost"/>
    /// (the Cosmos emulator in Development, Dev and Test, https://localhost:8081); in any other case a loopback host is rejected.
    /// </summary>
    private static bool IsRealHttpsUri(string? value, bool allowLocalhost)
    {
        if (string.IsNullOrWhiteSpace(value) || ConfigPlaceholders.HasPlaceholder(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        return allowLocalhost || !uri.IsLoopback;
    }

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
