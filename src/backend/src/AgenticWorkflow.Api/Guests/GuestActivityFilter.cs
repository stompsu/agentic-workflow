using System.Globalization;
using AgenticWorkflow.Api.Auth;
using AgenticWorkflow.Api.Http;

namespace AgenticWorkflow.Api.Guests;

/// <summary>Endpoint metadata: skip the guest-activity check (DELETE /api/me/data must work for an ended guest).</summary>
public sealed class SkipGuestActivityCheck
{
    public static readonly SkipGuestActivityCheck Instance = new();
}

/// <summary>
/// Applied to every /api endpoint (ADR 0006 section 8): for a guest caller, an ended guest (document missing, cached
/// 60 s positive / until exp negative) -> 401 guest_expired. Runs after authentication and the per-user rate limiter.
/// </summary>
public sealed class GuestActivityFilter(GuestActivityCache cache, ILogger<GuestActivityFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.GetEndpoint()?.Metadata.GetMetadata<SkipGuestActivityCheck>() is not null
            || !CurrentUser.TryGetUser(http.User, out var guestId, out var kind)
            || kind != UserKind.Guest)
        {
            return await next(context);
        }

        if (!await cache.IsActiveAsync(guestId, TokenExpiry(http), http.RequestAborted))
        {
            logger.LogInformation("Guest request rejected: guest_expired, enduser.id {EndUserId}", guestId);
            return Problems.GuestExpired();
        }

        return await next(context);
    }

    /// <summary>The validated token's <c>exp</c> (the end of the guest identity).</summary>
    public static DateTimeOffset TokenExpiry(HttpContext http) =>
        long.TryParse(http.User.FindFirst("exp")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().AddHours(1);
}
