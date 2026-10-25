using System.Net;
using System.Net.Http.Headers;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace SupportChat.Api.Tests;

public class AuthTests : IDisposable
{
    private readonly ApiFactory _factory = Fixtures.WithThrowawayWorkflow();

    public void Dispose() => _factory.Dispose();

    private async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.ReadJsonAsync();
        Assert.Equal((int)status, body["status"]!.GetValue<int>());
        Assert.Equal(code, body["code"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(body["traceId"]?.GetValue<string>()));
        Assert.DoesNotContain("IDW", body.ToJsonString()); // no token-validation detail
    }

    private HttpClient WithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task No_token_is_401_with_WWW_Authenticate_and_ProblemDetails()
    {
        var response = await _factory.CreateClient().GetAsync("/api/workflows");
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Fact]
    public async Task Garbage_token_is_401()
    {
        var response = await WithToken("not-a-jwt").GetAsync("/api/sessions");
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Expired_token_is_401()
    {
        var token = TestTokens.Create(TestTokens.NewOid(), expires: DateTime.UtcNow.AddMinutes(-5));
        var response = await WithToken(token).GetAsync("/api/sessions");
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_401()
    {
        var other = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("another-signing-key-another-signing-key-01"));
        var response = await WithToken(TestTokens.Create(TestTokens.NewOid(), signingKey: other)).GetAsync("/api/sessions");
        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
    }

    [Fact]
    public async Task Token_without_the_required_scope_is_403()
    {
        var response = await WithToken(TestTokens.Create(TestTokens.NewOid(), scopes: "some_other_scope")).GetAsync("/api/sessions");
        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task App_only_token_with_roles_but_no_scope_is_403()
    {
        var token = TestTokens.Create(TestTokens.NewOid(), scopes: null, roles: "Some.Role");
        var response = await WithToken(token).GetAsync("/api/sessions");
        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Token_without_oid_is_rejected_403()
    {
        var response = await WithToken(TestTokens.Create(oid: null)).GetAsync("/api/sessions");
        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Token_with_an_invalid_oid_is_rejected_403(string oid)
    {
        var response = await WithToken(TestTokens.Create(oid)).GetAsync("/api/sessions");
        await AssertProblemAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Missing_oid_is_rejected_on_every_endpoint()
    {
        var client = WithToken(TestTokens.Create(oid: null));
        var id = Guid.NewGuid();
        foreach (var response in new[]
                 {
                     await client.GetAsync("/api/workflows"),
                     await client.GetAsync("/api/sessions"),
                     await client.GetAsync($"/api/sessions/{id}"),
                     await client.DeleteAsync($"/api/sessions/{id}"),
                     await client.PostAsync("/api/sessions", Helpers.Json("""{"workflowId":"throwaway-test"}""")),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Valid_token_is_accepted()
    {
        var response = await _factory.CreateAuthorizedClient(TestTokens.NewOid()).GetAsync("/api/workflows");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_route_is_a_ProblemDetails_404_for_authenticated_users()
    {
        var response = await _factory.CreateAuthorizedClient(TestTokens.NewOid()).GetAsync("/api/does-not-exist");
        await AssertProblemAsync(response, HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task User_id_in_query_or_header_is_ignored()
    {
        var a = TestTokens.NewOid();
        var b = TestTokens.NewOid();
        var clientA = _factory.CreateAuthorizedClient(a);
        await clientA.CreateSessionAsync();

        var clientB = _factory.CreateAuthorizedClient(b);
        clientB.DefaultRequestHeaders.Add("X-User-Id", a);
        var response = await clientB.GetAsync($"/api/sessions?userId={a}&oid={a}");
        Assert.Empty((await response.ReadJsonAsync())["items"]!.AsArray());
    }
}
