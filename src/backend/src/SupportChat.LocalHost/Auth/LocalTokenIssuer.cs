using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SupportChat.LocalHost.Auth;

/// <summary>
/// ADR 0005 section 5, the LocalHost-only exception to "backend validates JWTs only" (decision L3, confirmed by the user):
/// an ECDSA P-256 key held in memory (never written anywhere; a restart invalidates every token) signs ES256 JWTs for
/// the local sign-in. The same instance's key validates them through <see cref="LocalJwtBearerConfigure"/>.
/// </summary>
public sealed class LocalTokenIssuer : IDisposable
{
    public const string Issuer = "supportchat-local";
    public const string Audience = "supportchat-local-api";
    public const string Scope = "access_as_user";

    /// <summary>Fixed namespace for the UUIDv5 oid of a local user name (stable across restarts).</summary>
    private static readonly Guid OidNamespace = new("6f1d3a52-9c47-4b8e-a0d2-5e7c31b84a19");

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly TimeProvider _time;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public LocalTokenIssuer(TimeProvider time, TimeSpan lifetime)
    {
        _time = time;
        Lifetime = lifetime;
        SigningKey = new ECDsaSecurityKey(_key) { KeyId = Guid.NewGuid().ToString("N") };
    }

    public TimeSpan Lifetime { get; }

    public SecurityKey SigningKey { get; }

    public static string OidFor(string user) => CreateUuidV5(OidNamespace, user).ToString("D");

    public (string Token, int ExpiresInSeconds) Issue(string user)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return (CreateToken(user, now, now + Lifetime), (int)Lifetime.TotalSeconds);
    }

    /// <summary>Test seam: explicit times and claims so expired / wrong issuer / wrong audience tokens carry the real signature.</summary>
    internal string CreateToken(string user, DateTime issuedAt, DateTime expires, string issuer = Issuer, string audience = Audience)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                ["scp"] = Scope,
                ["ver"] = "2.0",
                ["oid"] = OidFor(user),
                ["name"] = user,
            },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.EcdsaSha256),
        };
        return _handler.CreateToken(descriptor);
    }

    public void Dispose() => _key.Dispose();

    /// <summary>RFC 4122 version 5 (name based, SHA-1).</summary>
    internal static Guid CreateUuidV5(Guid namespaceId, string name)
    {
        var ns = namespaceId.ToByteArray();
        SwapGuidByteOrder(ns);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var data = new byte[ns.Length + nameBytes.Length];
        ns.CopyTo(data, 0);
        nameBytes.CopyTo(data, ns.Length);
        var hash = SHA1.HashData(data);
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        SwapGuidByteOrder(bytes);
        return new Guid(bytes);
    }

    private static void SwapGuidByteOrder(byte[] guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
