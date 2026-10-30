using Microsoft.Extensions.Configuration;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Infrastructure.Azure;

namespace AgenticWorkflow.Ingestion;

/// <summary>Fail-fast rules for a real (non dry-run) ingestion run. Messages name configuration keys only.</summary>
public static class IngestionStartupValidator
{
    public static List<string> Validate(
        IConfiguration configuration, string environmentName, IngestionOptions options, out AzureAuthMode mode)
    {
        var failures = new List<string>();
        mode = AzureAuthMode.ManagedIdentity;
        if (!Enum.TryParse(configuration["Azure:AuthMode"]?.Trim(), ignoreCase: true, out AzureAuthMode parsed) || !Enum.IsDefined(parsed))
        {
            failures.Add("Azure:AuthMode must be Key or ManagedIdentity.");
        }
        else
        {
            mode = parsed;
        }

        // Same allow-list as the API (AzureStartupValidator): Key only in Development, Dev, Test.
        if (mode == AzureAuthMode.Key && !AzureStartupValidator.IsKeyAllowedEnvironment(environmentName))
        {
            failures.Add("Azure:AuthMode=Key is allowed only in Development, Dev and Test.");
        }

        if (mode == AzureAuthMode.Key && string.IsNullOrWhiteSpace(options.SearchAdminKey))
        {
            failures.Add("Ingestion:SearchAdminKey is required when Azure:AuthMode=Key.");
        }

        if (mode == AzureAuthMode.ManagedIdentity && !string.IsNullOrWhiteSpace(options.SearchAdminKey))
        {
            failures.Add("Ingestion:SearchAdminKey must not be set when Azure:AuthMode=ManagedIdentity.");
        }

        foreach (var key in new[] { "Search:Endpoint", "Ingestion:VectorizerResourceUri" })
        {
            if (!Uri.TryCreate(configuration[key], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || (configuration[key]?.Contains('<') ?? true))
            {
                failures.Add($"{key} must be an absolute https URI.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.EmbeddingDeployment) || ConfigPlaceholders.HasPlaceholder(options.EmbeddingDeployment))
        {
            failures.Add("Ingestion:EmbeddingDeployment is required and must not be a placeholder.");
        }

        // Optional (defaults to EmbeddingDeployment), but a placeholder value is never acceptable.
        if (ConfigPlaceholders.HasPlaceholder(options.VectorizerDeployment))
        {
            failures.Add("Ingestion:VectorizerDeployment must not be a placeholder.");
        }

        return failures;
    }
}
