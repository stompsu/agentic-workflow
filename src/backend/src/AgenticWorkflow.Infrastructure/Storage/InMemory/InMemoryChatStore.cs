using System.Globalization;
using Microsoft.Extensions.AI;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Core.Storage;
using AgenticWorkflow.Infrastructure.Storage.Cosmos;

namespace AgenticWorkflow.Infrastructure.Storage.InMemory;

/// <summary>
/// In-memory <see cref="ISessionStore"/> + <see cref="IMessageStore"/> for tests. Enforces userId exactly like the
/// Cosmos partition key: every lookup is keyed by (userId, sessionId). Never registered by AddInfrastructure.
/// TTL: like Cosmos, sessions and messages carry <c>ttl</c> (RetentionDays * 86400, omitted when unset), counted from
/// their last write by the injected TimeProvider; expired documents are invisible (a session that expires leaves its
/// remaining messages behind as orphans, as in Cosmos).
/// </summary>
public sealed class InMemoryChatStore : ISessionStore, IMessageStore, IGuestStore
{
    private const int DeleteChunkSize = 100;

    private readonly object _gate = new();
    private readonly Dictionary<(string User, string Session), State> _sessions = new();
    private readonly List<(string User, Stored Message)> _orphans = [];
    private readonly ChatLimitsOptions _options;
    private readonly RetentionPolicy _retention;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, GuestState> _guests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _counterWrites = new(StringComparer.Ordinal);

    public InMemoryChatStore(TimeProvider timeProvider, ChatLimitsOptions? options = null, GuestOptions? guest = null)
    {
        this.timeProvider = timeProvider;
        _options = options ?? new ChatLimitsOptions();
        _retention = new RetentionPolicy(_options.RetentionDays, (guest ?? new GuestOptions()).DataTtl);
    }

    /// <summary>Test hook: runs after DELETE took the lock and before messages are removed (throw to simulate a crash).</summary>
    public Func<Task>? AfterDeleteLocked { get; set; }

    /// <summary>Test hook: called after each session mark and each message chunk of DeleteAllUserDataAsync.</summary>
    public Action? OnDeleteAllProgress { get; set; }

    /// <summary>Test hook: a message document without a session document (left by an interrupted delete).</summary>
    public void SeedOrphanMessage(string userId, MessageRecord message)
    {
        lock (_gate)
        {
            _orphans.Add((userId, NewStored(userId, message)));
        }
    }

    /// <summary>Test hook: ttl of the session document (null = property omitted). Throws if the session is gone.</summary>
    public int? SessionTtl(string userId, string sessionId)
    {
        lock (_gate)
        {
            Purge();
            return (Find(userId, sessionId) ?? throw new KeyNotFoundException()).Ttl;
        }
    }

    /// <summary>Test hook: ttl of every stored message of the session (null entries = property omitted).</summary>
    public IReadOnlyList<int?> MessageTtls(string userId, string sessionId)
    {
        lock (_gate)
        {
            Purge();
            return Find(userId, sessionId)?.Messages.Select(m => m.Ttl).ToList() ?? [];
        }
    }

    /// <summary>Test hook: number of documents (sessions of any state, messages, orphans) the user has.</summary>
    public int DocumentCount(string userId)
    {
        lock (_gate)
        {
            Purge();
            return _sessions.Where(kv => kv.Key.User == userId).Sum(kv => 1 + kv.Value.Messages.Count)
                   + _orphans.Count(o => o.User == userId);
        }
    }

    /// <summary>Test hook: true while the session document exists in state deleting.</summary>
    public bool IsDeleting(string userId, string sessionId)
    {
        lock (_gate)
        {
            Purge();
            return Find(userId, sessionId)?.Deleting == true;
        }
    }

    /// <summary>Test hook: the guest document (null = guest ended).</summary>
    public GuestRecord? GuestDocument(string guestUserId)
    {
        lock (_gate)
        {
            Purge();
            return _guests.TryGetValue(guestUserId, out var g) ? g.ToRecord() : null;
        }
    }

    /// <summary>Test hook: removes only the guest document (as ttl expiry would).</summary>
    public void RemoveGuestDocument(string guestUserId)
    {
        lock (_gate)
        {
            _guests.Remove(guestUserId);
        }
    }

    /// <summary>Test hook: value of a counter window (0 when absent); also the number of counter increments made.</summary>
    public long CounterValue(GuestCounter counter, DateTimeOffset window)
    {
        lock (_gate)
        {
            return _counters.GetValueOrDefault(CounterId(counter, window));
        }
    }

    /// <summary>Test hook: number of IncrementCounterAsync calls so far (proves "no Cosmos write while closed").</summary>
    public int CounterIncrementCalls { get; private set; }

    public Task CreateAsync(GuestRecord guest, CancellationToken ct)
    {
        lock (_gate)
        {
            Purge();
            var now = timeProvider.GetUtcNow();
            if (_guests.ContainsKey(guest.UserId))
            {
                throw new InvalidOperationException("Guest document already exists (409).");
            }

            _guests[guest.UserId] = new GuestState
            {
                UserId = guest.UserId,
                CreatedAt = guest.CreatedAt,
                ExpiresAt = guest.ExpiresAt,
                TurnCount = guest.TurnCount,
                MaxTurns = guest.MaxTurns,
                SessionCount = guest.SessionCount,
                MaxSessions = guest.MaxSessions,
                LastWrite = now,
            };
        }

        return Task.CompletedTask;
    }

    public Task<GuestRecord?> GetAsync(string guestUserId, CancellationToken ct)
    {
        lock (_gate)
        {
            Purge();
            return Task.FromResult(_guests.TryGetValue(guestUserId, out var g) ? g.ToRecord() : null);
        }
    }

    public Task<long> IncrementCounterAsync(GuestCounter counter, DateTimeOffset now, CancellationToken ct)
    {
        lock (_gate)
        {
            Purge();
            CounterIncrementCalls++;
            var id = CounterId(counter, now);
            _counters[id] = _counters.GetValueOrDefault(id) + 1;
            _counterWrites[id] = timeProvider.GetUtcNow();
            return Task.FromResult(_counters[id]);
        }
    }

    private static string CounterId(GuestCounter counter, DateTimeOffset window) => CosmosGuestStore.CounterId(counter, window);

    public Task<CreateSessionResult> CreateAsync(string userId, string workflowId, string? title, bool enforceGuestQuota, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var state = new State { Id = TurnRules.NewId(), UserId = userId, WorkflowId = workflowId, Title = title, CreatedAt = now, UpdatedAt = now };
        lock (_gate)
        {
            Purge();
            if (enforceGuestQuota)
            {
                if (!_guests.TryGetValue(userId, out var guest))
                {
                    return Task.FromResult<CreateSessionResult>(new CreateSessionResult.GuestExpired());
                }

                if (guest.SessionCount >= guest.MaxSessions)
                {
                    return Task.FromResult<CreateSessionResult>(new CreateSessionResult.GuestQuotaExceeded());
                }

                guest.SessionCount++;
                TouchGuest(guest, now);
            }

            Touch(state, now);
            _sessions[(userId, state.Id)] = state;
            return Task.FromResult<CreateSessionResult>(new CreateSessionResult.Created(state.ToRecord()));
        }
    }

    public Task<SessionRecord?> GetAsync(string userId, string sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            Purge();
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
            Purge();
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
            Purge();
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
                Touch(state, now);
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

    public Task<DeleteUserDataResult> DeleteAllUserDataAsync(string userId, TimeSpan budget, CancellationToken ct)
    {
        var deadline = timeProvider.GetUtcNow() + budget;
        bool OutOfBudget() => timeProvider.GetUtcNow() >= deadline;

        // 1. Mark every active session deleting, taking over any lock (live or stale).
        List<string> sessionIds;
        lock (_gate)
        {
            Purge();
            sessionIds = _sessions.Where(kv => kv.Key.User == userId).Select(kv => kv.Key.Session).ToList();
        }

        foreach (var id in sessionIds)
        {
            ct.ThrowIfCancellationRequested();
            if (OutOfBudget())
            {
                return Task.FromResult(DeleteUserDataResult.Incomplete);
            }

            lock (_gate)
            {
                Purge();
                if (Find(userId, id) is { Deleting: false } state)
                {
                    var now = timeProvider.GetUtcNow();
                    state.Deleting = true;
                    state.ActiveTurnId = TurnRules.NewId();
                    state.ActiveTurnStartedAt = now;
                    Touch(state, now);
                }
            }

            OnDeleteAllProgress?.Invoke();
        }

        // 2. Every message of the user (orphans included), in chunks.
        List<string> messageIds;
        lock (_gate)
        {
            messageIds = _sessions.Where(kv => kv.Key.User == userId).SelectMany(kv => kv.Value.Messages)
                .Concat(_orphans.Where(o => o.User == userId).Select(o => o.Message))
                .Select(m => m.Record.Id)
                .ToList();
        }

        foreach (var chunk in messageIds.Chunk(DeleteChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            if (OutOfBudget())
            {
                return Task.FromResult(DeleteUserDataResult.Incomplete);
            }

            var ids = chunk.ToHashSet(StringComparer.Ordinal);
            lock (_gate)
            {
                foreach (var kv in _sessions.Where(kv => kv.Key.User == userId))
                {
                    kv.Value.Messages.RemoveAll(m => ids.Contains(m.Record.Id));
                }

                _orphans.RemoveAll(o => o.User == userId && ids.Contains(o.Message.Record.Id));
            }

            OnDeleteAllProgress?.Invoke();
        }

        // 3. The session documents listed in step 1.
        lock (_gate)
        {
            foreach (var id in sessionIds)
            {
                _sessions.Remove((userId, id));
            }

            // 3a. A guest's identity ends with its data: the guest document goes last (data-model 1.7).
            if (GuestOptions.IsGuestId(userId))
            {
                _guests.Remove(userId);
            }
        }

        return Task.FromResult(DeleteUserDataResult.Completed);
    }

    public Task<StartTurnResult> TryStartTurnAsync(
        string userId, string sessionId, MessageRecord userMessage, bool enforceGuestQuota, CancellationToken ct)
    {
        TurnRules.EnsureUserMessage(sessionId, userMessage);
        lock (_gate)
        {
            Purge();
            if (Find(userId, sessionId) is not { Deleting: false } state)
            {
                return Task.FromResult<StartTurnResult>(new StartTurnResult.NotFound());
            }

            var now = timeProvider.GetUtcNow();
            if (state.ActiveTurnId is not null && !TurnRules.IsStale(state.ActiveTurnStartedAt, now, _options.TurnTimeout))
            {
                return Task.FromResult<StartTurnResult>(new StartTurnResult.Busy());
            }

            if (enforceGuestQuota)
            {
                if (!_guests.TryGetValue(userId, out var guest))
                {
                    return Task.FromResult<StartTurnResult>(new StartTurnResult.GuestExpired());
                }

                if (guest.TurnCount >= guest.MaxTurns)
                {
                    return Task.FromResult<StartTurnResult>(new StartTurnResult.GuestQuotaExceeded());
                }

                guest.TurnCount++;
                TouchGuest(guest, now);
            }

            var lease = new TurnLease(sessionId, TurnRules.NewId(), TurnRules.NewId(), now);
            state.ActiveTurnId = lease.LeaseId;
            state.ActiveTurnStartedAt = now;
            state.UpdatedAt = now;
            state.MessageCount++;
            state.Title ??= TurnRules.AutoTitle(userMessage.Content);
            state.Messages.Add(NewStored(userId, userMessage));
            Touch(state, now);
            return Task.FromResult<StartTurnResult>(new StartTurnResult.Started(lease));
        }
    }

    public Task<CompleteTurnResult> CompleteTurnAsync(string userId, TurnLease lease, MessageRecord? assistantMessage, CancellationToken ct)
    {
        lock (_gate)
        {
            Purge();
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

                state.Messages.Add(NewStored(userId, assistantMessage));
                state.MessageCount++;
            }

            var now = timeProvider.GetUtcNow();
            state.ActiveTurnId = null;
            state.UpdatedAt = now;
            Touch(state, now);
            return Task.FromResult(CompleteTurnResult.Completed);
        }
    }

    public Task<(IReadOnlyList<MessageRecord> Items, bool HasMore)> GetRecentAsync(string userId, string sessionId, int max, CancellationToken ct)
    {
        lock (_gate)
        {
            Purge();
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
            Purge();
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
        state.Messages.Select((m, i) => (m, i)).OrderBy(x => x.m.Record.CreatedAt).ThenBy(x => x.i).Select(x => x.m.Record).ToList();

    private Stored NewStored(string userId, MessageRecord record) => new(record, _retention.TtlSeconds(userId), timeProvider.GetUtcNow());

    /// <summary>Every session write refreshes ttl and the write time (Cosmos counts ttl from the last write).</summary>
    private void Touch(State state, DateTimeOffset now)
    {
        state.Ttl = _retention.TtlSeconds(state.UserId);
        state.LastWrite = now;
    }

    private void TouchGuest(GuestState guest, DateTimeOffset now) => guest.LastWrite = now;

    /// <summary>Removes expired documents (Cosmos hides them immediately). Call under the lock.</summary>
    private void Purge()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var key in _sessions.Keys.ToList())
        {
            var state = _sessions[key];
            state.Messages.RemoveAll(m => m.Expired(now));
            if (state.Ttl is { } ttl && now >= state.LastWrite + TimeSpan.FromSeconds(ttl))
            {
                _orphans.AddRange(state.Messages.Select(m => (key.User, m)));
                _sessions.Remove(key);
            }
        }

        _orphans.RemoveAll(o => o.Message.Expired(now));

        foreach (var (id, guest) in _guests.ToList())
        {
            if (now >= guest.LastWrite + TimeSpan.FromSeconds(_retention.GuestTtlSeconds))
            {
                _guests.Remove(id);
            }
        }

        foreach (var (id, counter) in _counterWrites.ToList())
        {
            if (now >= counter + TimeSpan.FromSeconds(172800))
            {
                _counterWrites.Remove(id);
                _counters.Remove(id);
            }
        }
    }

    private sealed record Stored(MessageRecord Record, int? Ttl, DateTimeOffset WrittenAt)
    {
        public bool Expired(DateTimeOffset now) => Ttl is { } ttl && now >= WrittenAt + TimeSpan.FromSeconds(ttl);
    }

    private sealed class GuestState
    {
        public required string UserId { get; init; }

        public DateTimeOffset CreatedAt { get; init; }

        public DateTimeOffset ExpiresAt { get; init; }

        public int TurnCount { get; set; }

        public int MaxTurns { get; init; }

        public int SessionCount { get; set; }

        public int MaxSessions { get; init; }

        public DateTimeOffset LastWrite { get; set; }

        public GuestRecord ToRecord() => new(UserId, CreatedAt, ExpiresAt, TurnCount, MaxTurns, SessionCount, MaxSessions);
    }

    private sealed class State
    {
        public required string Id { get; init; }

        public required string UserId { get; init; }

        public required string WorkflowId { get; init; }

        public string? Title { get; set; }

        public DateTimeOffset CreatedAt { get; init; }

        public DateTimeOffset UpdatedAt { get; set; }

        public int MessageCount { get; set; }

        public bool Deleting { get; set; }

        public string? ActiveTurnId { get; set; }

        public DateTimeOffset ActiveTurnStartedAt { get; set; }

        public int? Ttl { get; set; }

        public DateTimeOffset LastWrite { get; set; }

        public List<Stored> Messages { get; } = [];

        public SessionRecord ToRecord() => new(Id, WorkflowId, Title, CreatedAt, UpdatedAt, MessageCount);
    }
}
