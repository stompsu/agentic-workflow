using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using AgenticWorkflow.Api.Guests;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Infrastructure.Azure;

namespace AgenticWorkflow.Api.Tests.Guests;

/// <summary>Runs only when GUEST_V1_KEY_URI is set (a versionless key URI of the DEV vault). Never in CI.</summary>
public sealed class VaultFactAttribute : FactAttribute
{
    public const string Variable = "GUEST_V1_KEY_URI";

    public VaultFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Opt-in: set {Variable}=https://<dev-vault>.vault.azure.net/keys/guest-token-signing and sign in with az login (Key Vault Crypto User).";
        }
    }
}

/// <summary>
/// V1 (ADR 0006 section 14): Key Vault returns ES256 signatures in the JWS form (raw 64-byte R||S, not DER). Proven by a
/// round trip: Key Vault sign (versioned key id, TokenCredential only) -> Microsoft.IdentityModel validation.
/// Manual run: <c>GUEST_V1_KEY_URI=... dotnet test src/backend --filter VaultRoundTrip</c>.
/// </summary>
public class KeyVaultRoundTripTests
{
    [VaultFact]
    public async Task VaultRoundTrip_a_Key_Vault_signed_guest_token_validates_with_the_loaded_public_key()
    {
        var options = Options.Create(new GuestOptions
        {
            Enabled = true,
            Issuer = "agentwf-guest-v1check",
            SigningMode = GuestSigningMode.KeyVault,
            SigningKeyUri = Environment.GetEnvironmentVariable(VaultFactAttribute.Variable),
        });
        var credentials = new AzureCredentialProvider(
            new FakeEnvironment(Environments.Development), new ConfigurationBuilder().Build());
        var source = new KeyVaultGuestKeySource(options, credentials);
        var time = TimeProvider.System;

        var snapshot = await GuestSigningKeyLoader.LoadAsync(source, options.Value, time, NullLogger.Instance, default);
        Assert.NotNull(snapshot.SigningVersion);

        var cache = new GuestSigningKeyCache(time);
        cache.Update(snapshot);
        var signer = new KeyVaultGuestTokenSigner(cache, options, credentials);
        var service = new GuestTokenService(signer, options, time);
        var token = await service.IssueAsync("guest-" + Guid.NewGuid(), time.GetUtcNow().AddMinutes(5), default);

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.AccessToken, new TokenValidationParameters
        {
            ValidIssuer = options.Value.Issuer,
            ValidAudience = options.Value.Audience,
            ValidAlgorithms = ["ES256"],
            IssuerSigningKeyResolver = (_, _, kid, _) => [signer.ResolveValidationKey(kid)!],
        });

        Assert.True(result.IsValid, result.Exception?.Message);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "AgenticWorkflow.Api.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
