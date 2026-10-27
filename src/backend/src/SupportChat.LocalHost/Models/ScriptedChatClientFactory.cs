using Microsoft.Extensions.AI;
using SupportChat.Core.Models;

namespace SupportChat.LocalHost.Models;

/// <summary>
/// Local-mode replacement of the Foundry <c>ChatClientFactory</c> (ADR 0005 section 4). Every deployment name gets the same
/// stateless <see cref="ScriptedChatClient"/>: behaviour is decided by call shape, never by deployment name.
/// </summary>
public sealed class ScriptedChatClientFactory : IChatClientFactory
{
    private readonly ScriptedChatClient _client;

    public ScriptedChatClientFactory(TimeSpan tokenDelay = default)
    {
        _client = new ScriptedChatClient(tokenDelay);
    }

    public IChatClient Create(string deploymentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentName);
        return _client;
    }
}
