using Azure.Search.Documents.Indexes.Models;
using AgenticWorkflow.Infrastructure.Search;

namespace AgenticWorkflow.Ingestion;

/// <summary>The index `agentwf-knowledge` as code (data-model section 2). Creating/updating is idempotent.</summary>
public static class IndexDefinition
{
    public static SearchIndex Build(string indexName, IngestionOptions options)
    {
        var fields = new List<SearchField>
        {
            new(KnowledgeIndexSchema.IdField, SearchFieldDataType.String) { IsKey = true, IsFilterable = true, IsSortable = true },
            new(KnowledgeIndexSchema.DomainField, SearchFieldDataType.String) { IsFilterable = true, IsFacetable = true },
            new(KnowledgeIndexSchema.TitleField, SearchFieldDataType.String) { IsSearchable = true, AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
            new(KnowledgeIndexSchema.ContentField, SearchFieldDataType.String) { IsSearchable = true, AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
            new(KnowledgeIndexSchema.VectorField, SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true,
                IsHidden = true,
                IsStored = false,
                VectorSearchDimensions = options.EmbeddingDimensions,
                VectorSearchProfileName = KnowledgeIndexSchema.VectorProfileName,
            },
            new(KnowledgeIndexSchema.SourceUrlField, SearchFieldDataType.String),
        };

        var index = new SearchIndex(indexName, fields)
        {
            VectorSearch = new VectorSearch(),
            SemanticSearch = new SemanticSearch { DefaultConfigurationName = KnowledgeIndexSchema.SemanticConfigurationName },
        };

        index.VectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration(KnowledgeIndexSchema.AlgorithmName)
        {
            Parameters = new HnswParameters { Metric = VectorSearchAlgorithmMetric.Cosine, M = 4, EfConstruction = 400, EfSearch = 500 },
        });

        var profile = new VectorSearchProfile(KnowledgeIndexSchema.VectorProfileName, KnowledgeIndexSchema.AlgorithmName);
        if (!string.IsNullOrWhiteSpace(options.VectorizerResourceUri))
        {
            // The integrated vectorizer embeds the API's query text inside Search. Auth: the Search service's managed
            // identity (needs "Cognitive Services OpenAI User" on the model resource); no key is configured here.
            index.VectorSearch.Vectorizers.Add(new AzureOpenAIVectorizer(KnowledgeIndexSchema.VectorizerName)
            {
                Parameters = new AzureOpenAIVectorizerParameters
                {
                    ResourceUri = new Uri(options.VectorizerResourceUri),
                    DeploymentName = options.VectorizerDeployment ?? options.EmbeddingDeployment,
                    ModelName = options.VectorizerModelName,
                },
            });
            profile.VectorizerName = KnowledgeIndexSchema.VectorizerName;
        }

        index.VectorSearch.Profiles.Add(profile);

        index.SemanticSearch.Configurations.Add(new SemanticConfiguration(
            KnowledgeIndexSchema.SemanticConfigurationName,
            new SemanticPrioritizedFields
            {
                TitleField = new SemanticField(KnowledgeIndexSchema.TitleField),
                ContentFields = { new SemanticField(KnowledgeIndexSchema.ContentField) },
            }));

        return index;
    }
}
