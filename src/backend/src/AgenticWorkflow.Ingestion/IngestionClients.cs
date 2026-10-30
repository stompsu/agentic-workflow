// OpenAI embedding/responses client surfaces are marked experimental in the pinned prerelease.
#pragma warning disable OPENAI001
using System.ClientModel.Primitives;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Embeddings;
using AgenticWorkflow.Core.Options;

namespace AgenticWorkflow.Ingestion;

/// <summary>
/// Search clients for ingestion. Same auth switch as the API (azure-auth-switch skill, ADR 0003) but with the ADMIN
/// credential: <c>Ingestion:SearchAdminKey</c> in Key mode, the TokenCredential in ManagedIdentity mode. It never reads
/// <c>Search:Key</c> (the API's query key can neither create indexes nor upload documents).
/// </summary>
public sealed class IngestionClients(
    AzureAuthMode mode, Uri searchEndpoint, string indexName, string? adminKey, Func<TokenCredential> credential, HttpPipelineTransport? transport = null)
{
    public SearchIndexClient CreateIndexClient() => mode == AzureAuthMode.Key
        ? new SearchIndexClient(searchEndpoint, new AzureKeyCredential(RequireKey()), IndexOptions())
        : new SearchIndexClient(searchEndpoint, credential(), IndexOptions());

    public SearchClient CreateSearchClient() => mode == AzureAuthMode.Key
        ? new SearchClient(searchEndpoint, indexName, new AzureKeyCredential(RequireKey()), ClientOptions())
        : new SearchClient(searchEndpoint, indexName, credential(), ClientOptions());

    private SearchClientOptions ClientOptions() => transport is null ? new SearchClientOptions() : new SearchClientOptions { Transport = transport };

    private SearchClientOptions IndexOptions() => ClientOptions();

    private string RequireKey() =>
        string.IsNullOrWhiteSpace(adminKey) ? throw new InvalidOperationException("Ingestion:SearchAdminKey is not configured.") : adminKey;

    /// <summary>
    /// Models are always reached with a TokenCredential (no model keys). Embeddings use the resource's <c>/openai/v1</c>
    /// endpoint, not AIProjectClient: the project endpoint does not route embedding requests (404, plan.md D8).
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        Uri modelResourceUri, string deployment, TokenCredential credential, PipelineTransport? transport = null)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(new Uri(modelResourceUri.AbsoluteUri.TrimEnd('/') + "/"), "openai/v1/") };
        if (transport is not null)
        {
            options.Transport = transport;
        }

        var tokenPolicy = new BearerTokenPolicy(credential, "https://ai.azure.com/.default");
        return new EmbeddingClient(deployment, tokenPolicy, options).AsIEmbeddingGenerator();
    }
}
