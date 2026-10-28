using Microsoft.IdentityModel.Tokens;

namespace AgenticWorkflow.Api.Guests;

/// <summary>Raised when no key version may sign (all expired, disabled or not loaded). -> 500 internal_error.</summary>
public sealed class GuestSigningUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Signs and validates guest tokens (ADR 0006 section 5.5). Implementations: Key Vault, Ephemeral (Development only),
/// and a fake in tests. Deviation from the ADR sketch: the kid is chosen first (<see cref="GetSigningKid"/>) because it is
/// part of the signed header, then <see cref="SignAsync"/> signs with exactly that version (race-free across rotation).
/// </summary>
public interface IGuestTokenSigner
{
    /// <summary>"ES256".</summary>
    string Algorithm { get; }

    /// <summary>The key version (kid) to sign with now. Throws <see cref="GuestSigningUnavailableException"/> when none is eligible.</summary>
    string GetSigningKid();

    /// <summary>64-byte R||S signature (JWS ES256) over the JWS signing input, with the key version <paramref name="kid"/>.</summary>
    Task<byte[]> SignAsync(string kid, byte[] jwsSigningInput, CancellationToken ct);

    /// <summary>Synchronous, cache only (never calls Key Vault). Unknown kid -> null (and a throttled refresh request).</summary>
    SecurityKey? ResolveValidationKey(string kid);
}

public interface IGuestTokenService
{
    Task<GuestToken> IssueAsync(string guestUserId, DateTimeOffset expiresAt, CancellationToken ct);
}

public sealed record GuestToken(string AccessToken, DateTimeOffset ExpiresAt);
