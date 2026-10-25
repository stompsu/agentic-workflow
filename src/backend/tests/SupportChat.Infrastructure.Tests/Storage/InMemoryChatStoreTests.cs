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
}
