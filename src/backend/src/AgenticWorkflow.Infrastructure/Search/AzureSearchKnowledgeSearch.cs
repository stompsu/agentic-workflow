using System.Text.Json.Serialization;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgenticWorkflow.Core.Knowledge;
using AgenticWorkflow.Core.Options;

namespace AgenticWorkflow.Infrastructure.Search;

/// <summary>
/// Hybrid (keyword + vector via the index's integrated vectorizer) + semantic ranking, single domain
/// (data-model 2.4). Uses the QUERY-only SearchClient from <c>IAzureClientFactory</c>. The backend sends text only;
/// Search embeds the query, so no embedding client is needed here.
/// </summary>
public sealed class AzureSearchKnowledgeSearch(
    Lazy<SearchClient> lazyClient,
    IOptions<SearchServiceOptions> options,
    ILogger<AzureSearchKnowledgeSearch> logger) : IKnowledgeSearch
{
    // The client is resolved on first search, never while the workflow registry is built during startup validation
    // (ADR 0003 section 4: validation must run before any client is constructed).
    private SearchClient client => lazyClient.Value;

    public AzureSearchKnowledgeSearch(
        SearchClient client, IOptions<SearchServiceOptions> options, ILogger<AzureSearchKnowledgeSearch> logger)
        : this(new Lazy<SearchClient>(client), options, logger)
    {
    }

    public async Task<IReadOnlyList<KnowledgeChunk>> SearchAsync(string domain, string query, int top, CancellationToken cancellationToken)
    {
        var filter = DomainValue.ToFilter(domain);
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var settings = options.Value;
        var size = Math.Clamp(top > 0 ? top : settings.TopK, 1, KnowledgeIndexSchema.MaxTop);

        var searchOptions = new SearchOptions
        {
            Filter = filter,
            Size = size,
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions { SemanticConfigurationName = KnowledgeIndexSchema.SemanticConfigurationName },
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizableTextQuery(query)
                    {
                        KNearestNeighborsCount = KnowledgeIndexSchema.VectorNearestNeighbors,
                        Fields = { KnowledgeIndexSchema.VectorField },
                    },
                },
            },
        };
        foreach (var field in KnowledgeIndexSchema.SelectFields)
        {
            searchOptions.Select.Add(field);
        }

        var response = await client.SearchAsync<KnowledgeDocument>(query, searchOptions, cancellationToken);

        var chunks = new List<KnowledgeChunk>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
        {
            var score = result.SemanticSearch?.RerankerScore;
            if (score is { } s && s < settings.MinRerankerScore)
            {
                continue;
            }

            var doc = result.Document;
            if (string.IsNullOrEmpty(doc.Id))
            {
                continue;
            }

            chunks.Add(new KnowledgeChunk(
                doc.Id,
                doc.Domain ?? domain,
                doc.Title ?? string.Empty,
                doc.Content ?? string.Empty,
                string.IsNullOrWhiteSpace(doc.SourceUrl) ? null : doc.SourceUrl,
                score));
        }

        logger.LogDebug("Knowledge search returned {Count} chunks for domain {Domain}.", chunks.Count, domain);
        return chunks;
    }

    private sealed class KnowledgeDocument
    {
        [JsonPropertyName(KnowledgeIndexSchema.IdField)]
        public string? Id { get; set; }

        [JsonPropertyName(KnowledgeIndexSchema.DomainField)]
        public string? Domain { get; set; }

        [JsonPropertyName(KnowledgeIndexSchema.TitleField)]
        public string? Title { get; set; }

        [JsonPropertyName(KnowledgeIndexSchema.ContentField)]
        public string? Content { get; set; }

        [JsonPropertyName(KnowledgeIndexSchema.SourceUrlField)]
        public string? SourceUrl { get; set; }
    }
}
