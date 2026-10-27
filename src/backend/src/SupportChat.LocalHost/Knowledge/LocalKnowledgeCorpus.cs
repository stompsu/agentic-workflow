using SupportChat.Core.Health;
using SupportChat.Infrastructure.Search.Source;

namespace SupportChat.LocalHost.Knowledge;

/// <summary>
/// The repo knowledge/ files, chunked with the ingestion defaults (800 / 80 tokens) so chunk ids equal the Azure index
/// ids. Loaded once; fails if the folder is missing or yields no chunk (ADR 0005 section 3).
/// </summary>
public sealed class LocalKnowledgeCorpus(string root, ILogger<LocalKnowledgeCorpus> logger)
{
    public const int ChunkTokens = 800;
    public const int ChunkOverlapTokens = 80;

    /// <summary>The SupportTicket domains (knowledge folder names).</summary>
    public static readonly string[] SupportTicketDomains = ["policy", "claims", "health"];

    private readonly Lazy<IReadOnlyList<PlannedChunk>> _chunks = new(() => Load(root, logger), LazyThreadSafetyMode.ExecutionAndPublication);

    public IReadOnlyList<PlannedChunk> Chunks => _chunks.Value;

    public bool IsLoaded => _chunks.IsValueCreated;

    private static IReadOnlyList<PlannedChunk> Load(string root, ILogger logger)
    {
        if (!Directory.Exists(root))
        {
            throw new InvalidOperationException("LocalMode:Knowledge:SourcePath does not exist.");
        }

        var documents = KnowledgeSource.Read(root, message => logger.LogWarning("{Message}", message));
        var chunks = IngestionPlan.Build(documents, ChunkTokens, ChunkOverlapTokens);
        if (chunks.Count == 0)
        {
            throw new InvalidOperationException("The local knowledge folder yielded no chunks.");
        }

        foreach (var domain in SupportTicketDomains.Where(d => chunks.All(c => c.Domain != d)))
        {
            logger.LogWarning("The local knowledge corpus has no chunks for the SupportTicket domain '{Domain}'.", domain);
        }

        logger.LogInformation("Loaded {Count} local knowledge chunks from {Documents} documents.", chunks.Count, documents.Count);
        return chunks;
    }
}

/// <summary>Ready once the corpus is loaded.</summary>
public sealed class LocalReadinessCheck(LocalKnowledgeCorpus corpus) : IReadinessCheck
{
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => Task.FromResult(corpus.IsLoaded);
}

/// <summary>Loads the corpus during host start so a missing / empty knowledge folder fails startup.</summary>
public sealed class LocalKnowledgeLoader(LocalKnowledgeCorpus corpus) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = corpus.Chunks;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
