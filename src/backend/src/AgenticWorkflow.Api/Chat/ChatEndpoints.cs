using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using AgenticWorkflow.Api.Contracts;
using AgenticWorkflow.Api.Endpoints;
using AgenticWorkflow.Api.Guests;
using AgenticWorkflow.Api.Http;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Core.Security;
using AgenticWorkflow.Core.Storage;
using AgenticWorkflow.Core.Workflows;

namespace AgenticWorkflow.Api.Chat;

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
        TimeProvider time,
        GuestActivityCache guestActivity,
        GuestGlobalLimits guestLimits,
        GuestWorkflowPolicy guestWorkflows,
        IGuestStore guests,
        ILogger<GuestTurnLog> logger)
    {
        var isGuest = user.IsGuest;
        var guestExpiry = isGuest ? GuestActivityFilter.TokenExpiry(http) : default;

        // Guest guard order (ADR 0006 section 8): per-minute limiter (middleware) and the activity check (endpoint filter)
        // have run; a cached "turn quota exhausted" answers before any validation or Cosmos work.
        if (isGuest && guestActivity.IsTurnQuotaExhausted(user.UserId))
        {
            logger.LogInformation("Guest turn rejected: guest_quota_exceeded (cached), enduser.id {EndUserId}", user.UserId);
            return Problems.GuestQuotaExceeded();
        }

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
            return await NotFoundOrGuestExpiredAsync(isGuest, userId, guests, ct);
        }

        if (!registry.TryGet(session.WorkflowId, out var workflow) || (isGuest && !guestWorkflows.IsAllowed(session.WorkflowId)))
        {
            return Problems.Create(StatusCodes.Status409Conflict, Problems.Codes.WorkflowUnavailable);
        }

        if (isGuest)
        {
            // Global guest-turn caps: in-memory closed flags first (no Cosmos call while closed), then the counters.
            if (await guestLimits.TryAdmitTurnAsync(ct) is { } retry)
            {
                logger.LogInformation("Guest turn rejected by the {Limiter} limiter", "global-turn");
                return Problems.RateLimited(retry);
            }
        }

        var userMessage = new MessageRecord(
            Guid.CreateVersion7().ToString(), id, ChatRole.User, content, time.GetUtcNow(),
            MessageStatus.Complete, null, null, [], null, null);

        // A guest's turn quota is consumed inside this call's transaction (guest-document conditional patch).
        var started = await sessions.TryStartTurnAsync(userId, id, userMessage, isGuest, ct);
        switch (started)
        {
            case StartTurnResult.Started s:
                return await runner.RunAsync(http, userId, workflow, userMessage, s.Lease);
            case StartTurnResult.Busy:
                return Problems.TurnInProgress();
            case StartTurnResult.GuestQuotaExceeded:
                guestActivity.MarkTurnQuotaExhausted(userId, guestExpiry);
                logger.LogInformation("Guest turn rejected: guest_quota_exceeded, enduser.id {EndUserId}", userId);
                return Problems.GuestQuotaExceeded();
            case StartTurnResult.GuestExpired:
                guestActivity.MarkEnded(userId, guestExpiry);
                logger.LogInformation("Guest turn rejected: guest_expired, enduser.id {EndUserId}", userId);
                return Problems.GuestExpired();
            default:
                return await NotFoundOrGuestExpiredAsync(isGuest, userId, guests, ct);
        }
    }

    /// <summary>
    /// 404 for a normal caller. For a guest a missing session may mean the guest ended (DELETE /api/me/data on another
    /// replica), so a fresh uncached check turns that into 401 guest_expired (writes fail immediately, api-contract 2.9).
    /// </summary>
    private static async Task<IResult> NotFoundOrGuestExpiredAsync(
        bool isGuest, string userId, IGuestStore guests, CancellationToken ct)
    {
        if (isGuest && await guests.GetAsync(userId, ct) is null)
        {
            // A plain read miss is not cached long (a later request re-checks within the activity-cache duration).
            return Problems.GuestExpired();
        }

        return Problems.NotFound();
    }

    /// <summary>Logger category marker.</summary>
    public sealed class GuestTurnLog;
}
