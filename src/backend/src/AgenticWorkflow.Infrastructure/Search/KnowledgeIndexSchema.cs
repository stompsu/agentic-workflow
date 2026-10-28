namespace AgenticWorkflow.Infrastructure.Search;

/// <summary>
/// Names shared by the query side (<see cref="AzureSearchKnowledgeSearch"/>) and the index definition owned by
/// AgenticWorkflow.Ingestion (data-model section 2). One place so the two cannot drift.
/// </summary>
public static class KnowledgeIndexSchema
{
    public const string IdField = "id";
    public const string DomainField = "domain";
    public const string TitleField = "title";
    public const string ContentField = "content";
    public const string VectorField = "contentVector";
    public const string SourceUrlField = "sourceUrl";

    public const string VectorProfileName = "vector-profile";
    public const string AlgorithmName = "hnsw-cosine";
    public const string VectorizerName = "aoai-vectorizer";
    public const string SemanticConfigurationName = "semantic-default";

    /// <summary>Candidates for the vector leg of the hybrid query (data-model 2.4).</summary>
    public const int VectorNearestNeighbors = 50;

    public const int MaxTop = 50;

    public static readonly string[] SelectFields = [IdField, DomainField, TitleField, ContentField, SourceUrlField];
}
