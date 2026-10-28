using Azure.Search.Documents;
using Microsoft.Azure.Cosmos;

namespace AgenticWorkflow.Infrastructure.Azure;

public interface IAzureClientFactory
{
    CosmosClient CreateCosmosClient();

    SearchClient CreateSearchClient(string indexName);
}
