using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.AI;
using AgenticWorkflow.Api.Tests.Chat;
using AgenticWorkflow.Core.Storage;
using AgenticWorkflow.Core.Workflows;

namespace AgenticWorkflow.Api.Tests.Guests;

/// <summary>Guest behavior on the endpoints: filtering, quotas, ttl, delete, caches (api-contract 2.9, ADR 0006 sections 6, 8).</summary>
public sealed class GuestFlowTests : IDisposable
{
    private readonly GuestHost _host;

    public GuestFlowTests() => _host = new GuestHost(c => c["Guest:MaxTurns"] = "3");

    public void Dispose() => _host.Dispose();

    private ChatHost Chat => _host.Host;

    private Infrastructure.Storage.InMemory.InMemoryChatStore Store => _host.Factory.Store;

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(code, (await response.ReadJsonAsync())["code"]!.GetValue<string>());
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string sessionId, string content = "hi")
    {
        var response = await client.PostAsync($"/api/sessions/{sessionId}/messages", Helpers.Json($$"""{"content":"{{content}}"}"""));
        await response.Content.ReadAsStringAsync(); // drain the SSE body
        return response;
    }

    private static Task<HttpResponseMessage> CreateSessionRawAsync(HttpClient client, string workflowId = ScriptedWorkflow.WorkflowId) =>
        client.PostAsync("/api/sessions", Helpers.Json($$"""{"workflowId":"{{workflowId}}"}"""));

    // ---- workflow filtering ----

    [Fact]
    public async Task Workflows_list_and_session_creation_are_filtered_for_guests_but_not_for_signed_in_users()
    {
        var (_, _, guest) = await _host.NewGuestAsync();

        var guestList = (await (await guest.GetAsync("/api/workflows")).ReadJsonAsync())["items"]!.AsArray();
        Assert.Equal([ScriptedWorkflow.WorkflowId], guestList.Select(w => w!["id"]!.GetValue<string>()));
        var socialList = (await (await Chat.Client.GetAsync("/api/workflows")).ReadJsonAsync())["items"]!.AsArray();
        Assert.Contains("support-ticket", socialList.Select(w => w!["id"]!.GetValue<string>()));

        // Not enabled for guests: reported like an unknown id.
        await AssertProblemAsync(await CreateSessionRawAsync(guest, "support-ticket"), HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(HttpStatusCode.Created, (await CreateSessionRawAsync(Chat.Client, "support-ticket")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CreateSessionRawAsync(guest)).StatusCode);
    }

    [Fact]
    public async Task A_guest_session_whose_workflow_is_not_enabled_for_guests_is_409_workflow_unavailable_and_consumes_no_quota()
    {
        var (_, guestId, guest) = await _host.NewGuestAsync();
        var created = Assert.IsType<CreateSessionResult.Created>(await Store.CreateAsync(guestId, "support-ticket", null, false, default));

        await AssertProblemAsync(await PostAsync(guest, created.Session.Id), HttpStatusCode.Conflict, "workflow_unavailable");
        Assert.Equal(0, Store.GuestDocument(guestId)!.TurnCount);
    }

    // ---- session quota ----

    [Fact]
    public async Task Session_quota_is_429_guest_quota_exceeded_without_Retry_After_and_nothing_is_persisted()
    {
        using var host = new GuestHost(c => c["Guest:MaxSessions"] = "2");
        var (_, guestId, guest) = await host.NewGuestAsync();
        await host.Host.NewSessionAsync(guest);
        var second = await host.Host.NewSessionAsync(guest);

        var over = await CreateSessionRawAsync(guest);
        await AssertProblemAsync(over, (HttpStatusCode)429, "guest_quota_exceeded");
        Assert.False(over.Headers.Contains("Retry-After"));
        Assert.Equal(2, host.Factory.Store.DocumentCount(guestId)); // the third session was never written

        // Deleting a session does not give a slot back.
        Assert.Equal(HttpStatusCode.NoContent, (await guest.DeleteAsync($"/api/sessions/{second}")).StatusCode);
        await AssertProblemAsync(await CreateSessionRawAsync(guest), (HttpStatusCode)429, "guest_quota_exceeded");
        Assert.Equal(2, host.Factory.Store.GuestDocument(guestId)!.SessionCount);
    }

    // ---- turn quota ----

    [Fact]
    public async Task Turn_quota_is_429_guest_quota_exceeded_without_Retry_After_persists_nothing_and_then_answers_from_the_cache()
    {
        var (_, guestId, guest) = await _host.NewGuestAsync();
        var session = await Chat.NewSessionAsync(guest);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(guest, session)).StatusCode);
        }

        await ChatHost.WaitUntilAsync(async () => (await GuestHost.MessagesAsync(guest, session)).Count == 6, "all turns persisted");
        var before = Store.DocumentCount(guestId);

        var over = await PostAsync(guest, session, "must-not-be-stored");
        await AssertProblemAsync(over, (HttpStatusCode)429, "guest_quota_exceeded");
        Assert.False(over.Headers.Contains("Retry-After"));
        Assert.Equal(before, Store.DocumentCount(guestId));
        Assert.Equal(3, Store.GuestDocument(guestId)!.TurnCount);

        // Once seen, "exhausted" is cached: no further counter writes are spent on this guest.
        var counterCalls = Store.CounterIncrementCalls;
        await AssertProblemAsync(await PostAsync(guest, session), (HttpStatusCode)429, "guest_quota_exceeded");
        Assert.Equal(counterCalls, Store.CounterIncrementCalls);

        // Reads still work for an exhausted guest.
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/sessions/{session}")).StatusCode);
    }

    [Fact]
    public async Task Rejections_before_the_turn_is_accepted_consume_no_quota()
    {
        var (_, guestId, guest) = await _host.NewGuestAsync();
        var session = await Chat.NewSessionAsync(guest);

        // 404 (unknown session), 400 (validation), 404 (foreign session).
        await AssertProblemAsync(await PostAsync(guest, Guid.NewGuid().ToString()), HttpStatusCode.NotFound, "not_found");
        await AssertProblemAsync(
            await guest.PostAsync($"/api/sessions/{session}/messages", Helpers.Json("""{"content":""}""")), HttpStatusCode.BadRequest, "validation_failed");
        var other = await Chat.NewSessionAsync();
        await AssertProblemAsync(await PostAsync(guest, other), HttpStatusCode.NotFound, "not_found");
        Assert.Equal(0, Store.GuestDocument(guestId)!.TurnCount);

        // 409 turn_in_progress.
        var gate = new TaskCompletionSource();
        Chat.Workflow.Script = (_, ct) => ScriptedWorkflow.Hold([new AgentChanged("A"), new TokenDelta("x")], gate.Task, [new TurnCompleted()], ct);
        using var first = await SseResponse.PostAsync(guest, session, "one");
        await first.ReadEventAsync(); // session
        Assert.Equal(1, Store.GuestDocument(guestId)!.TurnCount);
        await AssertProblemAsync(await PostAsync(guest, session), HttpStatusCode.Conflict, "turn_in_progress");
        Assert.Equal(1, Store.GuestDocument(guestId)!.TurnCount); // the 409 consumed nothing
        gate.SetResult();
        await first.ReadAllEventsAsync();
    }

    [Fact]
    public async Task An_accepted_turn_consumes_quota_even_when_it_fails_or_is_cancelled()
    {
        var (_, guestId, guest) = await _host.NewGuestAsync();
        var session = await Chat.NewSessionAsync(guest);

        Chat.Workflow.Script = (_, _) => ScriptedWorkflow.Throw([new AgentChanged("A")], new InvalidOperationException("boom"));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(guest, session)).StatusCode);
        await ChatHost.WaitUntilAsync(() => Task.FromResult(Store.GuestDocument(guestId)!.TurnCount == 1), "failed turn counted");

        Chat.Workflow.Script = (_, ct) => ScriptedWorkflow.Hold([new AgentChanged("A"), new TokenDelta("x")], new TaskCompletionSource().Task, [], ct);
        using var abort = new CancellationTokenSource();
        await ChatHost.WaitUntilAsync(
            async () =>
            {
                using var sse = await SseResponse.PostAsync(guest, session, "q", abort.Token);
                if (!sse.Response.IsSuccessStatusCode)
                {
                    return false; // previous turn still releasing its lock
                }

                await sse.ReadEventAsync();
                await sse.ReadEventAsync();
                await abort.CancelAsync();
                return true;
            },
            "second turn accepted");
        Assert.Equal(2, Store.GuestDocument(guestId)!.TurnCount);
    }

    // ---- retention ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Guest_data_always_gets_the_guest_ttl_with_or_without_Chat_RetentionDays(bool retentionConfigured)
    {
        // Chat:RetentionDays may be unset only in Development (ADR 0003 rule 9).
        using var host = new GuestHost(
            c =>
            {
                if (!retentionConfigured)
                {
                    c.Remove("Chat:RetentionDays");
                }
            },
            environment: retentionConfigured ? "Test" : "Development");
        var (_, guestId, guest) = await host.NewGuestAsync();
        var session = await host.Host.NewSessionAsync(guest);
        await PostAsync(guest, session);
        await ChatHost.WaitUntilAsync(async () => (await GuestHost.MessagesAsync(guest, session)).Count == 2, "turn persisted");

        var store = host.Factory.Store;
        Assert.Equal(15300, store.SessionTtl(guestId, session));
        Assert.All(store.MessageTtls(guestId, session), ttl => Assert.Equal(15300, ttl));

        // A signed-in user keeps the retention rule (30 days, or none when unset).
        var userSession = await host.Host.NewSessionAsync();
        Assert.Equal(retentionConfigured ? 30 * 86400 : null, store.SessionTtl(host.Host.User, userSession));
    }

    // ---- delete my data / ended guest ----

    [Fact]
    public async Task Delete_my_data_ends_the_guest_writes_fail_at_once_and_a_repeat_is_204()
    {
        var (_, guestId, guest) = await _host.NewGuestAsync();
        var session = await Chat.NewSessionAsync(guest);
        await PostAsync(guest, session);
        await ChatHost.WaitUntilAsync(async () => (await GuestHost.MessagesAsync(guest, session)).Count == 2, "turn persisted");

        Assert.Equal(HttpStatusCode.NoContent, (await guest.DeleteAsync("/api/me/data")).StatusCode);
        Assert.Null(Store.GuestDocument(guestId));
        Assert.Equal(0, Store.DocumentCount(guestId));

        await AssertProblemAsync(await CreateSessionRawAsync(guest), HttpStatusCode.Unauthorized, "guest_expired");
        await AssertProblemAsync(await PostAsync(guest, session), HttpStatusCode.Unauthorized, "guest_expired");
        await AssertProblemAsync(await guest.GetAsync("/api/sessions"), HttpStatusCode.Unauthorized, "guest_expired");

        // Idempotent, and not blocked by the activity check.
        Assert.Equal(HttpStatusCode.NoContent, (await guest.DeleteAsync("/api/me/data")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await guest.DeleteAsync("/api/me/data")).StatusCode);
    }

    [Fact]
    public async Task Another_replicas_delete_is_seen_by_reads_after_the_cache_window_and_by_writes_immediately()
    {
        var (_, guestId, guest) = await _host.NewGuestAsync();
        var session = await Chat.NewSessionAsync(guest);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/api/sessions")).StatusCode); // primes the positive cache entry

        // "Another replica" deleted the guest: this replica's cache is not invalidated.
        await Store.DeleteAllUserDataAsync(guestId, TimeSpan.FromMinutes(1), default);
        Assert.Null(Store.GuestDocument(guestId));

        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/api/sessions")).StatusCode); // cached: reads lag
        await AssertProblemAsync(await PostAsync(guest, session), HttpStatusCode.Unauthorized, "guest_expired"); // writes: immediate
        await AssertProblemAsync(await CreateSessionRawAsync(guest), HttpStatusCode.Unauthorized, "guest_expired");

        _host.Factory.Time.Advance(TimeSpan.FromSeconds(61));
        await AssertProblemAsync(await guest.GetAsync("/api/sessions"), HttpStatusCode.Unauthorized, "guest_expired");
    }

    [Fact]
    public async Task Guest_data_expires_by_ttl_and_the_expired_document_ends_the_guest()
    {
        var (_, guestId, guest) = await _host.NewGuestAsync();
        _host.Factory.Time.Advance(TimeSpan.FromSeconds(15301)); // past DataTtl (255 min); the 4 h token is expired as well
        Assert.Null(Store.GuestDocument(guestId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.GetAsync("/api/sessions")).StatusCode);
    }

    // ---- isolation through the API ----

    [Fact]
    public async Task Guest_A_guest_B_and_a_signed_in_user_cannot_see_each_others_sessions()
    {
        var (_, _, a) = await _host.NewGuestAsync();
        var (_, _, b) = await _host.NewGuestAsync();
        var sessionA = await Chat.NewSessionAsync(a);
        var sessionUser = await Chat.NewSessionAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/sessions/{sessionA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Chat.Client.GetAsync($"/api/sessions/{sessionA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/sessions/{sessionUser}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/sessions/{sessionA}")).StatusCode);
        await AssertProblemAsync(await PostAsync(b, sessionA), HttpStatusCode.NotFound, "not_found");
        Assert.Empty((await (await b.GetAsync("/api/sessions")).ReadJsonAsync())["items"]!.AsArray());
        Assert.Single((await (await a.GetAsync("/api/sessions")).ReadJsonAsync())["items"]!.AsArray());
    }

    [Fact]
    public async Task The_workflow_receives_the_guest_id_as_the_user_id()
    {
        var (_, guestId, guest) = await _host.NewGuestAsync();
        var session = await Chat.NewSessionAsync(guest);
        await PostAsync(guest, session);
        Assert.Equal(guestId, Chat.Workflow.Requests.Single().UserId);
    }

    // ---- logs ----

    [Fact]
    public async Task Logs_never_contain_tokens_client_addresses_or_message_content()
    {
        using var host = new GuestHost(c => c["Guest:TrustClientIpHeader"] = "true");
        var (token, guestId, guest) = await host.NewGuestAsync(r =>
        {
            r.Headers.Add("X-Client-Ip", "203.0.113.77");
            r.Headers.Add("X-Forwarded-For", "198.51.100.23, 203.0.113.77");
        });
        var session = await host.Host.NewSessionAsync(guest);
        await PostAsync(guest, session, "MESSAGE-MARKER-4711");
        await host.Factory.CreateClient().PostAsync("/api/guest/token", Helpers.Json("{}")); // creation without a client address
        await guest.DeleteAsync("/api/me/data");
        await guest.GetAsync("/api/sessions"); // 401 guest_expired is logged too

        // Rejected tokens (forged key, garbage, expired) must not end up in the logs either.
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var forged = Jws.Es256(other, FakeGuestSigner.DefaultKid, GuestHost.Issuer, GuestHost.Audience, GuestHost.ValidGuestId(),
            host.Factory.Time.GetUtcNow(), host.Factory.Time.GetUtcNow().AddHours(1));
        await host.ClientWithToken(forged).GetAsync("/api/workflows");
        await host.ClientWithToken("garbage-token-value-8812").GetAsync("/api/workflows");
        host.Factory.Time.Advance(TimeSpan.FromHours(5));
        await host.ClientWithToken(token).GetAsync("/api/workflows"); // expired

        var logs = string.Join('\n', host.Factory.Logs.Messages);
        Assert.NotEmpty(logs);
        foreach (var forbidden in new[]
                 {
                     token, token.Split('.')[2], forged, forged.Split('.')[2], "garbage-token-value-8812", "203.0.113.77", "198.51.100.23", "MESSAGE-MARKER-4711", "botCheckToken",
                 })
        {
            Assert.DoesNotContain(forbidden, logs);
        }

        Assert.Contains(guestId, logs); // enduser.id is logged and links to no person
    }
}
