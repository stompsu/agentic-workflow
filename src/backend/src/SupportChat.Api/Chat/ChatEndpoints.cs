using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using SupportChat.Api.Contracts;
using SupportChat.Api.Endpoints;
using SupportChat.Api.Http;
using SupportChat.Core.Options;
using SupportChat.Core.Security;
using SupportChat.Core.Storage;
using SupportChat.Core.Workflows;

namespace SupportChat.Api.Chat;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/sessions/{sessionId}/messages", SendMessageAsync)
            .RequireRateLimiting(ChatRateLimiting.PolicyName);
    }

    /// <summary>
    /// Turn sequence (plan.md section 2): validate -> session (404) -> registry (409 workflow_unavailable) ->
    /// start turn (404/409) -> [ChatTurnRunner: history, session event, run, persist, done/error, release lock].
    /// Everything before the response starts returns ProblemDetails, never SSE.
    /// </summary>
    private static async Task<IResult> SendMessageAsync(
        string sessionId,
        HttpContext http,
        ICurrentUser user,
        IWorkflowRegistry registry,
        ISessionStore sessions,
        IOptions<ChatLimitsOptions> limits,
        ChatTurnRunner runner,
        TimeProvider time)
    {
        var (body, error) = await RequestBody.ReadJsonAsync<SendMessageRequest>(http);
        if (error is not null)
        {
            return error;
        }

        var max = limits.Value.MaxMessageChars;
        var content = body!.Content?.Trim();
        if (string.IsNullOrEmpty(content) || content.Length > max)
        {
            return Problems.Validation("content", $"Content must be between 1 and {max} characters.");
        }

        if (!SessionEndpoints.TryNormalize(sessionId, out var id))
        {
            return Problems.NotFound();
        }

        var userId = user.UserId;
        var ct = http.RequestAborted;

        var session = await sessions.GetAsync(userId, id, ct);
        if (session is null)
        {
            return Problems.NotFound();
        }

        if (!registry.TryGet(session.WorkflowId, out var workflow))
        {
            return Problems.Create(StatusCodes.Status409Conflict, Problems.Codes.WorkflowUnavailable);
        }

        var userMessage = new MessageRecord(
            Guid.CreateVersion7().ToString(), id, ChatRole.User, content, time.GetUtcNow(),
            MessageStatus.Complete, null, null, [], null, null);

        var started = await sessions.TryStartTurnAsync(userId, id, userMessage, ct);
        switch (started)
        {
            case StartTurnResult.Started s:
                return await runner.RunAsync(http, userId, workflow, userMessage, s.Lease);
            case StartTurnResult.Busy:
                return Problems.TurnInProgress();
            default:
                return Problems.NotFound();
        }
    }
}
