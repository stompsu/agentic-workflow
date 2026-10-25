using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using SupportChat.Api.Http;
using SupportChat.Core.Security;

namespace SupportChat.Api.Auth;

public static class AuthExtensions
{
    public const string ChatUserPolicy = "ChatUser";

    /// <summary>
    /// JWT bearer validation against the Entra External ID tenant (ADR 0001). Policy ChatUser: authenticated user,
    /// required scope, and a valid oid claim. 401/403 bodies are ProblemDetails with stable codes.
    /// </summary>
    public static IServiceCollection AddChatAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(configuration.GetSection("AzureAd"));

        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var previous = options.Events.OnChallenge;
            options.Events.OnChallenge = async context =>
            {
                await previous(context);
                context.HandleResponse();
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await Problems.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, Problems.Codes.Unauthorized);
            };

            var previousForbidden = options.Events.OnForbidden;
            options.Events.OnForbidden = async context =>
            {
                await previousForbidden(context);
                await Problems.WriteAsync(context.HttpContext, StatusCodes.Status403Forbidden, Problems.Codes.Forbidden);
            };
        });

        var scopes = configuration.GetSection("Auth:RequiredScopes").Get<string[]>() is { Length: > 0 } configured
            ? configured
            : ["access_as_user"];

        services.AddAuthorizationBuilder()
            .AddPolicy(ChatUserPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireScope(scopes)
                .RequireAssertion(context => CurrentUser.TryGetUserId(context.User, out _)))
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireScope(scopes)
                .RequireAssertion(context => CurrentUser.TryGetUserId(context.User, out _))
                .Build());

        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsAuthorizationResultHandler>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        return services;
    }
}
