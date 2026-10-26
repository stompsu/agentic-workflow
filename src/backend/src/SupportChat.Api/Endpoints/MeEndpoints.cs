using Microsoft.Extensions.Options;
using SupportChat.Api.Http;
using SupportChat.Core.Options;
using SupportChat.Core.Security;
using SupportChat.Core.Storage;

namespace SupportChat.Api.Endpoints;

public static class MeEndpoints
{
    public static void MapMeEndpoints(this RouteGroupBuilder api)
    {
        // No rate limiter on purpose (api-contract 2.8); never 404 or 409.
        api.MapDelete("/me/data", DeleteDataAsync);
    }

    /// <summary>
    /// Deletes ALL chat data of the token's oid (api-contract 2.8, data-model 1.7): 204 when done (also when there was
    /// nothing), 503 deletion_incomplete + Retry-After: 1 when the time budget ran out. A request body is ignored.
    /// Unexpected failures reach the exception handler (500 internal_error); progress is kept either way.
    /// </summary>
    private static async Task<IResult> DeleteDataAsync(
        ICurrentUser user, ISessionStore sessions, IOptions<ChatLimitsOptions> limits, ILogger<DeleteDataLog> logger, CancellationToken ct)
    {
        var userId = user.UserId;
        var result = await sessions.DeleteAllUserDataAsync(userId, limits.Value.DeleteUserDataBudget, ct);
        logger.LogInformation("Delete my data finished with {Result}, enduser.id {EndUserId}", result, userId);
        return result == DeleteUserDataResult.Completed ? Results.NoContent() : Problems.DeletionIncomplete();
    }

    /// <summary>Logger category marker for the delete-my-data endpoint.</summary>
    public sealed class DeleteDataLog;
}
