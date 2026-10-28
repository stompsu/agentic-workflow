using System.Text.Json.Serialization;
using AgenticWorkflow.Core.Storage;
using AgenticWorkflow.Core.Workflows;

namespace AgenticWorkflow.Api.Contracts;

// Response DTOs (api-contract section 2). None of them has userId, ttl, activeTurn, state or workflowMetadata.
public sealed record WorkflowDto(string Id, string DisplayName, string Description);

public sealed record ItemsDto<T>(IReadOnlyList<T> Items);

public sealed record PageDto<T>(IReadOnlyList<T> Items, string? ContinuationToken);

public sealed record SessionDto(string Id, string WorkflowId, string? Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CitationDto(string Ref, string Title, string DocId, string? Url);

public sealed record MessageDto(
    string Id,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    MessageStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MessageKind? Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AgentName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CitationDto>? Citations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorCode);

public sealed record SessionDetailDto(
    string Id,
    string WorkflowId,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool HasMoreMessages,
    IReadOnlyList<MessageDto> Messages);

// Request DTOs
public sealed record CreateSessionRequest(string? WorkflowId, string? Title);

public sealed record SendMessageRequest(string? Content);
