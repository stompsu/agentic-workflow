using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SupportChat.Agents.Prompts;
using SupportChat.Agents.Workflows.SupportTicket;
using SupportChat.Core.Knowledge;
using SupportChat.Core.Models;
using SupportChat.Core.Options;
using SupportChat.Core.Workflows;
using SupportChat.LocalHost.Knowledge;
using SupportChat.LocalHost.Models;

namespace SupportChat.LocalHost.Tests.Workflows;

/// <summary>The real SupportTicketWorkflow, prompts, KnowledgeSearchTool and CitationCollector over ScriptedChatClientFactory.</summary>
public class SupportTicketScriptedEndToEndTests
{
    private static SupportTicketWorkflow Build(IChatClientFactory models, IKnowledgeSearch search, Action<SupportTicketOptions>? tweak = null)
    {
        var options = new SupportTicketOptions
        {
            Agents =
            {
                ["Orchestrator"] = new() { Deployment = "scripted-orchestrator" },
                ["Policy"] = new() { Deployment = "scripted-policy" },
                ["Claims"] = new() { Deployment = "scripted-claims" },
                ["HealthAssessments"] = new() { Deployment = "scripted-health" },
            },
        };
        tweak?.Invoke(options);
        return new SupportTicketWorkflow(
            models, search, new EmbeddedPromptProvider(),
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(new SearchServiceOptions { TopK = 5 }),
            NullLogger<SupportTicketWorkflow>.Instance);
    }

    private static WorkflowRunRequest Request(string message, params HistoryMessage[] history) =>
        new("user-oid", "session-1", "turn-1", message, history);

    private static HistoryMessage User(string text) => new(ChatRole.User, text, null, null, null, DateTimeOffset.UnixEpoch);

    private static HistoryMessage Assistant(string text, MessageKind kind, string? category = null) =>
        new(ChatRole.Assistant, text, "agent", kind,
            category is null ? null : new System.Text.Json.Nodes.JsonObject { ["category"] = category }, DateTimeOffset.UnixEpoch);

    private static async Task<List<ChatStreamEvent>> Run(SupportTicketWorkflow w, WorkflowRunRequest r, CancellationToken ct = default)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in w.RunAsync(r, ct))
        {
            events.Add(e);
        }

        return events;
    }

    private static string Text(IEnumerable<ChatStreamEvent> events) =>
        events.OfType<TokenDelta>().Aggregate(new StringBuilder(), (sb, t) => sb.Append(t.Text)).ToString();

    [Theory]
    [InlineData("What does my policy cover?", "Policy specialist", "policy", "policy-1")]
    [InlineData("How do I file a claim?", "Claims specialist", "claims", "claims-1")]
    [InlineData("How should I prepare for my health assessment booking?", "Health assessments specialist", "healthAssessments", "health-1")]
    public async Task Each_category_routes_to_its_specialist_and_cites_a_knowledge_chunk(
        string question, string agent, string category, string topChunk)
    {
        var search = new KnowledgeFixture();
        var events = await Run(Build(new ScriptedChatClientFactory(), search), Request(question));

        Assert.Equal(new AgentChanged(agent), events[0]);
        var completed = Assert.IsType<TurnCompleted>(events[^1]);
        Assert.Equal(MessageKind.Answer, completed.Kind);
        Assert.Equal(category, completed.Metadata!["category"]!.GetValue<string>());
        Assert.Equal(0.9, completed.Metadata["confidence"]!.GetValue<double>());
        Assert.Single(events.OfType<AgentChanged>());
        Assert.Single(search.Calls);
        Assert.Equal(question, search.Calls[0].Query);

        var text = Text(events);
        Assert.EndsWith(ScriptedChatClient.ScriptedFooter, text);
        var citation = events.OfType<CitationFound>().First();
        Assert.Equal("S1", citation.Ref);
        Assert.Equal(topChunk, citation.DocId);
        Assert.Contains("[S1]", text);
        Assert.True(events.FindLastIndex(e => e is TokenDelta) < events.FindIndex(e => e is CitationFound));
    }

    [Fact]
    public async Task Hostile_content_never_yields_a_citation_for_a_fabricated_ref()
    {
        var events = await Run(
            Build(new ScriptedChatClientFactory(), new KnowledgeFixture()),
            Request("How do I appeal a rejected claim?"));

        var text = Text(events);
        Assert.DoesNotContain("S9", text);
        var refs = events.OfType<CitationFound>().Select(c => c.Ref).ToList();
        Assert.NotEmpty(refs);
        Assert.All(refs, r => Assert.Matches(@"^S[1-3]$", r));
        Assert.Equal("claims-2", events.OfType<CitationFound>().First().DocId);
    }

    [Fact]
    public async Task Search_failure_produces_the_fixed_not_found_answer_without_citations()
    {
        var events = await Run(Build(new ScriptedChatClientFactory(), new ThrowingSearch()), Request("How do I file a claim?"));

        Assert.Equal(ScriptedChatClient.NoResultsAnswer, Text(events));
        Assert.Empty(events.OfType<CitationFound>());
        Assert.IsType<TurnCompleted>(events[^1]);
    }

    [Fact]
    public async Task Ambiguous_message_asks_the_clarifying_question_and_does_not_search()
    {
        var search = new KnowledgeFixture();
        var events = await Run(Build(new ScriptedChatClientFactory(), search), Request("Hello there"));

        Assert.Equal(new AgentChanged(SupportTicketWorkflow.OrchestratorDisplayName), events[0]);
        Assert.Equal(ScriptedChatClient.ClarifyingQuestion, Text(events));
        var completed = Assert.IsType<TurnCompleted>(events[^1]);
        Assert.Equal(MessageKind.Clarification, completed.Kind);
        Assert.Empty(search.Calls);
        Assert.Empty(events.OfType<CitationFound>());
    }

    [Fact]
    public async Task Answer_to_a_clarification_routes_using_the_previous_user_message()
    {
        var events = await Run(
            Build(new ScriptedChatClientFactory(), new KnowledgeFixture()),
            Request("yes please", User("tell me about my premium and claim ideas hello"), Assistant("?", MessageKind.Clarification)));

        // "premium" (policy) and "claim" (claims) tie in the earlier message: still ambiguous.
        Assert.Equal(MessageKind.Clarification, Assert.IsType<TurnCompleted>(events[^1]).Kind);

        events = await Run(
            Build(new ScriptedChatClientFactory(), new KnowledgeFixture()),
            Request("yes please", User("tell me about my premium"), Assistant("?", MessageKind.Clarification)));

        Assert.Equal(new AgentChanged("Policy specialist"), events[0]);
    }

    [Fact]
    public async Task Terse_follow_up_after_an_answer_stays_on_the_previous_category()
    {
        var events = await Run(
            Build(new ScriptedChatClientFactory(), new KnowledgeFixture()),
            Request("and for my spouse?", User("How do I file a claim?"), Assistant("answer", MessageKind.Answer, "claims")));

        Assert.Equal(new AgentChanged("Claims specialist"), events[0]);
        Assert.Equal(0.8, Assert.IsType<TurnCompleted>(events[^1]).Metadata!["confidence"]!.GetValue<double>());
    }

    [Fact]
    public async Task Max_consecutive_clarifications_routes_a_low_confidence_known_category_anyway()
    {
        // Threshold above the scripted confidence 0.9 makes every known category "low confidence".
        var history = new[]
        {
            User("a"), Assistant("?", MessageKind.Clarification),
            User("b"), Assistant("?", MessageKind.Clarification),
        };

        var first = await Run(Build(new ScriptedChatClientFactory(), new KnowledgeFixture(), o => o.ConfidenceThreshold = 0.95),
            Request("How do I file a claim?"));
        var routed = await Run(Build(new ScriptedChatClientFactory(), new KnowledgeFixture(), o => o.ConfidenceThreshold = 0.95),
            Request("How do I file a claim?", history));

        Assert.Equal(MessageKind.Clarification, Assert.IsType<TurnCompleted>(first[^1]).Kind);
        Assert.Equal(new AgentChanged("Claims specialist"), routed[0]);
        Assert.Equal(MessageKind.Answer, Assert.IsType<TurnCompleted>(routed[^1]).Kind);
    }

    [Fact]
    public async Task An_unknown_category_keeps_clarifying_even_after_the_maximum()
    {
        var history = new[]
        {
            User("a"), Assistant("?", MessageKind.Clarification),
            User("b"), Assistant("?", MessageKind.Clarification),
        };

        var events = await Run(Build(new ScriptedChatClientFactory(), new KnowledgeFixture()), Request("still vague", history));

        Assert.Equal(MessageKind.Clarification, Assert.IsType<TurnCompleted>(events[^1]).Kind);
    }

    [Fact]
    public async Task Cancelling_mid_answer_stops_the_turn()
    {
        var factory = new ScriptedChatClientFactory(TimeSpan.FromMilliseconds(20));
        using var cts = new CancellationTokenSource();
        var tokens = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in Build(factory, new KnowledgeFixture()).RunAsync(Request("How do I file a claim?"), cts.Token))
            {
                if (e is TokenDelta && ++tokens == 3)
                {
                    await cts.CancelAsync();
                }
            }
        });
        Assert.Equal(3, tokens);
    }

    [Fact]
    public async Task Streamed_workflow_text_equals_the_scripted_answer()
    {
        var events = await Run(Build(new ScriptedChatClientFactory(), new KnowledgeFixture()), Request("What does my policy cover?"));

        var text = Text(events);
        Assert.StartsWith("Your policy covers hospital stays and outpatient care. Dental care is covered up to a yearly limit. [S1]", text);
        Assert.Contains("\n\n" + ScriptedChatClient.ScriptedFooter, text);
    }

    [Theory]
    [InlineData("How do I appeal a declined claim?", "Claims specialist", "claims/appeals.md")]
    [InlineData("What is the cooling-off period for my policy?", "Policy specialist", "policy/cancellation-and-cooling-off.md")]
    [InlineData("How do I book my health assessment appointment?", "Health assessments specialist", "health/booking-and-preparation.md")]
    public async Task Repo_knowledge_with_InMemoryKnowledgeSearch_gives_a_cited_answer(string question, string agent, string sourceFile)
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "knowledge", "claims")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var corpus = new LocalKnowledgeCorpus(Path.Combine(root, "knowledge"), NullLogger<LocalKnowledgeCorpus>.Instance);
        var search = new InMemoryKnowledgeSearch(corpus.Chunks, 5);

        var events = await Run(Build(new ScriptedChatClientFactory(), search), Request(question));

        Assert.Equal(new AgentChanged(agent), events[0]);
        var citation = events.OfType<CitationFound>().First();
        Assert.Equal("S1", citation.Ref);
        var top = corpus.Chunks.Single(c => c.Id == citation.DocId);
        Assert.Equal(sourceFile, top.SourcePath.Replace('\\', '/'));
        Assert.DoesNotContain("#", Text(events));
        Assert.EndsWith(ScriptedChatClient.ScriptedFooter, Text(events));
        Assert.All(events.OfType<CitationFound>(), c => Assert.Contains(corpus.Chunks, k => k.Id == c.DocId));
    }

    private sealed class ThrowingSearch : IKnowledgeSearch
    {
        public Task<IReadOnlyList<KnowledgeChunk>> SearchAsync(string domain, string query, int top, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("down");
    }
}
