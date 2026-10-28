using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using AgenticWorkflow.Api.Auth;
using AgenticWorkflow.Api.Guests;
using AgenticWorkflow.Core.Options;

namespace AgenticWorkflow.Api.Tests.Guests;

/// <summary>Scheme routing, discriminator and namespace tests (ADR 0006 section 5.3), with the REAL JwtBearer handlers.</summary>
public sealed partial class GuestAuthTests : IDisposable
{
    private readonly GuestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, (await response.ReadJsonAsync())["code"]!.GetValue<string>());
    }

    private DateTimeOffset Now => _host.Factory.Time.GetUtcNow();

    private string ForgedGuestToken(ECDsa key, string? oid = null, string kid = FakeGuestSigner.DefaultKid, string? audience = null, string? issuer = null) =>
        Jws.Es256(key, kid, issuer ?? GuestHost.Issuer, audience ?? GuestHost.Audience, oid ?? GuestHost.ValidGuestId(), Now, Now.AddHours(1));

    // ---- issuance ----

    [Fact]
    public async Task Issued_token_has_the_contract_claims_header_and_expiry_and_a_guest_document_exists()
    {
        var response = await _host.RequestTokenAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.ReadJsonAsync();
        Assert.Equal("Bearer", body["tokenType"]!.GetValue<string>());
        var token = body["accessToken"]!.GetValue<string>();

        var header = Jws.Header(token);
        Assert.Equal("ES256", header["alg"]!.GetValue<string>());
        Assert.Equal("JWT", header["typ"]!.GetValue<string>());
        Assert.Equal(FakeGuestSigner.DefaultKid, header["kid"]!.GetValue<string>());

        var payload = Jws.Payload(token);
        var oid = payload["oid"]!.GetValue<string>();
        Assert.Matches(GuestIdPattern(), oid);
        Assert.Equal(oid, payload["sub"]!.GetValue<string>());
        Assert.Equal(GuestHost.Issuer, payload["iss"]!.GetValue<string>());
        Assert.Equal(GuestHost.Audience, payload["aud"]!.GetValue<string>());
        Assert.Equal(Now.ToUnixTimeSeconds(), payload["iat"]!.GetValue<long>());
        Assert.Equal(Now.ToUnixTimeSeconds(), payload["nbf"]!.GetValue<long>());
        var exp = payload["exp"]!.GetValue<long>();
        Assert.Equal(Now.AddHours(4).ToUnixTimeSeconds(), exp);
        Assert.False(string.IsNullOrEmpty(payload["jti"]?.GetValue<string>()));
        foreach (var forbidden in new[] { "scp", "name", "email", "ip", "gst_exp" })
        {
            Assert.Null(payload[forbidden]);
        }

        // expiresAt = token exp, fixed-width UTC milliseconds.
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.000'Z'"), body["expiresAt"]!.GetValue<string>());

        // The signature is the 64-byte R||S form and verifies with the standard handler.
        Assert.Equal(64, Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(token.Split('.')[2]).Length);

        var doc = _host.Factory.Store.GuestDocument(oid);
        Assert.NotNull(doc);
        Assert.Equal((0, 30, 0, 5), (doc.TurnCount, doc.MaxTurns, doc.SessionCount, doc.MaxSessions));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(exp), doc.ExpiresAt);
    }

    [Fact]
    public async Task Every_call_creates_a_new_guest()
    {
        var a = await _host.NewGuestAsync();
        var b = await _host.NewGuestAsync();
        Assert.NotEqual(a.GuestId, b.GuestId);
    }

    [Fact]
    public async Task A_valid_guest_token_is_accepted_and_has_no_scope()
    {
        var (token, _, client) = await _host.NewGuestAsync();
        Assert.Null(Jws.Payload(token)["scp"]);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/workflows")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sessions")).StatusCode);
    }

    [Fact]
    public async Task Disabled_guests_return_404_guest_access_disabled_and_even_a_correctly_signed_token_is_401_unauthorized()
    {
        using var host = new GuestHost(c => c["Guest:Enabled"] = "false");
        await AssertProblemAsync(await host.RequestTokenAsync(), HttpStatusCode.NotFound, "guest_access_disabled");

        var token = Jws.Es256(host.Signer.Key, FakeGuestSigner.DefaultKid, GuestHost.Issuer, GuestHost.Audience, GuestHost.ValidGuestId(),
            host.Factory.Time.GetUtcNow(), host.Factory.Time.GetUtcNow().AddHours(1));
        await AssertProblemAsync(await host.ClientWithToken(token).GetAsync("/api/workflows"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Token_endpoint_requires_json_and_ignores_unknown_properties_and_an_empty_body()
    {
        var anonymous = _host.Anonymous();
        var textRequest = new HttpRequestMessage(HttpMethod.Post, "/api/guest/token") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "text/plain") };
        await AssertProblemAsync(await anonymous.SendAsync(textRequest), HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");

        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsync("/api/guest/token", Helpers.Json("""{"unknown":1,"botCheckToken":"x"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsync("/api/guest/token", Helpers.Json(string.Empty))).StatusCode);
    }

    [Fact]
    public async Task A_signing_failure_is_500_internal_error_and_a_later_call_works_again()
    {
        _host.Signer.Unavailable = true;
        await AssertProblemAsync(await _host.RequestTokenAsync(), HttpStatusCode.InternalServerError, "internal_error");
        _host.Signer.Unavailable = false;
        Assert.Equal(HttpStatusCode.OK, (await _host.RequestTokenAsync()).StatusCode);
    }

    // ---- forgeries and routing ----

    [Fact]
    public async Task Guest_issuer_signed_by_any_other_key_is_401_unauthorized()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var response = await _host.ClientWithToken(ForgedGuestToken(other)).GetAsync("/api/workflows");
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Unknown_kid_is_401_unauthorized()
    {
        var response = await _host.ClientWithToken(ForgedGuestToken(_host.Signer.Key, kid: "some-other-kid")).GetAsync("/api/workflows");
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task A_guest_key_signed_token_with_an_Entra_looking_issuer_is_routed_to_the_Entra_scheme_and_is_401()
    {
        var token = ForgedGuestToken(_host.Signer.Key, issuer: TestTokens.Issuer, audience: TestTokens.Audience);
        await AssertProblemAsync(await _host.ClientWithToken(token).GetAsync("/api/workflows"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Alg_none_and_HS256_with_the_guest_issuer_are_401()
    {
        var payload = Jws.B64($$"""{"iss":"{{GuestHost.Issuer}}","aud":"{{GuestHost.Audience}}","oid":"{{GuestHost.ValidGuestId()}}","exp":{{Now.AddHours(1).ToUnixTimeSeconds()}}}""");
        var none = Jws.B64("""{"alg":"none","typ":"JWT"}""") + "." + payload + ".";
        await AssertProblemAsync(await _host.ClientWithToken(none).GetAsync("/api/workflows"), HttpStatusCode.Unauthorized, "unauthorized");

        var hs = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = GuestHost.Issuer,
            Audience = GuestHost.Audience,
            Claims = new Dictionary<string, object> { ["oid"] = GuestHost.ValidGuestId() },
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(TestTokens.Key, SecurityAlgorithms.HmacSha256),
        });
        await AssertProblemAsync(await _host.ClientWithToken(hs).GetAsync("/api/workflows"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Wrong_audience_is_401()
    {
        var token = ForgedGuestToken(_host.Signer.Key, audience: TestTokens.Audience);
        await AssertProblemAsync(await _host.ClientWithToken(token).GetAsync("/api/workflows"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task An_expired_guest_token_is_401_guest_expired_and_an_expired_Entra_token_stays_unauthorized()
    {
        var (_, _, client) = await _host.NewGuestAsync();
        _host.Factory.Time.Advance(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(2));
        await AssertProblemAsync(await client.GetAsync("/api/workflows"), HttpStatusCode.Unauthorized, "guest_expired");

        var expiredEntra = TestTokens.Create(TestTokens.NewOid(), expires: DateTime.UtcNow.AddMinutes(-5));
        await AssertProblemAsync(await _host.ClientWithToken(expiredEntra).GetAsync("/api/workflows"), HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task A_validly_signed_guest_token_with_a_GUID_oid_is_403_forbidden()
    {
        var token = ForgedGuestToken(_host.Signer.Key, oid: Guid.NewGuid().ToString());
        await AssertProblemAsync(await _host.ClientWithToken(token).GetAsync("/api/workflows"), HttpStatusCode.Forbidden, "forbidden");
    }

    [Theory]
    [InlineData("guest-00000000-0000-7000-8000-000000000000")] // not a v4 uuid
    [InlineData("guest-3F0C2A7E-5B1D-4C8E-9A2F-6D7E8F901234")] // uppercase
    [InlineData("guest-abc")]
    [InlineData("system-guest-counter")]
    public async Task A_validly_signed_guest_token_with_a_malformed_guest_oid_is_403(string oid)
    {
        var token = ForgedGuestToken(_host.Signer.Key, oid: oid);
        await AssertProblemAsync(await _host.ClientWithToken(token).GetAsync("/api/workflows"), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task An_Entra_token_with_a_guest_shaped_oid_is_403_on_every_endpoint()
    {
        var client = _host.ClientWithToken(TestTokens.Create(GuestHost.ValidGuestId()));
        foreach (var response in new[]
                 {
                     await client.GetAsync("/api/workflows"),
                     await client.GetAsync("/api/sessions"),
                     await client.DeleteAsync("/api/me/data"),
                     await client.PostAsync("/api/sessions", Helpers.Json("""{"workflowId":"scripted-test"}""")),
                 })
        {
            await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
        }
    }

    [Fact]
    public async Task Entra_users_are_unaffected_by_guest_access_and_still_need_the_scope()
    {
        Assert.Equal(HttpStatusCode.OK, (await _host.Host.Client.GetAsync("/api/workflows")).StatusCode);
        var noScope = _host.ClientWithToken(TestTokens.Create(TestTokens.NewOid(), scopes: "other"));
        await AssertProblemAsync(await noScope.GetAsync("/api/workflows"), HttpStatusCode.Forbidden, "forbidden");
    }

    // ---- handler and TryGetUser unit tests ----

    private static ClaimsPrincipal Principal(string? authenticationType, string oid, string? issuer, string? scope = null)
    {
        var claims = new List<Claim> { new("oid", oid) };
        if (issuer is not null)
        {
            claims.Add(new Claim("iss", issuer));
        }

        if (scope is not null)
        {
            claims.Add(new Claim("scp", scope));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }

    private static async Task<bool> AuthorizeAsync(ClaimsPrincipal principal, bool guestsEnabled = true)
    {
        var handler = new ChatUserAuthorizationHandler(Options.Create(new GuestOptions { Enabled = guestsEnabled, Issuer = GuestHost.Issuer }));
        var requirement = new ChatUserRequirement(["access_as_user"]);
        var context = new AuthorizationHandlerContext([requirement], principal, null);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task Handler_accepts_a_guest_principal_with_the_guest_issuer_and_no_scope_only_while_guests_are_enabled()
    {
        var guest = Principal("guest", GuestHost.ValidGuestId(), GuestHost.Issuer);
        Assert.True(await AuthorizeAsync(guest));
        Assert.False(await AuthorizeAsync(guest, guestsEnabled: false));
    }

    [Fact]
    public async Task Handler_rejects_a_guest_authentication_type_with_a_foreign_issuer()
    {
        Assert.False(await AuthorizeAsync(Principal("guest", GuestHost.ValidGuestId(), TestTokens.Issuer)));
        Assert.False(await AuthorizeAsync(Principal("guest", GuestHost.ValidGuestId(), issuer: null)));
    }

    [Fact]
    public async Task Handler_rejects_a_guest_authentication_type_with_a_GUID_oid_and_an_Entra_identity_with_a_guest_oid()
    {
        Assert.False(await AuthorizeAsync(Principal("guest", Guid.NewGuid().ToString(), GuestHost.Issuer)));
        Assert.False(await AuthorizeAsync(Principal("AuthenticationTypes.Federation", GuestHost.ValidGuestId(), TestTokens.Issuer, "access_as_user")));
    }

    [Fact]
    public async Task Handler_requires_the_scope_and_a_non_guest_issuer_for_Entra_identities()
    {
        var oid = Guid.NewGuid().ToString();
        Assert.True(await AuthorizeAsync(Principal("AuthenticationTypes.Federation", oid, TestTokens.Issuer, "access_as_user")));
        Assert.True(await AuthorizeAsync(Principal("AuthenticationTypes.Federation", oid, TestTokens.Issuer, "other access_as_user")));
        Assert.False(await AuthorizeAsync(Principal("AuthenticationTypes.Federation", oid, TestTokens.Issuer, "other")));
        Assert.False(await AuthorizeAsync(Principal("AuthenticationTypes.Federation", oid, TestTokens.Issuer)));
        Assert.False(await AuthorizeAsync(Principal("AuthenticationTypes.Federation", oid, GuestHost.Issuer, "access_as_user"))); // Entra identity claiming the guest issuer
    }

    [Fact]
    public void TryGetUser_returns_the_kind_and_keeps_TryGetUserId_as_a_wrapper()
    {
        var guestId = GuestHost.ValidGuestId();
        Assert.True(CurrentUser.TryGetUser(Principal("guest", guestId, GuestHost.Issuer), out var id, out var kind));
        Assert.Equal((guestId, UserKind.Guest), (id, kind));

        var oid = Guid.NewGuid();
        Assert.True(CurrentUser.TryGetUser(Principal("x", oid.ToString().ToUpperInvariant(), null), out id, out kind));
        Assert.Equal((oid.ToString(), UserKind.Entra), (id, kind));
        Assert.True(CurrentUser.TryGetUserId(Principal("x", oid.ToString(), null), out id));
        Assert.Equal(oid.ToString(), id);

        Assert.False(CurrentUser.TryGetUser(Principal("guest", oid.ToString(), null), out _, out _));
        Assert.False(CurrentUser.TryGetUser(Principal("x", guestId, null), out _, out _));
        Assert.False(CurrentUser.TryGetUser(Principal("x", Guid.Empty.ToString(), null), out _, out _));
        Assert.False(CurrentUser.TryGetUser(new ClaimsPrincipal(), out _, out _));
        // Unauthenticated identity (no authentication type) never counts.
        Assert.False(CurrentUser.TryGetUser(Principal(null, oid.ToString(), null), out _, out _));
    }

    [Fact]
    public void TryGetUser_rejects_a_principal_with_two_authenticated_identities()
    {
        var principal = Principal("guest", GuestHost.ValidGuestId(), GuestHost.Issuer);
        principal.AddIdentity(new ClaimsIdentity([new Claim("oid", Guid.NewGuid().ToString())], "x"));
        Assert.False(CurrentUser.TryGetUser(principal, out _, out _));
    }

    // ---- limiter partitions ----

    [Fact]
    public async Task Guests_and_Entra_users_use_separate_rate_limit_partitions_with_their_own_limits()
    {
        using var host = new GuestHost(c =>
        {
            c["Guest:TurnsPerMinute"] = "1";
            c["Chat:TurnsPerMinute"] = "3";
        });
        var (_, _, guest) = await host.NewGuestAsync();
        var guestSession = await host.Host.NewSessionAsync(guest);

        Assert.Equal(HttpStatusCode.OK, (await Post(guest, guestSession)).StatusCode);
        var limited = await Post(guest, guestSession);
        await AssertProblemAsync(limited, (HttpStatusCode)429, "rate_limited");
        Assert.True(limited.Headers.Contains("Retry-After"));

        var entra = host.Host.Client;
        var entraSession = await host.Host.NewSessionAsync();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await Post(entra, entraSession)).StatusCode);
        }

        await AssertProblemAsync(await Post(entra, entraSession), (HttpStatusCode)429, "rate_limited");
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string sessionId)
    {
        var response = await client.PostAsync($"/api/sessions/{sessionId}/messages", Helpers.Json("""{"content":"hi"}"""));
        await response.Content.ReadAsStringAsync();
        return response;
    }

    [GeneratedRegex("^guest-[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex GuestIdPattern();
}
