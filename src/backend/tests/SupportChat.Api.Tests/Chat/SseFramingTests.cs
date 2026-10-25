using System.Net;
using System.Text.RegularExpressions;
using SupportChat.Core.Workflows;

namespace SupportChat.Api.Tests.Chat;

public partial class SseFramingTests : IDisposable
{
    private readonly ChatHost _host = new();

    public void Dispose() => _host.Dispose();

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex GuidPattern();

    private static string Golden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name)).Replace("\r\n", "\n");

    private async Task<string> RawBodyAsync(string sessionId, string sessionIdForMask)
    {
        using var sse = await SseResponse.PostAsync(_host.Client, sessionId, "How long does a claim take?");
        Assert.Equal(HttpStatusCode.OK, sse.Response.StatusCode);
        var raw = await sse.ReadRemainingRawAsync();
        return GuidPattern().Replace(raw.Replace(sessionIdForMask, "{SESSION_ID}"), "{MESSAGE_ID}");
    }

    [Fact]
    public async Task Routed_answer_matches_the_golden_file()
    {
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(
            new AgentChanged("Claims specialist"),
            new TokenDelta("Most claims are settled "),
            new TokenDelta(string.Empty),
            new TokenDelta("within 10 working days [S1]."),
            new CitationFound("S1", "Claims handling times", "claims-handling-0003", "https://kb.example.com/claims/handling"),
            new TurnCompleted());
        var id = await _host.NewSessionAsync();

        Assert.Equal(Golden("answer.sse"), await RawBodyAsync(id, id));
    }

    [Fact]
    public async Task Clarification_with_newline_unicode_and_a_non_http_citation_url_matches_the_golden_file()
    {
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(
            new AgentChanged("Support assistant"),
            new TokenDelta("Is your question about policy coverage,\nan existing claim, or a health assessment? Zażółć <b>"),
            new CitationFound("S1", "Raw <title> & more", "d1", "javascript:alert(1)"),
            new TurnCompleted(MessageKind.Clarification));
        var id = await _host.NewSessionAsync();

        Assert.Equal(Golden("clarification.sse"), await RawBodyAsync(id, id));
    }

    [Fact]
    public async Task Error_mid_stream_matches_the_golden_file_and_has_no_done()
    {
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(
            new AgentChanged("Policy specialist"), new TokenDelta("Your policy covers "), new TurnFailed("upstream_throttled"));
        var id = await _host.NewSessionAsync();

        Assert.Equal(Golden("error-midstream.sse"), await RawBodyAsync(id, id));
    }

    [Fact]
    public async Task Success_response_has_the_sse_headers_and_is_not_compressed()
    {
        var id = await _host.NewSessionAsync();

        using var sse = await SseResponse.PostAsync(
            _host.Client, id, "hi", configure: r => r.Headers.AcceptEncoding.ParseAdd("gzip, br"));

        var response = sse.Response;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.True(response.Headers.CacheControl!.NoCache);
        Assert.True(response.Headers.CacheControl.NoTransform);
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));
        Assert.Empty(response.Content.Headers.ContentEncoding);
        await sse.ReadAllEventsAsync();
    }

    [Fact]
    public async Task Event_order_is_session_agent_tokens_citations_done_with_consecutive_ids()
    {
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(
            new AgentChanged("A"), new TokenDelta("x"), new TokenDelta("y"), new CitationFound("S1", "t", "d", null), new TurnCompleted());
        var id = await _host.NewSessionAsync();

        using var sse = await SseResponse.PostAsync(_host.Client, id, "q");
        var events = await sse.ReadAllEventsAsync();

        Assert.Equal(["session", "agent", "token", "token", "citation", "done"], events.Select(e => e.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6], events.Select(e => e.Id!.Value));
        Assert.Equal(id, events[0].Data["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Persisted_assistant_message_carries_text_agent_kind_and_citations_and_done_uses_its_id()
    {
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(
            new AgentChanged("Claims specialist"), new TokenDelta("Ten "), new TokenDelta("days [S1]."),
            new CitationFound("S1", "Handling", "doc-1", "https://kb.example.com/x"), new CitationFound("S2", "Other", "doc-2", "ftp://x"),
            new TurnCompleted());
        var id = await _host.NewSessionAsync();

        using var sse = await SseResponse.PostAsync(_host.Client, id, "  How long?  ");
        var events = await sse.ReadAllEventsAsync();
        var doneId = events[^1].Data["messageId"]!.GetValue<string>();

        var messages = await _host.MessagesAsync(id);
        Assert.Equal(2, messages.Count);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("How long?", messages[0]!["content"]!.GetValue<string>());
        var assistant = messages[1]!;
        Assert.Equal(doneId, assistant["id"]!.GetValue<string>());
        Assert.Equal("Ten days [S1].", assistant["content"]!.GetValue<string>());
        Assert.Equal("complete", assistant["status"]!.GetValue<string>());
        Assert.Equal("answer", assistant["kind"]!.GetValue<string>());
        Assert.Equal("Claims specialist", assistant["agentName"]!.GetValue<string>());
        Assert.Equal(2, assistant["citations"]!.AsArray().Count);
        Assert.Null(assistant["citations"]![1]!["url"]);

        var session = await _host.GetSessionAsync(id);
        Assert.Equal("How long?", session["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Clarification_is_persisted_with_kind_clarification()
    {
        _host.Workflow.Script = (_, _) => ScriptedWorkflow.Seq(
            new AgentChanged("Support assistant"), new TokenDelta("Which one?"), new TurnCompleted(MessageKind.Clarification));
        var id = await _host.NewSessionAsync();

        using var sse = await SseResponse.PostAsync(_host.Client, id, "help");
        await sse.ReadAllEventsAsync();

        Assert.Equal("clarification", (await _host.MessagesAsync(id))[1]!["kind"]!.GetValue<string>());
    }
}
