using Microsoft.Extensions.AI;
using AgenticWorkflow.Core.Storage;
using AgenticWorkflow.Core.Workflows;

namespace AgenticWorkflow.Api.Contracts;

/// <summary>Record -> DTO mapping. The only place that decides what leaves the API.</summary>
public static class Mappers
{
    public static WorkflowDto ToDto(this WorkflowDescriptor w) => new(w.Id, w.DisplayName, w.Description);

    public static SessionDto ToDto(this SessionRecord s) => new(s.Id, s.WorkflowId, s.Title, s.CreatedAt, s.UpdatedAt);

    public static PageDto<SessionDto> ToDto(this Page<SessionRecord> page) =>
        new(page.Items.Select(ToDto).ToList(), page.ContinuationToken);

    public static SessionDetailDto ToDetailDto(this SessionRecord s, IReadOnlyList<MessageRecord> messages, bool hasMore) =>
        new(s.Id, s.WorkflowId, s.Title, s.CreatedAt, s.UpdatedAt, hasMore, messages.Select(ToDto).ToList());

    public static MessageDto ToDto(this MessageRecord m)
    {
        var isAssistant = m.Role == ChatRole.Assistant;
        return new MessageDto(
            m.Id,
            m.Role.Value,
            m.Content,
            m.CreatedAt,
            m.Status,
            isAssistant ? m.Kind ?? MessageKind.Answer : null,
            isAssistant ? m.AgentName : null,
            isAssistant ? m.Citations.Select(c => new CitationDto(c.Ref, c.Title, c.DocId, c.Url)).ToList() : null,
            m.Status == MessageStatus.Failed ? m.ErrorCode : null);
    }
}
