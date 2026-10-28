using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using AgenticWorkflow.Api.Http;

namespace AgenticWorkflow.Api.Auth;

/// <summary>Forbidden (valid token, missing scope or oid) -> 403 ProblemDetails. Challenges go through JwtBearer's OnChallenge.</summary>
public sealed class ProblemDetailsAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden)
        {
            return Problems.WriteAsync(context, StatusCodes.Status403Forbidden, Problems.Codes.Forbidden);
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
