using Azure.Search.Documents;
using Microsoft.Azure.Cosmos;

namespace SupportChat.Infrastructure.Azure;

public interface IAzureClientFactory
{
    CosmosClient CreateCosmosClient();

    SearchClient CreateSearchClient(string indexName);
}
