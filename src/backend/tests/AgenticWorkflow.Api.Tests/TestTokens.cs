using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AgenticWorkflow.Api.Tests;

/// <summary>Signs test JWTs with a symmetric key; the app's JwtBearer validates them for real.</summary>
public static class TestTokens
{
    public const string TenantId = "11111111-2222-3333-4444-555555555555";
    public const string Audience = "99999999-8888-7777-6666-555555555555";
    public const string Issuer = "https://11111111-2222-3333-4444-555555555555.ciamlogin.com/11111111-2222-3333-4444-555555555555/v2.0";

    public static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-signing-key-test-signing-key-0123456789"));

    public static TokenValidationParameters ValidationParameters() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = Key,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.Zero,
        NameClaimType = "name",
    };

    public static string Create(
        string? oid,
        string? scopes = "access_as_user",
        string? roles = null,
        DateTime? expires = null,
        SecurityKey? signingKey = null,
        IDictionary<string, object>? extraClaims = null)
    {
        var claims = new Dictionary<string, object> { ["tid"] = TenantId, ["ver"] = "2.0" };
        if (oid is not null)
        {
            claims["oid"] = oid;
        }

        if (scopes is not null)
        {
            claims["scp"] = scopes;
        }

        if (roles is not null)
        {
            claims["roles"] = new[] { roles };
        }

        foreach (var (k, v) in extraClaims ?? new Dictionary<string, object>())
        {
            claims[k] = v;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = claims,
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(30),
            SigningCredentials = new SigningCredentials(signingKey ?? Key, SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static string NewOid() => Guid.NewGuid().ToString();
}
