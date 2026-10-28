using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AgenticWorkflow.Core.Knowledge;
using AgenticWorkflow.Core.Models;

namespace AgenticWorkflow.Api.Tests.Chat;

/// <summary>
/// The REAL SupportTicketWorkflow (registered by AddAgentWorkflows) behind the real endpoint; only the model
/// (scripted IChatClient per deployment) and the knowledge search are fakes.
/// </summary>
public sealed class SupportTicketEndToEndTests : IDisposable
{
    private const string OrchestratorDeployment = "test-orchestrator";
    private const string SpecialistDeployment = "test-specialist";

    private readonly ScriptedModels _models = new();
    private readonly ScriptedSearch _search = new();
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public SupportTicketEndToEndTests()
    {
        _factory = new ApiFactory(services: s =>
        {
            s.RemoveAll<IChatClientFactory>();
            s.AddSingleton<IChatClientFactory>(_models);
            s.RemoveAll<IKnowledgeSearch>();
            s.AddSingleton<IKnowledgeSearch>(_search);
        });
        _client = _factory.CreateAuthorizedClient(TestTokens.NewOid());
    }

    public void Dispose() => _factory.Dispose();

    private async Task<(string SessionId, List<SseEvent> Events)> AskAsync(string content, string? sessionId = null)
    {
        sessionId ??= await _client.CreateSessionAsync("support-ticket");
        using var sse = await SseResponse.PostAsync(_client, sessionId, content);
        Assert.Equal(System.Net.HttpStatusCode.OK, sse.Response.StatusCode);
        return (sessionId, await sse.ReadAllEventsAsync());
    }

    private async Task<System.Text.Json.Nodes.JsonNode> AssistantAsync(string sessionId)
    {
        var response = await _client.GetAsync($"/api/sessions/{sessionId}");
        var messages = (await response.ReadJsonAsync())["messages"]!.AsArray();
        return messages[^1]!;
    }

    [Fact]
    public async Task Classified_question_streams_the_specialist_answer_with_a_citation()
    {
        _models[OrchestratorDeployment].Enqueue(Classification("claims", 0.95, null));
        _models[SpecialistDeployment].Enqueue(
            ToolCall("claim timing"),
            Text("Claims are settled ", "within ten days [S1]."));
        _search.Results = domain =>
            [new KnowledgeChunk("claims-1", domain, "Claims handling", "Settled within ten working days.", "https://kb.example.com/claims", 3.1)];

        var (sessionId, events) = await AskAsync("How long does a claim take?");

        Assert.Equal(["session", "agent"], events.Take(2).Select(e => e.Name));
        var names = events.Select(e => e.Name).ToList();
        var firstToken = names.IndexOf("token");
        Assert.True(firstToken >= 2, string.Join(",", names));
        Assert.All(events.Skip(2).SkipLast(2), e => Assert.Equal("token", e.Name));
        Assert.Equal(["citation", "done"], names.TakeLast(2));
        Assert.Equal("S1", events[^2].Data["ref"]!.GetValue<string>());
        Assert.Equal("claims", _search.Calls.Single().Domain);
        Assert.Equal("claim timing", _search.Calls.Single().Query);

        var assistant = await AssistantAsync(sessionId);
        Assert.Equal("Claims are settled within ten days [S1].", assistant["content"]!.GetValue<string>());
        Assert.Equal("answer", assistant["kind"]!.GetValue<string>());
        Assert.Equal(events[1].Data["name"]!.GetValue<string>(), assistant["agentName"]!.GetValue<string>());
        var citation = Assert.Single(assistant["citations"]!.AsArray())!;
        Assert.Equal("claims-1", citation["docId"]!.GetValue<string>());
        Assert.Equal("Claims handling", citation["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Low_confidence_yields_a_clarification_without_calling_a_specialist_or_search()
    {
        _models[OrchestratorDeployment].Enqueue(Classification("claims", 0.2, "Is this about a claim or your policy?"));

        var (sessionId, events) = await AskAsync("help me");

        Assert.Equal("done", events[^1].Name);
        Assert.DoesNotContain(events, e => e.Name == "citation");
        Assert.Empty(_search.Calls);
        Assert.Empty(_models[SpecialistDeployment].Calls);
        var assistant = await AssistantAsync(sessionId);
        Assert.Equal("clarification", assistant["kind"]!.GetValue<string>());
        Assert.Equal("Is this about a claim or your policy?", assistant["content"]!.GetValue<string>());
        Assert.NotNull(assistant["agentName"]);
    }

    private static FakeReply Classification(string category, double confidence, string? clarifying) =>
        Text(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["category"] = category,
            ["confidence"] = confidence,
            ["rationale"] = "because",
            ["clarifyingQuestion"] = clarifying,
        }));

    private static FakeReply Text(params string[] chunks) => _ => Stream(chunks.Select(c => new ChatResponseUpdate(ChatRole.Assistant, c)));

    private static FakeReply ToolCall(string query) => _ => Stream(
    [
        new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent("call-1", "search_knowledge", new Dictionary<string, object?> { ["query"] = query })],
        },
    ]);

    private static async IAsyncEnumerable<ChatResponseUpdate> Stream(IEnumerable<ChatResponseUpdate> updates, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var u in updates)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return u;
        }
    }

    private delegate IAsyncEnumerable<ChatResponseUpdate> FakeReply(IReadOnlyList<ChatMessage> messages);

    private sealed class ScriptedClient : IChatClient
    {
        private readonly Queue<FakeReply> _replies = new();

        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

        public void Enqueue(params FakeReply[] replies)
        {
            lock (_replies)
            {
                foreach (var r in replies)
                {
                    _replies.Enqueue(r);
                }
            }
        }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var u in GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                updates.Add(u);
            }

            return updates.ToChatResponse();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            FakeReply reply;
            lock (_replies)
            {
                Calls.Add(list);
                reply = _replies.Count > 0 ? _replies.Dequeue() : throw new InvalidOperationException("No scripted reply left.");
            }

            return reply(list);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedModels : IChatClientFactory
    {
        private readonly Dictionary<string, ScriptedClient> _clients = new(StringComparer.Ordinal);

        public ScriptedClient this[string deployment]
        {
            get
            {
                lock (_clients)
                {
                    if (!_clients.TryGetValue(deployment, out var c))
                    {
                        _clients[deployment] = c = new ScriptedClient();
                    }

                    return c;
                }
            }
        }

        public IChatClient Create(string deploymentName) => this[deploymentName];
    }

    private sealed class ScriptedSearch : IKnowledgeSearch
    {
        public List<(string Domain, string Query)> Calls { get; } = [];

        public Func<string, IReadOnlyList<KnowledgeChunk>> Results { get; set; } = _ => [];

        public Task<IReadOnlyList<KnowledgeChunk>> SearchAsync(string domain, string query, int top, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add((domain, query));
            }

            return Task.FromResult(Results(domain));
        }
    }
}
