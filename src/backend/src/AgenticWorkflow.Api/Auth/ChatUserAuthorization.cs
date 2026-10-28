using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using AgenticWorkflow.Api.Guests;
using AgenticWorkflow.Core.Options;

namespace AgenticWorkflow.Api.Auth;

/// <summary>
/// One requirement for both the ChatUser policy and the fallback policy (replaces RequireScope + RequireAssertion):
/// a valid identity (<see cref="CurrentUser.TryGetUser"/>), then per kind: Entra needs one of <see cref="Scopes"/> and an
/// issuer other than the guest issuer; a guest needs guests enabled and the guest issuer, and has no scope.
/// </summary>
public sealed class ChatUserRequirement(IReadOnlyList<string> scopes) : IAuthorizationRequirement
{
    public IReadOnlyList<string> Scopes { get; } = scopes;
}

public sealed class ChatUserAuthorizationHandler(IOptions<GuestOptions> guestOptions) : AuthorizationHandler<ChatUserRequirement>
{
    private const string ScopeClaim = "scp";
    private const string ScopeClaimLong = "http://schemas.microsoft.com/identity/claims/scope";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ChatUserRequirement requirement)
    {
        if (!CurrentUser.TryGetUser(context.User, out _, out var kind))
        {
            return Task.CompletedTask;
        }

        var guest = guestOptions.Value;
        var issuer = context.User.FindFirst("iss")?.Value;
        var isGuestIssuer = guest.Issuer is { Length: > 0 } && string.Equals(issuer, guest.Issuer, StringComparison.Ordinal);

        if (kind == UserKind.Guest)
        {
            if (guest.Enabled && isGuestIssuer)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }

        if (!isGuestIssuer && HasRequiredScope(context, requirement.Scopes))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    /// <summary>Same claim types and semantics as Microsoft.Identity.Web's RequireScope: any required scope, space-separated.</summary>
    private static bool HasRequiredScope(AuthorizationHandlerContext context, IReadOnlyList<string> required)
    {
        var granted = context.User.Claims
            .Where(c => c.Type is ScopeClaim or ScopeClaimLong)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);
        return required.Any(granted.Contains);
    }
}
