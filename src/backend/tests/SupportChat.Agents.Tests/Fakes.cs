using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SupportChat.Core.Knowledge;
using SupportChat.Core.Models;

namespace SupportChat.Agents.Tests;

/// <summary>One call received by a <see cref="FakeChatClient"/>.</summary>
public sealed record FakeCall(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options, CancellationToken CancellationToken)
{
    public bool IsStructured => Options?.ResponseFormat is ChatResponseFormatJson;
}

public delegate IAsyncEnumerable<ChatResponseUpdate> FakeReply(FakeCall call);

/// <summary>Scripted IChatClient: each call consumes the next reply. No network, no model.</summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly Queue<FakeReply> _replies = new();
    private readonly object _gate = new();

    public List<FakeCall> Calls { get; } = [];

    public FakeChatClient Enqueue(params FakeReply[] replies)
    {
        foreach (var r in replies)
        {
            _replies.Enqueue(r);
        }

        return this;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
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
        FakeReply reply;
        var call = new FakeCall(messages.ToList(), options, cancellationToken);
        lock (_gate)
        {
            Calls.Add(call);
            if (_replies.Count == 0)
            {
                throw new InvalidOperationException("FakeChatClient has no scripted reply left.");
            }

            reply = _replies.Dequeue();
        }

        return reply(call);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

public static class Replies
{
    public static FakeReply Text(params string[] chunks) => _ => Stream(chunks.Select(c => new ChatResponseUpdate(ChatRole.Assistant, c)));

    public static FakeReply Classification(string category, double confidence, string? clarifying = null, string rationale = "because") =>
        Text(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["category"] = category,
            ["confidence"] = confidence,
            ["rationale"] = rationale,
            ["clarifyingQuestion"] = clarifying,
        }));

    public static FakeReply ToolCall(string query, IDictionary<string, object?>? extraArgs = null) => _ =>
    {
        var args = new Dictionary<string, object?> { ["query"] = query };
        if (extraArgs is not null)
        {
            foreach (var kv in extraArgs)
            {
                args[kv.Key] = kv.Value;
            }
        }

        var update = new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new FunctionCallContent("call-" + Guid.NewGuid().ToString("N"), "search_knowledge", args)],
        };
        return Stream([update]);
    };

    /// <summary>Emits the given chunks, then waits until the call is cancelled.</summary>
    public static FakeReply TextThenHang(params string[] chunks) => call => HangAfter(chunks, call.CancellationToken);

    public static FakeReply Throws(Exception ex) => _ => ThrowAsync(ex);

    private static async IAsyncEnumerable<ChatResponseUpdate> Stream(IEnumerable<ChatResponseUpdate> updates)
    {
        foreach (var u in updates)
        {
            await Task.Yield();
            yield return u;
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> HangAfter(
        string[] chunks, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var c in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, c);
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowAsync(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

public sealed class FakeChatClientFactory : IChatClientFactory
{
    public Dictionary<string, FakeChatClient> Clients { get; } = new(StringComparer.Ordinal);

    public List<string> Requested { get; } = [];

    public FakeChatClient this[string deployment]
    {
        get
        {
            if (!Clients.TryGetValue(deployment, out var c))
            {
                Clients[deployment] = c = new FakeChatClient();
            }

            return c;
        }
    }

    public IChatClient Create(string deploymentName)
    {
        Requested.Add(deploymentName);
        return this[deploymentName];
    }
}

public sealed class FakeKnowledgeSearch : IKnowledgeSearch
{
    public List<(string Domain, string Query, int Top)> Calls { get; } = [];

    public Func<string, IReadOnlyList<KnowledgeChunk>> Results { get; set; } = _ => [];

    public Exception? Throw { get; set; }

    public Task<IReadOnlyList<KnowledgeChunk>> SearchAsync(string domain, string query, int top, CancellationToken cancellationToken)
    {
        Calls.Add((domain, query, top));
        if (Throw is not null)
        {
            throw Throw;
        }

        return Task.FromResult(Results(domain));
    }
}

public sealed class CapturingLogger<T>(LogLevel minimum = LogLevel.Information) : ILogger<T>
{
    public List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var props = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
        Entries.Add((logLevel, formatter(state, exception), props));
    }
}
