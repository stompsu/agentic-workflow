using System.Net;
using System.Text;

namespace AgenticWorkflow.Api.Tests;

public class SessionEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = Fixtures.WithThrowawayWorkflow();
    private readonly string _userA = TestTokens.NewOid();
    private readonly string _userB = TestTokens.NewOid();

    public void Dispose() => _factory.Dispose();

    private HttpClient A => _factory.CreateAuthorizedClient(_userA);

    private HttpClient B => _factory.CreateAuthorizedClient(_userB);

    [Fact]
    public async Task Create_returns_201_with_Location_and_the_contract_shape()
    {
        var response = await A.PostAsync("/api/sessions", Helpers.Json("""{"workflowId":"throwaway-test","title":"  My claim  "}"""));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.ReadJsonAsync();
        var id = body["id"]!.GetValue<string>();
        Assert.Equal($"/api/sessions/{id}", response.Headers.Location!.OriginalString);
        Assert.Equal(Guid.Parse(id).ToString("D"), id);
        Assert.Equal(7, Guid.Parse(id).Version);
        Assert.Equal("My claim", body["title"]!.GetValue<string>());
        Assert.Equal("throwaway-test", body["workflowId"]!.GetValue<string>());
        Assert.Equal("2026-10-25T10:00:00.000Z", body["createdAt"]!.GetValue<string>());
        Assert.Equal("2026-10-25T10:00:00.000Z", body["updatedAt"]!.GetValue<string>());
    }

    [Fact]
    public async Task Create_without_title_returns_an_explicit_null_title()
    {
        var response = await A.PostAsync("/api/sessions", Helpers.Json("""{"workflowId":"throwaway-test"}"""));
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"title\":null", raw);
    }

    [Theory]
    [InlineData("""{"workflowId":"nope"}""", "workflowId")]
    [InlineData("""{"workflowId":""}""", "workflowId")]
    [InlineData("""{}""", "workflowId")]
    [InlineData("""{"workflowId":"throwaway-test","title":"   "}""", "title")]
    [InlineData("""{"workflowId":"throwaway-test","title":""}""", "title")]
    public async Task Create_validation_failures_are_400_validation_failed(string body, string field)
    {
        var response = await A.PostAsync("/api/sessions", Helpers.Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal("validation_failed", problem["code"]!.GetValue<string>());
        Assert.NotNull(problem["errors"]![field]);
    }

    [Fact]
    public async Task Title_longer_than_200_characters_is_rejected_and_200_is_accepted()
    {
        var tooLong = await A.PostAsync("/api/sessions", Helpers.Json($$"""{"workflowId":"throwaway-test","title":"{{new string('t', 201)}}"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var ok = await A.PostAsync("/api/sessions", Helpers.Json($$"""{"workflowId":"throwaway-test","title":"{{new string('t', 200)}}"}"""));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task Invalid_json_and_empty_bodies_are_400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await A.PostAsync("/api/sessions", Helpers.Json("{not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await A.PostAsync("/api/sessions", Helpers.Json(""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await A.PostAsync("/api/sessions", Helpers.Json("null"))).StatusCode);
    }

    [Fact]
    public async Task Non_json_content_type_is_415()
    {
        var response = await A.PostAsync("/api/sessions", new StringContent("workflowId=x", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("unsupported_media_type", (await response.ReadJsonAsync())["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Body_larger_than_32KB_is_413()
    {
        var big = $$"""{"workflowId":"throwaway-test","title":"{{new string('x', 40 * 1024)}}"}""";
        var response = await A.PostAsync("/api/sessions", Helpers.Json(big));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("payload_too_large", (await response.ReadJsonAsync())["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task List_is_scoped_to_the_user_and_ordered_by_latest_activity()
    {
        var first = await A.CreateSessionAsync(title: "first");
        _factory.Time.Advance(TimeSpan.FromMinutes(1));
        var second = await A.CreateSessionAsync(title: "second");
        await B.CreateSessionAsync(title: "b's");

        var items = (await (await A.GetAsync("/api/sessions")).ReadJsonAsync())["items"]!.AsArray();
        Assert.Equal([second, first], items.Select(i => i!["id"]!.GetValue<string>()));

        var bItems = (await (await B.GetAsync("/api/sessions")).ReadJsonAsync())["items"]!.AsArray();
        Assert.Equal("b's", Assert.Single(bItems)!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task List_pages_with_an_opaque_continuation_token()
    {
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await A.CreateSessionAsync());
            _factory.Time.Advance(TimeSpan.FromSeconds(1));
        }

        var page1 = await (await A.GetAsync("/api/sessions?pageSize=2")).ReadJsonAsync();
        Assert.Equal(2, page1["items"]!.AsArray().Count);
        var token = page1["continuationToken"]!.GetValue<string>();

        var page2 = await (await A.GetAsync($"/api/sessions?pageSize=2&continuationToken={Uri.EscapeDataString(token)}")).ReadJsonAsync();
        Assert.Equal(ids[0], Assert.Single(page2["items"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.Null(page2["continuationToken"]);
        Assert.Contains("\"continuationToken\":null", page2.ToJsonString());
    }

    [Theory]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=51")]
    [InlineData("pageSize=abc")]
    [InlineData("pageSize=-1")]
    [InlineData("continuationToken=%21%21bad%21%21")]
    public async Task List_bad_query_is_400_validation_failed(string query)
    {
        var response = await A.GetAsync($"/api/sessions?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", (await response.ReadJsonAsync())["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Get_returns_the_session_with_messages_ascending()
    {
        var id = await A.CreateSessionAsync(title: "chat");
        var lease = await StartTurnAsync(_userA, id, "How long does a claim take?");
        _factory.Time.Advance(TimeSpan.FromSeconds(2));
        await _factory.Store.CompleteTurnAsync(_userA, lease, Assistant(id, "Within 10 days [S1].", _factory.Time.GetUtcNow()), default);

        var response = await A.GetAsync($"/api/sessions/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.False(body["hasMoreMessages"]!.GetValue<bool>());
        var messages = body["messages"]!.AsArray();
        Assert.Equal(["user", "assistant"], messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("complete", messages[0]!["status"]!.GetValue<string>());
        Assert.Equal("answer", messages[1]!["kind"]!.GetValue<string>());
        Assert.Equal("Claims specialist", messages[1]!["agentName"]!.GetValue<string>());
        Assert.Equal("S1", messages[1]!["citations"]![0]!["ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task Responses_never_expose_internal_fields()
    {
        var id = await A.CreateSessionAsync(title: "chat");
        var lease = await StartTurnAsync(_userA, id, "q");
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        await _factory.Store.CompleteTurnAsync(_userA, lease, Assistant(id, "a", _factory.Time.GetUtcNow()), default);
        var lease2 = await StartTurnAsync(_userA, id, "q2"); // leaves an activeTurn behind

        var responses = new[]
        {
            await A.GetAsync($"/api/sessions/{id}"),
            await A.GetAsync("/api/sessions"),
            await A.PostAsync("/api/sessions", Helpers.Json("""{"workflowId":"throwaway-test"}""")),
            await A.GetAsync("/api/workflows"),
        };

        foreach (var response in responses)
        {
            var names = Helpers.AllPropertyNames(await response.ReadJsonAsync()).ToList();
            Assert.Empty(names.Intersect(Helpers.InternalFields, StringComparer.OrdinalIgnoreCase));
        }

        Assert.NotNull(lease2);
    }

    [Fact]
    public async Task Failed_assistant_messages_carry_errorCode_and_user_messages_carry_no_assistant_fields()
    {
        var id = await A.CreateSessionAsync();
        var lease = await StartTurnAsync(_userA, id, "q");
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        await _factory.Store.CompleteTurnAsync(
            _userA, lease, Assistant(id, "part", _factory.Time.GetUtcNow(), AgenticWorkflow.Core.Storage.MessageStatus.Failed), default);

        var messages = (await (await A.GetAsync($"/api/sessions/{id}")).ReadJsonAsync())["messages"]!.AsArray();
        var user = messages[0]!.AsObject();
        Assert.False(user.ContainsKey("kind") || user.ContainsKey("agentName") || user.ContainsKey("citations") || user.ContainsKey("errorCode"));
        Assert.Equal("failed", messages[1]!["status"]!.GetValue<string>());
        Assert.Equal("model_error", messages[1]!["errorCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Get_of_a_foreign_unknown_or_malformed_id_is_an_identical_404_except_traceId()
    {
        var foreign = await A.CreateSessionAsync();

        var asForeign = await B.GetAsync($"/api/sessions/{foreign}");
        var unknown = await B.GetAsync($"/api/sessions/{Guid.CreateVersion7()}");
        var malformed = await B.GetAsync("/api/sessions/not-a-guid");

        foreach (var r in new[] { asForeign, unknown, malformed })
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
        }

        var expected = await asForeign.BodyWithoutTraceIdAsync();
        Assert.Equal(expected, await unknown.BodyWithoutTraceIdAsync());
        Assert.Equal(expected, await malformed.BodyWithoutTraceIdAsync());
        Assert.Contains("\"code\":\"not_found\"", expected);
    }

    [Fact]
    public async Task Delete_of_a_foreign_unknown_or_malformed_id_is_an_identical_404_except_traceId_and_leaves_the_session()
    {
        var foreign = await A.CreateSessionAsync();

        var asForeign = await B.DeleteAsync($"/api/sessions/{foreign}");
        var unknown = await B.DeleteAsync($"/api/sessions/{Guid.CreateVersion7()}");
        var malformed = await B.DeleteAsync("/api/sessions/xyz");

        Assert.All(new[] { asForeign, unknown, malformed }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        var expected = await asForeign.BodyWithoutTraceIdAsync();
        Assert.Equal(expected, await unknown.BodyWithoutTraceIdAsync());
        Assert.Equal(expected, await malformed.BodyWithoutTraceIdAsync());
        Assert.Equal(HttpStatusCode.OK, (await A.GetAsync($"/api/sessions/{foreign}")).StatusCode);
    }

    [Fact]
    public async Task Delete_returns_204_then_the_session_is_404_everywhere()
    {
        var id = await A.CreateSessionAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await A.DeleteAsync($"/api/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await A.GetAsync($"/api/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await A.DeleteAsync($"/api/sessions/{id}")).StatusCode);
        Assert.Empty((await (await A.GetAsync("/api/sessions")).ReadJsonAsync())["items"]!.AsArray());
    }

    [Fact]
    public async Task Delete_during_an_active_turn_is_409_turn_in_progress_and_works_after_the_turn()
    {
        var id = await A.CreateSessionAsync();
        var lease = await StartTurnAsync(_userA, id, "q");

        var busy = await A.DeleteAsync($"/api/sessions/{id}");
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal("turn_in_progress", (await busy.ReadJsonAsync())["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await A.GetAsync($"/api/sessions/{id}")).StatusCode);

        await _factory.Store.CompleteTurnAsync(_userA, lease, null, default);
        Assert.Equal(HttpStatusCode.NoContent, (await A.DeleteAsync($"/api/sessions/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_stale_lock_does_not_block_delete()
    {
        var id = await A.CreateSessionAsync();
        await StartTurnAsync(_userA, id, "q");
        _factory.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.NoContent, (await A.DeleteAsync($"/api/sessions/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_session_being_deleted_is_404_hidden_from_the_list_and_a_repeated_delete_resumes()
    {
        var id = await A.CreateSessionAsync();
        _factory.Store.AfterDeleteLocked = () => throw new InvalidOperationException("simulated crash after the lock");

        var crashed = await A.DeleteAsync($"/api/sessions/{id}");
        Assert.Equal(HttpStatusCode.InternalServerError, crashed.StatusCode);
        Assert.Equal("internal_error", (await crashed.ReadJsonAsync())["code"]!.GetValue<string>());
        _factory.Store.AfterDeleteLocked = null;

        Assert.Equal(HttpStatusCode.NotFound, (await A.GetAsync($"/api/sessions/{id}")).StatusCode);
        Assert.Empty((await (await A.GetAsync("/api/sessions")).ReadJsonAsync())["items"]!.AsArray());

        Assert.Equal(HttpStatusCode.NoContent, (await A.DeleteAsync($"/api/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await A.DeleteAsync($"/api/sessions/{id}")).StatusCode);
    }

    [Fact]
    public async Task Server_errors_do_not_leak_details()
    {
        var id = await A.CreateSessionAsync();
        _factory.Store.AfterDeleteLocked = () => throw new InvalidOperationException("SECRET-INTERNAL-DETAIL");
        var response = await A.DeleteAsync($"/api/sessions/{id}");
        Assert.DoesNotContain("SECRET-INTERNAL-DETAIL", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Logs_contain_no_tokens_titles_or_user_message_content()
    {
        var token = TestTokens.Create(_userA, extraClaims: new Dictionary<string, object> { ["preferred_username"] = "person@example.com", ["name"] = "Some Person" });
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var id = await client.CreateSessionAsync(title: "TITLE-MARKER-123");
        await StartTurnAsync(_userA, id, "MESSAGE-MARKER-456");
        await client.GetAsync($"/api/sessions/{id}");
        await client.DeleteAsync($"/api/sessions/{id}");
        await _factory.CreateClient().GetAsync("/api/sessions");

        var logs = string.Join('\n', _factory.Logs.Messages);
        Assert.NotEmpty(logs);
        foreach (var forbidden in new[] { token, "TITLE-MARKER-123", "MESSAGE-MARKER-456", "person@example.com", "Some Person" })
        {
            Assert.DoesNotContain(forbidden, logs);
        }
    }

    private async Task<AgenticWorkflow.Core.Storage.TurnLease> StartTurnAsync(string user, string sessionId, string text)
    {
        var message = new AgenticWorkflow.Core.Storage.MessageRecord(
            Guid.CreateVersion7().ToString(), sessionId, Microsoft.Extensions.AI.ChatRole.User, text, _factory.Time.GetUtcNow(),
            AgenticWorkflow.Core.Storage.MessageStatus.Complete, null, null, [], null, null);
        var result = await _factory.Store.TryStartTurnAsync(user, sessionId, message, false, default);
        return Assert.IsType<AgenticWorkflow.Core.Storage.StartTurnResult.Started>(result).Lease;
    }

    private static AgenticWorkflow.Core.Storage.MessageRecord Assistant(
        string sessionId, string content, DateTimeOffset at, AgenticWorkflow.Core.Storage.MessageStatus status = AgenticWorkflow.Core.Storage.MessageStatus.Complete) =>
        new(Guid.CreateVersion7().ToString(), sessionId, Microsoft.Extensions.AI.ChatRole.Assistant, content, at, status,
            AgenticWorkflow.Core.Workflows.MessageKind.Answer, "Claims specialist",
            [new AgenticWorkflow.Core.Storage.CitationRecord("S1", "Claims handling times", "claims-0003", "https://kb.example.com/c")],
            status == AgenticWorkflow.Core.Storage.MessageStatus.Failed ? "model_error" : null,
            new System.Text.Json.Nodes.JsonObject { ["category"] = "claims", ["confidence"] = 0.9 });
}
