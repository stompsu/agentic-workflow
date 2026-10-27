using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SupportChat.LocalHost.Auth;

namespace SupportChat.LocalHost.Tests.Auth;

public class TokenEndpointTests : IClassFixture<DefaultLocalHost>
{
    private readonly DefaultLocalHost _factory;

    public TokenEndpointTests(DefaultLocalHost factory) => _factory = factory;

    private static HttpRequestMessage Token(string json = """{"user":"alice"}""", string? contentType = "application/json", string? origin = null, string? host = null)
    {
        var request = Http.Post("/local-auth/token", json, contentType);
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        return request;
    }

    [Fact]
    public async Task Issues_an_ES256_token_with_the_specified_claims()
    {
        var response = await _factory.CreateClient().SendAsync(Token());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("alice", body["user"]!.GetValue<string>());
        Assert.Equal(3600, body["expiresIn"]!.GetValue<int>());

        var jwt = new JsonWebToken(body["accessToken"]!.GetValue<string>());
        Assert.Equal("ES256", jwt.Alg);
        Assert.Equal("supportchat-local", jwt.Issuer);
        Assert.Contains("supportchat-local-api", jwt.Audiences);
        Assert.Equal("access_as_user", jwt.GetClaim("scp").Value);
        Assert.Equal("2.0", jwt.GetClaim("ver").Value);
        Assert.Equal("alice", jwt.GetClaim("name").Value);
        Assert.Equal(LocalTokenIssuer.OidFor("alice"), jwt.GetClaim("oid").Value);
        Assert.Equal(TimeSpan.FromHours(1), jwt.ValidTo - jwt.ValidFrom);
    }

    [Fact]
    public void Oid_is_a_stable_lowercase_UUIDv5_per_user()
    {
        Assert.Equal(LocalTokenIssuer.OidFor("alice"), LocalTokenIssuer.OidFor("alice"));
        Assert.NotEqual(LocalTokenIssuer.OidFor("alice"), LocalTokenIssuer.OidFor("bob"));
        var oid = LocalTokenIssuer.OidFor("alice");
        Assert.Equal(oid.ToLowerInvariant(), oid);
        Assert.Equal('5', oid[14]);
        // RFC 4122 reference vector: uuid5(DNS namespace, "www.example.com").
        Assert.Equal("2ed6657d-e927-568b-95e1-2665a8aea6a2",
            LocalTokenIssuer.CreateUuidV5(new Guid("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "www.example.com").ToString("D"));
    }

    [Fact]
    public async Task Rebound_host_gets_400_for_every_route()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Token(host: "evil.example"))).StatusCode);

        var healthz = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        healthz.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(healthz)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    public async Task Non_json_content_type_gets_415(string? contentType)
    {
        var response = await _factory.CreateClient().SendAsync(Token(contentType: contentType));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://localhost:5174")]
    [InlineData("https://localhost:5173")]
    [InlineData("null")]
    public async Task Foreign_origin_gets_403(string origin)
    {
        var response = await _factory.CreateClient().SendAsync(Token(origin: origin));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", (await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData(null)]
    public async Task Allowed_or_absent_origin_gets_200(string? origin)
    {
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().SendAsync(Token(origin: origin))).StatusCode);
    }

    [Theory]
    [InlineData("""{"user":"Alice"}""")]
    [InlineData("""{"user":""}""")]
    [InlineData("""{"user":"a b"}""")]
    [InlineData("""{"user":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    [InlineData("""{"user":"alice\n"}""")]
    [InlineData("""{}""")]
    [InlineData("""{"user":5}""")]
    [InlineData("not json")]
    public async Task Invalid_user_gets_400_validation_failed(string body)
    {
        var response = await _factory.CreateClient().SendAsync(Token(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", (await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Logs_contain_no_token_and_no_request_body()
    {
        using var factory = new LocalHostFactory();
        using var client = factory.CreateClient();
        var response = await client.SendAsync(Token("""{"user":"secret-user-name"}"""));
        var token = (await response.Content.ReadFromJsonAsync<JsonObject>())!["accessToken"]!.GetValue<string>();
        await client.SendAsync(Token("""{"user":"BAD USER BODY"}"""));
        await client.GetAsync("/api/workflows", HttpCompletionOption.ResponseContentRead);

        var logs = string.Join('\n', factory.Logs.Messages);
        Assert.NotEmpty(factory.Logs.Messages);
        Assert.DoesNotContain(token, logs);
        Assert.DoesNotContain("eyJ", logs);
        Assert.DoesNotContain("secret-user-name", logs);
        Assert.DoesNotContain("BAD USER BODY", logs);
    }
}

public class LocalBearerValidationTests : IClassFixture<DefaultLocalHost>
{
    private readonly DefaultLocalHost _factory;

    public LocalBearerValidationTests(DefaultLocalHost factory) => _factory = factory;

    private LocalTokenIssuer Issuer => _factory.Services.GetRequiredService<LocalTokenIssuer>();

    private async Task<HttpStatusCode> CallAsync(string? token)
    {
        using var client = _factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        return (await client.GetAsync("/api/workflows")).StatusCode;
    }

    [Fact]
    public async Task Valid_token_is_accepted_and_missing_token_is_401()
    {
        Assert.Equal(HttpStatusCode.OK, await CallAsync(await _factory.GetTokenAsync("alice")));
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(null));
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync("garbage"));
    }

    [Fact]
    public async Task Expired_token_is_401()
    {
        var now = DateTime.UtcNow;
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(Issuer.CreateToken("alice", now.AddHours(-2), now.AddHours(-1))));
    }

    [Fact]
    public async Task Wrong_issuer_and_wrong_audience_are_401_even_with_the_real_signature()
    {
        var now = DateTime.UtcNow;
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(Issuer.CreateToken("alice", now, now.AddHours(1), issuer: "someone-else")));
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(Issuer.CreateToken("alice", now, now.AddHours(1), audience: "other-api")));
        Assert.Equal(HttpStatusCode.OK, await CallAsync(Issuer.CreateToken("alice", now, now.AddHours(1))));
    }

    [Fact]
    public async Task Token_signed_by_another_key_is_401()
    {
        using var other = new LocalTokenIssuer(TimeProvider.System, TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(other.Issue("alice").Token));
    }

    [Fact]
    public async Task Other_algorithms_are_401()
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object> { ["scp"] = "access_as_user", ["oid"] = LocalTokenIssuer.OidFor("alice") };

        // HS256 signed with a symmetric key (even one derived from public material).
        var hs = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LocalTokenIssuer.Issuer,
            Audience = LocalTokenIssuer.Audience,
            Expires = now.AddHours(1),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef")), SecurityAlgorithms.HmacSha256),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(hs));

        // ES384 with a P-384 key.
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
        var es384 = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LocalTokenIssuer.Issuer,
            Audience = LocalTokenIssuer.Audience,
            Expires = now.AddHours(1),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(p384), SecurityAlgorithms.EcdsaSha384),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(es384));

        // alg "none", no signature.
        static string B64(string json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));
        var payload = $$"""{"iss":"{{LocalTokenIssuer.Issuer}}","aud":"{{LocalTokenIssuer.Audience}}","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}},"scp":"access_as_user","oid":"{{LocalTokenIssuer.OidFor("alice")}}"}""";
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync($"{B64("""{"alg":"none","typ":"JWT"}""")}.{B64(payload)}."));
    }

    [Fact]
    public async Task Token_without_the_scope_or_oid_is_rejected()
    {
        var now = DateTime.UtcNow;
        var handler = new JsonWebTokenHandler();
        string Make(Dictionary<string, object> claims) => handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = LocalTokenIssuer.Issuer,
            Audience = LocalTokenIssuer.Audience,
            Expires = now.AddHours(1),
            Claims = claims,
            SigningCredentials = new SigningCredentials(Issuer.SigningKey, SecurityAlgorithms.EcdsaSha256),
        });

        Assert.Equal(HttpStatusCode.Forbidden, await CallAsync(Make(new() { ["oid"] = LocalTokenIssuer.OidFor("a") })));
        Assert.Equal(HttpStatusCode.Forbidden, await CallAsync(Make(new() { ["scp"] = "access_as_user" })));
        Assert.Equal(HttpStatusCode.Forbidden, await CallAsync(Make(new() { ["scp"] = "access_as_user", ["oid"] = Guid.Empty.ToString() })));
    }

    [Fact]
    public void Restart_means_a_new_key()
    {
        using var other = new LocalTokenIssuer(TimeProvider.System, TimeSpan.FromHours(1));
        Assert.NotEqual(((ECDsaSecurityKey)Issuer.SigningKey).KeyId, ((ECDsaSecurityKey)other.SigningKey).KeyId);
    }

    [Fact]
    public void Jwt_parameters_are_as_specified()
    {
        var p = _factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;
        Assert.Equal(["ES256"], p.ValidAlgorithms);
        Assert.True(p.RequireSignedTokens);
        Assert.True(p.ValidateIssuer && p.ValidateAudience && p.ValidateLifetime && p.ValidateIssuerSigningKey && p.RequireExpirationTime);
        Assert.Equal(TimeSpan.FromSeconds(30), p.ClockSkew);
        Assert.Equal("supportchat-local", p.ValidIssuer);
        Assert.Equal("supportchat-local-api", p.ValidAudience);
    }
}
