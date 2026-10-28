using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using AgenticWorkflow.Api.Http;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Core.Storage;
using AgenticWorkflow.Core.Workflows;

namespace AgenticWorkflow.Api.Chat;

/// <summary>
/// Runs one accepted turn (the turn has been started: user message persisted, lock taken) for ANY workflow:
/// history, SSE framing, event-protocol enforcement, cancellation/timeout mapping, persistence and lock release
/// (api-contract 2.6, 4; ADR 0002 section 3; plan.md section 2). Knows nothing about specific workflows.
/// Logs carry ids, codes and exception types only, never message content.
/// </summary>
public sealed class ChatTurnRunner
{
    private readonly ISessionStore _sessions;
    private readonly IMessageStore _messages;
    private readonly IOptions<ChatLimitsOptions> _limits;
    private readonly TimeProvider _time;
    private readonly ILogger<ChatTurnRunner> _logger;

    public ChatTurnRunner(
        ISessionStore sessions,
        IMessageStore messages,
        IOptions<ChatLimitsOptions> limits,
        TimeProvider time,
        ILogger<ChatTurnRunner> logger)
    {
        _sessions = sessions;
        _messages = messages;
        _limits = limits;
        _time = time;
        _logger = logger;
    }

    public async Task<IResult> RunAsync(HttpContext http, string userId, IWorkflowDefinition workflow, MessageRecord userMessage, TurnLease lease)
    {
        var turn = new Execution(this, http, userId, workflow, userMessage, lease);
        try
        {
            return await turn.RunAsync();
        }
        finally
        {
            // Always release the lock: no-op when the end-turn batch already ran.
            await turn.EnsureLockReleasedAsync();
        }
    }

    private abstract record Outcome
    {
        public sealed record Completed(MessageKind Kind, JsonObject? Metadata) : Outcome;

        public sealed record Failed(string Code) : Outcome;

        public sealed record Cancelled : Outcome;
    }

    private sealed class Execution(
        ChatTurnRunner owner, HttpContext http, string userId, IWorkflowDefinition workflow, MessageRecord userMessage, TurnLease lease)
    {
        private readonly ChatLimitsOptions _options = owner._limits.Value;
        private readonly StringBuilder _text = new();
        private readonly List<CitationRecord> _citations = [];
        private SseWriter _writer = null!;
        private string? _agentName;
        private bool _sawAgent;
        private int _tokenCount;
        private bool _lockReleased;

        public async Task<IResult> RunAsync()
        {
            IReadOnlyList<HistoryMessage> history;
            try
            {
                var stored = await owner._messages.GetHistoryAsync(
                    userId, lease.SessionId, userMessage.Id, _options.HistoryMaxMessages, http.RequestAborted);
                history = ChatHistoryBuilder.Build(stored, userMessage.Content, _options.HistoryMaxTokens);

                ApplySseHeaders(http.Response);
                http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                _writer = new SseWriter(http.Response.Body, http.RequestAborted);
                await _writer.WriteEventAsync(SseEvents.Session, new SessionEventData(lease.SessionId));
            }
            catch (Exception ex)
            {
                if (http.RequestAborted.IsCancellationRequested)
                {
                    return Results.Empty;
                }

                owner._logger.LogError(
                    "Turn failed before the session event. SessionId={SessionId} LeaseId={LeaseId} ExceptionType={ExceptionType}",
                    lease.SessionId, lease.LeaseId, ex.GetType().Name);

                // The lock is released by the caller's finally before the 500 is written.
                if (http.Response.HasStarted)
                {
                    http.Abort();
                    return Results.Empty;
                }

                RemoveSseHeaders(http.Response);
                return Problems.Create(StatusCodes.Status500InternalServerError, Problems.Codes.InternalError);
            }

            var outcome = await RunWorkflowAsync(history);
            await FinishAsync(outcome);
            return Results.Empty;
        }

        public async Task EnsureLockReleasedAsync()
        {
            if (_lockReleased)
            {
                return;
            }

            try
            {
                await CompleteAsync(null);
            }
            catch (Exception ex)
            {
                owner._logger.LogError(
                    "Releasing the turn lock failed; it expires as a stale lock. SessionId={SessionId} LeaseId={LeaseId} ExceptionType={ExceptionType}",
                    lease.SessionId, lease.LeaseId, ex.GetType().Name);
            }
        }

        private async Task<Outcome> RunWorkflowAsync(IReadOnlyList<HistoryMessage> history)
        {
            using var timeoutCts = new CancellationTokenSource(_options.TurnTimeout, owner._time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, timeoutCts.Token);
            var ct = linked.Token;
            var request = new WorkflowRunRequest(userId, lease.SessionId, lease.LeaseId, userMessage.Content, history);

            IAsyncEnumerator<ChatStreamEvent>? enumerator = null;
            Task<bool>? pending = null;
            try
            {
                enumerator = workflow.RunAsync(request, ct).GetAsyncEnumerator(ct);
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    pending ??= enumerator.MoveNextAsync().AsTask();
                    if (!pending.IsCompleted)
                    {
                        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var heartbeat = Task.Delay(_options.HeartbeatInterval, owner._time, heartbeatCts.Token);
                        var first = await Task.WhenAny(pending, heartbeat);
                        await heartbeatCts.CancelAsync();
                        if (first != pending)
                        {
                            // The delay is only cancelled when the turn is cancelled (client abort or timeout).
                            ct.ThrowIfCancellationRequested();
                            await _writer.WriteHeartbeatAsync();
                            continue;
                        }
                    }

                    var hasNext = await pending;
                    pending = null;
                    if (!hasNext)
                    {
                        owner._logger.LogWarning(
                            "Workflow ended without a terminal event. WorkflowId={WorkflowId} SessionId={SessionId} LeaseId={LeaseId}",
                            workflow.Id, lease.SessionId, lease.LeaseId);
                        return new Outcome.Failed(StreamErrors.InternalError);
                    }

                    if (await HandleAsync(enumerator.Current) is { } terminal)
                    {
                        return terminal;
                    }
                }
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                return new Outcome.Cancelled();
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                return new Outcome.Failed(StreamErrors.Timeout);
            }
            catch (OperationCanceledException)
            {
                // Neither client abort nor turn timeout (for example an SDK-internal timeout).
                return new Outcome.Failed(StreamErrors.InternalError);
            }
            catch (Exception) when (http.RequestAborted.IsCancellationRequested)
            {
                // Typically a write to a connection the client already closed.
                return new Outcome.Cancelled();
            }
            catch (Exception ex)
            {
                var code = StreamErrors.CodeFor(ex);
                owner._logger.LogWarning(
                    "Workflow failed. WorkflowId={WorkflowId} SessionId={SessionId} LeaseId={LeaseId} Code={Code} ExceptionType={ExceptionType}",
                    workflow.Id, lease.SessionId, lease.LeaseId, code, ex.GetType().Name);
                return new Outcome.Failed(code);
            }
            finally
            {
                await DisposeEnumeratorAsync(enumerator, pending);
            }
        }

        /// <summary>Applies one workflow event. Returns the terminal outcome, or null to keep reading.</summary>
        private async Task<Outcome?> HandleAsync(ChatStreamEvent ev)
        {
            switch (ev)
            {
                case AgentChanged agent:
                    if (_sawAgent)
                    {
                        return Violation("second AgentChanged");
                    }

                    _sawAgent = true;
                    _agentName = agent.Name;
                    await _writer.WriteEventAsync(SseEvents.Agent, new AgentEventData(agent.Name));
                    return null;

                case TokenDelta token:
                    if (string.IsNullOrEmpty(token.Text))
                    {
                        return null;
                    }

                    if (!_sawAgent)
                    {
                        return Violation("TokenDelta before AgentChanged");
                    }

                    if (_citations.Count > 0)
                    {
                        return Violation("TokenDelta after CitationFound");
                    }

                    _text.Append(token.Text);
                    _tokenCount++;
                    await _writer.WriteEventAsync(SseEvents.Token, new TokenEventData(token.Text));
                    return null;

                case CitationFound citation:
                    if (!_sawAgent)
                    {
                        return Violation("CitationFound before AgentChanged");
                    }

                    var url = SafeUrl(citation.Url);
                    _citations.Add(new CitationRecord(citation.Ref, citation.Title, citation.DocId, url));
                    await _writer.WriteEventAsync(
                        SseEvents.Citation, new CitationEventData(citation.Ref, citation.Title, citation.DocId, url));
                    return null;

                case TurnCompleted completed:
                    return _tokenCount == 0
                        ? Violation("TurnCompleted without tokens")
                        : new Outcome.Completed(completed.Kind, completed.Metadata);

                case TurnFailed failed:
                    if (!StreamErrors.IsKnown(failed.Code))
                    {
                        owner._logger.LogWarning(
                            "Workflow reported a code outside the contract; mapped to internal_error. WorkflowId={WorkflowId} SessionId={SessionId}",
                            workflow.Id, lease.SessionId);
                    }

                    return new Outcome.Failed(StreamErrors.Normalize(failed.Code));

                default:
                    return Violation("unknown event type");
            }
        }

        private Outcome Violation(string what)
        {
            owner._logger.LogWarning(
                "Workflow event protocol violation: {Violation}. WorkflowId={WorkflowId} SessionId={SessionId} LeaseId={LeaseId}",
                what, workflow.Id, lease.SessionId, lease.LeaseId);
            return new Outcome.Failed(StreamErrors.WorkflowError);
        }

        private async Task FinishAsync(Outcome outcome)
        {
            switch (outcome)
            {
                case Outcome.Cancelled:
                    // Client abort: persist the partial only when at least one token was produced; write nothing.
                    await TryCompleteAsync(_tokenCount > 0 ? BuildMessage(MessageStatus.Cancelled, MessageKind.Answer, null, null) : null);
                    return;

                case Outcome.Completed done:
                {
                    CompleteTurnResult result;
                    try
                    {
                        result = await CompleteAsync(BuildMessage(MessageStatus.Complete, done.Kind, done.Metadata, null));
                    }
                    catch (Exception ex)
                    {
                        owner._logger.LogError(
                            "Persisting the assistant message failed. SessionId={SessionId} LeaseId={LeaseId} ExceptionType={ExceptionType}",
                            lease.SessionId, lease.LeaseId, ex.GetType().Name);
                        await TryWriteErrorAsync(StreamErrors.InternalError);
                        return;
                    }

                    if (result == CompleteTurnResult.Completed)
                    {
                        await TryWriteAsync(SseEvents.Done, new DoneEventData(lease.AssistantMessageId));
                    }
                    else
                    {
                        await TryWriteErrorAsync(StreamErrors.InternalError);
                    }

                    return;
                }

                case Outcome.Failed failed:
                    await TryCompleteAsync(BuildMessage(MessageStatus.Failed, MessageKind.Answer, null, failed.Code));
                    await TryWriteErrorAsync(failed.Code);
                    return;
            }
        }

        private MessageRecord BuildMessage(MessageStatus status, MessageKind kind, JsonObject? metadata, string? errorCode) =>
            new(
                lease.AssistantMessageId,
                lease.SessionId,
                ChatRole.Assistant,
                _text.ToString(),
                owner._time.GetUtcNow(),
                status,
                kind,
                _agentName,
                status == MessageStatus.Complete ? _citations.ToList() : [],
                errorCode,
                metadata);

        private async Task TryCompleteAsync(MessageRecord? message)
        {
            try
            {
                await CompleteAsync(message);
            }
            catch (Exception ex)
            {
                owner._logger.LogError(
                    "Persisting the assistant message failed. SessionId={SessionId} LeaseId={LeaseId} ExceptionType={ExceptionType}",
                    lease.SessionId, lease.LeaseId, ex.GetType().Name);
            }
        }

        /// <summary>Never uses the request token: bounded by Chat:PersistTimeout only (api-contract 4.5).</summary>
        private async Task<CompleteTurnResult> CompleteAsync(MessageRecord? message)
        {
            using var persistCts = new CancellationTokenSource(_options.PersistTimeout, owner._time);
            var result = await owner._sessions.CompleteTurnAsync(userId, lease, message, persistCts.Token);
            _lockReleased = true;
            if (result != CompleteTurnResult.Completed)
            {
                owner._logger.LogWarning(
                    "Turn ended with nothing persisted. SessionId={SessionId} LeaseId={LeaseId} Result={Result}",
                    lease.SessionId, lease.LeaseId, result);
            }

            return result;
        }

        private Task TryWriteErrorAsync(string code) =>
            TryWriteAsync(SseEvents.Error, new ErrorEventData(code, StreamErrors.MessageFor(code)));

        private async Task TryWriteAsync<T>(string eventName, T data)
        {
            try
            {
                await _writer.WriteEventAsync(eventName, data);
            }
            catch (Exception)
            {
                // The client is gone; the turn is already persisted. Nothing more to do.
            }
        }

        private async Task DisposeEnumeratorAsync(IAsyncEnumerator<ChatStreamEvent>? enumerator, Task<bool>? pending)
        {
            if (enumerator is null)
            {
                return;
            }

            try
            {
                if (pending is { IsCompleted: false })
                {
                    // A workflow that ignores cancellation is still inside MoveNextAsync; disposing now would be
                    // unsupported. Dispose after it finishes, without holding up the response or the lock release.
                    _ = pending.ContinueWith(
                        _ => enumerator.DisposeAsync().AsTask().ContinueWith(static _ => { }, TaskScheduler.Default),
                        TaskScheduler.Default);
                    return;
                }

                await enumerator.DisposeAsync();
            }
            catch (Exception ex)
            {
                owner._logger.LogDebug("Disposing the workflow enumerator failed. ExceptionType={ExceptionType}", ex.GetType().Name);
            }
        }

        private static string? SafeUrl(string? url) =>
            url is not null
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host)
                ? url
                : null;

        private static void ApplySseHeaders(HttpResponse response)
        {
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.Headers.CacheControl = "no-cache, no-transform";
            response.Headers["X-Accel-Buffering"] = "no";
        }

        private static void RemoveSseHeaders(HttpResponse response)
        {
            response.Headers.CacheControl = default;
            response.Headers.Remove("X-Accel-Buffering");
            response.ContentType = null;
        }
    }
}
