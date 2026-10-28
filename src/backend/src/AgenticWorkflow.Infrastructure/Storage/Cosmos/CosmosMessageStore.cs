using System.Net;
using Microsoft.Azure.Cosmos;
using AgenticWorkflow.Core.Storage;

namespace AgenticWorkflow.Infrastructure.Storage.Cosmos;

/// <summary>Cosmos message reads (data-model 1.5): single-partition queries, userId in the filter and the options.</summary>
public sealed class CosmosMessageStore(CosmosChatContainer chat) : IMessageStore
{
    public async Task<(IReadOnlyList<MessageRecord> Items, bool HasMore)> GetRecentAsync(
        string userId, string sessionId, int max, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        if (!await SessionIsVisibleAsync(userId, sessionId, ct))
        {
            return (Array.Empty<MessageRecord>(), false);
        }

        var docs = await QueryNewestAsync(
            userId, sessionId, max + 1, "AND c.type = 'message' AND c.sessionId = @sessionId", excludeId: null, ct);
        var hasMore = docs.Count > max;
        var newestFirst = docs.Take(max).ToList();
        newestFirst.Reverse();
        return (newestFirst.Select(d => d.ToRecord()).ToList(), hasMore);
    }

    public async Task<IReadOnlyList<MessageRecord>> GetHistoryAsync(
        string userId, string sessionId, string excludeMessageId, int maxMessages, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);
        if (!await SessionIsVisibleAsync(userId, sessionId, ct))
        {
            return Array.Empty<MessageRecord>();
        }

        var docs = await QueryNewestAsync(
            userId, sessionId, maxMessages, "AND c.type = 'message' AND c.sessionId = @sessionId AND c.id != @excludeId", excludeMessageId, ct);
        docs.Reverse();
        return TurnRules.ApplyHistoryRules(docs.Select(d => d.ToRecord()).ToList());
    }

    /// <summary>
    /// Point read (1 RU) of the session document in the user's partition: messages are visible only for an existing,
    /// non-deleting session owned by <paramref name="userId"/>. Matches <c>ISessionStore.GetAsync</c> and the in-memory store.
    /// </summary>
    private async Task<bool> SessionIsVisibleAsync(string userId, string sessionId, CancellationToken ct)
    {
        try
        {
            var response = await chat.Container.ReadItemAsync<SessionDoc>(sessionId, new PartitionKey(userId), cancellationToken: ct);
            var doc = response.Resource;
            return doc.Type == DocumentTypes.Session && doc.UserId == userId && doc.State == DocumentTypes.Active;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<List<MessageDoc>> QueryNewestAsync(
        string userId, string sessionId, int take, string filter, string? excludeId, CancellationToken ct)
    {
        // take is an int produced by this class; it is interpolated because TOP is not reliably parameterizable.
        var query = new QueryDefinition(
                $"SELECT TOP {take} * FROM c WHERE c.userId = @userId {filter} ORDER BY c.createdAt DESC")
            .WithParameter("@userId", userId)
            .WithParameter("@sessionId", sessionId);
        if (excludeId is not null)
        {
            query = query.WithParameter("@excludeId", excludeId);
        }

        var options = new QueryRequestOptions { PartitionKey = new PartitionKey(userId), MaxItemCount = take };
        var result = new List<MessageDoc>();
        using var iterator = chat.Container.GetItemQueryIterator<MessageDoc>(query, requestOptions: options);
        while (iterator.HasMoreResults && result.Count < take)
        {
            result.AddRange(await iterator.ReadNextAsync(ct));
        }

        return result.Take(take).ToList();
    }
}
