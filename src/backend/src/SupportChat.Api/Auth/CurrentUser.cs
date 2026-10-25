using System.Security.Claims;
using Microsoft.Identity.Web;
using SupportChat.Core.Security;

namespace SupportChat.Api.Auth;

/// <summary>UserId = the validated token's oid claim (lowercase GUID). Never read from route, query or body.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string UserId =>
        accessor.HttpContext?.User is { } principal && TryGetUserId(principal, out var id)
            ? id
            : throw new InvalidOperationException("The request has no valid oid claim.");

    public static bool TryGetUserId(ClaimsPrincipal principal, out string userId)
    {
        userId = string.Empty;
        var oid = principal.GetObjectId();
        if (string.IsNullOrWhiteSpace(oid) || !Guid.TryParse(oid, out var guid) || guid == Guid.Empty)
        {
            return false;
        }

        userId = guid.ToString("D");
        return true;
    }
}
