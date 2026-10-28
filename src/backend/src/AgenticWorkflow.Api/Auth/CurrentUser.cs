using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Identity.Web;
using AgenticWorkflow.Api.Guests;
using AgenticWorkflow.Core.Security;

namespace AgenticWorkflow.Api.Auth;

public enum UserKind
{
    Entra,
    Guest,
}

/// <summary>UserId = the validated token's oid claim. Never read from route, query or body.</summary>
public sealed partial class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string UserId =>
        accessor.HttpContext?.User is { } principal && TryGetUser(principal, out var id, out _)
            ? id
            : throw new InvalidOperationException("The request has no valid oid claim.");

    public bool IsGuest =>
        accessor.HttpContext?.User is { } principal && TryGetUser(principal, out _, out var kind) && kind == UserKind.Guest;

    /// <summary>
    /// Single source of truth for the caller's identity (ADR 0006 section 5.3). Exactly one authenticated identity. The
    /// kind comes ONLY from what the validating scheme stamped (<c>AuthenticationType == "guest"</c>), never from the id:
    /// a guest identity needs a <c>guest-&lt;guid v4&gt;</c> oid, any other identity needs a non-empty GUID oid. A GUID oid
    /// on a guest identity and a guest-shaped oid on an Entra identity both fail.
    /// </summary>
    public static bool TryGetUser(ClaimsPrincipal principal, out string userId, out UserKind kind)
    {
        userId = string.Empty;
        kind = UserKind.Entra;

        var identities = principal.Identities.Where(i => i.IsAuthenticated).Take(2).ToList();
        if (identities.Count != 1)
        {
            return false;
        }

        var oid = principal.GetObjectId();
        if (string.IsNullOrWhiteSpace(oid))
        {
            return false;
        }

        if (string.Equals(identities[0].AuthenticationType, GuestAuth.AuthenticationType, StringComparison.Ordinal))
        {
            if (!GuestId().IsMatch(oid))
            {
                return false;
            }

            userId = oid;
            kind = UserKind.Guest;
            return true;
        }

        if (!Guid.TryParse(oid, out var guid) || guid == Guid.Empty)
        {
            return false;
        }

        userId = guid.ToString("D");
        return true;
    }

    /// <summary>Wrapper kept for existing callers.</summary>
    public static bool TryGetUserId(ClaimsPrincipal principal, out string userId) => TryGetUser(principal, out userId, out _);

    [GeneratedRegex("^guest-[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex GuestId();
}
