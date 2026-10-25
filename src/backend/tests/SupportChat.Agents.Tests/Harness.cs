using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SupportChat.Agents.Prompts;
using SupportChat.Agents.Workflows.SupportTicket;
using SupportChat.Core.Options;
using SupportChat.Core.Workflows;
using System.Text;
using System.Text.Json.Nodes;

namespace SupportChat.Agents.Tests;

public sealed class SupportTicketHarness
{
    public const string OrchestratorModel = "model-orchestrator-mini";
    public const string PolicyModel = "model-policy";
    public const string ClaimsModel = "model-claims";
    public const string HealthModel = "model-health";

    public FakeChatClientFactory Models { get; } = new();

    public FakeKnowledgeSearch Search { get; } = new();

    public SupportTicketOptions Options { get; } = new()
    {
        Agents =
        {
            ["Orchestrator"] = new() { Deployment = OrchestratorModel },
            ["Policy"] = new() { Deployment = PolicyModel },
            ["Claims"] = new() { Deployment = ClaimsModel },
            ["HealthAssessments"] = new() { Deployment = HealthModel },
        },
    };

    public EmbeddedPromptProvider Prompts { get; } = new();

    public FakeChatClient Orchestrator => Models[OrchestratorModel];

    public ILogger<SupportTicketWorkflow> Logger { get; set; } = NullLogger<SupportTicketWorkflow>.Instance;

    public SupportTicketWorkflow Build() => new(
        Models,
        Search,
        Prompts,
        Microsoft.Extensions.Options.Options.Create(Options),
        Microsoft.Extensions.Options.Options.Create(new SearchServiceOptions { TopK = 5 }),
        Logger);

    public static WorkflowRunRequest Request(string message, params HistoryMessage[] history) =>
        new("user-oid", "session-1", "turn-1", message, history);

    public static HistoryMessage User(string text) =>
        new(ChatRole.User, text, null, null, null, DateTimeOffset.UnixEpoch);

    public static HistoryMessage Assistant(string text, MessageKind kind, string? category = null) =>
        new(ChatRole.Assistant, text, "agent", kind, category is null ? null : new JsonObject { ["category"] = category }, DateTimeOffset.UnixEpoch);

    public static async Task<List<ChatStreamEvent>> Collect(
        IWorkflowDefinition workflow, WorkflowRunRequest request, CancellationToken cancellationToken = default)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in workflow.RunAsync(request, cancellationToken))
        {
            events.Add(e);
        }

        return events;
    }

    /// <summary>v1 protocol: exactly one AgentChanged first, >= 1 token, citations, then TurnCompleted last.</summary>
    public static TurnCompleted AssertProtocol(IReadOnlyList<ChatStreamEvent> events)
    {
        Assert.True(events.Count >= 3, "too few events");
        Assert.IsType<AgentChanged>(events[0]);
        Assert.Single(events.OfType<AgentChanged>());
        var completed = Assert.IsType<TurnCompleted>(events[^1]);
        Assert.Single(events.OfType<TurnCompleted>());
        Assert.Empty(events.OfType<TurnFailed>());

        var body = events.Skip(1).Take(events.Count - 2).ToList();
        var firstCitation = body.FindIndex(e => e is CitationFound);
        var lastToken = body.FindLastIndex(e => e is TokenDelta);
        Assert.True(lastToken >= 0, "no tokens");
        Assert.All(body, e => Assert.True(e is TokenDelta or CitationFound));
        if (firstCitation >= 0)
        {
            Assert.True(firstCitation > lastToken, "citation before the last token");
        }

        Assert.All(events.OfType<TokenDelta>(), t => Assert.NotEmpty(t.Text));
        return completed;
    }

    public static string Text(IEnumerable<ChatStreamEvent> events) =>
        events.OfType<TokenDelta>().Aggregate(new StringBuilder(), (sb, t) => sb.Append(t.Text)).ToString();
}
