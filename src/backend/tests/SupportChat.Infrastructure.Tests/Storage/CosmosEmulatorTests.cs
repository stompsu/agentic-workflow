using System.Net;
using System.Net.Http;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SupportChat.Core.Options;
using SupportChat.Core.Serialization;
using SupportChat.Core.Storage;
using SupportChat.Infrastructure.Storage;
using SupportChat.Infrastructure.Storage.Cosmos;

namespace SupportChat.Infrastructure.Tests.Storage;

/// <summary>Skipped unless the Cosmos DB emulator answers at COSMOS_EMULATOR_ENDPOINT (default https://localhost:8081/).</summary>
internal static class CosmosEmulator
{
    public static readonly string Endpoint = Environment.GetEnvironmentVariable("COSMOS_EMULATOR_ENDPOINT") ?? "https://localhost:8081/";

    // The documented, public, well-known emulator key (not a secret); override with COSMOS_EMULATOR_KEY.
    public static readonly string Key = Environment.GetEnvironmentVariable("COSMOS_EMULATOR_KEY")
        ?? "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    public static readonly Lazy<bool> Available = new(Probe);

    private static bool Probe()
    {
        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1.5) };
            using var response = http.GetAsync(new Uri(Endpoint)).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class EmulatorFactAttribute : FactAttribute
{
    public EmulatorFactAttribute()
    {
        if (!CosmosEmulator.Available.Value)
        {
            Skip = "Cosmos DB emulator not reachable (set COSMOS_EMULATOR_ENDPOINT / start the emulator).";
        }
    }
}

public sealed class CosmosEmulatorFixture : IAsyncLifetime
{
    private CosmosClient? _client;
    private Database? _database;

    public Container? Container { get; private set; }

    public Task InitializeAsync() => InitializeCoreAsync();

    public Task DisposeAsync() => DisposeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        if (!CosmosEmulator.Available.Value)
        {
            return;
        }

        _client = new CosmosClient(CosmosEmulator.Endpoint, CosmosEmulator.Key, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            UseSystemTextJsonSerializerWithOptions = JsonDefaults.Create(),
            HttpClientFactory = () => new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }),
        });
        _database = (await _client.CreateDatabaseIfNotExistsAsync($"supportchat-tests-{Guid.NewGuid():N}")).Database;

        var properties = new ContainerProperties("chat", "/userId") { DefaultTimeToLive = -1 };
        properties.IndexingPolicy.Automatic = true;
        properties.IndexingPolicy.IndexingMode = IndexingMode.Consistent;
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/*" });
        foreach (var path in new[] { "/userId/?", "/type/?", "/sessionId/?", "/state/?", "/status/?", "/createdAt/?", "/updatedAt/?" })
        {
            properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = path });
        }

        properties.IndexingPolicy.CompositeIndexes.Add(
        [
            new CompositePath { Path = "/type", Order = CompositePathSortOrder.Ascending },
            new CompositePath { Path = "/state", Order = CompositePathSortOrder.Ascending },
            new CompositePath { Path = "/updatedAt", Order = CompositePathSortOrder.Descending },
        ]);
        properties.IndexingPolicy.CompositeIndexes.Add(
        [
            new CompositePath { Path = "/type", Order = CompositePathSortOrder.Ascending },
            new CompositePath { Path = "/sessionId", Order = CompositePathSortOrder.Ascending },
            new CompositePath { Path = "/createdAt", Order = CompositePathSortOrder.Descending },
        ]);
        Container = (await _database.CreateContainerIfNotExistsAsync(properties)).Container;
    }

    private async Task DisposeCoreAsync()
    {
        if (_database is not null)
        {
            await _database.DeleteAsync();
        }

        _client?.Dispose();
    }
}

/// <summary>
/// Runs the shared store contract (isolation, locking, delete, history) against the real Cosmos stores.
/// Each test is skipped when the emulator is not reachable.
/// </summary>
public class CosmosStoreEmulatorTests : IClassFixture<CosmosEmulatorFixture>
{
    private readonly CosmosEmulatorFixture _fixture;

    public CosmosStoreEmulatorTests(CosmosEmulatorFixture fixture) => _fixture = fixture;

    private sealed class Contract : StoreContract
    {
        private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

        private readonly Container _container;

        public Contract(Container container)
        {
            _container = container;
            var chat = new CosmosChatContainer(container);
            Sessions = new CosmosSessionStore(chat, Options.Create(new ChatLimitsOptions()), _time, new RetentionPolicy(null, TimeSpan.FromMinutes(255)));
            Messages = new CosmosMessageStore(chat);
            Guests = new CosmosGuestStore(chat, new RetentionPolicy(null, TimeSpan.FromMinutes(255)));
        }

        protected override IGuestStore Guests { get; }

        protected override async Task RemoveGuestDocumentAsync(string guestUserId) =>
            await _container.DeleteItemAsync<GuestDoc>(GuestDoc.DocumentId, new PartitionKey(guestUserId));

        protected override ISessionStore Sessions { get; }

        protected override IMessageStore Messages { get; }

        protected override DateTimeOffset Now => _time.GetUtcNow();

        protected override void Advance(TimeSpan by) => _time.Advance(by);

        protected override async Task SeedOrphanAsync(string user, MessageRecord message) =>
            await _container.CreateItemAsync(MessageDoc.From(user, message, null), new PartitionKey(user));

        protected override async Task<int> CountDocumentsAsync(string user)
        {
            var query = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.userId = @userId").WithParameter("@userId", user);
            using var iterator = _container.GetItemQueryIterator<int>(
                query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(user) });
            var total = 0;
            while (iterator.HasMoreResults)
            {
                total += (await iterator.ReadNextAsync()).Sum();
            }

            return total;
        }

        protected override void SetAutoAdvance(TimeSpan by) => _time.AutoAdvanceAmount = by;

        public Task HistoryLeadingAsync() => History_drops_a_leading_assistant_message();

        public Task DeleteAllEmptyAsync() => DeleteAll_for_a_user_without_data_completes_and_can_be_repeated();

        public Task DeleteAllScopedAsync() => DeleteAll_removes_every_session_message_and_orphan_of_the_caller_and_nothing_of_others();

        public Task DeleteAllTakeoverAsync() => DeleteAll_takes_over_a_live_lock_and_the_old_lease_can_no_longer_write();

        public Task DeleteAllBudgetAsync() => DeleteAll_with_an_exhausted_budget_is_Incomplete_and_a_later_call_resumes_to_Completed();

        public Task DeleteAllResumeAsync() => DeleteAll_resumes_a_session_already_in_deleting_and_marked_sessions_stay_invisible();

        public Task DeleteAllCancelAsync() => DeleteAll_with_a_cancelled_token_throws_OperationCanceled();

        protected override async Task MarkDeletingAsync(string user, string sessionId)
        {
            var pk = new PartitionKey(user);
            var doc = (await _container.ReadItemAsync<SessionDoc>(sessionId, pk)).Resource;
            doc.State = DocumentTypes.Deleting;
            await _container.ReplaceItemAsync(doc, sessionId, pk);
        }

        public Task DeletingMessagesAsync() => Messages_of_a_deleting_session_are_not_readable();

        public Task UnknownMessagesAsync() => Messages_of_an_unknown_session_are_empty();

        public Task ForeignMessagesAsync() => Messages_of_a_foreign_session_are_empty();

        public Task Run(Func<Contract, Task> test) => test(this);

        public Task IsolationAsync() => User_B_cannot_read_list_delete_or_lock_user_A_data();

        public Task LifecycleAsync() => Turn_lifecycle_sets_title_persists_messages_and_releases_the_lock();

        public Task StaleAsync() => Stale_lock_is_taken_over_and_the_old_lease_is_lost_without_writes();

        public Task DeleteAsync() => Delete_removes_session_and_messages_and_then_reports_NotFound();

        public Task DeleteBusyAsync() => Delete_during_an_active_turn_is_Busy();

        public Task HistoryAsync() => History_excludes_current_message_orphans_failed_and_cancelled_and_strips_markers();

        public Task PagingAsync() => List_returns_only_the_users_sessions_newest_activity_first_with_paging();
    }

    [EmulatorFact]
    public Task User_B_cannot_read_list_delete_or_lock_user_A_data() => new Contract(_fixture.Container!).IsolationAsync();

    [EmulatorFact]
    public Task Turn_lifecycle() => new Contract(_fixture.Container!).LifecycleAsync();

    [EmulatorFact]
    public Task Stale_lock_takeover_and_lease_loss() => new Contract(_fixture.Container!).StaleAsync();

    [EmulatorFact]
    public Task Delete_removes_everything() => new Contract(_fixture.Container!).DeleteAsync();

    [EmulatorFact]
    public Task Delete_during_active_turn_is_busy() => new Contract(_fixture.Container!).DeleteBusyAsync();

    [EmulatorFact]
    public Task History_rules() => new Contract(_fixture.Container!).HistoryAsync();

    [EmulatorFact]
    public Task List_paging_scoped_to_user() => new Contract(_fixture.Container!).PagingAsync();

    [EmulatorFact]
    public Task Messages_of_deleting_session_are_empty() => new Contract(_fixture.Container!).DeletingMessagesAsync();

    [EmulatorFact]
    public Task Messages_of_unknown_session_are_empty() => new Contract(_fixture.Container!).UnknownMessagesAsync();

    [EmulatorFact]
    public Task History_drops_a_leading_assistant() => new Contract(_fixture.Container!).HistoryLeadingAsync();

    [EmulatorFact]
    public Task DeleteAll_without_data() => new Contract(_fixture.Container!).DeleteAllEmptyAsync();

    [EmulatorFact]
    public Task DeleteAll_is_scoped_to_the_caller_and_removes_orphans() => new Contract(_fixture.Container!).DeleteAllScopedAsync();

    [EmulatorFact]
    public Task DeleteAll_takes_over_live_lock() => new Contract(_fixture.Container!).DeleteAllTakeoverAsync();

    [EmulatorFact]
    public Task DeleteAll_budget_then_resume() => new Contract(_fixture.Container!).DeleteAllBudgetAsync();

    [EmulatorFact]
    public Task DeleteAll_resumes_deleting_sessions() => new Contract(_fixture.Container!).DeleteAllResumeAsync();

    [EmulatorFact]
    public Task DeleteAll_honours_cancellation() => new Contract(_fixture.Container!).DeleteAllCancelAsync();

    [EmulatorFact]
    public async Task Ttl_is_written_on_every_write_path_and_omitted_when_unset()
    {
        foreach (int? days in new int?[] { 30, null })
        {
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var chat = new CosmosChatContainer(_fixture.Container!);
            var store = new CosmosSessionStore(chat, Options.Create(new ChatLimitsOptions { RetentionDays = days }), time, new RetentionPolicy(days, TimeSpan.FromMinutes(255)));
            var user = Guid.NewGuid().ToString();
            var pk = new PartitionKey(user);
            var expected = days is null ? null : (int?)(days * 86400);

            async Task<System.Text.Json.Nodes.JsonObject> RawAsync(string id)
            {
                using var stream = (await _fixture.Container!.ReadItemStreamAsync(id, pk)).Content;
                return System.Text.Json.Nodes.JsonNode.Parse(stream)!.AsObject();
            }

            void AssertTtl(System.Text.Json.Nodes.JsonObject doc)
            {
                if (expected is null)
                {
                    Assert.False(doc.ContainsKey("ttl"));
                }
                else
                {
                    Assert.Equal(expected, doc["ttl"]!.GetValue<int>());
                }
            }

            var s = await store.CreateAsync(user, "wf", "t", default);
            AssertTtl(await RawAsync(s.Id));
            var userMsg = Msg.User(s.Id, "q", time.GetUtcNow());
            var lease = Assert.IsType<StartTurnResult.Started>(await store.TryStartTurnAsync(user, s.Id, userMsg, default)).Lease;
            AssertTtl(await RawAsync(s.Id));
            AssertTtl(await RawAsync(userMsg.Id));
            var assistant = Msg.Assistant(s.Id, "a", time.GetUtcNow());
            await store.CompleteTurnAsync(user, lease, assistant, default);
            AssertTtl(await RawAsync(s.Id));
            AssertTtl(await RawAsync(assistant.Id));

            var d = await store.CreateAsync(user, "wf", "d", default);
            Assert.Equal(DeleteSessionResult.Deleted, await store.DeleteAsync(user, d.Id, default));
            Assert.Equal(DeleteUserDataResult.Completed, await store.DeleteAllUserDataAsync(user, TimeSpan.FromMinutes(1), default));
        }
    }

    [EmulatorFact]
    public async Task Container_has_ttl_enabled_with_default_minus_one()
    {
        var properties = (await _fixture.Container!.ReadContainerAsync()).Resource;
        Assert.Equal(-1, properties.DefaultTimeToLive);
    }

    [EmulatorFact]
    public Task Guest_document_roundtrip() => new Contract(_fixture.Container!).Guest_document_roundtrips_and_an_unknown_guest_is_null();

    [EmulatorFact]
    public Task Guest_session_quota() => new Contract(_fixture.Container!).Guest_session_quota_stops_at_max_sessions_and_persists_nothing_beyond_it();

    [EmulatorFact]
    public Task Guest_turn_quota() => new Contract(_fixture.Container!).Guest_turn_quota_stops_at_max_turns_and_persists_nothing_beyond_it();

    [EmulatorFact]
    public Task Guest_without_document_is_expired() => new Contract(_fixture.Container!).Guest_without_a_document_is_GuestExpired_and_nothing_is_written();

    [EmulatorFact]
    public Task Guest_quota_not_consumed_by_busy_or_not_found() => new Contract(_fixture.Container!).Guest_turn_quota_is_not_consumed_by_Busy_or_NotFound();

    [EmulatorFact]
    public Task Social_user_is_not_quota_limited() => new Contract(_fixture.Container!).A_social_user_is_never_subject_to_a_guest_quota_when_enforcement_is_off();

    [EmulatorFact]
    public Task Guest_A_guest_B_social_isolation() => new Contract(_fixture.Container!).Guest_A_guest_B_and_a_social_user_cannot_see_or_touch_each_other();

    [EmulatorFact]
    public Task Guest_delete_all_removes_guest_document_last() => new Contract(_fixture.Container!).DeleteAll_for_a_guest_removes_the_guest_document_last_and_can_be_repeated();

    [EmulatorFact]
    public Task Guest_delete_all_incomplete_keeps_guest_document() => new Contract(_fixture.Container!).DeleteAll_with_an_exhausted_budget_keeps_the_guest_document();

    [EmulatorFact]
    public Task Counters_are_atomic() => new Contract(_fixture.Container!).Counters_increment_atomically_per_window_and_start_at_one();

    /// <summary>
    /// ADR 0006 V3 (blocking): a transactional batch with PatchItem + FilterPredicate + Increment next to a create and a
    /// replace. Failed predicate -> that operation 412, the others 424, nothing written; missing document -> 404 on the
    /// patch operation; property-to-property predicate; success writes everything and refreshes ttl.
    /// </summary>
    [EmulatorFact]
    public async Task V3_batch_patch_with_filter_predicate_reports_per_operation_results_and_writes_nothing_on_failure()
    {
        var container = _fixture.Container!;
        var guest = "guest-" + Guid.NewGuid();
        var pk = new PartitionKey(guest);
        var retention = new RetentionPolicy(null, TimeSpan.FromMinutes(255));
        await new CosmosGuestStore(new CosmosChatContainer(container), retention)
            .CreateAsync(new GuestRecord(guest, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(4), 0, 1, 0, 1), default);

        SessionDoc NewSession() => new() { Id = Guid.NewGuid().ToString(), UserId = guest, SessionId = Guid.NewGuid().ToString(), WorkflowId = "wf" };

        async Task<TransactionalBatchResponse> RunAsync(SessionDoc session, string patchId = GuestDoc.DocumentId) =>
            await container.CreateTransactionalBatch(pk)
                .CreateItem(session)
                .PatchItem(patchId, GuestQuota.PatchOperations("/turnCount", 600), GuestQuota.Predicate("turnCount", "maxTurns"))
                .ExecuteAsync();

        async Task<bool> ExistsAsync(string id)
        {
            using var response = await container.ReadItemStreamAsync(id, pk);
            return response.StatusCode == HttpStatusCode.OK;
        }

        // 1. Predicate true (0 < 1): both applied; counter incremented, ttl refreshed.
        var first = NewSession();
        using (var ok = await RunAsync(first))
        {
            Assert.True(ok.IsSuccessStatusCode);
            Assert.All(Enumerable.Range(0, ok.Count), i => Assert.Equal(HttpStatusCode.OK, ok[i].StatusCode is HttpStatusCode.Created ? HttpStatusCode.OK : ok[i].StatusCode));
        }

        Assert.True(await ExistsAsync(first.Id));
        var afterFirst = (await container.ReadItemAsync<GuestDoc>(GuestDoc.DocumentId, pk)).Resource;
        Assert.Equal(1, afterFirst.TurnCount);
        Assert.Equal(600, afterFirst.Ttl);

        // 2. Predicate false (1 < 1): patch 412, create 424, nothing written, counter unchanged.
        var second = NewSession();
        using (var failed = await RunAsync(second))
        {
            Assert.False(failed.IsSuccessStatusCode);
            Assert.Equal(2, failed.Count);
            Assert.Equal(HttpStatusCode.FailedDependency, failed[0].StatusCode);
            Assert.Equal(HttpStatusCode.PreconditionFailed, failed[1].StatusCode);
            Assert.Equal(HttpStatusCode.PreconditionFailed, failed.StatusCode);
        }

        Assert.False(await ExistsAsync(second.Id));
        Assert.Equal(1, (await container.ReadItemAsync<GuestDoc>(GuestDoc.DocumentId, pk)).Resource.TurnCount);

        // 3. Missing guest document: patch 404, create 424, nothing written.
        var otherGuest = new PartitionKey("guest-" + Guid.NewGuid());
        var third = new SessionDoc { Id = Guid.NewGuid().ToString(), UserId = otherGuest.ToString().Trim('[', ']', '"'), SessionId = "x", WorkflowId = "wf" };
        using (var missing = await container.CreateTransactionalBatch(new PartitionKey(third.UserId))
                   .CreateItem(third)
                   .PatchItem(GuestDoc.DocumentId, GuestQuota.PatchOperations("/turnCount", 600), GuestQuota.Predicate("turnCount", "maxTurns"))
                   .ExecuteAsync())
        {
            Assert.False(missing.IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.FailedDependency, missing[0].StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, missing[1].StatusCode);
        }

        using (var response = await container.ReadItemStreamAsync(third.Id, new PartitionKey(third.UserId)))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [EmulatorFact]
    public Task Messages_of_foreign_session_are_empty() => new Contract(_fixture.Container!).ForeignMessagesAsync();

    [EmulatorFact]
    public Task Concurrent_guest_turn_starts_respect_the_quota() =>
        new Contract(_fixture.Container!).Run(c => c.Concurrent_guest_turn_starts_on_separate_sessions_never_exceed_max_turns());

    [EmulatorFact]
    public Task Concurrent_guest_session_creation_respects_the_quota() =>
        new Contract(_fixture.Container!).Run(c => c.Concurrent_guest_session_creation_never_exceeds_max_sessions());

    [EmulatorFact]
    public Task Concurrent_turn_starts_on_one_session_respect_the_quota() =>
        new Contract(_fixture.Container!).Run(c => c.Concurrent_turn_starts_on_one_session_are_Busy_or_quota_and_never_exceed_max_turns());
}
