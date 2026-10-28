using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.AI;
using AgenticWorkflow.Core.Storage;
using AgenticWorkflow.Core.Workflows;

namespace AgenticWorkflow.Api.Tests.Chat;

/// <summary>DELETE /api/me/data (api-contract 2.8, data-model 1.7) against the real endpoints and the in-memory store.</summary>
public class DeleteMyDataTests : IDisposable
{
    private readonly ChatHost _host = new(c => c["Chat:DeleteUserDataBudget"] = "00:00:05");

    public void Dispose() => _host.Dispose();

    private Infrastructure.Storage.InMemory.InMemoryChatStore Store => _host.Factory.Store;

    private async Task<List<SseEvent>> RunAsync(HttpClient client, string sessionId, string content)
    {
        using var sse = await SseResponse.PostAsync(client, sessionId, content);
        Assert.Equal(HttpStatusCode.OK, sse.Response.StatusCode);
        return await sse.ReadAllEventsAsync();
    }

    [Fact]
    public async Task User_without_data_gets_204_with_no_body_and_a_repeated_call_204_again()
    {
        for (var i = 0; i < 2; i++)
        {
            var response = await _host.Client.DeleteAsync("/api/me/data");
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task A_request_body_is_ignored()
    {
        var response = await _host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/me/data")
        {
            Content = Helpers.Json("""{"userId":"someone-else"}"""),
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Requires_a_token()
    {
        var response = await _host.Factory.CreateClient().DeleteAsync("/api/me/data");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", (await response.ReadJsonAsync())["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Deletes_all_sessions_messages_and_orphans_of_the_caller_and_leaves_other_users_alone()
    {
        var otherOid = TestTokens.NewOid();
        var other = _host.Factory.CreateAuthorizedClient(otherOid);
        var otherId = await _host.NewSessionAsync(other);
        await RunAsync(other, otherId, "other user question");
        var otherDocuments = Store.DocumentCount(otherOid);
        Assert.Equal(3, otherDocuments);

        var s1 = await _host.NewSessionAsync();
        var s2 = await _host.NewSessionAsync();
        await RunAsync(_host.Client, s1, "first");
        await RunAsync(_host.Client, s1, "second");
        await RunAsync(_host.Client, s2, "third");
        Store.SeedOrphanMessage(_host.User, Orphan());
        Assert.Equal(2 + 6 + 1, Store.DocumentCount(_host.User));

        var response = await _host.Client.DeleteAsync("/api/me/data");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, Store.DocumentCount(_host.User));
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.GetAsync($"/api/sessions/{s1}")).StatusCode);
        Assert.Empty((await (await _host.Client.GetAsync("/api/sessions")).ReadJsonAsync())["items"]!.AsArray());

        // The other user is untouched.
        Assert.Equal(otherDocuments, Store.DocumentCount(otherOid));
        var messages = (await _host.GetSessionAsync(otherId, other))["messages"]!.AsArray();
        Assert.Equal(2, messages.Count);

        // Idempotent.
        Assert.Equal(HttpStatusCode.NoContent, (await _host.Client.DeleteAsync("/api/me/data")).StatusCode);
    }

    [Fact]
    public async Task Budget_exhaustion_is_503_deletion_incomplete_with_Retry_After_then_a_repeat_completes()
    {
        var s1 = await _host.NewSessionAsync();
        var s2 = await _host.NewSessionAsync();
        await RunAsync(_host.Client, s1, "one");
        await RunAsync(_host.Client, s2, "two");

        // The budget (5 s) runs out right after the first session was marked.
        var calls = 0;
        Store.OnDeleteAllProgress = () =>
        {
            if (++calls == 1)
            {
                _host.Factory.Time.Advance(TimeSpan.FromSeconds(10));
            }
        };

        var response = await _host.Client.DeleteAsync("/api/me/data");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("1", Assert.Single(response.Headers.GetValues("Retry-After")));
        var body = await response.ReadJsonAsync();
        Assert.Equal("deletion_incomplete", body["code"]!.GetValue<string>());
        Assert.Equal(503, body["status"]!.GetValue<int>());
        Assert.NotNull(body["traceId"]);

        // Progress is kept: exactly one session is already invisible, the other is still readable.
        var visible = new[] { s1, s2 }.Count(id => Store.IsDeleting(_host.User, id) is false);
        Assert.Equal(1, visible);

        Store.OnDeleteAllProgress = null;
        Assert.Equal(HttpStatusCode.NoContent, (await _host.Client.DeleteAsync("/api/me/data")).StatusCode);
        Assert.Equal(0, Store.DocumentCount(_host.User));
    }

    [Fact]
    public async Task Takes_over_a_live_lock_the_in_flight_turn_ends_with_internal_error_and_persists_nothing()
    {
        var gate = new TaskCompletionSource();
        _host.Workflow.Script = (_, ct) => ScriptedWorkflow.Hold(
            [new AgentChanged("A"), new TokenDelta("par"), new TokenDelta("tial")], gate.Task, [new TokenDelta("more"), new TurnCompleted()], ct);
        var id = await _host.NewSessionAsync();

        using var sse = await SseResponse.PostAsync(_host.Client, id, "streaming question");
        Assert.Equal("session", (await sse.ReadEventAsync())!.Name);
        await sse.ReadEventAsync(); // agent
        await sse.ReadEventAsync(); // token
        await sse.ReadEventAsync(); // token

        // The turn is live: a session DELETE would be 409, the user-data deletion takes the lock anyway.
        Assert.Equal(HttpStatusCode.Conflict, (await _host.Client.DeleteAsync($"/api/sessions/{id}")).StatusCode);
        var calls = 0;
        Store.OnDeleteAllProgress = () =>
        {
            // Stop right after the mark so the session document (with the taken-over lock) is still there: LeaseLost.
            if (++calls == 1)
            {
                _host.Factory.Time.Advance(TimeSpan.FromSeconds(10));
            }
        };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _host.Client.DeleteAsync("/api/me/data")).StatusCode);
        Store.OnDeleteAllProgress = null;

        // From the mark on the session is invisible and cannot start turns.
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client.GetAsync($"/api/sessions/{id}")).StatusCode);
        var post = await _host.Client.PostAsync($"/api/sessions/{id}/messages", Helpers.Json("""{"content":"hello"}"""));
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);

        gate.SetResult();
        var rest = await sse.ReadAllEventsAsync();

        Assert.Equal("error", rest[^1].Name);
        Assert.Equal("internal_error", rest[^1].Data["code"]!.GetValue<string>());
        Assert.DoesNotContain(rest, e => e.Name == "done");
        var complete = Assert.Single(_host.Control.Completes);
        Assert.NotNull(complete.Message);

        // Nothing was persisted by the lost lease: only the user message exists, until the repeated call removes it.
        Assert.Equal(2, Store.DocumentCount(_host.User)); // session (deleting) + user message, no assistant message
        Assert.Equal(HttpStatusCode.NoContent, (await _host.Client.DeleteAsync("/api/me/data")).StatusCode);
        Assert.Equal(0, Store.DocumentCount(_host.User));
    }

    [Fact]
    public async Task Is_not_covered_by_the_messages_rate_limiter()
    {
        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await _host.Client.DeleteAsync("/api/me/data")).StatusCode);
        }
    }

    [Fact]
    public async Task Cannot_address_another_user_the_token_oid_is_always_used()
    {
        var otherOid = TestTokens.NewOid();
        var other = _host.Factory.CreateAuthorizedClient(otherOid);
        var otherId = await _host.NewSessionAsync(other);

        var response = await _host.Client.DeleteAsync($"/api/me/data?userId={otherOid}&oid={otherOid}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"/api/sessions/{otherId}")).StatusCode);
    }

    [Fact]
    public async Task Logs_carry_counts_and_the_user_id_but_never_titles_or_content()
    {
        var id = await _host.Client.CreateSessionAsync(ScriptedWorkflow.WorkflowId, "Secret title");
        await RunAsync(_host.Client, id, "secret content");

        Assert.Equal(HttpStatusCode.NoContent, (await _host.Client.DeleteAsync("/api/me/data")).StatusCode);

        var logs = string.Join('\n', _host.Factory.Logs.Messages);
        Assert.Contains("Delete my data finished", logs);
        Assert.Contains(_host.User, logs);
        Assert.DoesNotContain("Secret title", logs);
        Assert.DoesNotContain("secret content", logs);
    }

    private static MessageRecord Orphan() =>
        new(Guid.CreateVersion7().ToString(), Guid.NewGuid().ToString(), ChatRole.User, "orphan", DateTimeOffset.UtcNow,
            MessageStatus.Complete, null, null, [], null, null);
}
