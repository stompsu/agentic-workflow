using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using AgenticWorkflow.Api.Guests;
using AgenticWorkflow.Api.Http;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Core.Security;
using AgenticWorkflow.Infrastructure.Smoke;

namespace AgenticWorkflow.Api.Auth;

public static class AuthExtensions
{
    public const string ChatUserPolicy = "ChatUser";

    /// <summary>
    /// Two bearer schemes behind one default policy scheme (ADR 0001, ADR 0006 section 5.3): the Microsoft.Identity.Web
    /// "Bearer" scheme for Entra External ID tokens, and (only when Guest:Enabled) the "Guest" scheme for tokens issued by
    /// <c>POST /api/guest/token</c>. Policy ChatUser and the fallback policy both use <see cref="ChatUserRequirement"/>.
    /// 401/403 bodies are ProblemDetails with stable codes.
    /// </summary>
    public static IServiceCollection AddChatAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var authentication = services.AddAuthentication(options =>
        {
            options.DefaultScheme = GuestAuth.ApiBearerScheme;
            options.DefaultChallengeScheme = GuestAuth.ApiBearerScheme;
        });
        authentication.AddMicrosoftIdentityWebApi(configuration.GetSection("AzureAd"));
        authentication.AddPolicyScheme(GuestAuth.ApiBearerScheme, displayName: null, options =>
            {
                // Routing only: peeks at the UNVALIDATED issuer. The chosen scheme validates the token for real.
                options.ForwardDefaultSelector = context =>
                    context.RequestServices.GetRequiredService<IOptions<GuestOptions>>().Value is { Enabled: true, Issuer: { Length: > 0 } issuer }
                    && PeekIssuer(context) == issuer
                        ? GuestAuth.Scheme
                        : JwtBearerDefaults.AuthenticationScheme;
            });

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

        AddGuestScheme(services);

        var scopes = configuration.GetSection("Auth:RequiredScopes").Get<string[]>() is { Length: > 0 } configured
            ? configured
            : ["access_as_user"];
        var requirement = new ChatUserRequirement(scopes);

        services.AddAuthorizationBuilder()
            .AddPolicy(ChatUserPolicy, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(requirement))
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(requirement)
                .Build());

        services.AddSingleton<IAuthorizationHandler, ChatUserAuthorizationHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsAuthorizationResultHandler>();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        return services;
    }

    /// <summary>
    /// Registers the "Guest" JwtBearer scheme LAZILY (when the authentication options are first built, from
    /// <see cref="GuestOptions"/>), so it exists only when Guest:Enabled is true regardless of when configuration was loaded.
    /// ES256 only, signed tokens only, expiry required; <c>AuthenticationType = "guest"</c> is stamped by the validation
    /// parameters; keys come from <see cref="IGuestTokenSigner.ResolveValidationKey"/> (cache only, never Key Vault).
    /// </summary>
    private static void AddGuestScheme(IServiceCollection services)
    {
        services.AddOptions<AuthenticationOptions>().Configure<IOptions<GuestOptions>>((auth, guest) =>
        {
            if (guest.Value.Enabled)
            {
                auth.AddScheme<JwtBearerHandler>(GuestAuth.Scheme, displayName: null);
            }
        });

        services.AddOptions<JwtBearerOptions>(GuestAuth.Scheme).Configure<IOptions<GuestOptions>, IGuestTokenSigner, TimeProvider>((options, guestOptions, signer, time) =>
        {
            var guest = guestOptions.Value;
            options.MapInboundClaims = false;
            options.SaveToken = false;
            options.RequireHttpsMetadata = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = guest.Issuer,
                ValidateAudience = true,
                ValidAudience = guest.Audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                ValidAlgorithms = [GuestAuth.Algorithm],
                ClockSkew = TimeSpan.FromMinutes(1),
                // Lifetime on the injected TimeProvider (testable); an expired token raises SecurityTokenExpiredException,
                // which OnChallenge maps to 401 guest_expired.
                LifetimeValidator = (notBefore, expires, _, parameters) =>
                {
                    var now = time.GetUtcNow().UtcDateTime;
                    if (expires is null)
                    {
                        throw new SecurityTokenNoExpirationException("The guest token has no exp claim.");
                    }

                    if (notBefore is { } nbf && nbf > now + parameters.ClockSkew)
                    {
                        throw new SecurityTokenNotYetValidException("The guest token is not valid yet.") { NotBefore = nbf };
                    }

                    if (expires.Value < now - parameters.ClockSkew)
                    {
                        throw new SecurityTokenExpiredException("The guest token has expired.") { Expires = expires.Value };
                    }

                    return true;
                },
                AuthenticationType = GuestAuth.AuthenticationType,
                IssuerSigningKeyResolver = (_, _, kid, _) =>
                    kid is not null && signer.ResolveValidationKey(kid) is { } key ? [key] : [],
            };
            options.Events = new JwtBearerEvents
            {
                OnChallenge = async context =>
                {
                    context.HandleResponse();
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    // An expired guest token means the guest identity has ended (exp IS the identity end).
                    var code = context.AuthenticateFailure is SecurityTokenExpiredException
                        ? Problems.Codes.GuestExpired
                        : Problems.Codes.Unauthorized;
                    await Problems.WriteAsync(context.HttpContext, StatusCodes.Status401Unauthorized, code);
                },
                OnForbidden = context => Problems.WriteAsync(context.HttpContext, StatusCodes.Status403Forbidden, Problems.Codes.Forbidden),
            };
        });
    }

    /// <summary>Guest signing, key cache, activity cache, limits and the smoke probe (all inert while Guest:Enabled is false).</summary>
    public static IServiceCollection AddGuestAccess(this IServiceCollection services)
    {
        services.AddSingleton<GuestSigningKeyCache>();
        services.AddSingleton<IGuestKeySource, KeyVaultGuestKeySource>();
        services.AddSingleton<IGuestTokenSigner>(sp =>
            sp.GetRequiredService<IOptions<GuestOptions>>().Value.SigningMode == GuestSigningMode.Ephemeral
                ? new EphemeralGuestTokenSigner()
                : ActivatorUtilities.CreateInstance<KeyVaultGuestTokenSigner>(sp));
        services.AddSingleton<IGuestTokenService, GuestTokenService>();
        services.AddHostedService<GuestSigningKeyRefresher>();
        services.AddSingleton<ISmokeProbe, GuestSigningSmokeProbe>();

        services.AddSingleton<GuestActivityCache>();
        services.AddSingleton<GuestClientLimiter>();
        services.AddSingleton<GuestGlobalLimits>();
        services.AddSingleton<GuestWorkflowPolicy>();
        return services;
    }

    /// <summary>Routing peek: the unvalidated <c>iss</c> of the bearer token, or null. Never used for authorization.</summary>
    private static string? PeekIssuer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header["Bearer ".Length..].Trim();
        if (token.Length is 0 or > 8192)
        {
            return null;
        }

        try
        {
            return new JsonWebToken(token).Issuer;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
