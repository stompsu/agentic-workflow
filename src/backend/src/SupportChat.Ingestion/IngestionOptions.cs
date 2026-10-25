namespace SupportChat.Ingestion;

/// <summary>Section "Ingestion". The admin key is used here only and is never given to the API (data-model 2.6).</summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>Folder with <c>&lt;domain&gt;/**/*.md</c>.</summary>
    public string SourcePath { get; set; } = "knowledge";

    /// <summary>Embedding model deployment in the Foundry project (TokenCredential only).</summary>
    public string? EmbeddingDeployment { get; set; }

    public int EmbeddingDimensions { get; set; } = 1536;

    public int ChunkTokens { get; set; } = 800;

    public int ChunkOverlapTokens { get; set; } = 80;

    /// <summary>Key mode only (Development/Dev/Test), secret. Production uses Managed Identity / RBAC.</summary>
    public string? SearchAdminKey { get; set; }

    /// <summary>Resource of the Azure OpenAI / Foundry model used by the index's integrated vectorizer.</summary>
    public string? VectorizerResourceUri { get; set; }

    /// <summary>Defaults to <see cref="EmbeddingDeployment"/>.</summary>
    public string? VectorizerDeployment { get; set; }

    public string VectorizerModelName { get; set; } = "text-embedding-3-small";

    public int EmbeddingBatchSize { get; set; } = 16;
}
