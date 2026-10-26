using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportChat.Agents.Knowledge;
using SupportChat.Core.Knowledge;
using SupportChat.Core.Models;
using SupportChat.Core.Options;
using SupportChat.Core.Prompts;
using SupportChat.Core.Serialization;
using SupportChat.Core.Workflows;

namespace SupportChat.Agents.Workflows.SupportTicket;

/// <summary>
/// SupportTicket (ADR 0002 section 6, decision D2: plain C# orchestration over ChatClientAgents). The orchestrator classifies
/// the message (structured output); code routes to one specialist or asks one clarifying question. Exactly one agent segment
/// per turn. Definition is a singleton; all per-turn state lives in <see cref="RunAsync"/>.
/// </summary>
public sealed class SupportTicketWorkflow : IWorkflowDefinition
{
    public const string WorkflowId = "support-ticket";

    public const string OrchestratorKey = "Orchestrator";
    public const string OrchestratorDisplayName = "Support assistant";

    /// <summary>Domain constants (data-model 2.5). The model never supplies these.</summary>
    public const string PolicyDomain = "policy";
    public const string ClaimsDomain = "claims";
    public const string HealthDomain = "health";

    private const string PromptRoot = "Workflows/SupportTicket/Prompts/";
    private const string OrchestratorPrompt = PromptRoot + "orchestrator.v1.md";
    private const string ClarificationFallbackPrompt = PromptRoot + "clarification-fallback.v1.md";
    private const string LastCategoryPlaceholder = "{{last_category}}";

    private static readonly Specialist PolicySpecialist =
        new(TicketCategory.Policy, "Policy", "Policy specialist", PolicyDomain, PromptRoot + "policy.v1.md");

    private static readonly Specialist ClaimsSpecialist =
        new(TicketCategory.Claims, "Claims", "Claims specialist", ClaimsDomain, PromptRoot + "claims.v1.md");

    private static readonly Specialist HealthSpecialist =
        new(TicketCategory.HealthAssessments, "HealthAssessments", "Health assessments specialist", HealthDomain, PromptRoot + "health-assessments.v1.md");

    /// <summary>Config keys under Workflows:SupportTicket:Agents.</summary>
    public static IReadOnlyList<string> AgentKeys { get; } =
        [OrchestratorKey, PolicySpecialist.Key, ClaimsSpecialist.Key, HealthSpecialist.Key];

    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    private readonly IChatClientFactory _clients;
    private readonly IKnowledgeSearch _search;
    private readonly IPromptProvider _prompts;
    // Options are read lazily: the registry is built while startup validation runs, and reading validated options
    // here would surface one options class's failures instead of all of them.
    private readonly IOptions<SupportTicketOptions> _optionsAccessor;
    private readonly IOptions<SearchServiceOptions> _searchOptions;
    private SupportTicketOptions _options => _optionsAccessor.Value;
    private int _searchTop => Math.Max(1, _searchOptions.Value.TopK);
    private readonly ILogger<SupportTicketWorkflow> _logger;

    public SupportTicketWorkflow(
        IChatClientFactory clients,
        IKnowledgeSearch search,
        IPromptProvider prompts,
        IOptions<SupportTicketOptions> options,
        IOptions<SearchServiceOptions> searchOptions,
        ILogger<SupportTicketWorkflow> logger)
    {
        _clients = clients;
        _search = search;
        _prompts = prompts;
        _optionsAccessor = options;
        _searchOptions = searchOptions;
        _logger = logger;
    }

    public string Id => WorkflowId;

    public string DisplayName => "Support ticket";

    public string Description => "Routes your question to the policy, claims or health assessments specialist.";

    public async IAsyncEnumerable<ChatStreamEvent> RunAsync(
        WorkflowRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var messages = BuildMessages(request);
        var lastCategory = FindLastRoutedCategory(request.History);
        var classification = await ClassifyAsync(messages, lastCategory, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        LogClassification(classification);

        var lowConfidence = classification.Category == TicketCategory.Unknown
                            || classification.Confidence < _options.ConfidenceThreshold;
        var clarificationsInARow = CountConsecutiveClarifications(request.History);
        var routeAnyway = lowConfidence
                          && classification.Category != TicketCategory.Unknown
                          && clarificationsInARow >= _options.MaxConsecutiveClarifications;

        if (lowConfidence && !routeAnyway)
        {
            yield return new AgentChanged(OrchestratorDisplayName);
            var question = string.IsNullOrWhiteSpace(classification.ClarifyingQuestion)
                ? _prompts.Get(ClarificationFallbackPrompt)
                : classification.ClarifyingQuestion.Trim();
            yield return new TokenDelta(question);
            yield return new TurnCompleted(MessageKind.Clarification, ClassificationMetadata(classification));
            yield break;
        }

        var specialist = SpecialistFor(classification.Category);
        yield return new AgentChanged(specialist.DisplayName);

        var collector = new CitationCollector();
        var text = new StringBuilder();
        var agent = CreateSpecialistAgent(specialist, collector, out var promptVersion);
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var update in agent.RunStreamingAsync(messages, session, options: null, cancellationToken).ConfigureAwait(false))
        {
            var delta = update.Text;
            if (string.IsNullOrEmpty(delta))
            {
                continue;
            }

            text.Append(delta);
            yield return new TokenDelta(delta);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length == 0)
        {
            throw new WorkflowException("workflow_error", "The specialist produced no text.");
        }

        foreach (var citation in collector.Resolve(text.ToString()))
        {
            yield return citation;
        }

        var metadata = ClassificationMetadata(classification);
        metadata["prompts"] = new JsonObject
        {
            ["orchestrator"] = PromptVersion(OrchestratorPrompt),
            ["specialist"] = promptVersion,
        };
        yield return new TurnCompleted(MessageKind.Answer, metadata);
    }

    private async Task<TicketClassification> ClassifyAsync(
        List<ChatMessage> messages,
        string lastCategory,
        CancellationToken cancellationToken)
    {
        var agentOptions = GetAgentOptions(OrchestratorKey);
        var instructions = _prompts.Get(OrchestratorPrompt).Replace(LastCategoryPlaceholder, lastCategory, StringComparison.Ordinal);
        var agent = new ChatClientAgent(
            _clients.Create(agentOptions.Deployment!),
            new ChatClientAgentOptions
            {
                Name = OrchestratorKey,
                ChatOptions = new ChatOptions
                {
                    Instructions = instructions,
                    Temperature = agentOptions.Temperature ?? 0f,
                },
            });

        // Output is validated; one retry on invalid JSON / out-of-range values, then workflow_error (ADR 0002 section 6).
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var response = await agent.RunAsync<TicketClassification>(messages, session, Json, options: null, cancellationToken)
                    .ConfigureAwait(false);
                var result = response.Result; // throws JsonException / InvalidOperationException on invalid JSON
                if (IsValid(result))
                {
                    return result!;
                }

                _logger.LogWarning("Classification output out of range (attempt {Attempt})", attempt);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                // Not logged with content; only the failure class. OperationCanceledException is deliberately not caught.
                _logger.LogWarning("Classification output could not be parsed (attempt {Attempt}, {ErrorType})", attempt, ex.GetType().Name);
            }
        }

        throw new WorkflowException("workflow_error", "Classification output was invalid after a retry.");
    }

    private static bool IsValid(TicketClassification? c) =>
        c is not null
        && Enum.IsDefined(c.Category)
        && double.IsFinite(c.Confidence)
        && c.Confidence is >= 0 and <= 1;

    private ChatClientAgent CreateSpecialistAgent(Specialist specialist, CitationCollector collector, out string promptVersion)
    {
        var agentOptions = GetAgentOptions(specialist.Key);
        var tool = KnowledgeSearchTool.Create(_search, specialist.Domain, collector, _searchTop, _logger);
        promptVersion = PromptVersion(specialist.PromptPath);

        // ChatClientAgent only inserts its own FunctionInvokingChatClient when the pipeline has none, so we supply one
        // to cap tool-call round trips per turn. Verified empirically (MEAI 10.10): N iterations execute exactly N tool calls,
        // then the client makes one final model call and returns it unexecuted; a model that still wants a tool yields no text
        // and the turn fails with workflow_error (a model that answers instead completes normally).
        var client = new FunctionInvokingChatClient(_clients.Create(agentOptions.Deployment!))
        {
            MaximumIterationsPerRequest = _options.MaxToolCallsPerTurn,
        };
        return new ChatClientAgent(
            client,
            new ChatClientAgentOptions
            {
                Name = specialist.Key,
                ChatOptions = new ChatOptions
                {
                    Instructions = _prompts.Get(specialist.PromptPath),
                    Tools = [tool],
                    Temperature = agentOptions.Temperature,
                },
            });
    }

    private SupportTicketAgentOptions GetAgentOptions(string key)
    {
        if (!_options.Agents.TryGetValue(key, out var options) || string.IsNullOrWhiteSpace(options.Deployment))
        {
            throw new InvalidOperationException($"{SupportTicketOptions.SectionName}:Agents:{key}:Deployment is not configured.");
        }

        return options;
    }

    private static Specialist SpecialistFor(TicketCategory category) => category switch
    {
        TicketCategory.Policy => PolicySpecialist,
        TicketCategory.Claims => ClaimsSpecialist,
        TicketCategory.HealthAssessments => HealthSpecialist,
        _ => throw new WorkflowException("workflow_error", "No specialist for category."),
    };

    private static List<ChatMessage> BuildMessages(WorkflowRunRequest request)
    {
        var messages = new List<ChatMessage>(request.History.Count + 1);
        foreach (var h in request.History)
        {
            if ((h.Role == ChatRole.User || h.Role == ChatRole.Assistant) && !string.IsNullOrWhiteSpace(h.Content))
            {
                messages.Add(new ChatMessage(h.Role, h.Content));
            }
        }

        messages.Add(new ChatMessage(ChatRole.User, request.UserMessage));
        return messages;
    }

    /// <summary>Category of the most recent answered (routed) turn, from workflowMetadata.category; "none" if absent.</summary>
    private static string FindLastRoutedCategory(IReadOnlyList<HistoryMessage> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var h = history[i];
            if (h.Role != ChatRole.Assistant || h.Kind != MessageKind.Answer)
            {
                continue;
            }

            if (h.WorkflowMetadata?["category"] is JsonValue v
                && v.TryGetValue<string>(out var value)
                && value is "policy" or "claims" or "healthAssessments")
            {
                return value;
            }
        }

        return "none";
    }

    /// <summary>Clarifications at the end of the history (assistant messages only; stops at the first non-clarification).</summary>
    private static int CountConsecutiveClarifications(IReadOnlyList<HistoryMessage> history)
    {
        var count = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role != ChatRole.Assistant)
            {
                continue;
            }

            if (history[i].Kind != MessageKind.Clarification)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static JsonObject ClassificationMetadata(TicketClassification c) => new()
    {
        ["category"] = JsonSerializer.SerializeToNode(c.Category, Json),
        ["confidence"] = Math.Round(c.Confidence, 3),
    };

    private static string PromptVersion(string path)
    {
        var file = path[(path.LastIndexOf('/') + 1)..];
        return file.EndsWith(".md", StringComparison.Ordinal) ? file[..^3] : file;
    }

    /// <summary>Category and confidence only. The rationale is model text that can paraphrase user content: never logged.</summary>
    private void LogClassification(TicketClassification c) =>
        _logger.LogInformation("Classified ticket as {Category} with confidence {Confidence}", c.Category, c.Confidence);

    private sealed record Specialist(TicketCategory Category, string Key, string DisplayName, string Domain, string PromptPath);
}
