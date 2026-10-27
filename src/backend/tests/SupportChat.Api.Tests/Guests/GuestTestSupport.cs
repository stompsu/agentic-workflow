using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using SupportChat.Api.Guests;
using SupportChat.Api.Tests.Chat;

namespace SupportChat.Api.Tests.Guests
{
    /// <summary>In-memory P-256 signer registered explicitly by the test host (never relies on Ephemeral, which is Development only).</summary>
    public sealed class FakeGuestSigner : IGuestTokenSigner, IDisposable
    {
        public const string DefaultKid = "test-kid-0001";

        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public string Kid { get; set; } = DefaultKid;

        public bool Unavailable { get; set; }

        public string Algorithm => "ES256";

        public ECDsa Key => _key;

        public string GetSigningKid() =>
            Unavailable ? throw new GuestSigningUnavailableException("fake: no signing key") : Kid;

        public Task<byte[]> SignAsync(string kid, byte[] jwsSigningInput, CancellationToken ct) =>
            Task.FromResult(_key.SignData(jwsSigningInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public SecurityKey? ResolveValidationKey(string kid) => kid == Kid ? new ECDsaSecurityKey(_key) { KeyId = Kid } : null;

        public void Dispose() => _key.Dispose();
    }

    /// <summary>Key source that never touches the network (the refresher is a no-op over it).</summary>
    public sealed class FakeKeySource : IGuestKeySource
    {
        public Task<IReadOnlyList<GuestKeyVersionInfo>> ListVersionsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GuestKeyVersionInfo>>([]);

        public Task<GuestKeyMaterial?> GetKeyAsync(string version, CancellationToken ct) => Task.FromResult<GuestKeyMaterial?>(null);
    }

    /// <summary>Compact JWS helpers to forge guest-looking tokens in tests.</summary>
    public static class Jws
    {
        public static string B64(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

        public static string Es256(
            ECDsa key, string kid, string issuer, string audience, string oid, DateTimeOffset now, DateTimeOffset expires, string? sub = null)
        {
            var header = B64($$"""{"alg":"ES256","typ":"JWT","kid":"{{kid}}"}""");
            var payload = B64(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["iss"] = issuer,
                ["aud"] = audience,
                ["sub"] = sub ?? oid,
                ["oid"] = oid,
                ["iat"] = now.ToUnixTimeSeconds(),
                ["nbf"] = now.ToUnixTimeSeconds(),
                ["exp"] = expires.ToUnixTimeSeconds(),
            }));
            var input = header + "." + payload;
            var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return input + "." + Base64Url.EncodeToString(signature);
        }

        public static JsonNode Payload(string jwt) => JsonNode.Parse(Base64Url.DecodeFromChars(jwt.Split('.')[1]))!;

        public static JsonNode Header(string jwt) => JsonNode.Parse(Base64Url.DecodeFromChars(jwt.Split('.')[0]))!;
    }

    /// <summary>
    /// The real app (Test environment) with guests enabled, a scripted workflow, the fake signer and the fake key source.
    /// Guest workflow list defaults to the scripted test workflow.
    /// </summary>
    public sealed class GuestHost : IDisposable
    {
        public const string Issuer = "supportchat-guest-test";
        public const string Audience = "supportchat-api-guest";

        public GuestHost(Action<Dictionary<string, string?>>? configure = null, Action<IServiceCollection>? services = null, string environment = "Test")
        {
            Signer = new FakeGuestSigner();
            Host = new ChatHost(
                c =>
                {
                    c["Guest:Enabled"] = "true";
                    c["Guest:Issuer"] = Issuer;
                    c["Guest:SigningMode"] = "KeyVault";
                    c["Guest:SigningKeyUri"] = "https://test-vault.vault.azure.net/keys/guest-token-signing";
                    c["Guest:Workflows:0"] = ScriptedWorkflow.WorkflowId;
                    c["Guest:Workflows:1"] = "throwaway-test";
                    configure?.Invoke(c);
                },
                s =>
                {
                    s.RemoveAll<IGuestTokenSigner>();
                    s.AddSingleton<IGuestTokenSigner>(Signer);
                    s.RemoveAll<IGuestKeySource>();
                    s.AddSingleton<IGuestKeySource, FakeKeySource>();
                    services?.Invoke(s);
                },
                environment);
        }

        public FakeGuestSigner Signer { get; }

        public ChatHost Host { get; }

        public ApiFactory Factory => Host.Factory;

        /// <summary>Messages of a session as the given caller sees them.</summary>
        public static async Task<JsonArray> MessagesAsync(HttpClient client, string sessionId)
        {
            var response = await client.GetAsync($"/api/sessions/{sessionId}");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            return (await response.ReadJsonAsync())["messages"]!.AsArray();
        }

        public static string ValidGuestId() => "guest-" + Guid.NewGuid();

        public HttpClient Anonymous() => Factory.CreateClient();

        public HttpClient ClientWithToken(string token)
        {
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        public async Task<HttpResponseMessage> RequestTokenAsync(Action<HttpRequestMessage>? configure = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/guest/token") { Content = Helpers.Json("{}") };
            configure?.Invoke(request);
            return await Anonymous().SendAsync(request);
        }

        /// <summary>Creates a guest through the real endpoint. Returns the token and a client that sends it.</summary>
        public async Task<(string Token, string GuestId, HttpClient Client)> NewGuestAsync(Action<HttpRequestMessage>? configure = null)
        {
            var response = await RequestTokenAsync(configure);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            var token = (await response.ReadJsonAsync())["accessToken"]!.GetValue<string>();
            return (token, Jws.Payload(token)["oid"]!.GetValue<string>(), ClientWithToken(token));
        }

        public void Dispose()
        {
            Host.Dispose();
            Signer.Dispose();
        }
    }
}
