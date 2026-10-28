using Azure;
using Azure.Search.Documents;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Core.Serialization;

namespace AgenticWorkflow.Infrastructure.Azure;

/// <summary>
/// The only place (besides options classes and the startup validator) that reads Cosmos:Key / Search:Key.
/// Key mode: endpoint + key. ManagedIdentity mode: same constructors with the TokenCredential. No network calls.
/// </summary>
public sealed class AzureClientFactory(
    IOptions<AzureOptions> azure,
    IOptions<CosmosOptions> cosmos,
    IOptions<SearchServiceOptions> search,
    IAzureCredentialProvider credentials) : IAzureClientFactory
{
    public const string ApplicationName = "agentwf-api";

    public CosmosClient CreateCosmosClient()
    {
        var o = cosmos.Value;
        var endpoint = Require(o.Endpoint, "Cosmos:Endpoint");
        var options = new CosmosClientOptions
        {
            ApplicationName = ApplicationName,
            UseSystemTextJsonSerializerWithOptions = JsonDefaults.Create(),
        };

        return GetMode() switch
        {
            AzureAuthMode.Key => new CosmosClient(endpoint, Require(o.Key, "Cosmos:Key"), options),
            _ => new CosmosClient(endpoint, credentials.Credential, options),
        };
    }

    public SearchClient CreateSearchClient(string indexName)
    {
        var o = search.Value;
        var endpoint = new Uri(Require(o.Endpoint, "Search:Endpoint"));

        return GetMode() switch
        {
            AzureAuthMode.Key => new SearchClient(endpoint, indexName, new AzureKeyCredential(Require(o.Key, "Search:Key"))),
            _ => new SearchClient(endpoint, indexName, credentials.Credential),
        };
    }

    private AzureAuthMode GetMode() =>
        Enum.TryParse<AzureAuthMode>(azure.Value.AuthMode, ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : throw new InvalidOperationException("Azure:AuthMode must be Key or ManagedIdentity.");

    private static string Require(string? value, string key) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{key} is not configured.") : value;
}
