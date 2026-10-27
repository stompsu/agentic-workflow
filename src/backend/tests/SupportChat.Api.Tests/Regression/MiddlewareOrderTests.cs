using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using SupportChat.Core.Storage;

namespace SupportChat.Api.Tests.Regression;

/// <summary>Observable middleware order of the pipeline (ADR 0005 section 1): the ApiHost refactor must not change it.</summary>
public class MiddlewareOrderTests
{
    [Fact]
    public async Task Anonymous_api_requests_are_401_problem_details_never_429()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();
        for (var i = 0; i < 100; i++)
        {
            var response = await client.PostAsync("/api/sessions/s1/messages", Helpers.Json("""{"content":"x"}"""));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("unauthorized", (await response.ReadJsonAsync())["code"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Unknown_route_is_404_problem_details_from_status_pages()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateAuthorizedClient("11111111-1111-1111-1111-111111111111");

        var response = await client.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("not_found", (await response.ReadJsonAsync())["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Throwing_store_is_500_internal_error_problem_details()
    {
        using var factory = new ApiFactory(services: s => s.AddSingleton<ISessionStore, ThrowingSessionStore>());
        var client = factory.CreateAuthorizedClient("11111111-1111-1111-1111-111111111111");

        var response = await client.GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.ReadJsonAsync();
        Assert.Equal("internal_error", body["code"]!.GetValue<string>());
        Assert.DoesNotContain("SECRET", body.ToJsonString());
    }

    [Fact]
    public async Task Json_is_compressed_when_gzip_is_accepted()
    {
        using var factory = new ApiFactory();
        var client = factory.CreateAuthorizedClient("11111111-1111-1111-1111-111111111111");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/workflows");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task Responses_carry_no_Server_header()
    {
        using var factory = new ApiFactory();
        var response = await factory.CreateClient().GetAsync("/healthz");
        Assert.False(response.Headers.Contains("Server"));
    }

    private sealed class ThrowingSessionStore : ISessionStore
    {
        private static InvalidOperationException Boom() => new("SECRET");

        public Task<SessionRecord> CreateAsync(string userId, string workflowId, string? title, CancellationToken ct) => throw Boom();

        public Task<SessionRecord?> GetAsync(string userId, string sessionId, CancellationToken ct) => throw Boom();

        public Task<Page<SessionRecord>> ListAsync(string userId, int pageSize, string? continuationToken, CancellationToken ct) => throw Boom();

        public Task<DeleteSessionResult> DeleteAsync(string userId, string sessionId, CancellationToken ct) => throw Boom();

        public Task<DeleteUserDataResult> DeleteAllUserDataAsync(string userId, TimeSpan budget, CancellationToken ct) => throw Boom();

        public Task<StartTurnResult> TryStartTurnAsync(string userId, string sessionId, MessageRecord userMessage, CancellationToken ct) => throw Boom();

        public Task<CompleteTurnResult> CompleteTurnAsync(string userId, TurnLease lease, MessageRecord? assistantMessage, CancellationToken ct) => throw Boom();
    }
}
