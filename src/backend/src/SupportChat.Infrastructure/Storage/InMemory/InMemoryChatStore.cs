using System.Globalization;
using Microsoft.Extensions.AI;
using SupportChat.Core.Options;
using SupportChat.Core.Storage;

namespace SupportChat.Infrastructure.Storage.InMemory;

/// <summary>
/// In-memory <see cref="ISessionStore"/> + <see cref="IMessageStore"/> for tests. Enforces userId exactly like the
/// Cosmos partition key: every lookup is keyed by (userId, sessionId). Never registered by AddInfrastructure.
/// </summary>
public sealed class InMemoryChatStore(TimeProvider timeProvider, ChatLimitsOptions? options = null) : ISessionStore, IMessageStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string User, string Session), State> _sessions = new();
    private readonly ChatLimitsOptions _options = options ?? new ChatLimitsOptions();

    /// <summary>Test hook: runs after DELETE took the lock and before messages are removed (throw to simulate a crash).</summary>
    public Func<Task>? AfterDeleteLocked { get; set; }

    public Task<SessionRecord> CreateAsync(string userId, string workflowId, string? title, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var state = new State { Id = TurnRules.NewId(), WorkflowId = workflowId, Title = title, CreatedAt = now, UpdatedAt = now };
        lock (_gate)
        {
            _sessions[(userId, state.Id)] = state;
            return Task.FromResult(state.ToRecord());
        }
    }

    public Task<SessionRecord?> GetAsync(string userId, string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(Find(userId, sessionId) is { Deleting: false } s ? s.ToRecord() : null);
        }
    }

    public Task<Page<SessionRecord>> ListAsync(string userId, int pageSize, string? continuationToken, CancellationToken ct)
    {
        var offset = 0;
        if (continuationToken is not null)
        {
            var inner = ContinuationTokens.Decode(continuationToken);
            if (!inner.StartsWith("o:", StringComparison.Ordinal)
                || !int.TryParse(inner.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out offset))
            {
                throw new InvalidContinuationTokenException();
            }
        }

        lock (_gate)
        {
            var all = _sessions
                .Where(kv => kv.Key.User == userId && !kv.Value.Deleting)
                .Select(kv => kv.Value)
                .OrderByDescending(s => s.UpdatedAt)
                .ThenByDescending(s => s.Id, StringComparer.Ordinal)
                .ToList();
            var items = all.Skip(offset).Take(pageSize).Select(s => s.ToRecord()).ToList();
            var next = offset + items.Count < all.Count ? ContinuationTokens.Encode($"o:{offset + items.Count}") : null;
            return Task.FromResult(new Page<SessionRecord>(items, next));
        }
    }

    public async Task<DeleteSessionResult> DeleteAsync(string userId, string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Find(userId, sessionId) is not { } state)
            {
                return DeleteSessionResult.NotFound;
            }

            if (!state.Deleting)
            {
                var now = timeProvider.GetUtcNow();
                if (state.ActiveTurnId is not null && !TurnRules.IsStale(state.ActiveTurnStartedAt, now, _options.TurnTimeout))
                {
                    return DeleteSessionResult.Busy;
                }

                state.Deleting = true;
                state.ActiveTurnId = TurnRules.NewId();
                state.ActiveTurnStartedAt = now;
            }
        }

        if (AfterDeleteLocked is { } hook)
        {
            await hook();
        }

        lock (_gate)
        {
            _sessions.Remove((userId, sessionId));
        }

        return DeleteSessionResult.Deleted;
    }

    public Task<StartTurnResult> TryStartTurnAsync(string userId, string sessionId, MessageRecord userMessage, CancellationToken ct)
    {
        TurnRules.EnsureUserMessage(sessionId, userMessage);
        lock (_gate)
        {
            if (Find(userId, sessionId) is not { Deleting: false } state)
            {
                return Task.FromResult<StartTurnResult>(new StartTurnResult.NotFound());
            }

            var now = timeProvider.GetUtcNow();
            if (state.ActiveTurnId is not null && !TurnRules.IsStale(state.ActiveTurnStartedAt, now, _options.TurnTimeout))
            {
                return Task.FromResult<StartTurnResult>(new StartTurnResult.Busy());
            }

            var lease = new TurnLease(sessionId, TurnRules.NewId(), TurnRules.NewId(), now);
            state.ActiveTurnId = lease.LeaseId;
            state.ActiveTurnStartedAt = now;
            state.UpdatedAt = now;
            state.MessageCount++;
            state.Title ??= TurnRules.AutoTitle(userMessage.Content);
            state.Messages.Add(userMessage);
            return Task.FromResult<StartTurnResult>(new StartTurnResult.Started(lease));
        }
    }

    public Task<CompleteTurnResult> CompleteTurnAsync(string userId, TurnLease lease, MessageRecord? assistantMessage, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Find(userId, lease.SessionId) is not { } state)
            {
                return Task.FromResult(CompleteTurnResult.SessionGone);
            }

            if (state.ActiveTurnId != lease.LeaseId)
            {
                return Task.FromResult(CompleteTurnResult.LeaseLost);
            }

            if (assistantMessage is not null)
            {
                if (assistantMessage.SessionId != lease.SessionId)
                {
                    throw new ArgumentException("The message does not belong to the lease's session.", nameof(assistantMessage));
                }

                state.Messages.Add(assistantMessage);
                state.MessageCount++;
            }

            state.ActiveTurnId = null;
            state.UpdatedAt = timeProvider.GetUtcNow();
            return Task.FromResult(CompleteTurnResult.Completed);
        }
    }

    public Task<(IReadOnlyList<MessageRecord> Items, bool HasMore)> GetRecentAsync(string userId, string sessionId, int max, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Find(userId, sessionId) is not { Deleting: false } state)
            {
                return Task.FromResult<(IReadOnlyList<MessageRecord>, bool)>((Array.Empty<MessageRecord>(), false));
            }

            var ordered = Ordered(state);
            var items = ordered.Skip(Math.Max(0, ordered.Count - max)).ToList();
            return Task.FromResult<(IReadOnlyList<MessageRecord>, bool)>((items, ordered.Count > max));
        }
    }

    public Task<IReadOnlyList<MessageRecord>> GetHistoryAsync(
        string userId, string sessionId, string excludeMessageId, int maxMessages, CancellationToken ct)
    {
        lock (_gate)
        {
            if (Find(userId, sessionId) is not { Deleting: false } state)
            {
                return Task.FromResult<IReadOnlyList<MessageRecord>>(Array.Empty<MessageRecord>());
            }

            var window = Ordered(state).Where(m => m.Id != excludeMessageId).ToList();
            window = window.Skip(Math.Max(0, window.Count - maxMessages)).ToList();
            return Task.FromResult(TurnRules.ApplyHistoryRules(window));
        }
    }

    private State? Find(string userId, string sessionId) =>
        _sessions.TryGetValue((userId, sessionId), out var state) ? state : null;

    private static List<MessageRecord> Ordered(State state) =>
        state.Messages.Select((m, i) => (m, i)).OrderBy(x => x.m.CreatedAt).ThenBy(x => x.i).Select(x => x.m).ToList();

    private sealed class State
    {
        public required string Id { get; init; }

        public required string WorkflowId { get; init; }

        public string? Title { get; set; }

        public DateTimeOffset CreatedAt { get; init; }

        public DateTimeOffset UpdatedAt { get; set; }

        public int MessageCount { get; set; }

        public bool Deleting { get; set; }

        public string? ActiveTurnId { get; set; }

        public DateTimeOffset ActiveTurnStartedAt { get; set; }

        public List<MessageRecord> Messages { get; } = [];

        public SessionRecord ToRecord() => new(Id, WorkflowId, Title, CreatedAt, UpdatedAt, MessageCount);
    }
}
