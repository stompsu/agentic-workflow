using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgenticWorkflow.Core.Serialization;

namespace AgenticWorkflow.Api.Chat;

public static class SseEvents
{
    public const string Session = "session";
    public const string Agent = "agent";
    public const string Token = "token";
    public const string Citation = "citation";
    public const string Done = "done";
    public const string Error = "error";
}

public sealed record SessionEventData(string SessionId);

public sealed record AgentEventData(string Name);

public sealed record TokenEventData(string Text);

public sealed record CitationEventData(string Ref, string Title, string DocId, string? Url);

public sealed record DoneEventData(string MessageId);

public sealed record ErrorEventData(string Code, string Message);

/// <summary>
/// Writes SSE frames (api-contract 4.1): <c>id</c> counter starting at 1, <c>event</c>, one-line camelCase JSON
/// <c>data</c>, blank line; comment heartbeats carry no id. Flushes after every frame.
/// </summary>
public sealed class SseWriter(Stream body, CancellationToken cancellationToken)
{
    private static readonly JsonSerializerOptions DataOptions = CreateOptions();

    private static readonly byte[] HeartbeatFrame = Encoding.UTF8.GetBytes(": hb\n\n");

    private int _id;

    public int LastId => _id;

    public async Task WriteEventAsync<T>(string eventName, T data)
    {
        var json = JsonSerializer.Serialize(data, DataOptions);
        var frame = Encoding.UTF8.GetBytes($"id: {++_id}\nevent: {eventName}\ndata: {json}\n\n");
        await body.WriteAsync(frame, cancellationToken);
        await body.FlushAsync(cancellationToken);
    }

    public async Task WriteHeartbeatAsync()
    {
        await body.WriteAsync(HeartbeatFrame, cancellationToken);
        await body.FlushAsync(cancellationToken);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        // Same camelCase rules as the REST API; nulls are written (citation url: null). Control characters,
        // including newlines, are always escaped, so "data" stays on one line. Non-ASCII text is kept readable.
        var options = JsonDefaults.Create(omitNulls: false);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.WriteIndented = false;
        return options;
    }
}
