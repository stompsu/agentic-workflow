using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using SupportChat.Core.Options;

namespace SupportChat.Infrastructure.Storage.Cosmos;

/// <summary>Resolves the single chat container (partition key /userId). Creating the reference makes no network call.</summary>
public sealed class CosmosChatContainer
{
    public CosmosChatContainer(CosmosClient client, IOptions<CosmosOptions> options)
        : this(client.GetContainer(options.Value.DatabaseName, options.Value.ContainerName))
    {
    }

    public CosmosChatContainer(Container container) => Container = container;

    public Container Container { get; }
}
