using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace SupportChat.LocalHost.Auth;

/// <summary>
/// Configures the "Bearer" scheme with the issuer singleton's key, so signing and validation share one key. No
/// authority / metadata: ES256 only, signature, issuer, audience and lifetime are all required.
/// </summary>
public sealed class LocalJwtBearerConfigure(LocalTokenIssuer issuer) : IConfigureNamedOptions<JwtBearerOptions>
{
    /// <summary>
    /// The JwtBearer default, which the production Entra scheme inherits. ProductionIsolationTests' equality test (resolved
    /// Api options vs LocalHost options) is the real guard if Entra ever overrides it.
    /// </summary>
    public static readonly bool ProductionMapInboundClaims = new JwtBearerOptions().MapInboundClaims;

    public void Configure(JwtBearerOptions options) => Configure(JwtBearerDefaults.AuthenticationScheme, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        options.MapInboundClaims = ProductionMapInboundClaims;
        options.SaveToken = false;
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = LocalTokenIssuer.Issuer,
            ValidAudience = LocalTokenIssuer.Audience,
            IssuerSigningKey = issuer.SigningKey,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
        };
    }
}
