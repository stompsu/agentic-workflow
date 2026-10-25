using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace SupportChat.Core.Workflows;

/// <summary>
/// A workflow type (ADR 0002). Definitions are singletons; all per-turn state lives inside <see cref="RunAsync"/>.
/// </summary>
public interface IWorkflowDefinition
{
    /// <summary>Stable, url-safe, lowercase kebab-case. Persisted in sessions; never rename.</summary>
    string Id { get; }

    string DisplayName { get; }

    string Description { get; }

    /// <summary>
    /// Runs one turn. Must honor the cancellation token. Must not persist anything.
    /// v1 protocol: exactly one <see cref="AgentChanged"/>, then at least one <see cref="TokenDelta"/>, then
    /// <see cref="CitationFound"/>*, then <see cref="TurnCompleted"/>; or <see cref="TurnFailed"/> / throw at any point.
    /// </summary>
    IAsyncEnumerable<ChatStreamEvent> RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken);
}

public interface IWorkflowRegistry
{
    IReadOnlyList<WorkflowDescriptor> List();

    bool TryGet(string workflowId, [NotNullWhen(true)] out IWorkflowDefinition? workflow);
}

public sealed record WorkflowDescriptor(string Id, string DisplayName, string Description);

public sealed record WorkflowRunRequest(
    string UserId,
    string SessionId,
    string TurnId,
    string UserMessage,
    IReadOnlyList<HistoryMessage> History);

public sealed record HistoryMessage(
    ChatRole Role,
    string Content,
    string? AgentName,
    MessageKind? Kind,
    JsonObject? WorkflowMetadata,
    DateTimeOffset CreatedAt);

/// <summary>Serialized camelCase ("answer", "clarification").</summary>
public enum MessageKind
{
    Answer,
    Clarification,
}

public abstract record ChatStreamEvent;

public sealed record AgentChanged(string Name) : ChatStreamEvent;

public sealed record TokenDelta(string Text) : ChatStreamEvent;

public sealed record CitationFound(string Ref, string Title, string DocId, string? Url) : ChatStreamEvent;

public sealed record TurnCompleted(MessageKind Kind = MessageKind.Answer, JsonObject? Metadata = null) : ChatStreamEvent;

public sealed record TurnFailed(string Code) : ChatStreamEvent;

/// <summary>Thrown by workflows to fail a turn with one of the api-contract 4.4 error codes.</summary>
public sealed class WorkflowException(string code, string? message = null, Exception? inner = null)
    : Exception(message ?? code, inner)
{
    public string Code { get; } = code;
}
