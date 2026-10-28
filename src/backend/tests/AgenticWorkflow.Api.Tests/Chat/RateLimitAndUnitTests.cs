using System.Net;
using Azure;
using Microsoft.Extensions.AI;
using AgenticWorkflow.Api.Chat;
using AgenticWorkflow.Core.Storage;
using AgenticWorkflow.Core.Workflows;

namespace AgenticWorkflow.Api.Tests.Chat;

public class RateLimitTests : IDisposable
{
    private readonly ChatHost _host = new(c => c["Chat:TurnsPerMinute"] = "2");

    public void Dispose() => _host.Dispose();

    private static Task<HttpResponseMessage> Post(HttpClient client, string id) =>
        client.PostAsync($"/api/sessions/{id}/messages", Helpers.Json("""{"content":"hi"}"""));

    [Fact]
    public async Task Third_turn_in_a_minute_is_429_rate_limited_with_Retry_After_and_other_users_are_unaffected()
    {
        var id = await _host.NewSessionAsync();
        for (var i = 0; i < 2; i++)
        {
            var ok = await Post(_host.Client, id);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            await ok.Content.ReadAsStringAsync();
        }

        var limited = await Post(_host.Client, id);

        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType!.MediaType);
        Assert.Equal("rate_limited", (await limited.ReadJsonAsync())["code"]!.GetValue<string>());
        var seconds = int.Parse(Assert.Single(limited.Headers.GetValues("Retry-After")));
        Assert.InRange(seconds, 1, 60);

        var other = _host.Factory.CreateAuthorizedClient(TestTokens.NewOid());
        var otherSession = await other.CreateSessionAsync(ScriptedWorkflow.WorkflowId);
        Assert.Equal(HttpStatusCode.OK, (await Post(other, otherSession)).StatusCode);
    }
}

public class StreamErrorsTests
{
    [Theory]
    [InlineData("model_error", "The assistant could not answer. Please try again.")]
    [InlineData("upstream_throttled", "The service is busy. Please try again in a moment.")]
    [InlineData("content_filtered", "This request could not be processed. Please rephrase.")]
    [InlineData("timeout", "The answer took too long. Please try again.")]
    [InlineData("workflow_error", "Something went wrong. Please try again.")]
    [InlineData("internal_error", "Something went wrong. Please try again.")]
    [InlineData("whatever", "Something went wrong. Please try again.")]
    public void Message_table_matches_the_contract(string code, string message) => Assert.Equal(message, StreamErrors.MessageFor(code));

    [Fact]
    public void Exception_mapping_follows_the_ADR()
    {
        Assert.Equal("workflow_error", StreamErrors.CodeFor(new WorkflowException("workflow_error")));
        Assert.Equal("internal_error", StreamErrors.CodeFor(new WorkflowException("nope")));
        Assert.Equal("upstream_throttled", StreamErrors.CodeFor(new RequestFailedException(429, "slow down")));
        Assert.Equal("model_error", StreamErrors.CodeFor(new RequestFailedException(500, "boom")));
        Assert.Equal("content_filtered", StreamErrors.CodeFor(new RequestFailedException(400, "blocked", "content_filter", null)));
        Assert.Equal("upstream_throttled", StreamErrors.CodeFor(new InvalidOperationException("wrapped", new RequestFailedException(429, "x"))));
        Assert.Equal("internal_error", StreamErrors.CodeFor(new InvalidOperationException("x")));
    }
}

public class ChatHistoryBuilderTests
{
    private static int _seq;

    private static MessageRecord M(ChatRole role, string content) =>
        new(Guid.NewGuid().ToString(), "s", role, content, new DateTimeOffset(2026, 1, 1, 0, 0, ++_seq, TimeSpan.Zero),
            MessageStatus.Complete, role == ChatRole.Assistant ? MessageKind.Answer : null, role == ChatRole.Assistant ? "Agent" : null, [], null, null);

    private static List<MessageRecord> Pairs(int count, int charsEach) =>
        Enumerable.Range(0, count).SelectMany(i => new[] { M(ChatRole.User, new string('u', charsEach)), M(ChatRole.Assistant, new string('a', charsEach)) }).ToList();

    [Fact]
    public void Everything_fits_when_under_budget() =>
        Assert.Equal(6, ChatHistoryBuilder.Build(Pairs(3, 40), "q", 6000).Count);

    [Fact]
    public void Oldest_pairs_are_trimmed_first_and_pairs_stay_intact()
    {
        var all = Pairs(4, 400); // 100 tokens per message, 200 per pair
        var result = ChatHistoryBuilder.Build(all, string.Empty, 450);

        Assert.Equal(4, result.Count);
        Assert.Equal(all[4].Content, result[0].Content); // pairs 3 and 4 remain
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Equal(ChatRole.Assistant, result[^1].Role);
    }

    [Fact]
    public void Current_message_estimate_is_reserved_from_the_budget()
    {
        var all = Pairs(2, 400);
        Assert.Equal(4, ChatHistoryBuilder.Build(all, string.Empty, 400).Count);
        Assert.Equal(2, ChatHistoryBuilder.Build(all, new string('q', 400), 400).Count);
        Assert.Empty(ChatHistoryBuilder.Build(all, new string('q', 4000), 400));
    }

    [Fact]
    public void A_leading_assistant_message_without_its_user_message_is_dropped()
    {
        var list = new List<MessageRecord> { M(ChatRole.Assistant, "orphan reply") };
        list.AddRange(Pairs(1, 8));
        var result = ChatHistoryBuilder.Build(list, "q", 6000);
        Assert.Equal(2, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);
    }

    [Fact]
    public void Zero_budget_yields_empty_history() => Assert.Empty(ChatHistoryBuilder.Build(Pairs(2, 8), "q", 0));
}
