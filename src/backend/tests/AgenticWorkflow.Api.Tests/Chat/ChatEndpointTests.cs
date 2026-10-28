using System.Net;
using Microsoft.Extensions.AI;
using AgenticWorkflow.Core.Storage;
using AgenticWorkflow.Core.Workflows;

namespace AgenticWorkflow.Api.Tests.Chat;

public class ChatEndpointTests : IDisposable
{
    private readonly ChatHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<List<SseEvent>> RunAsync(string sessionId, string content = "q")
    {
        using var sse = await SseResponse.PostAsync(_host.Client, sessionId, content);
        Assert.Equal(HttpStatusCode.OK, sse.Response.StatusCode);
        return await sse.ReadAllEventsAsync();
    }

    private async Task AssertLockReleasedAsync(string sessionId)
    {
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(new AgentChanged("A"), new TokenDelta("ok"), new TurnCompleted());
        await ChatHost.WaitUntilAsync(
            async () =>
            {
                using var again = await SseResponse.PostAsync(_host.Client, sessionId, "again");
                if (again.Response.StatusCode == HttpStatusCode.Conflict)
                {
                    return false;
                }

                Assert.Equal(HttpStatusCode.OK, again.Response.StatusCode);
                await again.ReadAllEventsAsync();
                return true;
            },
            "turn lock released");
    }

    // ---- cancellation ----

    [Fact]
    public async Task Client_abort_after_a_token_persists_the_cancelled_partial_with_the_turn_message_id_and_releases_the_lock()
    {
        _host.Workflow.Script = (_, ct) => ScriptedWorkflow.Hold(
            [new AgentChanged("A"), new TokenDelta("par"), new TokenDelta("tial")], new TaskCompletionSource().Task, [], ct);
        var id = await _host.NewSessionAsync();
        using var abort = new CancellationTokenSource();

        using (var sse = await SseResponse.PostAsync(_host.Client, id, "q", abort.Token))
        {
            await sse.ReadEventAsync(); // session
            await sse.ReadEventAsync(); // agent
            await sse.ReadEventAsync(); // token
            await sse.ReadEventAsync(); // token
            await abort.CancelAsync();
        }

        await ChatHost.WaitUntilAsync(async () => (await _host.MessagesAsync(id)).Count == 2, "partial persisted");
        var assistant = (await _host.MessagesAsync(id))[1]!;
        Assert.Equal("cancelled", assistant["status"]!.GetValue<string>());
        Assert.Equal("partial", assistant["content"]!.GetValue<string>());
        var lease = _host.Control.Completes.Single().Lease;
        Assert.Equal(lease.AssistantMessageId, assistant["id"]!.GetValue<string>());

        // Stop-then-send: the next turn is accepted once the aborted turn finished persisting.
        await AssertLockReleasedAsync(id);
    }

    [Fact]
    public async Task Client_abort_before_any_token_persists_no_assistant_message_but_releases_the_lock()
    {
        _host.Workflow.Script = (_, ct) => ScriptedWorkflow.Hold([new AgentChanged("A")], new TaskCompletionSource().Task, [], ct);
        var id = await _host.NewSessionAsync();
        using var abort = new CancellationTokenSource();

        using (var sse = await SseResponse.PostAsync(_host.Client, id, "q", abort.Token))
        {
            await sse.ReadEventAsync();
            await sse.ReadEventAsync();
            await abort.CancelAsync();
        }

        await ChatHost.WaitUntilAsync(() => Task.FromResult(_host.Control.Completes.Count == 1), "turn completed");
        Assert.Null(_host.Control.Completes[0].Message);
        var messages = await _host.MessagesAsync(id);
        Assert.Equal("user", Assert.Single(messages)!["role"]!.GetValue<string>());
        await AssertLockReleasedAsync(id);
    }

    // ---- end-of-turn store results ----

    [Theory]
    [InlineData(CompleteTurnResult.LeaseLost)]
    [InlineData(CompleteTurnResult.SessionGone)]
    public async Task Lost_lease_or_vanished_session_sends_error_internal_error_instead_of_done(CompleteTurnResult result)
    {
        _host.Control.CompleteOverride = result;
        var id = await _host.NewSessionAsync();

        var events = await RunAsync(id);

        Assert.Equal(["session", "agent", "token", "error"], events.Select(e => e.Name));
        Assert.Equal("internal_error", events[^1].Data["code"]!.GetValue<string>());
        Assert.Equal("Something went wrong. Please try again.", events[^1].Data["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Persistence_failure_sends_internal_error_and_still_releases_the_lock()
    {
        _host.Control.CompleteFailures = 1;
        var id = await _host.NewSessionAsync();

        var events = await RunAsync(id);

        Assert.Equal("internal_error", events[^1].Data["code"]!.GetValue<string>());
        await AssertLockReleasedAsync(id);
    }

    // ---- failures before the stream ----

    [Fact]
    public async Task Failure_before_the_session_event_is_a_500_problem_and_the_lock_is_released()
    {
        _host.Control.HistoryFailure = new InvalidOperationException("db down");
        var id = await _host.NewSessionAsync();

        var response = await _host.Client.PostAsync(
            $"/api/sessions/{id}/messages", Helpers.Json("""{"content":"hello there"}"""));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.ReadJsonAsync();
        Assert.Equal("internal_error", body["code"]!.GetValue<string>());
        Assert.NotNull(body["traceId"]);
        Assert.DoesNotContain("db down", body.ToJsonString());
        Assert.Empty(_host.Workflow.Requests);

        // Lock released (lock-only complete), user message stays as an orphan.
        Assert.Null(Assert.Single(_host.Control.Completes).Message);
        var messages = await _host.MessagesAsync(id);
        Assert.Equal("hello there", Assert.Single(messages)!["content"]!.GetValue<string>());

        _host.Control.HistoryFailure = null;
        var events = await RunAsync(id, "again");
        Assert.Equal("done", events[^1].Name);
    }

    [Fact]
    public async Task Unknown_session_and_foreign_session_are_identical_404s_except_traceId()
    {
        var mine = await _host.NewSessionAsync();
        var other = _host.Factory.CreateAuthorizedClient(TestTokens.NewOid());
        var foreign = await other.PostAsync($"/api/sessions/{mine}/messages", Helpers.Json("""{"content":"x"}"""));
        var unknown = await other.PostAsync($"/api/sessions/{Guid.CreateVersion7()}/messages", Helpers.Json("""{"content":"x"}"""));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await unknown.BodyWithoutTraceIdAsync(), await foreign.BodyWithoutTraceIdAsync());
        Assert.Empty(await _host.MessagesAsync(mine)); // nothing written for the foreign user
        Assert.Empty(_host.Workflow.Requests);
    }

    [Fact]
    public async Task Session_being_deleted_returns_404()
    {
        var id = await _host.NewSessionAsync();
        _host.Factory.Store.AfterDeleteLocked = () => throw new InvalidOperationException("crash after lock");
        var delete = await _host.Client.DeleteAsync($"/api/sessions/{id}");
        Assert.Equal(HttpStatusCode.InternalServerError, delete.StatusCode);

        var response = await _host.Client.PostAsync($"/api/sessions/{id}/messages", Helpers.Json("""{"content":"x"}"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", (await response.ReadJsonAsync())["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Session_whose_workflow_is_no_longer_registered_is_409_workflow_unavailable()
    {
        var session = ((AgenticWorkflow.Core.Storage.CreateSessionResult.Created)await _host.Factory.Store.CreateAsync(
            _host.User, "retired-workflow", null, false, CancellationToken.None)).Session;

        var response = await _host.Client.PostAsync($"/api/sessions/{session.Id}/messages", Helpers.Json("""{"content":"x"}"""));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("workflow_unavailable", (await response.ReadJsonAsync())["code"]!.GetValue<string>());
        Assert.Empty((await _host.GetSessionAsync(session.Id))["messages"]!.AsArray()); // nothing persisted
    }

    [Fact]
    public async Task Concurrent_turn_is_409_turn_in_progress_and_the_first_turn_is_unaffected()
    {
        var gate = new TaskCompletionSource();
        _host.Workflow.Script = (_, ct) => ScriptedWorkflow.Hold(
            [new AgentChanged("A"), new TokenDelta("x")], gate.Task, [new TurnCompleted()], ct);
        var id = await _host.NewSessionAsync();

        using var first = await SseResponse.PostAsync(_host.Client, id, "one");
        await first.ReadEventAsync();

        var second = await _host.Client.PostAsync($"/api/sessions/{id}/messages", Helpers.Json("""{"content":"two"}"""));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("turn_in_progress", (await second.ReadJsonAsync())["code"]!.GetValue<string>());

        var delete = await _host.Client.DeleteAsync($"/api/sessions/{id}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        gate.SetResult();
        var rest = await first.ReadAllEventsAsync();
        Assert.Equal("done", rest[^1].Name);
        Assert.Equal(2, (await _host.MessagesAsync(id)).Count);
    }

    // ---- validation ----

    [Theory]
    [InlineData("""{"content":""}""")]
    [InlineData("""{"content":"   "}""")]
    [InlineData("""{}""")]
    public async Task Invalid_content_is_400_validation_failed_before_any_stream(string body)
    {
        var id = await _host.NewSessionAsync();
        var response = await _host.Client.PostAsync($"/api/sessions/{id}/messages", Helpers.Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal("validation_failed", problem["code"]!.GetValue<string>());
        Assert.NotNull(problem["errors"]!["content"]);
        Assert.Empty(await _host.MessagesAsync(id));
    }

    [Fact]
    public async Task Content_limit_is_4000_characters_after_trim()
    {
        var id = await _host.NewSessionAsync();
        var tooLong = await _host.Client.PostAsync($"/api/sessions/{id}/messages", Helpers.Json($$"""{"content":"{{new string('a', 4001)}}"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        using var ok = await SseResponse.PostAsync(_host.Client, id, new string('a', 4000));
        Assert.Equal(HttpStatusCode.OK, ok.Response.StatusCode);
        await ok.ReadAllEventsAsync();
    }

    [Fact]
    public async Task Wrong_media_type_and_oversized_body_are_rejected_with_problem_details()
    {
        var id = await _host.NewSessionAsync();
        var text = await _host.Client.PostAsync($"/api/sessions/{id}/messages", new StringContent("hi", System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);

        var big = await _host.Client.PostAsync($"/api/sessions/{id}/messages", Helpers.Json($$"""{"content":"{{new string('a', 40_000)}}"}"""));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        Assert.Equal("payload_too_large", (await big.ReadJsonAsync())["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unauthenticated_request_is_401()
    {
        var id = await _host.NewSessionAsync();
        var response = await _host.Factory.CreateClient().PostAsync($"/api/sessions/{id}/messages", Helpers.Json("""{"content":"x"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- event protocol enforcement ----

    private async Task AssertFailedTurnAsync(string id, string expectedCode, string expectedContent)
    {
        var events = await RunAsync(id);
        var error = events[^1];
        Assert.Equal("error", error.Name);
        Assert.Equal(expectedCode, error.Data["code"]!.GetValue<string>());
        Assert.DoesNotContain(events, e => e.Name == "done");

        var assistant = (await _host.MessagesAsync(id))[^1]!;
        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        Assert.Equal("failed", assistant["status"]!.GetValue<string>());
        Assert.Equal(expectedCode, assistant["errorCode"]!.GetValue<string>());
        Assert.Equal(expectedContent, assistant["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task TurnCompleted_without_tokens_is_workflow_error()
    {
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(new AgentChanged("A"), new TurnCompleted());
        await AssertFailedTurnAsync(await _host.NewSessionAsync(), "workflow_error", string.Empty);
    }

    [Fact]
    public async Task Enumeration_ending_without_a_terminal_event_is_internal_error_keeping_the_partial_text()
    {
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(new AgentChanged("A"), new TokenDelta("half"));
        await AssertFailedTurnAsync(await _host.NewSessionAsync(), "internal_error", "half");
    }

    [Fact]
    public async Task Second_AgentChanged_is_a_protocol_violation()
    {
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(new AgentChanged("A"), new TokenDelta("x"), new AgentChanged("B"), new TurnCompleted());
        await AssertFailedTurnAsync(await _host.NewSessionAsync(), "workflow_error", "x");
    }

    [Fact]
    public async Task Token_before_AgentChanged_is_a_protocol_violation_and_nothing_is_streamed_for_it()
    {
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(new TokenDelta("x"), new AgentChanged("A"), new TurnCompleted());
        var id = await _host.NewSessionAsync();
        await AssertFailedTurnAsync(id, "workflow_error", string.Empty);
    }

    [Fact]
    public async Task Token_after_a_citation_is_a_protocol_violation()
    {
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(
            new AgentChanged("A"), new TokenDelta("x"), new CitationFound("S1", "t", "d", null), new TokenDelta("y"), new TurnCompleted());
        await AssertFailedTurnAsync(await _host.NewSessionAsync(), "workflow_error", "x");
    }

    [Fact]
    public async Task TurnFailed_codes_come_from_the_table_and_unknown_codes_become_internal_error()
    {
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(new AgentChanged("A"), new TurnFailed("content_filtered"));
        var events = await RunAsync(await _host.NewSessionAsync());
        Assert.Equal("This request could not be processed. Please rephrase.", events[^1].Data["message"]!.GetValue<string>());

        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(new TurnFailed("<script>boom</script>"));
        events = await RunAsync(await _host.NewSessionAsync());
        Assert.Equal(["session", "error"], events.Select(e => e.Name));
        Assert.Equal("internal_error", events[^1].Data["code"]!.GetValue<string>());
        Assert.DoesNotContain("script", events[^1].Raw);
    }

    [Theory]
    [InlineData("workflow", "model_error")]
    [InlineData("plain", "internal_error")]
    [InlineData("unknowncode", "internal_error")]
    [InlineData("oce", "internal_error")]
    public async Task Exceptions_are_mapped_and_never_leak_their_message(string kind, string expectedCode)
    {
        const string Secret = "secret-detail-xyz";
        Exception exception = kind switch
        {
            "workflow" => new WorkflowException("model_error", Secret),
            "unknowncode" => new WorkflowException("totally_made_up", Secret),
            "oce" => new OperationCanceledException(Secret), // neither client abort nor turn timeout
            _ => new InvalidOperationException(Secret),
        };
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Throw([new AgentChanged("A"), new TokenDelta("t")], exception);
        var id = await _host.NewSessionAsync();

        var events = await RunAsync(id);

        Assert.Equal(expectedCode, events[^1].Data["code"]!.GetValue<string>());
        Assert.DoesNotContain(Secret, events[^1].Raw);
        Assert.DoesNotContain(_host.Factory.Logs.Messages, m => m.Contains(Secret));
        Assert.Equal("t", (await _host.MessagesAsync(id))[1]!["content"]!.GetValue<string>());
    }

    // ---- history ----

    [Fact]
    public async Task History_excludes_the_current_message_and_orphans_and_strips_markers()
    {
        var id = await _host.NewSessionAsync();

        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(
            new AgentChanged("Claims specialist"), new TokenDelta("Ten days [S1]."), new TurnCompleted(MessageKind.Answer, new() { ["category"] = "claims" }));
        await RunAsync(id, "first question");

        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(new AgentChanged("A"), new TurnFailed("model_error"));
        await RunAsync(id, "failed question");

        List<WorkflowRunRequest> seen = [];
        _host.Workflow.Script = (r, _) =>
        {
            seen.Add(r);
            return ScriptedWorkflow.Seq(new AgentChanged("A"), new TokenDelta("fine"), new TurnCompleted());
        };
        await RunAsync(id, "third question");

        var request = Assert.Single(seen);
        Assert.Equal("third question", request.UserMessage);
        Assert.Equal(_host.User, request.UserId);
        Assert.Equal(id, request.SessionId);
        Assert.Equal(_host.Control.Completes[^1].Lease.LeaseId, request.TurnId);
        Assert.Equal(["first question", "Ten days ."], request.History.Select(h => h.Content));
        Assert.Equal([ChatRole.User, ChatRole.Assistant], request.History.Select(h => h.Role));
        Assert.Equal("Claims specialist", request.History[1].AgentName);
        Assert.Equal("claims", request.History[1].WorkflowMetadata!["category"]!.GetValue<string>());
    }

    [Fact]
    public async Task First_turn_has_empty_history()
    {
        List<WorkflowRunRequest> seen = [];
        _host.Workflow.Script = (r, _) =>
        {
            seen.Add(r);
            return ScriptedWorkflow.Seq(new AgentChanged("A"), new TokenDelta("x"), new TurnCompleted());
        };
        await RunAsync(await _host.NewSessionAsync(), "hello");
        Assert.Empty(Assert.Single(seen).History);
    }
}
