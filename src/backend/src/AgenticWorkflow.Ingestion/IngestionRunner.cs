using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;
using AgenticWorkflow.Infrastructure.Search;

namespace AgenticWorkflow.Ingestion;

public sealed record IngestionSummary(int Documents, int Chunks, int Uploaded, int Deleted);

public sealed class IngestionRunner(
    IngestionOptions options,
    string indexName,
    IngestionClients? clients,
    IEmbeddingGenerator<string, Embedding<float>>? embeddings,
    Action<string> log)
{
    private const int UploadBatchSize = 100;

    /// <summary>
    /// Dry run: read + chunk + report, no Azure calls. Otherwise: create/update the index, embed, upsert by
    /// deterministic id, delete chunks whose source disappeared.
    /// </summary>
    public async Task<IngestionSummary> RunAsync(bool dryRun, bool allowEmpty, CancellationToken ct)
    {
        var documents = KnowledgeSource.Read(options.SourcePath, log);
        var planned = IngestionPlan.Build(documents, options.ChunkTokens, options.ChunkOverlapTokens);
        log($"Read {documents.Count} documents -> {planned.Count} chunks from '{options.SourcePath}'.");
        foreach (var group in planned.GroupBy(p => p.Domain).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            log($"  domain {group.Key}: {group.Count()} chunks");
        }

        if (dryRun)
        {
            return new IngestionSummary(documents.Count, planned.Count, 0, 0);
        }

        if (clients is null || embeddings is null)
        {
            throw new InvalidOperationException("Clients are required for a real run.");
        }

        if (string.IsNullOrWhiteSpace(options.VectorizerResourceUri))
        {
            throw new InvalidOperationException("Ingestion:VectorizerResourceUri is required to create the index (integrated vectorizer).");
        }

        var indexClient = clients.CreateIndexClient();
        await indexClient.CreateOrUpdateIndexAsync(IndexDefinition.Build(indexName, options), cancellationToken: ct);
        log($"Index '{indexName}' created or updated.");

        var search = clients.CreateSearchClient();
        var existing = await ListIdsAsync(search, ct);
        if (planned.Count == 0 && existing.Count > 0 && !allowEmpty)
        {
            throw new InvalidOperationException(
                $"No source documents found but the index holds {existing.Count} chunks; refusing to delete everything (use --allow-empty).");
        }

        var uploaded = 0;
        foreach (var batch in planned.Chunk(Math.Max(1, options.EmbeddingBatchSize)))
        {
            var vectors = await embeddings.GenerateAsync(batch.Select(b => b.Title + "\n" + b.Content).ToList(), cancellationToken: ct);
            var docs = batch.Select((p, i) => new SearchDocument
            {
                [KnowledgeIndexSchema.IdField] = p.Id,
                [KnowledgeIndexSchema.DomainField] = p.Domain,
                [KnowledgeIndexSchema.TitleField] = p.Title,
                [KnowledgeIndexSchema.ContentField] = p.Content,
                [KnowledgeIndexSchema.VectorField] = vectors[i].Vector.ToArray(),
                [KnowledgeIndexSchema.SourceUrlField] = p.SourceUrl ?? string.Empty,
            }).ToList();

            foreach (var upload in docs.Chunk(UploadBatchSize))
            {
                await search.MergeOrUploadDocumentsAsync(upload, cancellationToken: ct);
                uploaded += upload.Length;
            }
        }

        var keep = planned.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var stale = existing.Where(id => !keep.Contains(id)).ToList();
        foreach (var batch in stale.Chunk(UploadBatchSize))
        {
            await search.DeleteDocumentsAsync(KnowledgeIndexSchema.IdField, batch, cancellationToken: ct);
        }

        log($"Uploaded {uploaded} chunks, deleted {stale.Count} stale chunks.");
        return new IngestionSummary(documents.Count, planned.Count, uploaded, stale.Count);
    }

    private const int PageSize = 1000;

    /// <summary>
    /// Lists every chunk id. Keyset paging (order by id, filter id gt last) has no 100k skip cap and is stable while
    /// the index is unchanged; the id field is filterable and sortable (IndexDefinition).
    /// </summary>
    internal static async Task<List<string>> ListIdsAsync(SearchClient search, CancellationToken ct)
    {
        var ids = new List<string>();
        string? last = null;
        while (true)
        {
            var searchOptions = new SearchOptions { Size = PageSize };
            searchOptions.Select.Add(KnowledgeIndexSchema.IdField);
            searchOptions.OrderBy.Add(KnowledgeIndexSchema.IdField);
            if (last is not null)
            {
                searchOptions.Filter = $"{KnowledgeIndexSchema.IdField} gt '{last.Replace("'", "''", StringComparison.Ordinal)}'";
            }

            var response = await search.SearchAsync<SearchDocument>("*", searchOptions, ct);
            var count = 0;
            await foreach (var result in response.Value.GetResultsAsync().WithCancellation(ct))
            {
                ids.Add((string)result.Document[KnowledgeIndexSchema.IdField]);
                count++;
            }

            if (count == 0)
            {
                return ids;
            }

            last = ids[^1];
        }
    }
}
