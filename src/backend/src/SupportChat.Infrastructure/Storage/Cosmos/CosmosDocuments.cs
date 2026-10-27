using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using SupportChat.Core.Storage;
using SupportChat.Core.Workflows;

namespace SupportChat.Infrastructure.Storage.Cosmos;

internal static class DocumentTypes
{
    public const string Session = "session";
    public const string Message = "message";
    public const string Guest = "guest";
    public const string GuestCounter = "guestCounter";
    public const string Active = "active";
    public const string Deleting = "deleting";
    public const int SchemaVersion = 1;
}

internal sealed class ActiveTurnDoc
{
    public required string TurnId { get; set; }

    public DateTimeOffset StartedAt { get; set; }
}

/// <summary>Session document (data-model 1.2). Serialized with the shared camelCase System.Text.Json options.</summary>
internal sealed class SessionDoc
{
    public required string Id { get; set; }

    public string Type { get; set; } = DocumentTypes.Session;

    public required string UserId { get; set; }

    public required string SessionId { get; set; }

    public required string WorkflowId { get; set; }

    public string? Title { get; set; }

    public string State { get; set; } = DocumentTypes.Active;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int MessageCount { get; set; }

    public ActiveTurnDoc? ActiveTurn { get; set; }

    public int SchemaVersion { get; set; } = DocumentTypes.SchemaVersion;

    public int? Ttl { get; set; }

    public SessionRecord ToRecord() => new(Id, WorkflowId, Title, CreatedAt, UpdatedAt, MessageCount);
}

/// <summary>Projection returned by the list query (no userId/state/ttl/activeTurn selected).</summary>
internal sealed class SessionListItem
{
    public required string Id { get; set; }

    public required string WorkflowId { get; set; }

    public string? Title { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int MessageCount { get; set; }

    public SessionRecord ToRecord() => new(Id, WorkflowId, Title, CreatedAt, UpdatedAt, MessageCount);
}

internal sealed class MessageDoc
{
    public required string Id { get; set; }

    public string Type { get; set; } = DocumentTypes.Message;

    public required string UserId { get; set; }

    public required string SessionId { get; set; }

    public required string Role { get; set; }

    public required string Content { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public MessageStatus Status { get; set; }

    public MessageKind? Kind { get; set; }

    public string? AgentName { get; set; }

    public List<CitationRecord>? Citations { get; set; }

    public string? ErrorCode { get; set; }

    public JsonObject? WorkflowMetadata { get; set; }

    public int SchemaVersion { get; set; } = DocumentTypes.SchemaVersion;

    public int? Ttl { get; set; }

    public static MessageDoc From(string userId, MessageRecord m, int? ttl) => new()
    {
        Id = m.Id,
        UserId = userId,
        SessionId = m.SessionId,
        Role = m.Role.Value,
        Content = m.Content,
        CreatedAt = m.CreatedAt,
        Status = m.Status,
        Kind = m.Kind,
        AgentName = m.AgentName,
        Citations = m.Citations.Count == 0 ? null : [.. m.Citations],
        ErrorCode = m.ErrorCode,
        WorkflowMetadata = m.WorkflowMetadata,
        Ttl = ttl,
    };

    public MessageRecord ToRecord() => new(
        Id,
        SessionId,
        new ChatRole(Role),
        Content,
        CreatedAt,
        Status,
        Kind,
        AgentName,
        Citations ?? [],
        ErrorCode,
        WorkflowMetadata);
}

/// <summary>Guest document (data-model 1.8): one per guest partition, id "guest". Never returned by the API.</summary>
internal sealed class GuestDoc
{
    public const string DocumentId = "guest";

    public string Id { get; set; } = DocumentId;

    public string Type { get; set; } = DocumentTypes.Guest;

    public required string UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public int TurnCount { get; set; }

    public int MaxTurns { get; set; }

    public int SessionCount { get; set; }

    public int MaxSessions { get; set; }

    public int SchemaVersion { get; set; } = DocumentTypes.SchemaVersion;

    public int? Ttl { get; set; }

    public static GuestDoc From(GuestRecord g, int ttl) => new()
    {
        UserId = g.UserId,
        CreatedAt = g.CreatedAt,
        ExpiresAt = g.ExpiresAt,
        TurnCount = g.TurnCount,
        MaxTurns = g.MaxTurns,
        SessionCount = g.SessionCount,
        MaxSessions = g.MaxSessions,
        Ttl = ttl,
    };

    public GuestRecord ToRecord() => new(UserId, CreatedAt, ExpiresAt, TurnCount, MaxTurns, SessionCount, MaxSessions);
}

/// <summary>Global guest counter window (data-model 1.8) in the reserved partition.</summary>
internal sealed class GuestCounterDoc
{
    public const string PartitionValue = "system-guest-counter";
    public const int TtlSeconds = 172800;

    public required string Id { get; set; }

    public string Type { get; set; } = DocumentTypes.GuestCounter;

    public string UserId { get; set; } = PartitionValue;

    public long Count { get; set; }

    public int Ttl { get; set; } = TtlSeconds;
}
