using Microsoft.Extensions.Options;
using SupportChat.Api.Guests;
using SupportChat.Api.Http;
using SupportChat.Core.Options;
using SupportChat.Core.Storage;

namespace SupportChat.Api.Endpoints;

public sealed record GuestTokenRequest(string? BotCheckToken);

public sealed record GuestTokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt);

public static class GuestEndpoints
{
    /// <summary>
    /// POST /api/guest/token (anonymous; api-contract 2.9). Guard order (ADR 0006 section 8): 404 when disabled, 415 not
    /// JSON, per-client in-memory limiter, global creation cap (in-memory closed flag first, then the Cosmos counter),
    /// guest document, then signing. No refresh endpoint exists. Bot protection (G7) is not part of v1, so
    /// <c>bot_check_failed</c> is never returned and <c>botCheckToken</c> is ignored.
    /// </summary>
    public static void MapGuestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/guest/token", IssueAsync).AllowAnonymous();
    }

    private static async Task<IResult> IssueAsync(
        HttpContext http,
        IOptions<GuestOptions> options,
        GuestClientLimiter clientLimiter,
        GuestGlobalLimits globalLimits,
        IGuestStore guests,
        IGuestTokenService tokens,
        TimeProvider time,
        ILogger<GuestTokenLog> logger)
    {
        var guest = options.Value;
        if (!guest.Enabled)
        {
            return Problems.Create(StatusCodes.Status404NotFound, Problems.Codes.GuestAccessDisabled);
        }

        var (_, error) = await RequestBody.ReadJsonAsync(http, new GuestTokenRequest(null));
        if (error is not null)
        {
            return error;
        }

        // The key is a limiter partition held in memory only; nothing derived from it is logged.
        if (clientLimiter.TryAcquire(GuestClientAddress.KeyFor(http, guest)) is { } clientRetry)
        {
            logger.LogInformation("Guest creation rejected by the {Limiter} limiter", "client");
            return Problems.RateLimited(clientRetry);
        }

        if (await globalLimits.TryAdmitCreationAsync(http.RequestAborted) is { } globalRetry)
        {
            logger.LogInformation("Guest creation rejected by the {Limiter} limiter", "global-create");
            return Problems.RateLimited(globalRetry);
        }

        var now = time.GetUtcNow();
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds((now + guest.MaxLifetime).ToUnixTimeSeconds()); // = the token's exp
        var guestId = GuestOptions.IdPrefix + Guid.NewGuid().ToString("D");

        // The guest document exists BEFORE the token is signed, so a token never exists without its document.
        await guests.CreateAsync(
            new GuestRecord(guestId, now, expiresAt, 0, guest.MaxTurns, 0, guest.MaxSessions), http.RequestAborted);
        var token = await tokens.IssueAsync(guestId, expiresAt, http.RequestAborted);

        logger.LogInformation("Guest created, enduser.id {EndUserId}", guestId);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(new GuestTokenResponse(token.AccessToken, "Bearer", token.ExpiresAt), ApiJson.Options);
    }

    /// <summary>Logger category marker.</summary>
    public sealed class GuestTokenLog;
}
