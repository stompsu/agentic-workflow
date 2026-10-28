using System.Globalization;
using AgenticWorkflow.Api.Contracts;
using AgenticWorkflow.Api.Guests;
using AgenticWorkflow.Api.Http;
using AgenticWorkflow.Core.Security;
using AgenticWorkflow.Core.Storage;
using AgenticWorkflow.Core.Workflows;

namespace AgenticWorkflow.Api.Endpoints;

public static class SessionEndpoints
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;
    public const int MaxTitleLength = 200;
    public const int DetailMessageCount = 100;

    public static void MapSessionEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/sessions", CreateAsync);
        api.MapGet("/sessions", ListAsync);
        api.MapGet("/sessions/{sessionId}", GetAsync);
        api.MapDelete("/sessions/{sessionId}", DeleteAsync);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http, ICurrentUser user, IWorkflowRegistry registry, ISessionStore sessions,
        GuestWorkflowPolicy guestWorkflows, GuestActivityCache guestActivity, CancellationToken ct)
    {
        var (body, error) = await RequestBody.ReadJsonAsync<CreateSessionRequest>(http);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>();
        var workflowId = body!.WorkflowId?.Trim();
        if (string.IsNullOrEmpty(workflowId))
        {
            errors["workflowId"] = ["WorkflowId is required."];
        }
        else if (!registry.TryGet(workflowId, out _) || (user.IsGuest && !guestWorkflows.IsAllowed(workflowId)))
        {
            // A workflow not enabled for guests is reported exactly like an unknown id (api-contract 2.9).
            errors["workflowId"] = ["WorkflowId is not a registered workflow."];
        }

        string? title = null;
        if (body.Title is not null)
        {
            title = body.Title.Trim();
            if (title.Length is 0 or > MaxTitleLength)
            {
                errors["title"] = [$"Title must be between 1 and {MaxTitleLength} characters."];
            }
        }

        if (errors.Count > 0)
        {
            return Problems.Create(StatusCodes.Status400BadRequest, Problems.Codes.ValidationFailed, errors);
        }

        // One transaction for a guest: session create + conditional patch of the guest document (sessionCount < maxSessions).
        var created = await sessions.CreateAsync(user.UserId, workflowId!, title, user.IsGuest, ct);
        switch (created)
        {
            case CreateSessionResult.Created c:
                return Results.Json(c.Session.ToDto(), ApiJson.Options, statusCode: StatusCodes.Status201Created)
                    .WithLocation($"/api/sessions/{c.Session.Id}");
            case CreateSessionResult.GuestQuotaExceeded:
                return Problems.GuestQuotaExceeded();
            default:
                guestActivity.MarkEnded(user.UserId, GuestActivityFilter.TokenExpiry(http));
                return Problems.GuestExpired();
        }
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, ICurrentUser user, ISessionStore sessions, string? pageSize, string? continuationToken, CancellationToken ct)
    {
        var size = DefaultPageSize;
        if (pageSize is not null
            && (!int.TryParse(pageSize, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is < 1 or > MaxPageSize))
        {
            return Problems.Validation("pageSize", $"PageSize must be between 1 and {MaxPageSize}.");
        }

        try
        {
            var page = await sessions.ListAsync(user.UserId, size, string.IsNullOrEmpty(continuationToken) ? null : continuationToken, ct);
            return Results.Json(page.ToDto(), ApiJson.Options);
        }
        catch (InvalidContinuationTokenException)
        {
            return Problems.Validation("continuationToken", "The continuation token is invalid.");
        }
    }

    private static async Task<IResult> GetAsync(
        string sessionId, ICurrentUser user, ISessionStore sessions, IMessageStore messages, CancellationToken ct)
    {
        if (!TryNormalize(sessionId, out var id))
        {
            return Problems.NotFound();
        }

        var userId = user.UserId;
        var session = await sessions.GetAsync(userId, id, ct);
        if (session is null)
        {
            return Problems.NotFound();
        }

        var (items, hasMore) = await messages.GetRecentAsync(userId, id, DetailMessageCount, ct);
        return Results.Json(session.ToDetailDto(items, hasMore), ApiJson.Options);
    }

    private static async Task<IResult> DeleteAsync(string sessionId, ICurrentUser user, ISessionStore sessions, CancellationToken ct)
    {
        if (!TryNormalize(sessionId, out var id))
        {
            return Problems.NotFound();
        }

        return await sessions.DeleteAsync(user.UserId, id, ct) switch
        {
            DeleteSessionResult.Deleted => Results.NoContent(),
            DeleteSessionResult.Busy => Problems.TurnInProgress(),
            _ => Problems.NotFound(),
        };
    }

    /// <summary>Session ids are lowercase GUIDs; anything else cannot exist, so it is "not found".</summary>
    internal static bool TryNormalize(string sessionId, out string id)
    {
        if (Guid.TryParse(sessionId, out var guid))
        {
            id = guid.ToString("D");
            return true;
        }

        id = string.Empty;
        return false;
    }

    private static IResult WithLocation(this IResult inner, string location) => new LocationResult(inner, location);

    private sealed class LocationResult(IResult inner, string location) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.Location = location;
            return inner.ExecuteAsync(httpContext);
        }
    }
}
