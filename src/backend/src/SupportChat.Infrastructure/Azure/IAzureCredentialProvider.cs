using Azure.Core;

namespace SupportChat.Infrastructure.Azure;

public interface IAzureCredentialProvider
{
    /// <summary>Singleton credential used for AIProjectClient and for Managed Identity mode of Cosmos/Search.</summary>
    TokenCredential Credential { get; }
}
