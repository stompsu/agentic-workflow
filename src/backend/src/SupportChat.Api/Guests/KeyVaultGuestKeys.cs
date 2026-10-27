using System.Collections.Concurrent;
using System.Security.Cryptography;
using Azure;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SupportChat.Core.Options;
using SupportChat.Infrastructure.Azure;

namespace SupportChat.Api.Guests;

/// <summary>
/// Key Vault access for the guest signing key. ALWAYS through the app's TokenCredential (IAzureCredentialProvider,
/// the user-assigned managed identity): no keys, no secrets, no connection strings.
/// The identity needs "Key Vault Crypto User" on the vault (read key versions + sign/verify).
/// </summary>
public sealed class KeyVaultGuestKeySource(IOptions<GuestOptions> options, IAzureCredentialProvider credentials) : IGuestKeySource
{
    private readonly Lazy<(KeyClient Client, string Name)> _client = new(() =>
    {
        var (vault, name) = KeyVaultKeyUri.Parse(options.Value.SigningKeyUri);
        return (new KeyClient(vault, credentials.Credential), name);
    });

    public async Task<IReadOnlyList<GuestKeyVersionInfo>> ListVersionsAsync(CancellationToken ct)
    {
        var (client, name) = _client.Value;
        var result = new List<GuestKeyVersionInfo>();
        await foreach (var p in client.GetPropertiesOfKeyVersionsAsync(name, ct))
        {
            result.Add(new GuestKeyVersionInfo(p.Version, p.Enabled ?? true, p.NotBefore, p.ExpiresOn, p.CreatedOn ?? DateTimeOffset.MinValue));
        }

        return result;
    }

    public async Task<GuestKeyMaterial?> GetKeyAsync(string version, CancellationToken ct)
    {
        var (client, name) = _client.Value;
        KeyVaultKey key = await client.GetKeyAsync(name, version, ct);
        if (key.KeyType != KeyType.Ec && key.KeyType != KeyType.EcHsm)
        {
            return null;
        }

        if (key.Key.CurveName != KeyCurveName.P256)
        {
            return null;
        }

        return new GuestKeyMaterial(version, key.Key.ToECDsa(), key.KeyOperations.Contains(KeyOperation.Sign));
    }
}

internal static class KeyVaultKeyUri
{
    /// <summary>"https://v.vault.azure.net/keys/name" (versionless, validated at startup) -> vault URI + key name.</summary>
    public static (Uri Vault, string Name) Parse(string? uri)
    {
        if (!Uri.TryCreate(uri?.Trim(), UriKind.Absolute, out var parsed))
        {
            throw new InvalidOperationException("Guest:SigningKeyUri is not a valid URI.");
        }

        var segments = parsed.AbsolutePath.Trim('/').Split('/');
        return (new Uri(parsed.GetLeftPart(UriPartial.Authority)), segments[^1]);
    }
}

/// <summary>
/// Signs with <c>CryptographyClient.SignDataAsync(ES256)</c> on the VERSIONED key id, so the kid in the token header
/// always names the version that signed (ADR 0006 section 5.2). The SDK hashes locally; Key Vault signs the digest.
/// </summary>
public sealed class KeyVaultGuestTokenSigner(
    GuestSigningKeyCache cache, IOptions<GuestOptions> options, IAzureCredentialProvider credentials) : IGuestTokenSigner
{
    private readonly ConcurrentDictionary<string, CryptographyClient> _clients = new(StringComparer.Ordinal);

    public string Algorithm => GuestAuth.Algorithm;

    public string GetSigningKid() =>
        cache.Current.SigningVersion
        ?? throw new GuestSigningUnavailableException("No guest signing key version is available (GuestSigningKeyUnavailable).");

    public async Task<byte[]> SignAsync(string kid, byte[] jwsSigningInput, CancellationToken ct)
    {
        var client = _clients.GetOrAdd(kid, version =>
        {
            var (vault, name) = KeyVaultKeyUri.Parse(options.Value.SigningKeyUri);
            return new CryptographyClient(new Uri(vault, $"keys/{name}/{version}"), credentials.Credential);
        });

        SignResult result = await client.SignDataAsync(SignatureAlgorithm.ES256, jwsSigningInput, ct);
        return result.Signature;
    }

    public SecurityKey? ResolveValidationKey(string kid) => cache.Resolve(kid);
}

/// <summary>Verifies a signature produced for the JWS form (V1, used by the smoke check and tests).</summary>
internal static class GuestSignatureCheck
{
    public static bool Verify(ECDsa publicKey, byte[] data, byte[] signature) =>
        signature.Length == 64
        && publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
}

/// <summary>Maps a Key Vault 403 to an actionable message that names the missing role (no secrets involved).</summary>
internal static class KeyVaultErrors
{
    public static bool IsForbidden(Exception ex) => ex is RequestFailedException { Status: 403 };

    public const string MissingRoleMessage =
        "Key Vault returned 403: the app identity needs the 'Key Vault Crypto User' role on the vault that holds the guest signing key.";
}
