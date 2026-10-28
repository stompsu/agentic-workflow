using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using AgenticWorkflow.Core.Options;

namespace AgenticWorkflow.Api.Guests;

/// <summary>
/// Assembles the JWS compact serialization (RFC 7515, ADR 0006 section 5.2): header and payload as compact JSON,
/// base64url without padding, signature over the ASCII signing input. Crypto is delegated to <see cref="IGuestTokenSigner"/>.
/// Claims: iss, aud, sub = oid = guest id, iat = nbf, exp, jti. No scp, name, email, IP or device data.
/// </summary>
public sealed class GuestTokenService(IGuestTokenSigner signer, IOptions<GuestOptions> options, TimeProvider time) : IGuestTokenService
{
    public async Task<GuestToken> IssueAsync(string guestUserId, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var o = options.Value;
        var kid = signer.GetSigningKid();
        var issuedAt = time.GetUtcNow();

        var signingInput = Build(signer.Algorithm, kid, o.Issuer ?? throw new InvalidOperationException("Guest:Issuer is not configured."),
            o.Audience, guestUserId, issuedAt, expiresAt);
        var signature = await signer.SignAsync(kid, Encoding.ASCII.GetBytes(signingInput), ct);
        if (signature.Length != 64)
        {
            throw new InvalidOperationException("The guest signer returned a signature that is not the 64-byte R||S form (ES256).");
        }

        return new GuestToken(signingInput + "." + Base64Url.EncodeToString(signature), expiresAt);
    }

    /// <summary>BASE64URL(header) "." BASE64URL(payload).</summary>
    internal static string Build(
        string alg, string kid, string issuer, string audience, string guestUserId, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var header = Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("alg", alg);
            w.WriteString("typ", "JWT");
            w.WriteString("kid", kid);
            w.WriteEndObject();
        });
        var payload = Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("iss", issuer);
            w.WriteString("aud", audience);
            w.WriteString("sub", guestUserId);
            w.WriteString("oid", guestUserId);
            w.WriteNumber("iat", issuedAt.ToUnixTimeSeconds());
            w.WriteNumber("nbf", issuedAt.ToUnixTimeSeconds());
            w.WriteNumber("exp", expiresAt.ToUnixTimeSeconds());
            w.WriteString("jti", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
            w.WriteEndObject();
        });
        return Base64Url.EncodeToString(header) + "." + Base64Url.EncodeToString(payload);
    }

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            body(writer);
        }

        return stream.ToArray();
    }
}
