using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SupportChat.Core.Storage;
using SupportChat.Core.Workflows;
using SupportChat.Infrastructure.Storage.InMemory;

namespace SupportChat.Api.Tests.Chat;

/// <summary>A workflow type that exists only in tests; its behavior is a script the test sets per scenario.</summary>
public sealed class ScriptedWorkflow : IWorkflowDefinition
{
    public const string WorkflowId = "scripted-test";

    private readonly List<WorkflowRunRequest> _requests = [];

    public string Id => WorkflowId;

    public string DisplayName => "Scripted";

    public string Description => "Scripted by tests.";

    public Func<WorkflowRunRequest, CancellationToken, IAsyncEnumerable<ChatStreamEvent>> Script { get; set; } =
        (_, _) => Seq(new AgentChanged("Scripted agent"), new TokenDelta("hello"), new TurnCompleted());

    public IReadOnlyList<WorkflowRunRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public IAsyncEnumerable<ChatStreamEvent> RunAsync(WorkflowRunRequest request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        return Script(request, cancellationToken);
    }

    public static async IAsyncEnumerable<ChatStreamEvent> Seq(params ChatStreamEvent[] events)
    {
        foreach (var e in events)
        {
            yield return e;
        }

        await Task.CompletedTask;
    }

    /// <summary>Yields <paramref name="before"/>, then waits for <paramref name="gate"/> (or cancellation), then <paramref name="after"/>.</summary>
    public static async IAsyncEnumerable<ChatStreamEvent> Hold(
        ChatStreamEvent[] before, Task gate, ChatStreamEvent[] after, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var e in before)
        {
            yield return e;
        }

        await gate.WaitAsync(ct);
        foreach (var e in after)
        {
            yield return e;
        }
    }

    public static async IAsyncEnumerable<ChatStreamEvent> Throw(ChatStreamEvent[] before, Exception exception)
    {
        foreach (var e in before)
        {
            yield return e;
        }

        await Task.Yield();
        throw exception;
    }
}

/// <summary>Decorates the in-memory store with failure injection and call recording.</summary>
public sealed class ControlledStore(InMemoryChatStore inner) : ISessionStore, IMessageStore
{
    public Exception? HistoryFailure { get; set; }

    public CompleteTurnResult? CompleteOverride { get; set; }

    public int CompleteFailures { get; set; }

    public List<(TurnLease Lease, MessageRecord? Message)> Completes { get; } = [];

    public Task<CreateSessionResult> CreateAsync(string userId, string workflowId, string? title, bool enforceGuestQuota, CancellationToken ct) =>
        inner.CreateAsync(userId, workflowId, title, enforceGuestQuota, ct);

    public Task<SessionRecord?> GetAsync(string userId, string sessionId, CancellationToken ct) => inner.GetAsync(userId, sessionId, ct);

    public Task<Page<SessionRecord>> ListAsync(string userId, int pageSize, string? continuationToken, CancellationToken ct) =>
        inner.ListAsync(userId, pageSize, continuationToken, ct);

    public Task<DeleteSessionResult> DeleteAsync(string userId, string sessionId, CancellationToken ct) =>
        inner.DeleteAsync(userId, sessionId, ct);

    public Task<DeleteUserDataResult> DeleteAllUserDataAsync(string userId, TimeSpan budget, CancellationToken ct) =>
        inner.DeleteAllUserDataAsync(userId, budget, ct);

    public Task<StartTurnResult> TryStartTurnAsync(string userId, string sessionId, MessageRecord userMessage, bool enforceGuestQuota, CancellationToken ct) =>
        inner.TryStartTurnAsync(userId, sessionId, userMessage, enforceGuestQuota, ct);

    public Task<CompleteTurnResult> CompleteTurnAsync(string userId, TurnLease lease, MessageRecord? assistantMessage, CancellationToken ct)
    {
        lock (Completes)
        {
            Completes.Add((lease, assistantMessage));
        }

        if (CompleteFailures > 0)
        {
            CompleteFailures--;
            throw new InvalidOperationException("injected persistence failure");
        }

        return CompleteOverride is { } forced ? Task.FromResult(forced) : inner.CompleteTurnAsync(userId, lease, assistantMessage, ct);
    }

    public Task<(IReadOnlyList<MessageRecord> Items, bool HasMore)> GetRecentAsync(string userId, string sessionId, int max, CancellationToken ct) =>
        inner.GetRecentAsync(userId, sessionId, max, ct);

    public Task<IReadOnlyList<MessageRecord>> GetHistoryAsync(
        string userId, string sessionId, string excludeMessageId, int maxMessages, CancellationToken ct) =>
        HistoryFailure is { } failure
            ? throw failure
            : inner.GetHistoryAsync(userId, sessionId, excludeMessageId, maxMessages, ct);
}

/// <summary>The real app with a scripted workflow, a failure-injecting store and a fake clock.</summary>
public sealed class ChatHost : IDisposable
{
    public ChatHost(Action<Dictionary<string, string?>>? configure = null, Action<IServiceCollection>? extraServices = null, string environment = "Test")
    {
        ApiFactory? factory = null;
        Factory = factory = new ApiFactory(
            environment,
            configure: c =>
            {
                c["Foundry:ProjectEndpoint"] = "https://example.invalid/api/projects/p";
                configure?.Invoke(c);
            },
            services: s =>
            {
                Control = new ControlledStore(factory!.Store);
                s.AddSingleton<IWorkflowDefinition>(Workflow);
                s.RemoveAll<ISessionStore>();
                s.RemoveAll<IMessageStore>();
                s.AddSingleton<ISessionStore>(Control);
                s.AddSingleton<IMessageStore>(Control);
                extraServices?.Invoke(s);
            });
        User = TestTokens.NewOid();
        Client = Factory.CreateAuthorizedClient(User);
    }

    public ApiFactory Factory { get; }

    public ScriptedWorkflow Workflow { get; } = new();

    public ControlledStore Control { get; private set; } = null!;

    public string User { get; }

    public HttpClient Client { get; }

    public Task<string> NewSessionAsync(HttpClient? client = null) => (client ?? Client).CreateSessionAsync(ScriptedWorkflow.WorkflowId);

    public void Dispose() => Factory.Dispose();

    public async Task<JsonNode> GetSessionAsync(string sessionId, HttpClient? client = null)
    {
        var response = await (client ?? Client).GetAsync($"/api/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    public async Task<JsonArray> MessagesAsync(string sessionId) => (await GetSessionAsync(sessionId))["messages"]!.AsArray();

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(15);
        }
    }
}

public sealed record SseEvent(int? Id, string Name, JsonNode Data, string Raw);

/// <summary>Incremental SSE reader over a streaming response (headers read, body not buffered).</summary>
public sealed class SseResponse : IDisposable
{
    private readonly HttpResponseMessage _response;
    private readonly StreamReader _reader;

    private SseResponse(HttpResponseMessage response, StreamReader reader)
    {
        _response = response;
        _reader = reader;
    }

    public HttpResponseMessage Response => _response;

    public static async Task<SseResponse> PostAsync(
        HttpClient client, string sessionId, string content, CancellationToken cancellationToken = default, Action<HttpRequestMessage>? configure = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/messages")
        {
            Content = JsonContent.Create(new { content }),
        };
        request.Headers.Accept.ParseAdd("text/event-stream");
        configure?.Invoke(request);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var stream = response.IsSuccessStatusCode ? await response.Content.ReadAsStreamAsync(cancellationToken) : Stream.Null;
        return new SseResponse(response, new StreamReader(stream));
    }

    /// <summary>Next frame (event or comment) as raw text without the terminating blank line; null at end of stream.</summary>
    public async Task<string?> ReadRawFrameAsync(CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        while (true)
        {
            var line = await _reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return lines.Count == 0 ? null : string.Join('\n', lines);
            }

            if (line.Length == 0)
            {
                if (lines.Count == 0)
                {
                    continue;
                }

                return string.Join('\n', lines);
            }

            lines.Add(line);
        }
    }

    /// <summary>Next non-comment event; heartbeats are skipped.</summary>
    public async Task<SseEvent?> ReadEventAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var raw = await ReadRawFrameAsync(cancellationToken);
            if (raw is null)
            {
                return null;
            }

            if (raw.StartsWith(':'))
            {
                continue;
            }

            return Parse(raw);
        }
    }

    public async Task<List<SseEvent>> ReadAllEventsAsync()
    {
        var list = new List<SseEvent>();
        while (await ReadEventAsync() is { } e)
        {
            list.Add(e);
        }

        return list;
    }

    public async Task<string> ReadRemainingRawAsync() => await _reader.ReadToEndAsync();

    public static SseEvent Parse(string raw)
    {
        int? id = null;
        string? name = null;
        string? data = null;
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = int.Parse(line[4..]);
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line[7..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                Assert.Null(data);
                data = line[6..];
            }
        }

        return new SseEvent(id, name!, JsonNode.Parse(data!)!, raw);
    }

    public void Dispose()
    {
        _reader.Dispose();
        _response.Dispose();
    }
}
