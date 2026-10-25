// OpenAI Responses APIs are marked experimental (OPENAI001) in the pinned prerelease; this file is the single consumer (ADR 0002 section 4).
#pragma warning disable OPENAI001
using System.Collections.Concurrent;
using Azure.AI.Extensions.OpenAI;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SupportChat.Core.Models;
using SupportChat.Core.Options;
using SupportChat.Infrastructure.Azure;

namespace SupportChat.Infrastructure.Models;

/// <summary>
/// The ONLY consumer of AIProjectClient (ADR 0002 section 4). The client is built lazily, on the first
/// <see cref="Create"/>, from Foundry:ProjectEndpoint and a TokenCredential. There is no key path for models.
/// </summary>
public sealed class ChatClientFactory : IChatClientFactory
{
    private readonly Lazy<AIProjectClient> _projectClient;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.Ordinal);

    public ChatClientFactory(IOptions<FoundryOptions> options, IAzureCredentialProvider credentials)
        : this(options, credentials, static (endpoint, credential) => new AIProjectClient(endpoint, credential))
    {
    }

    internal ChatClientFactory(
        IOptions<FoundryOptions> options,
        IAzureCredentialProvider credentials,
        Func<Uri, TokenCredential, AIProjectClient> projectClientFactory)
    {
        _projectClient = new Lazy<AIProjectClient>(
            () =>
            {
                var endpoint = options.Value.ProjectEndpoint;
                if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                {
                    throw new InvalidOperationException("Foundry:ProjectEndpoint is not configured.");
                }

                return projectClientFactory(uri, credentials.Credential);
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>True once the AIProjectClient has been constructed (tests).</summary>
    internal bool ProjectClientCreated => _projectClient.IsValueCreated;

    public IChatClient Create(string deploymentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        return _clients.GetOrAdd(
            deploymentName,
            name => _projectClient.Value.GetProjectOpenAIClient().GetProjectResponsesClient().AsIChatClient(name));
    }
}
