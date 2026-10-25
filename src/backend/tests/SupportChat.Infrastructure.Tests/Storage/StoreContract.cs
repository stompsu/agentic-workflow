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

    protected abstract DateTimeOffset Now { get; }

    protected abstract void Advance(TimeSpan by);

    /// <summary>Puts an existing session into the 'deleting' state (as a crashed DELETE would leave it).</summary>
    protected abstract Task MarkDeletingAsync(string user, string sessionId);

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
}
