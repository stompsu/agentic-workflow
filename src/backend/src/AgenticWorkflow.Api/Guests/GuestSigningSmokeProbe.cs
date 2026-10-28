using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Infrastructure.Azure;
using AgenticWorkflow.Infrastructure.Smoke;

namespace AgenticWorkflow.Api.Guests;

/// <summary>
/// <c>--smoke-check</c> probe (ADR 0003 section 1a, ADR 0006 section 11), only when guests are enabled in KeyVault mode:
/// loads the key versions, selects the signing version (fails when none is eligible, warns when it expires within 30 days,
/// both through the loader's log events), signs a fixed test payload through Key Vault and verifies it with the loaded
/// public key (V1: the signature must be the 64-byte R||S form). A Key Vault 403 fails with a message naming the role.
/// </summary>
public sealed class GuestSigningSmokeProbe(
    IGuestKeySource source,
    IOptions<GuestOptions> options,
    IAzureCredentialProvider credentials,
    TimeProvider time,
    ILogger<GuestSigningSmokeProbe> logger) : ISmokeProbe
{
    public string Name => "GuestSigningKey";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var guest = options.Value;
        if (guest is not { Enabled: true, SigningMode: GuestSigningMode.KeyVault })
        {
            return;
        }

        try
        {
            var snapshot = await GuestSigningKeyLoader.LoadAsync(source, guest, time, logger, cancellationToken);
            if (snapshot.SigningVersion is not { } version)
            {
                throw new InvalidOperationException("No eligible guest signing key version (see the GuestSigningKeyUnavailable event).");
            }

            var cache = new GuestSigningKeyCache(time);
            cache.Update(snapshot);
            var signer = new KeyVaultGuestTokenSigner(cache, options, credentials);
            var payload = Encoding.ASCII.GetBytes("agentwf-guest-smoke-check");
            var signature = await signer.SignAsync(version, payload, cancellationToken);

            var key = (ECDsaSecurityKey)snapshot.VerificationKeys[version];
            if (!GuestSignatureCheck.Verify(key.ECDsa, payload, signature))
            {
                throw new InvalidOperationException("The Key Vault signature did not verify as a 64-byte ES256 (R||S) signature (V1).");
            }
        }
        catch (Exception ex) when (KeyVaultErrors.IsForbidden(ex))
        {
            logger.LogError("{Message}", KeyVaultErrors.MissingRoleMessage);
            throw;
        }
    }
}
