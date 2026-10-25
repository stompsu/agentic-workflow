using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace SupportChat.Infrastructure.Azure;

/// <summary>
/// Always a TokenCredential (ADR 0003 section 3). Construction performs no network calls and no token requests.
/// </summary>
public sealed class AzureCredentialProvider : IAzureCredentialProvider
{
    private readonly Lazy<TokenCredential> _credential;

    public AzureCredentialProvider(IHostEnvironment environment, IConfiguration configuration)
    {
        var environmentName = environment.EnvironmentName;
        var clientId = configuration["AZURE_CLIENT_ID"];
        _credential = new Lazy<TokenCredential>(() => new DefaultAzureCredential(BuildOptions(environmentName, clientId)));
    }

    public TokenCredential Credential => _credential.Value;

    /// <summary>
    /// Development: developer login (az login / Visual Studio), no managed identity probing.
    /// Everything else: DefaultAzureCredential reduced to the (user-assigned) managed identity only.
    /// </summary>
    internal static DefaultAzureCredentialOptions BuildOptions(string environmentName, string? clientId)
    {
        if (string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            return new DefaultAzureCredentialOptions
            {
                ExcludeManagedIdentityCredential = true,
                ExcludeWorkloadIdentityCredential = true,
            };
        }

        return new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId,
            ExcludeEnvironmentCredential = true,
            ExcludeWorkloadIdentityCredential = true,
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeAzureCliCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeAzureDeveloperCliCredential = true,
            ExcludeInteractiveBrowserCredential = true,
            ExcludeManagedIdentityCredential = false,
        };
    }
}
