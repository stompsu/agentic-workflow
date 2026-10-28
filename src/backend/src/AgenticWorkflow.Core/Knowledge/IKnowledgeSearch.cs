namespace AgenticWorkflow.Core.Knowledge;

public interface IKnowledgeSearch
{
    Task<IReadOnlyList<KnowledgeChunk>> SearchAsync(string domain, string query, int top, CancellationToken cancellationToken);
}

public sealed record KnowledgeChunk(string Id, string Domain, string Title, string Content, string? SourceUrl, double? RerankerScore);
