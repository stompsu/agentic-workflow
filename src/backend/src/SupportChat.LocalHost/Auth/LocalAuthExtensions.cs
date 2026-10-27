using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using SupportChat.Api.Http;

namespace SupportChat.LocalHost.Auth;

public static partial class LocalAuthExtensions
{
    public const string TokenRoute = "/local-auth/token";

    /// <summary>The only <c>Origin</c> accepted by the token endpoint (the Vite dev server, ADR 0005 section 5).</summary>
    public const string AllowedOrigin = "http://localhost:5173";

    /// <summary>The "addJwtBearer" callback of <c>ApiHost.ConfigureServices</c> for LocalMode:Auth=Local.</summary>
    public static IServiceCollection AddLocalJwtBearer(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new LocalTokenIssuer(
            sp.GetRequiredService<TimeProvider>(), LocalModeOptions.From(sp.GetRequiredService<IConfiguration>()).TokenLifetime));
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, LocalJwtBearerConfigure>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, _ => { });

        // The Entra scheme registers the handler behind RequireScope (the ChatUser policy); without it the scope requirement can never succeed.
        services.AddRequiredScopeAuthorization();
        return services;
    }

    /// <summary>Maps POST /local-auth/token (anonymous, outside /api) when the issuer is registered (Auth=Local).</summary>
    public static void MapLocalAuth(this WebApplication app)
    {
        if (app.Services.GetService<LocalTokenIssuer>() is null)
        {
            return;
        }

        app.MapPost(TokenRoute, IssueAsync).AllowAnonymous();
    }

    // Request and response bodies of this endpoint are never logged (and nothing here logs at all).
    private static async Task<IResult> IssueAsync(HttpContext http, LocalTokenIssuer issuer)
    {
        if (http.Request.Headers.TryGetValue("Origin", out var origins)
            && (origins.Count != 1 || !string.Equals(origins[0], AllowedOrigin, StringComparison.Ordinal)))
        {
            return Problems.Create(StatusCodes.Status403Forbidden, Problems.Codes.Forbidden);
        }

        var (body, error) = await RequestBody.ReadJsonAsync<TokenRequest>(http);
        if (error is not null)
        {
            return error;
        }

        if (body!.User is not { } user || !UserPattern().IsMatch(user))
        {
            return Problems.Validation("user", "User must match ^[a-z0-9-]{1,32}$.");
        }

        var (token, expiresIn) = issuer.Issue(user);
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(new TokenResponse(token, expiresIn, user));
    }

    [GeneratedRegex(@"^[a-z0-9-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UserPattern();

    private sealed record TokenRequest(string? User);

    private sealed record TokenResponse(string AccessToken, int ExpiresIn, string User);
}
