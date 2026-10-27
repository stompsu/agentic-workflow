using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using SupportChat.Core.Storage;
using SupportChat.Core.Workflows;

namespace SupportChat.Infrastructure.Tests.Storage;

internal static class Msg
{
    public static MessageRecord User(string sessionId, string content, DateTimeOffset at, string? id = null) =>
        new(id ?? Guid.CreateVersion7().ToString(), sessionId, ChatRole.User, content, at, MessageStatus.Complete,
            null, null, [], null, null);

    public static MessageRecord Assistant(
        string sessionId, string content, DateTimeOffset at, MessageStatus status = MessageStatus.Complete, string? id = null) =>
        new(id ?? Guid.CreateVersion7().ToString(), sessionId, ChatRole.Assistant, content, at, status,
            MessageKind.Answer, "Claims specialist", [new CitationRecord("S1", "Title", "doc-1", "https://kb.example.com/x")],
            status == MessageStatus.Failed ? "model_error" : null, new JsonObject { ["category"] = "claims" });
}

/// <summary>
/// Behavior every ISessionStore/IMessageStore implementation must satisfy. The in-memory store and the Cosmos store
/// (emulator) run the same cases, so the in-memory store used by Api.Tests is a faithful stand-in.
/// </summary>
public abstract class StoreContract
{
    // Unique per test instance so that shared containers (emulator) never see another test's data.
    protected string UserA { get; } = Guid.NewGuid().ToString();

    protected string UserB { get; } = Guid.NewGuid().ToString();

    protected abstract ISessionStore Sessions { get; }

    protected abstract IMessageStore Messages { get; }

    protected abstract IGuestStore Guests { get; }

    /// <summary>Deletes only the guest document (simulates ttl expiry of the guest document).</summary>
    protected abstract Task RemoveGuestDocumentAsync(string guestUserId);

    protected abstract DateTimeOffset Now { get; }

    protected abstract void Advance(TimeSpan by);

    /// <summary>Puts an existing session into the 'deleting' state (as a crashed DELETE would leave it).</summary>
    protected abstract Task MarkDeletingAsync(string user, string sessionId);

    /// <summary>Writes a message document without a session document (left by an interrupted delete).</summary>
    protected abstract Task SeedOrphanAsync(string user, MessageRecord message);

    /// <summary>Number of documents (sessions of any state + messages) in the user's partition.</summary>
    protected abstract Task<int> CountDocumentsAsync(string user);

    /// <summary>Makes every read of the clock advance it by this amount (used to exhaust a time budget deterministically).</summary>
    protected abstract void SetAutoAdvance(TimeSpan by);

    protected async Task<TurnLease> StartAsync(string user, string sessionId, string text = "hello")
    {
        var result = await Sessions.TryStartTurnAsync(user, sessionId, Msg.User(sessionId, text, Now), default);
        return Assert.IsType<StartTurnResult.Started>(result).Lease;
    }

    [Fact]
    public async Task User_B_cannot_read_list_delete_or_lock_user_A_data()
    {
        var a = await Sessions.CreateAsync(UserA, "wf", "A's chat", default);
        var lease = await StartAsync(UserA, a.Id, "secret question");
        await Sessions.CompleteTurnAsync(UserA, lease, Msg.Assistant(a.Id, "secret answer", Now), default);

        Assert.Null(await Sessions.GetAsync(UserB, a.Id, default));
        Assert.Empty((await Sessions.ListAsync(UserB, 50, null, default)).Items);
        Assert.Empty((await Messages.GetRecentAsync(UserB, a.Id, 100, default)).Items);
        Assert.Empty(await Messages.GetHistoryAsync(UserB, a.Id, "none", 20, default));
        Assert.IsType<StartTurnResult.NotFound>(
            await Sessions.TryStartTurnAsync(UserB, a.Id, Msg.User(a.Id, "intrude", Now), default));
        Assert.Equal(CompleteTurnResult.SessionGone, await Sessions.CompleteTurnAsync(UserB, lease, null, default));
        Assert.Equal(DeleteSessionResult.NotFound, await Sessions.DeleteAsync(UserB, a.Id, default));

        // A's data is untouched and still lockable (B did not take or break the lock).
        Assert.NotNull(await Sessions.GetAsync(UserA, a.Id, default));
        Assert.Equal(2, (await Messages.GetRecentAsync(UserA, a.Id, 100, default)).Items.Count);
        await StartAsync(UserA, a.Id, "second");
    }

    [Fact]
    public async Task Messages_of_a_deleting_session_are_not_readable()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);
        var lease = await StartAsync(UserA, s.Id, "q");
        await Sessions.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, "a", Now), default);
        Assert.Equal(2, (await Messages.GetRecentAsync(UserA, s.Id, 100, default)).Items.Count);

        await MarkDeletingAsync(UserA, s.Id);

        var (items, hasMore) = await Messages.GetRecentAsync(UserA, s.Id, 100, default);
        Assert.Empty(items);
        Assert.False(hasMore);
        Assert.Empty(await Messages.GetHistoryAsync(UserA, s.Id, "none", 20, default));
    }

    [Fact]
    public async Task Messages_of_an_unknown_session_are_empty()
    {
        var unknown = Guid.NewGuid().ToString();
        var (items, hasMore) = await Messages.GetRecentAsync(UserA, unknown, 100, default);
        Assert.Empty(items);
        Assert.False(hasMore);
        Assert.Empty(await Messages.GetHistoryAsync(UserA, unknown, "none", 20, default));
    }

    [Fact]
    public async Task Messages_of_a_foreign_session_are_empty()
    {
        var a = await Sessions.CreateAsync(UserA, "wf", "t", default);
        var lease = await StartAsync(UserA, a.Id, "q");
        await Sessions.CompleteTurnAsync(UserA, lease, null, default);

        var (items, hasMore) = await Messages.GetRecentAsync(UserB, a.Id, 100, default);
        Assert.Empty(items);
        Assert.False(hasMore);
        Assert.Empty(await Messages.GetHistoryAsync(UserB, a.Id, "none", 20, default));
    }

    [Fact]
    public async Task List_returns_only_the_users_sessions_newest_activity_first_with_paging()
    {
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await Sessions.CreateAsync(UserA, "wf", $"s{i}", default)).Id);
            Advance(TimeSpan.FromSeconds(1));
        }

        await Sessions.CreateAsync(UserB, "wf", "other", default);

        var page1 = await Sessions.ListAsync(UserA, 2, null, default);
        Assert.Equal([ids[4], ids[3]], page1.Items.Select(s => s.Id));
        Assert.NotNull(page1.ContinuationToken);

        var page2 = await Sessions.ListAsync(UserA, 2, page1.ContinuationToken, default);
        Assert.Equal([ids[2], ids[1]], page2.Items.Select(s => s.Id));

        var page3 = await Sessions.ListAsync(UserA, 2, page2.ContinuationToken, default);
        Assert.Equal([ids[0]], page3.Items.Select(s => s.Id));
        Assert.Null(page3.ContinuationToken);
    }

    [Fact]
    public async Task Invalid_continuation_token_is_rejected()
    {
        await Sessions.CreateAsync(UserA, "wf", null, default);
        await Assert.ThrowsAsync<InvalidContinuationTokenException>(() => Sessions.ListAsync(UserA, 10, "***not-base64url***", default));
        await Assert.ThrowsAsync<InvalidContinuationTokenException>(() => Sessions.ListAsync(UserA, 10, "bm9wZQ", default));
    }

    [Fact]
    public async Task Turn_lifecycle_sets_title_persists_messages_and_releases_the_lock()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", null, default);
        Assert.Null(s.Title);

        var lease = await StartAsync(UserA, s.Id, new string('x', 100));
        Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(60, (await Sessions.GetAsync(UserA, s.Id, default))!.Title!.Length);
        Assert.IsType<StartTurnResult.Busy>(
            await Sessions.TryStartTurnAsync(UserA, s.Id, Msg.User(s.Id, "again", Now), default));

        Assert.Equal(CompleteTurnResult.Completed,
            await Sessions.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, "answer [S1]", Now, id: lease.AssistantMessageId), default));

        var after = (await Sessions.GetAsync(UserA, s.Id, default))!;
        Assert.Equal(2, after.MessageCount);
        var (items, hasMore) = await Messages.GetRecentAsync(UserA, s.Id, 100, default);
        Assert.False(hasMore);
        Assert.Equal(["user", "assistant"], items.Select(m => m.Role.Value));
        Assert.Equal("answer [S1]", items[1].Content);
        Assert.Equal("claims", items[1].WorkflowMetadata!["category"]!.GetValue<string>());
        Assert.Single(items[1].Citations);

        // lock released: next turn starts, title is kept
        await StartAsync(UserA, s.Id, "next");
        Assert.Equal(60, (await Sessions.GetAsync(UserA, s.Id, default))!.Title!.Length);
    }

    [Fact]
    public async Task Complete_without_a_message_releases_the_lock_only()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);
        var lease = await StartAsync(UserA, s.Id);
        Assert.Equal(CompleteTurnResult.Completed, await Sessions.CompleteTurnAsync(UserA, lease, null, default));
        Assert.Single((await Messages.GetRecentAsync(UserA, s.Id, 100, default)).Items);
        await StartAsync(UserA, s.Id);
    }

    [Fact]
    public async Task Stale_lock_is_taken_over_and_the_old_lease_is_lost_without_writes()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);
        var oldLease = await StartAsync(UserA, s.Id, "first");

        Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(61));
        var newLease = await StartAsync(UserA, s.Id, "second");

        Assert.Equal(CompleteTurnResult.LeaseLost,
            await Sessions.CompleteTurnAsync(UserA, oldLease, Msg.Assistant(s.Id, "late", Now), default));
        var messages = (await Messages.GetRecentAsync(UserA, s.Id, 100, default)).Items;
        Assert.DoesNotContain(messages, m => m.Content == "late");

        // the new lease still holds the lock
        Assert.IsType<StartTurnResult.Busy>(await Sessions.TryStartTurnAsync(UserA, s.Id, Msg.User(s.Id, "x", Now), default));
        Assert.Equal(CompleteTurnResult.Completed, await Sessions.CompleteTurnAsync(UserA, newLease, null, default));
    }

    [Fact]
    public async Task Lock_younger_than_timeout_plus_grace_is_not_stale()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);
        await StartAsync(UserA, s.Id);
        Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(30));
        Assert.IsType<StartTurnResult.Busy>(await Sessions.TryStartTurnAsync(UserA, s.Id, Msg.User(s.Id, "x", Now), default));
        Assert.Equal(DeleteSessionResult.Busy, await Sessions.DeleteAsync(UserA, s.Id, default));
    }

    [Fact]
    public async Task Complete_for_a_missing_session_reports_SessionGone()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);
        var lease = await StartAsync(UserA, s.Id);
        Assert.Equal(DeleteSessionResult.Busy, await Sessions.DeleteAsync(UserA, s.Id, default));
        Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(DeleteSessionResult.Deleted, await Sessions.DeleteAsync(UserA, s.Id, default));
        Assert.Equal(CompleteTurnResult.SessionGone, await Sessions.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, "x", Now), default));
    }

    [Fact]
    public async Task Delete_removes_session_and_messages_and_then_reports_NotFound()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);
        var lease = await StartAsync(UserA, s.Id);
        await Sessions.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, "a", Now), default);

        Assert.Equal(DeleteSessionResult.Deleted, await Sessions.DeleteAsync(UserA, s.Id, default));
        Assert.Null(await Sessions.GetAsync(UserA, s.Id, default));
        Assert.Empty((await Messages.GetRecentAsync(UserA, s.Id, 100, default)).Items);
        Assert.Empty((await Sessions.ListAsync(UserA, 10, null, default)).Items);
        Assert.Equal(DeleteSessionResult.NotFound, await Sessions.DeleteAsync(UserA, s.Id, default));
    }

    [Fact]
    public async Task Delete_during_an_active_turn_is_Busy()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);
        var lease = await StartAsync(UserA, s.Id);
        Assert.Equal(DeleteSessionResult.Busy, await Sessions.DeleteAsync(UserA, s.Id, default));
        Assert.NotNull(await Sessions.GetAsync(UserA, s.Id, default));
        await Sessions.CompleteTurnAsync(UserA, lease, null, default);
        Assert.Equal(DeleteSessionResult.Deleted, await Sessions.DeleteAsync(UserA, s.Id, default));
    }

    [Fact]
    public async Task Recent_messages_are_ascending_with_HasMore()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);
        for (var i = 0; i < 3; i++)
        {
            var lease = await StartAsync(UserA, s.Id, $"q{i}");
            Advance(TimeSpan.FromMilliseconds(10));
            await Sessions.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, $"a{i}", Now), default);
            Advance(TimeSpan.FromMilliseconds(10));
        }

        var (items, hasMore) = await Messages.GetRecentAsync(UserA, s.Id, 4, default);
        Assert.True(hasMore);
        Assert.Equal(["q1", "a1", "q2", "a2"], items.Select(m => m.Content));

        (items, hasMore) = await Messages.GetRecentAsync(UserA, s.Id, 6, default);
        Assert.False(hasMore);
        Assert.Equal(6, items.Count);
    }

    [Fact]
    public async Task History_excludes_current_message_orphans_failed_and_cancelled_and_strips_markers()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);

        async Task Turn(string q, MessageStatus? status, string a)
        {
            var lease = await StartAsync(UserA, s.Id, q);
            Advance(TimeSpan.FromMilliseconds(10));
            await Sessions.CompleteTurnAsync(UserA, lease, status is null ? null : Msg.Assistant(s.Id, a, Now, status.Value), default);
            Advance(TimeSpan.FromMilliseconds(10));
        }

        await Turn("q1", MessageStatus.Complete, "a1 [S1] and [S12]");
        await Turn("q2", MessageStatus.Failed, "partial");
        await Turn("q3", null, "");
        await Turn("q4", MessageStatus.Cancelled, "cut");
        await Turn("q5", MessageStatus.Complete, "a5");

        var currentId = Guid.CreateVersion7().ToString();
        var lease = await StartAsync(UserA, s.Id, "q6-current");
        // The endpoint passes the id of the user message it just stored; find it.
        var all = (await Messages.GetRecentAsync(UserA, s.Id, 100, default)).Items;
        currentId = all.Last(m => m.Content == "q6-current").Id;

        var history = await Messages.GetHistoryAsync(UserA, s.Id, currentId, 20, default);

        Assert.Equal(["q1", "a1  and ", "q5", "a5"], history.Select(m => m.Content));
        Assert.All(history, m => Assert.DoesNotContain("[S", m.Content));
        await Sessions.CompleteTurnAsync(UserA, lease, null, default);
    }

    [Fact]
    public async Task History_is_limited_to_the_most_recent_messages()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);
        for (var i = 0; i < 4; i++)
        {
            var lease = await StartAsync(UserA, s.Id, $"q{i}");
            Advance(TimeSpan.FromMilliseconds(10));
            await Sessions.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, $"a{i}", Now), default);
            Advance(TimeSpan.FromMilliseconds(10));
        }

        var history = await Messages.GetHistoryAsync(UserA, s.Id, "none", 4, default);
        Assert.Equal(["q2", "a2", "q3", "a3"], history.Select(m => m.Content));
    }

    [Fact]
    public async Task History_drops_a_leading_assistant_message()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);
        for (var i = 0; i < 2; i++)
        {
            var lease = await StartAsync(UserA, s.Id, $"q{i}");
            Advance(TimeSpan.FromMilliseconds(10));
            await Sessions.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, $"a{i}", Now), default);
            Advance(TimeSpan.FromMilliseconds(10));
        }

        // The window of 3 starts with a0 (its user message q0 is outside the window): it must be dropped.
        var history = await Messages.GetHistoryAsync(UserA, s.Id, "none", 3, default);
        Assert.Equal(["q1", "a1"], history.Select(m => m.Content));
    }

    [Fact]
    public async Task DeleteAll_for_a_user_without_data_completes_and_can_be_repeated()
    {
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(DeleteUserDataResult.Completed, await Sessions.DeleteAllUserDataAsync(UserA, TimeSpan.FromMinutes(1), default));
        }
    }

    [Fact]
    public async Task DeleteAll_removes_every_session_message_and_orphan_of_the_caller_and_nothing_of_others()
    {
        var live = await Sessions.CreateAsync(UserA, "wf", "live", default);
        await StartAsync(UserA, live.Id, "in flight"); // live lock, never completed
        var done = await Sessions.CreateAsync(UserA, "wf", "done", default);
        var lease = await StartAsync(UserA, done.Id, "q");
        await Sessions.CompleteTurnAsync(UserA, lease, Msg.Assistant(done.Id, "a", Now), default);
        var marked = await Sessions.CreateAsync(UserA, "wf", "marked", default);
        await MarkDeletingAsync(UserA, marked.Id);
        await SeedOrphanAsync(UserA, Msg.User(Guid.NewGuid().ToString(), "orphan", Now));

        var b = await Sessions.CreateAsync(UserB, "wf", "B's chat", default);
        var bLease = await StartAsync(UserB, b.Id, "B question");
        await Sessions.CompleteTurnAsync(UserB, bLease, Msg.Assistant(b.Id, "B answer", Now), default);
        var bBefore = await CountDocumentsAsync(UserB);
        Assert.Equal(3, bBefore);

        Assert.Equal(DeleteUserDataResult.Completed, await Sessions.DeleteAllUserDataAsync(UserA, TimeSpan.FromMinutes(1), default));

        Assert.Equal(0, await CountDocumentsAsync(UserA));
        Assert.Null(await Sessions.GetAsync(UserA, live.Id, default));
        Assert.Empty((await Sessions.ListAsync(UserA, 10, null, default)).Items);
        Assert.Equal(bBefore, await CountDocumentsAsync(UserB));
        Assert.Equal(2, (await Messages.GetRecentAsync(UserB, b.Id, 100, default)).Items.Count);

        // Idempotent.
        Assert.Equal(DeleteUserDataResult.Completed, await Sessions.DeleteAllUserDataAsync(UserA, TimeSpan.FromMinutes(1), default));
        Assert.Equal(bBefore, await CountDocumentsAsync(UserB));
    }

    [Fact]
    public async Task DeleteAll_takes_over_a_live_lock_and_the_old_lease_can_no_longer_write()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);
        var lease = await StartAsync(UserA, s.Id);

        Assert.Equal(DeleteUserDataResult.Completed, await Sessions.DeleteAllUserDataAsync(UserA, TimeSpan.FromMinutes(1), default));

        Assert.Equal(CompleteTurnResult.SessionGone,
            await Sessions.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, "late", Now), default));
        Assert.Equal(0, await CountDocumentsAsync(UserA));
    }

    [Fact]
    public async Task DeleteAll_with_an_exhausted_budget_is_Incomplete_and_a_later_call_resumes_to_Completed()
    {
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var s = await Sessions.CreateAsync(UserA, "wf", $"s{i}", default);
            ids.Add(s.Id);
            var lease = await StartAsync(UserA, s.Id, "q");
            await Sessions.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, "a", Now), default);
        }

        Assert.Equal(DeleteUserDataResult.Incomplete, await Sessions.DeleteAllUserDataAsync(UserA, TimeSpan.Zero, default));
        Assert.Equal(9, await CountDocumentsAsync(UserA)); // nothing happened with a zero budget

        // Every clock read costs one second: a 2 s budget runs out part-way (progress is kept).
        SetAutoAdvance(TimeSpan.FromSeconds(1));
        Assert.Equal(DeleteUserDataResult.Incomplete, await Sessions.DeleteAllUserDataAsync(UserA, TimeSpan.FromSeconds(2), default));
        SetAutoAdvance(TimeSpan.Zero);

        Assert.Equal(DeleteUserDataResult.Completed, await Sessions.DeleteAllUserDataAsync(UserA, TimeSpan.FromMinutes(1), default));
        Assert.Equal(0, await CountDocumentsAsync(UserA));
    }

    [Fact]
    public async Task DeleteAll_resumes_a_session_already_in_deleting_and_marked_sessions_stay_invisible()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);
        var lease = await StartAsync(UserA, s.Id);
        await Sessions.CompleteTurnAsync(UserA, lease, Msg.Assistant(s.Id, "a", Now), default);
        await MarkDeletingAsync(UserA, s.Id);

        Assert.Null(await Sessions.GetAsync(UserA, s.Id, default));
        Assert.IsType<StartTurnResult.NotFound>(await Sessions.TryStartTurnAsync(UserA, s.Id, Msg.User(s.Id, "x", Now), default));
        Assert.Equal(DeleteUserDataResult.Completed, await Sessions.DeleteAllUserDataAsync(UserA, TimeSpan.FromMinutes(1), default));
        Assert.Equal(0, await CountDocumentsAsync(UserA));
    }

    [Fact]
    public async Task DeleteAll_with_a_cancelled_token_throws_OperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Sessions.CreateAsync(UserA, "wf", "t", default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Sessions.DeleteAllUserDataAsync(UserA, TimeSpan.FromMinutes(1), cts.Token));
    }

    // ---- guest documents, quotas and counters (ADR 0006 sections 6.2, 6.4; data-model 1.8) ----

    private static string NewGuestId() => "guest-" + Guid.NewGuid();

    private async Task<string> NewGuestAsync(int maxTurns = 30, int maxSessions = 5)
    {
        var id = NewGuestId();
        await Guests.CreateAsync(new GuestRecord(id, Now, Now.AddHours(4), 0, maxTurns, 0, maxSessions), default);
        return id;
    }

    private async Task<SessionRecord> GuestSessionAsync(string guest) =>
        Assert.IsType<CreateSessionResult.Created>(await Sessions.CreateAsync(guest, "wf", "t", true, default)).Session;

    [Fact]
    public async Task Guest_document_roundtrips_and_an_unknown_guest_is_null()
    {
        var id = await NewGuestAsync(7, 3);
        var read = await Guests.GetAsync(id, default);
        Assert.NotNull(read);
        Assert.Equal((id, 0, 7, 0, 3), (read.UserId, read.TurnCount, read.MaxTurns, read.SessionCount, read.MaxSessions));
        Assert.Null(await Guests.GetAsync(NewGuestId(), default));
        Assert.Null(await Guests.GetAsync(UserA, default)); // a social user's partition has no guest document
    }

    [Fact]
    public async Task Guest_session_quota_stops_at_max_sessions_and_persists_nothing_beyond_it()
    {
        var guest = await NewGuestAsync(maxSessions: 2);
        await GuestSessionAsync(guest);
        var second = await GuestSessionAsync(guest);
        Assert.IsType<CreateSessionResult.GuestQuotaExceeded>(await Sessions.CreateAsync(guest, "wf", "t", true, default));

        Assert.Equal(2, (await Sessions.ListAsync(guest, 50, null, default)).Items.Count);
        Assert.Equal(2, (await Guests.GetAsync(guest, default))!.SessionCount);

        // Deleting a session does not give the quota back.
        Assert.Equal(DeleteSessionResult.Deleted, await Sessions.DeleteAsync(guest, second.Id, default));
        Assert.IsType<CreateSessionResult.GuestQuotaExceeded>(await Sessions.CreateAsync(guest, "wf", "t", true, default));
    }

    [Fact]
    public async Task Guest_turn_quota_stops_at_max_turns_and_persists_nothing_beyond_it()
    {
        var guest = await NewGuestAsync(maxTurns: 2);
        var s = await GuestSessionAsync(guest);
        for (var i = 0; i < 2; i++)
        {
            var started = await Sessions.TryStartTurnAsync(guest, s.Id, Msg.User(s.Id, $"q{i}", Now), true, default);
            var lease = Assert.IsType<StartTurnResult.Started>(started).Lease;
            await Sessions.CompleteTurnAsync(guest, lease, Msg.Assistant(s.Id, $"a{i}", Now), default);
        }

        var documentsBefore = await CountDocumentsAsync(guest);
        Assert.IsType<StartTurnResult.GuestQuotaExceeded>(
            await Sessions.TryStartTurnAsync(guest, s.Id, Msg.User(s.Id, "q3", Now), true, default));

        Assert.Equal(documentsBefore, await CountDocumentsAsync(guest));
        Assert.Equal(2, (await Guests.GetAsync(guest, default))!.TurnCount);
        // The lock was not taken either: a (non-quota) start still succeeds.
        Assert.IsType<StartTurnResult.Started>(await Sessions.TryStartTurnAsync(guest, s.Id, Msg.User(s.Id, "x", Now), false, default));
    }

    [Fact]
    public async Task Guest_without_a_document_is_GuestExpired_and_nothing_is_written()
    {
        var guest = await NewGuestAsync();
        var s = await GuestSessionAsync(guest);
        await RemoveGuestDocumentAsync(guest);
        var documentsBefore = await CountDocumentsAsync(guest);

        Assert.IsType<CreateSessionResult.GuestExpired>(await Sessions.CreateAsync(guest, "wf", "t", true, default));
        Assert.IsType<StartTurnResult.GuestExpired>(await Sessions.TryStartTurnAsync(guest, s.Id, Msg.User(s.Id, "q", Now), true, default));

        Assert.Equal(documentsBefore, await CountDocumentsAsync(guest));
        // No lock was taken and no message written.
        Assert.IsType<StartTurnResult.Started>(await Sessions.TryStartTurnAsync(guest, s.Id, Msg.User(s.Id, "q", Now), false, default));
    }

    [Fact]
    public async Task Guest_turn_quota_is_not_consumed_by_Busy_or_NotFound()
    {
        var guest = await NewGuestAsync(maxTurns: 5);
        var s = await GuestSessionAsync(guest);
        await StartAsync(guest, s.Id); // non-quota start only to hold the lock

        Assert.IsType<StartTurnResult.Busy>(await Sessions.TryStartTurnAsync(guest, s.Id, Msg.User(s.Id, "x", Now), true, default));
        var unknown = Guid.NewGuid().ToString();
        Assert.IsType<StartTurnResult.NotFound>(await Sessions.TryStartTurnAsync(guest, unknown, Msg.User(unknown, "x", Now), true, default));

        Assert.Equal(0, (await Guests.GetAsync(guest, default))!.TurnCount);
    }

    [Fact]
    public async Task A_social_user_is_never_subject_to_a_guest_quota_when_enforcement_is_off()
    {
        var s = await Sessions.CreateAsync(UserA, "wf", "t", default);
        Assert.IsType<StartTurnResult.Started>(await Sessions.TryStartTurnAsync(UserA, s.Id, Msg.User(s.Id, "q", Now), false, default));
        Assert.Null(await Guests.GetAsync(UserA, default));
    }

    [Fact]
    public async Task Guest_A_guest_B_and_a_social_user_cannot_see_or_touch_each_other()
    {
        var a = await NewGuestAsync();
        var b = await NewGuestAsync();
        var sa = await GuestSessionAsync(a);
        var lease = Assert.IsType<StartTurnResult.Started>(await Sessions.TryStartTurnAsync(a, sa.Id, Msg.User(sa.Id, "secret", Now), true, default)).Lease;
        await Sessions.CompleteTurnAsync(a, lease, Msg.Assistant(sa.Id, "answer", Now), default);
        var social = await Sessions.CreateAsync(UserA, "wf", "social", default);

        foreach (var other in new[] { b, UserA })
        {
            Assert.Null(await Sessions.GetAsync(other, sa.Id, default));
            Assert.Empty(await Messages.GetHistoryAsync(other, sa.Id, "none", 20, default));
            Assert.Empty((await Messages.GetRecentAsync(other, sa.Id, 100, default)).Items);
            Assert.Equal(DeleteSessionResult.NotFound, await Sessions.DeleteAsync(other, sa.Id, default));
        }

        foreach (var other in new[] { a, b })
        {
            Assert.Null(await Sessions.GetAsync(other, social.Id, default));
        }

        Assert.Null(await Sessions.GetAsync(a, social.Id, default));
        Assert.IsType<StartTurnResult.NotFound>(await Sessions.TryStartTurnAsync(b, sa.Id, Msg.User(sa.Id, "intrude", Now), true, default));
        Assert.Equal(0, (await Guests.GetAsync(b, default))!.TurnCount); // B's quota untouched by the attempt on A's session
        Assert.Equal(1, (await Guests.GetAsync(a, default))!.TurnCount);

        // Deleting B's data leaves A and the social user untouched.
        Assert.Equal(DeleteUserDataResult.Completed, await Sessions.DeleteAllUserDataAsync(b, TimeSpan.FromMinutes(1), default));
        Assert.Null(await Guests.GetAsync(b, default));
        Assert.NotNull(await Guests.GetAsync(a, default));
        Assert.NotNull(await Sessions.GetAsync(a, sa.Id, default));
        Assert.NotNull(await Sessions.GetAsync(UserA, social.Id, default));
    }

    [Fact]
    public async Task DeleteAll_for_a_guest_removes_the_guest_document_last_and_can_be_repeated()
    {
        var guest = await NewGuestAsync();
        var s = await GuestSessionAsync(guest);
        var lease = Assert.IsType<StartTurnResult.Started>(await Sessions.TryStartTurnAsync(guest, s.Id, Msg.User(s.Id, "q", Now), true, default)).Lease;
        await Sessions.CompleteTurnAsync(guest, lease, Msg.Assistant(s.Id, "a", Now), default);

        Assert.Equal(DeleteUserDataResult.Completed, await Sessions.DeleteAllUserDataAsync(guest, TimeSpan.FromMinutes(1), default));
        Assert.Equal(0, await CountDocumentsAsync(guest));
        Assert.Null(await Guests.GetAsync(guest, default));

        // Ended guest: writes fail at once, the repeated delete still completes (idempotent).
        Assert.IsType<CreateSessionResult.GuestExpired>(await Sessions.CreateAsync(guest, "wf", "t", true, default));
        Assert.Equal(DeleteUserDataResult.Completed, await Sessions.DeleteAllUserDataAsync(guest, TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task DeleteAll_with_an_exhausted_budget_keeps_the_guest_document()
    {
        var guest = await NewGuestAsync();
        await GuestSessionAsync(guest);
        SetAutoAdvance(TimeSpan.FromMinutes(5));
        Assert.Equal(DeleteUserDataResult.Incomplete, await Sessions.DeleteAllUserDataAsync(guest, TimeSpan.FromSeconds(1), default));
        SetAutoAdvance(TimeSpan.Zero);
        Assert.NotNull(await Guests.GetAsync(guest, default));
    }

    [Fact]
    public async Task Concurrent_guest_turn_starts_on_separate_sessions_never_exceed_max_turns()
    {
        const int max = 5;
        const int extra = 7;
        var guest = await NewGuestAsync(maxTurns: max, maxSessions: max + extra);
        var sessions = new List<SessionRecord>();
        for (var i = 0; i < max + extra; i++)
        {
            sessions.Add(await GuestSessionAsync(guest));
        }

        var results = await Task.WhenAll(sessions.Select(s => Task.Run(
            () => Sessions.TryStartTurnAsync(guest, s.Id, Msg.User(s.Id, "q", Now), true, default))));

        // Each turn is on its own session, so Busy cannot happen here: exactly max start, the rest are over quota.
        Assert.Equal(max, results.OfType<StartTurnResult.Started>().Count());
        Assert.Equal(extra, results.OfType<StartTurnResult.GuestQuotaExceeded>().Count());
        Assert.Equal(max, (await Guests.GetAsync(guest, default))!.TurnCount);
    }

    [Fact]
    public async Task Concurrent_guest_session_creation_never_exceeds_max_sessions()
    {
        const int max = 3;
        const int extra = 6;
        var guest = await NewGuestAsync(maxSessions: max);

        var results = await Task.WhenAll(Enumerable.Range(0, max + extra).Select(_ => Task.Run(
            () => Sessions.CreateAsync(guest, "wf", "t", true, default))));

        Assert.Equal(max, results.OfType<CreateSessionResult.Created>().Count());
        Assert.Equal(extra, results.OfType<CreateSessionResult.GuestQuotaExceeded>().Count());
        Assert.Equal(max, (await Guests.GetAsync(guest, default))!.SessionCount);
        Assert.Equal(max, (await Sessions.ListAsync(guest, 50, null, default)).Items.Count);
    }

    [Fact]
    public async Task Concurrent_turn_starts_on_one_session_are_Busy_or_quota_and_never_exceed_max_turns()
    {
        const int max = 2;
        var guest = await NewGuestAsync(maxTurns: max);
        var s = await GuestSessionAsync(guest);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            () => Sessions.TryStartTurnAsync(guest, s.Id, Msg.User(s.Id, "q", Now), true, default))));

        Assert.Single(results.OfType<StartTurnResult.Started>()); // the lock admits one
        Assert.Equal(8, results.Count(r => r is StartTurnResult.Started or StartTurnResult.Busy or StartTurnResult.GuestQuotaExceeded));
        var turns = (await Guests.GetAsync(guest, default))!.TurnCount;
        Assert.Equal(1, turns); // Busy and quota rejections consume nothing
    }

    [Fact]
    public async Task Counters_increment_atomically_per_window_and_start_at_one()
    {
        var hour = new DateTimeOffset(2030, 1, 2, 3, 15, 0, TimeSpan.Zero) + TimeSpan.FromDays(Random.Shared.Next(1, 3000));
        Assert.Equal(1, await Guests.IncrementCounterAsync(GuestCounter.TurnsPerHour, hour, default));
        Assert.Equal(2, await Guests.IncrementCounterAsync(GuestCounter.TurnsPerHour, hour.AddMinutes(30), default));
        Assert.Equal(1, await Guests.IncrementCounterAsync(GuestCounter.TurnsPerHour, hour.AddHours(1), default)); // next hour
        Assert.Equal(1, await Guests.IncrementCounterAsync(GuestCounter.TurnsPerDay, hour, default)); // other counter
        Assert.Equal(1, await Guests.IncrementCounterAsync(GuestCounter.CreationsPerHour, hour, default));

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Guests.IncrementCounterAsync(GuestCounter.TurnsPerDay, hour, default)));
        Assert.Equal(Enumerable.Range(2, 20).Select(i => (long)i), results.OrderBy(x => x));
    }
}
