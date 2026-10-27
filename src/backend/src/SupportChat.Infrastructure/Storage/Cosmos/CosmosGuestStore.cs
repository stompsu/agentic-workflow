using System.Globalization;
using System.Net;
using Microsoft.Azure.Cosmos;
using SupportChat.Core.Storage;

namespace SupportChat.Infrastructure.Storage.Cosmos;

/// <summary>
/// Guest document and global counters (data-model 1.8). Guest reads and creates use PartitionKey(guestUserId); counters
/// live in the reserved partition "system-guest-counter". There is no query: everything is addressed by id.
/// </summary>
public sealed class CosmosGuestStore(CosmosChatContainer chat, RetentionPolicy retention) : IGuestStore
{
    private Container Container => chat.Container;

    public async Task CreateAsync(GuestRecord guest, CancellationToken ct) =>
        await Container.CreateItemAsync(GuestDoc.From(guest, retention.GuestTtlSeconds), new PartitionKey(guest.UserId), cancellationToken: ct);

    public async Task<GuestRecord?> GetAsync(string guestUserId, CancellationToken ct)
    {
        try
        {
            var response = await Container.ReadItemAsync<GuestDoc>(GuestDoc.DocumentId, new PartitionKey(guestUserId), cancellationToken: ct);
            return response.Resource is { Type: DocumentTypes.Guest } doc && doc.UserId == guestUserId ? doc.ToRecord() : null;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<long> IncrementCounterAsync(GuestCounter counter, DateTimeOffset now, CancellationToken ct)
    {
        var id = CounterId(counter, now);
        var pk = new PartitionKey(GuestCounterDoc.PartitionValue);
        IReadOnlyList<PatchOperation> increment = [PatchOperation.Increment("/count", 1)];

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var patched = await Container.PatchItemAsync<GuestCounterDoc>(id, pk, increment, cancellationToken: ct);
                return patched.Resource.Count;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // First use of this window: create it with count = 1.
            }

            try
            {
                var created = await Container.CreateItemAsync(new GuestCounterDoc { Id = id, Count = 1 }, pk, cancellationToken: ct);
                return created.Resource.Count;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                // Another replica created it first: patch again.
            }
        }

        throw new InvalidOperationException("Incrementing the guest counter failed after repeated create conflicts.");
    }

    /// <summary>create-h-yyyyMMddHH, turn-h-yyyyMMddHH, turn-d-yyyyMMdd (UTC windows).</summary>
    internal static string CounterId(GuestCounter counter, DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        return counter switch
        {
            GuestCounter.CreationsPerHour => "create-h-" + utc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture),
            GuestCounter.TurnsPerHour => "turn-h-" + utc.ToString("yyyyMMddHH", CultureInfo.InvariantCulture),
            GuestCounter.TurnsPerDay => "turn-d-" + utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(counter)),
        };
    }
}
