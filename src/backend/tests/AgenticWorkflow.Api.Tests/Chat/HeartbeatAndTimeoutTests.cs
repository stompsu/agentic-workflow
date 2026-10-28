using System.Net;
using System.Text.Json.Nodes;
using AgenticWorkflow.Core.Workflows;

namespace AgenticWorkflow.Api.Tests.Chat;

public class HeartbeatAndTimeoutTests : IDisposable
{
    private readonly ChatHost _host = new();

    public void Dispose() => _host.Dispose();

    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Heartbeat_comment_without_id_is_sent_every_interval_and_not_before()
    {
        var gate = new TaskCompletionSource();
        _host.Workflow.Script = (_, ct) => ScriptedWorkflow.Hold(
            [new AgentChanged("A"), new TokenDelta("x")], gate.Task, [new TurnCompleted()], ct);
        var id = await _host.NewSessionAsync();

        using var sse = await SseResponse.PostAsync(_host.Client, id, "q");
        Assert.Equal("session", (await sse.ReadEventAsync())!.Name);
        Assert.Equal("agent", (await sse.ReadEventAsync())!.Name);
        Assert.Equal("token", (await sse.ReadEventAsync())!.Name);
        await Task.Delay(100); // let the endpoint arm its heartbeat timer

        var next = sse.ReadRawFrameAsync();
        var elapsed = TimeSpan.Zero;
        while (!next.IsCompleted && elapsed < TimeSpan.FromSeconds(20))
        {
            _host.Factory.Time.Advance(Step);
            elapsed += Step;
            await Task.Delay(20);
        }

        Assert.Equal(": hb", await next);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(17));

        // A second heartbeat follows one interval later; heartbeats never consume ids.
        var second = sse.ReadRawFrameAsync();
        elapsed = TimeSpan.Zero;
        while (!second.IsCompleted && elapsed < TimeSpan.FromSeconds(20))
        {
            _host.Factory.Time.Advance(Step);
            elapsed += Step;
            await Task.Delay(20);
        }

        Assert.Equal(": hb", await second);
        Assert.InRange(elapsed, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(17));

        gate.SetResult();
        var rest = await sse.ReadAllEventsAsync();
        Assert.Equal(4, rest[0].Id); // session=1 agent=2 token=3, next event is 4
        Assert.Equal("done", rest[0].Name);
    }

    [Fact]
    public async Task Turn_timeout_persists_a_failed_message_and_sends_error_timeout()
    {
        _host.Workflow.Script = (_, ct) => ScriptedWorkflow.Hold(
            [new AgentChanged("A"), new TokenDelta("partial")], Task.Delay(Timeout.Infinite, CancellationToken.None), [], ct);
        var id = await _host.NewSessionAsync();

        using var sse = await SseResponse.PostAsync(_host.Client, id, "q");
        Assert.Equal("session", (await sse.ReadEventAsync())!.Name);
        Assert.Equal("agent", (await sse.ReadEventAsync())!.Name);
        Assert.Equal("token", (await sse.ReadEventAsync())!.Name);
        await Task.Delay(100);

        _host.Factory.Time.Advance(TimeSpan.FromMinutes(3));

        var events = await sse.ReadAllEventsAsync();
        var error = Assert.Single(events);
        Assert.Equal("error", error.Name);
        Assert.Equal("timeout", error.Data["code"]!.GetValue<string>());
        Assert.Equal("The answer took too long. Please try again.", error.Data["message"]!.GetValue<string>());

        var assistant = (await _host.MessagesAsync(id))[1]!;
        Assert.Equal("failed", assistant["status"]!.GetValue<string>());
        Assert.Equal("timeout", assistant["errorCode"]!.GetValue<string>());
        Assert.Equal("partial", assistant["content"]!.GetValue<string>());
        await AssertLockReleasedAsync(id);
    }

    [Fact]
    public async Task Workflow_that_ignores_cancellation_still_times_out_and_releases_the_lock()
    {
        var never = new TaskCompletionSource();
        _host.Workflow.Script = (_, _) => Stubborn(never.Task);
        var id = await _host.NewSessionAsync();

        using var sse = await SseResponse.PostAsync(_host.Client, id, "q");
        Assert.Equal("session", (await sse.ReadEventAsync())!.Name);
        await Task.Delay(100);
        _host.Factory.Time.Advance(TimeSpan.FromMinutes(3));

        var events = await sse.ReadAllEventsAsync();
        Assert.Equal("timeout", Assert.Single(events).Data["code"]!.GetValue<string>());
        await AssertLockReleasedAsync(id);
        never.SetResult();
    }

    private static async IAsyncEnumerable<ChatStreamEvent> Stubborn(Task gate)
    {
        await gate; // deliberately ignores the cancellation token
        yield return new TurnCompleted();
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
}
