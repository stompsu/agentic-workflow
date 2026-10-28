using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace AgenticWorkflow.Api.Guests;

/// <summary>
/// Development only (startup rule 14): an ECDsa P-256 key generated in memory at process start, never persisted.
/// Guest tokens die with the process. No Azure dependency and no secret.
/// </summary>
public sealed class EphemeralGuestTokenSigner : IGuestTokenSigner, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _kid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    private readonly ECDsaSecurityKey _securityKey;

    public EphemeralGuestTokenSigner() => _securityKey = new ECDsaSecurityKey(_key) { KeyId = _kid };

    public string Algorithm => GuestAuth.Algorithm;

    public string GetSigningKid() => _kid;

    public Task<byte[]> SignAsync(string kid, byte[] jwsSigningInput, CancellationToken ct) =>
        kid == _kid
            ? Task.FromResult(_key.SignData(jwsSigningInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            : throw new InvalidOperationException("Unknown ephemeral key id.");

    public SecurityKey? ResolveValidationKey(string kid) => kid == _kid ? _securityKey : null;

    public void Dispose() => _key.Dispose();
}
