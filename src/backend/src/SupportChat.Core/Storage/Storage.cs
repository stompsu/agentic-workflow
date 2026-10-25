using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using SupportChat.Core.Workflows;

namespace SupportChat.Core.Storage;

/// <summary>
/// Turn lock held by one in-flight turn. LeaseId is the turn id (= WorkflowRunRequest.TurnId), stored in the session
/// document as activeTurn.turnId. AssistantMessageId is generated at turn start and used for done/failed/cancelled.
/// </summary>
public sealed record TurnLease(string SessionId, string LeaseId, string AssistantMessageId, DateTimeOffset StartedAt);

/// <summary>No userId, ttl, activeTurn, state, type, schemaVersion: callers already know the user.</summary>
public sealed record SessionRecord(
    string Id, string WorkflowId, string? Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount);

/// <summary>Serialized camelCase.</summary>
public enum MessageStatus
{
    Complete,
    Cancelled,
    Failed,
}

public sealed record CitationRecord(string Ref, string Title, string DocId, string? Url);

public sealed record MessageRecord(
    string Id,
    string SessionId,
    ChatRole Role,
    string Content,
    DateTimeOffset CreatedAt,
    MessageStatus Status,
    MessageKind? Kind,
    string? AgentName,
    IReadOnlyList<CitationRecord> Citations,
    string? ErrorCode,
    JsonObject? WorkflowMetadata);

/// <summary>A null continuation token means last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? ContinuationToken);

public abstract record StartTurnResult
{
    public sealed record Started(TurnLease Lease) : StartTurnResult;

    /// <summary>Missing, foreign, or state = deleting -> 404 not_found.</summary>
    public sealed record NotFound : StartTurnResult;

    /// <summary>-> 409 turn_in_progress.</summary>
    public sealed record Busy : StartTurnResult;
}

public enum CompleteTurnResult
{
    Completed,

    /// <summary>Session document missing: nothing written, no exception.</summary>
    SessionGone,

    /// <summary>activeTurn.turnId != lease.LeaseId (stale lock taken over by a turn or a DELETE): nothing written.</summary>
    LeaseLost,
}

public enum DeleteSessionResult
{
    Deleted,
    NotFound,

    /// <summary>-> 409 turn_in_progress.</summary>
    Busy,
}

/// <summary>Thrown by <see cref="ISessionStore.ListAsync"/> when the opaque continuation token is invalid (-> 400).</summary>
public sealed class InvalidContinuationTokenException : Exception
{
    public InvalidContinuationTokenException()
        : base("The continuation token is invalid.")
    {
    }

    public InvalidContinuationTokenException(Exception inner)
        : base("The continuation token is invalid.", inner)
    {
    }
}

public interface ISessionStore
{
    Task<SessionRecord> CreateAsync(string userId, string workflowId, string? title, CancellationToken ct);

    /// <summary>Null for missing, foreign and deleting sessions.</summary>
    Task<SessionRecord?> GetAsync(string userId, string sessionId, CancellationToken ct);

    /// <summary>Newest activity first; excludes deleting sessions.</summary>
    Task<Page<SessionRecord>> ListAsync(string userId, int pageSize, string? continuationToken, CancellationToken ct);

    /// <summary>Lock-then-delete sequence (data-model 1.5). Resumes a session already in state deleting.</summary>
    Task<DeleteSessionResult> DeleteAsync(string userId, string sessionId, CancellationToken ct);

    /// <summary>Persists the user message, sets the lock, sets title from the message if null (one batch).</summary>
    Task<StartTurnResult> TryStartTurnAsync(string userId, string sessionId, MessageRecord userMessage, CancellationToken ct);

    /// <summary>Writes the assistant message (if any) and releases the lock.</summary>
    Task<CompleteTurnResult> CompleteTurnAsync(string userId, TurnLease lease, MessageRecord? assistantMessage, CancellationToken ct);
}

public interface IMessageStore
{
    /// <summary>Most recent <paramref name="max"/> messages, ascending by createdAt. HasMore when older ones exist.</summary>
    Task<(IReadOnlyList<MessageRecord> Items, bool HasMore)> GetRecentAsync(string userId, string sessionId, int max, CancellationToken ct);

    /// <summary>
    /// Excludes excludeMessageId (the current user message); applies pairing + marker stripping (data-model 1.6 steps 1-3).
    /// Does NOT trim by tokens.
    /// </summary>
    Task<IReadOnlyList<MessageRecord>> GetHistoryAsync(string userId, string sessionId, string excludeMessageId, int maxMessages, CancellationToken ct);
}
