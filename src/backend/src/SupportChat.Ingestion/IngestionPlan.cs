namespace SupportChat.Ingestion;

public sealed record PlannedChunk(string Id, string Domain, string Title, string Content, string? SourceUrl, string SourcePath);

/// <summary>Pure, deterministic mapping from source documents to the chunks that must exist in the index.</summary>
public static class IngestionPlan
{
    public static IReadOnlyList<PlannedChunk> Build(IEnumerable<SourceDocument> documents, int chunkTokens, int overlapTokens)
    {
        var planned = new List<PlannedChunk>();
        foreach (var document in documents)
        {
            foreach (var chunk in MarkdownChunker.Chunk(document.Title, document.Markdown, chunkTokens, overlapTokens))
            {
                planned.Add(new PlannedChunk(
                    ChunkIds.For(document.SourcePath, chunk.Index), document.Domain, chunk.Title, chunk.Content, document.SourceUrl, document.SourcePath));
            }
        }

        return planned;
    }
}
