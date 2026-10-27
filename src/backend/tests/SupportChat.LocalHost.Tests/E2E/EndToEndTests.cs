using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using SupportChat.LocalHost.Auth;
using SupportChat.Infrastructure.Search.Source;

namespace SupportChat.LocalHost.Tests.E2E;

/// <summary>The real LocalHost over HTTP/SSE: real local tokens, in-memory stores, in-memory knowledge, scripted model.</summary>
public class EndToEndTests : IClassFixture<DefaultLocalHost>
{
    private readonly DefaultLocalHost _factory;

    public EndToEndTests(DefaultLocalHost factory) => _factory = factory;

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/sessions", Http.Json("""{"workflowId":"support-ticket"}"""));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
    }

    private static HttpRequestMessage Message(string sessionId, string content)
    {
        var request = Http.Post($"/api/sessions/{sessionId}/messages", new JsonObject { ["content"] = content }.ToJsonString());
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    private static async Task<List<SseEvent>> SendAsync(HttpClient client, string sessionId, string content)
    {
        var response = await client.SendAsync(Message(sessionId, content));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return await SseReader.ReadAllAsync(response);
    }

    private static string Text(IEnumerable<SseEvent> events) =>
        string.Concat(events.Where(e => e.Type == "token").Select(e => e.Data["text"]!.GetValue<string>()));

    [Fact]
    public async Task Claims_question_streams_session_agent_tokens_citation_done()
    {
        using var client = await _factory.CreateUserClientAsync("alice");
        var sessionId = await CreateSessionAsync(client);

        var events = await SendAsync(client, sessionId, "How do I file a claim?");

        Assert.Equal(sessionId, events[0].Data["sessionId"]!.GetValue<string>());
        Assert.Equal("session", events[0].Type);
        Assert.Equal("agent", events[1].Type);
        Assert.Equal("Claims specialist", events[1].Data["name"]!.GetValue<string>());
        Assert.Contains(events, e => e.Type == "token");
        Assert.Equal("done", events[^1].Type);

        var citations = events.Where(e => e.Type == "citation").ToList();
        Assert.NotEmpty(citations);
        var chunkIds = IngestionPlan.Build(KnowledgeSource.Read(Path.Combine(AppContext.BaseDirectory, "knowledge")), 800, 80).Select(c => c.Id).ToHashSet();
        Assert.All(citations, c => Assert.Contains(c.Data["docId"]!.GetValue<string>(), chunkIds));

        // Every marker in the answer is a cited ref.
        var text = Text(events);
        var cited = citations.Select(c => c.Data["ref"]!.GetValue<string>()).ToHashSet();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\[(S\d+)\]"))
        {
            Assert.Contains(m.Groups[1].Value, cited);
        }

        var session = (await (await client.GetAsync($"/api/sessions/{sessionId}")).Content.ReadFromJsonAsync<JsonObject>())!;
        var messages = session["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);
        Assert.Equal("answer", messages[1]!["kind"]!.GetValue<string>());
        Assert.Equal("complete", messages[1]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ambiguous_message_gets_a_clarification()
    {
        using var client = await _factory.CreateUserClientAsync("alice");
        var sessionId = await CreateSessionAsync(client);

        var events = await SendAsync(client, sessionId, "Hello there");

        Assert.Equal("done", events[^1].Type);
        Assert.DoesNotContain(events, e => e.Type == "citation");
        Assert.Contains("policy, a claim, or a health assessment", Text(events));

        var session = (await (await client.GetAsync($"/api/sessions/{sessionId}")).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("clarification", session["messages"]![1]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Another_user_gets_404_on_the_session()
    {
        using var alice = await _factory.CreateUserClientAsync("alice");
        using var bob = await _factory.CreateUserClientAsync("bob");
        var sessionId = await CreateSessionAsync(alice);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/sessions/{sessionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.SendAsync(Message(sessionId, "How do I file a claim?"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/sessions/{sessionId}")).StatusCode);

        var bobList = (await (await bob.GetAsync("/api/sessions")).Content.ReadFromJsonAsync<JsonObject>())!["items"]!.AsArray();
        Assert.DoesNotContain(bobList, i => i!["id"]!.GetValue<string>() == sessionId);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/sessions/{sessionId}")).StatusCode);
    }

    [Fact]
    public async Task Delete_my_data_returns_204_and_removes_the_sessions()
    {
        using var client = await _factory.CreateUserClientAsync("carol");
        var sessionId = await CreateSessionAsync(client);
        await SendAsync(client, sessionId, "How do I file a claim?");

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/me/data")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/sessions/{sessionId}")).StatusCode);
    }

    [Fact]
    public async Task Client_abort_persists_a_cancelled_partial_message()
    {
        using var slow = new LocalHostFactory(c => c["LocalMode:Scripted:TokenDelay"] = "00:00:00.200");
        using var client = await slow.CreateUserClientAsync("dave");
        var sessionId = await CreateSessionAsync(client);

        using var cts = new CancellationTokenSource();
        using (var response = await client.SendAsync(Message(sessionId, "How do I file a claim?"), HttpCompletionOption.ResponseHeadersRead, cts.Token))
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            var sawToken = false;
            while (!sawToken)
            {
                var line = await reader.ReadLineAsync(cts.Token) ?? throw new InvalidOperationException("stream ended early");
                sawToken = line == "event: token";
            }

            await cts.CancelAsync();
        } // disposing the response is what the test server turns into RequestAborted

        JsonObject? last = null;
        for (var i = 0; i < 100; i++)
        {
            var session = (await (await client.GetAsync($"/api/sessions/{sessionId}")).Content.ReadFromJsonAsync<JsonObject>())!;
            last = session["messages"]!.AsArray().LastOrDefault()?.AsObject();
            if (last?["role"]?.GetValue<string>() == "assistant")
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.Equal("assistant", last?["role"]?.GetValue<string>());
        Assert.Equal("cancelled", last!["status"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(last["content"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Expired_and_wrong_issuer_tokens_get_401_with_problem_details()
    {
        var issuer = _factory.Services.GetService(typeof(LocalTokenIssuer)) as LocalTokenIssuer;
        Assert.NotNull(issuer);
        var now = DateTime.UtcNow;

        foreach (var token in new[]
        {
            issuer.CreateToken("alice", now.AddHours(-2), now.AddHours(-1)),
            issuer.CreateToken("alice", now, now.AddHours(1), issuer: "evil"),
        })
        {
            using var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var response = await client.GetAsync("/api/sessions");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task Echo_workflow_is_listed_locally()
    {
        using var client = await _factory.CreateUserClientAsync("alice");
        var ids = (await (await client.GetAsync("/api/workflows")).Content.ReadFromJsonAsync<JsonObject>())!["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).ToList();
        Assert.Contains("support-ticket", ids);
        Assert.Contains("echo", ids);
    }
}
