using Microsoft.Extensions.Time.Testing;
using SupportChat.Core.Storage;
using SupportChat.Infrastructure.Storage.InMemory;

namespace SupportChat.Infrastructure.Tests.Storage;

public class InMemoryChatStoreTests : StoreContract
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
    private readonly InMemoryChatStore _store;

    public InMemoryChatStoreTests() => _store = new InMemoryChatStore(_time);

    protected override ISessionStore Sessions => _store;

    protected override IMessageStore Messages => _store;

    protected override DateTimeOffset Now => _time.GetUtcNow();

    protected override void Advance(TimeSpan by) => _time.Advance(by);

    protected override Task SeedOrphanAsync(string user, MessageRecord message)
    {
        _store.SeedOrphanMessage(user, message);
        return Task.CompletedTask;
    }

    protected override Task<int> CountDocumentsAsync(string user) => Task.FromResult(_store.DocumentCount(user));

    protected override void SetAutoAdvance(TimeSpan by) => _time.AutoAdvanceAmount = by;

    protected override async Task MarkDeletingAsync(string user, string sessionId)
    {
        _store.AfterDeleteLocked = () => throw new InvalidOperationException("simulated crash");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.DeleteAsync(user, sessionId, default));
        _store.AfterDeleteLocked = null;
    }

    [Fact]
    public async Task A_crash_after_the_lock_leaves_the_session_invisible_and_a_repeated_delete_resumes()
    {
        var s = await _store.CreateAsync(UserA, "wf", "t", default);
        _store.AfterDeleteLocked = () => throw new InvalidOperationException("simulated crash");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.DeleteAsync(UserA, s.Id, default));
        _store.AfterDeleteLocked = null;

        Assert.Null(await _store.GetAsync(UserA, s.Id, default));
        Assert.Empty((await _store.ListAsync(UserA, 10, null, default)).Items);
        Assert.IsType<StartTurnResult.NotFound>(
            await _store.TryStartTurnAsync(UserA, s.Id, Msg.User(s.Id, "x", Now), default));

        Assert.Equal(DeleteSessionResult.Deleted, await _store.DeleteAsync(UserA, s.Id, default));
        Assert.Equal(DeleteSessionResult.NotFound, await _store.DeleteAsync(UserA, s.Id, default));
    }

    [Fact]
    public async Task A_turn_whose_session_is_being_deleted_cannot_write_afterwards()
    {
        var s = await _store.CreateAsync(UserA, "wf", "t", default);
        var lease = await StartAsync(UserA, s.Id);
        Advance(TimeSpan.FromMinutes(5)); // lock becomes stale
        _store.AfterDeleteLocked = () => throw new InvalidOperationException("crash");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.DeleteAsync(UserA, s.Id, default));

        Assert.Equal(CompleteTurnResult.LeaseLost,
            await _store.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, "late", Now), default));
    }

    [Fact]
    public async Task DeleteAll_stopped_after_the_mark_leaves_the_session_invisible_with_a_taken_over_lock()
    {
        var s = await _store.CreateAsync(UserA, "wf", "t", default);
        var lease = await StartAsync(UserA, s.Id);
        _store.OnDeleteAllProgress = () => _time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(DeleteUserDataResult.Incomplete, await _store.DeleteAllUserDataAsync(UserA, TimeSpan.FromSeconds(10), default));
        _store.OnDeleteAllProgress = null;

        Assert.True(_store.IsDeleting(UserA, s.Id));
        Assert.Null(await _store.GetAsync(UserA, s.Id, default));
        Assert.Equal(CompleteTurnResult.LeaseLost,
            await _store.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, "late", Now), default));
        Assert.Equal(2, _store.DocumentCount(UserA)); // session + user message, no assistant message

        Assert.Equal(DeleteUserDataResult.Completed, await _store.DeleteAllUserDataAsync(UserA, TimeSpan.FromMinutes(1), default));
        Assert.Equal(0, _store.DocumentCount(UserA));
    }

    [Fact]
    public async Task DeleteAll_deletes_more_than_one_chunk_of_messages()
    {
        var s = await _store.CreateAsync(UserA, "wf", "t", default);
        for (var i = 0; i < 130; i++)
        {
            _store.SeedOrphanMessage(UserA, Msg.User(s.Id, $"m{i}", Now));
        }

        var chunks = 0;
        _store.OnDeleteAllProgress = () => chunks++;
        Assert.Equal(DeleteUserDataResult.Completed, await _store.DeleteAllUserDataAsync(UserA, TimeSpan.FromMinutes(1), default));
        Assert.Equal(1 + 2, chunks); // one session mark + two message chunks
        Assert.Equal(0, _store.DocumentCount(UserA));
    }

    // ---- ttl (data-model 1.3 R1-R6) ----

    private static (InMemoryChatStore Store, FakeTimeProvider Time) WithRetention(int? days)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));
        return (new InMemoryChatStore(time, new SupportChat.Core.Options.ChatLimitsOptions { RetentionDays = days }), time);
    }

    [Fact]
    public async Task Ttl_is_retention_days_times_86400_on_every_session_and_message_write()
    {
        var (store, time) = WithRetention(30);
        const int expected = 30 * 86400;
        var s = await store.CreateAsync(UserA, "wf", "t", default);
        Assert.Equal(expected, store.SessionTtl(UserA, s.Id));

        var lease = Assert.IsType<StartTurnResult.Started>(
            await store.TryStartTurnAsync(UserA, s.Id, Msg.User(s.Id, "q", time.GetUtcNow()), default)).Lease;
        Assert.Equal(expected, store.SessionTtl(UserA, s.Id));
        Assert.Equal([expected], store.MessageTtls(UserA, s.Id));

        await store.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, "a", time.GetUtcNow()), default);
        Assert.Equal(expected, store.SessionTtl(UserA, s.Id));
        Assert.Equal([expected, expected], store.MessageTtls(UserA, s.Id));

        var lease2 = Assert.IsType<StartTurnResult.Started>(
            await store.TryStartTurnAsync(UserA, s.Id, Msg.User(s.Id, "q2", time.GetUtcNow()), default)).Lease;
        await store.CompleteTurnAsync(UserA, lease2, null, default); // lock-only release
        Assert.Equal(expected, store.SessionTtl(UserA, s.Id));

        // Delete marks (single session and delete-my-data) also write ttl.
        var d1 = await store.CreateAsync(UserA, "wf", "d1", default);
        store.AfterDeleteLocked = () => throw new InvalidOperationException("crash");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAsync(UserA, d1.Id, default));
        Assert.Equal(expected, store.SessionTtl(UserA, d1.Id));
        store.AfterDeleteLocked = null;

        int? ttlAfterMark = null;
        store.OnDeleteAllProgress = () => ttlAfterMark ??= store.SessionTtl(UserA, s.Id);
        await store.DeleteAllUserDataAsync(UserA, TimeSpan.FromMinutes(1), default);
        Assert.Equal(expected, ttlAfterMark);
    }

    [Fact]
    public async Task Ttl_is_absent_when_retention_is_unset()
    {
        var (store, time) = WithRetention(null);
        var s = await store.CreateAsync(UserA, "wf", "t", default);
        var lease = Assert.IsType<StartTurnResult.Started>(
            await store.TryStartTurnAsync(UserA, s.Id, Msg.User(s.Id, "q", time.GetUtcNow()), default)).Lease;
        await store.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, "a", time.GetUtcNow()), default);

        Assert.Null(store.SessionTtl(UserA, s.Id));
        Assert.All(store.MessageTtls(UserA, s.Id), Assert.Null);
        time.Advance(TimeSpan.FromDays(4000));
        Assert.NotNull(await store.GetAsync(UserA, s.Id, default));
    }

    [Fact]
    public async Task Session_ttl_is_refreshed_per_turn_messages_expire_first_and_history_drops_a_leading_assistant()
    {
        var (store, time) = WithRetention(30);
        var s = await store.CreateAsync(UserA, "wf", "t", default);
        async Task Turn(string q, string a)
        {
            var lease = Assert.IsType<StartTurnResult.Started>(
                await store.TryStartTurnAsync(UserA, s.Id, Msg.User(s.Id, q, time.GetUtcNow()), default)).Lease;
            time.Advance(TimeSpan.FromSeconds(5));
            await store.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, a, time.GetUtcNow()), default);
        }

        await Turn("q0", "a0");
        time.Advance(TimeSpan.FromDays(20));
        await Turn("q1", "a1");

        // 31 days after turn 0 (just past its messages' ttl by seconds), 11 days after turn 1: session lives on.
        time.Advance(TimeSpan.FromDays(10) + TimeSpan.FromSeconds(1));
        Assert.NotNull(await store.GetAsync(UserA, s.Id, default));
        var (items, _) = await store.GetRecentAsync(UserA, s.Id, 100, default);
        Assert.Equal(["q1", "a1"], items.Select(m => m.Content));

        // q1 expires 5 s before a1 (they were written 5 s apart): a leading assistant message remains.
        time.Advance(TimeSpan.FromDays(20) - TimeSpan.FromSeconds(4));
        (items, _) = await store.GetRecentAsync(UserA, s.Id, 100, default);
        Assert.Equal(["a1"], items.Select(m => m.Content));
        Assert.Empty(await store.GetHistoryAsync(UserA, s.Id, "none", 20, default));

        // The session expires 30 days after its last write.
        time.Advance(TimeSpan.FromDays(1));
        Assert.Null(await store.GetAsync(UserA, s.Id, default));
    }
}
