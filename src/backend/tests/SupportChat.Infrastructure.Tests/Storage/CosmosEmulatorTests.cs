using System.Net;
using System.Net.Http;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SupportChat.Core.Options;
using SupportChat.Core.Serialization;
using SupportChat.Core.Storage;
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
            Sessions = new CosmosSessionStore(chat, Options.Create(new ChatLimitsOptions()), _time);
            Messages = new CosmosMessageStore(chat);
        }

        protected override ISessionStore Sessions { get; }

        protected override IMessageStore Messages { get; }

        protected override DateTimeOffset Now => _time.GetUtcNow();

        protected override void Advance(TimeSpan by) => _time.Advance(by);

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
    public Task Messages_of_foreign_session_are_empty() => new Contract(_fixture.Container!).ForeignMessagesAsync();
}
