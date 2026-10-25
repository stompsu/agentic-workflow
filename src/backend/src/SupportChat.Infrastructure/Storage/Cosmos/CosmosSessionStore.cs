using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using SupportChat.Core.Options;
using SupportChat.Core.Storage;

namespace SupportChat.Infrastructure.Storage.Cosmos;

/// <summary>
/// Cosmos session store (data-model 1.5). Every operation passes PartitionKey(userId) in the request options AND
/// filters on userId; there is no query without it.
/// </summary>
public sealed class CosmosSessionStore(CosmosChatContainer chat, IOptions<ChatLimitsOptions> options, TimeProvider timeProvider) : ISessionStore
{
    private const int DeleteBatchSize = 100;
    private Container Container => chat.Container;
    private ChatLimitsOptions Options => options.Value;

    public async Task<SessionRecord> CreateAsync(string userId, string workflowId, string? title, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var id = TurnRules.NewId();
        var doc = new SessionDoc
        {
            Id = id,
            UserId = userId,
            SessionId = id,
            WorkflowId = workflowId,
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
            Ttl = TurnRules.TtlSeconds(Options.RetentionDays),
        };
        await Container.CreateItemAsync(doc, new PartitionKey(userId), cancellationToken: ct);
        return doc.ToRecord();
    }

    public async Task<SessionRecord?> GetAsync(string userId, string sessionId, CancellationToken ct)
    {
        var read = await ReadAsync(userId, sessionId, ct);
        return read is { Doc.State: DocumentTypes.Active } r ? r.Doc.ToRecord() : null;
    }

    public async Task<Page<SessionRecord>> ListAsync(string userId, int pageSize, string? continuationToken, CancellationToken ct)
    {
        var cosmosToken = continuationToken is null ? null : ContinuationTokens.Decode(continuationToken);
        var query = new QueryDefinition(
                "SELECT c.id, c.workflowId, c.title, c.createdAt, c.updatedAt, c.messageCount FROM c " +
                "WHERE c.userId = @userId AND c.type = 'session' AND c.state = 'active' ORDER BY c.updatedAt DESC")
            .WithParameter("@userId", userId);
        var requestOptions = new QueryRequestOptions { PartitionKey = new PartitionKey(userId), MaxItemCount = pageSize };

        try
        {
            using var iterator = Container.GetItemQueryIterator<SessionListItem>(query, cosmosToken, requestOptions);
            var response = await iterator.ReadNextAsync(ct);
            var items = response.Select(i => i.ToRecord()).ToList();
            var next = iterator.HasMoreResults && !string.IsNullOrEmpty(response.ContinuationToken)
                ? ContinuationTokens.Encode(response.ContinuationToken)
                : null;
            return new Page<SessionRecord>(items, next);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.BadRequest && cosmosToken is not null)
        {
            throw new InvalidContinuationTokenException(ex);
        }
        catch (ArgumentException ex) when (cosmosToken is not null)
        {
            throw new InvalidContinuationTokenException(ex);
        }
    }

    public async Task<DeleteSessionResult> DeleteAsync(string userId, string sessionId, CancellationToken ct)
    {
        var pk = new PartitionKey(userId);
        var read = await ReadAsync(userId, sessionId, ct);
        if (read is not { } current)
        {
            return DeleteSessionResult.NotFound;
        }

        var (doc, etag) = current;
        if (doc.State == DocumentTypes.Active)
        {
            var now = timeProvider.GetUtcNow();
            if (doc.ActiveTurn is { } active && !TurnRules.IsStale(active.StartedAt, now, Options.TurnTimeout))
            {
                return DeleteSessionResult.Busy;
            }

            doc.State = DocumentTypes.Deleting;
            doc.ActiveTurn = new ActiveTurnDoc { TurnId = TurnRules.NewId(), StartedAt = now };
            try
            {
                await Container.ReplaceItemAsync(doc, doc.Id, pk, new ItemRequestOptions { IfMatchEtag = etag }, ct);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                return DeleteSessionResult.Busy;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return DeleteSessionResult.NotFound;
            }
        }

        // From here the session is invisible (state = deleting). Repeating the DELETE resumes at this point.
        var idsQuery = new QueryDefinition(
                "SELECT c.id FROM c WHERE c.userId = @userId AND c.type = 'message' AND c.sessionId = @sessionId")
            .WithParameter("@userId", userId)
            .WithParameter("@sessionId", sessionId);
        var ids = new List<string>();
        using (var iterator = Container.GetItemQueryIterator<IdOnly>(
                   idsQuery, requestOptions: new QueryRequestOptions { PartitionKey = pk, MaxItemCount = 500 }))
        {
            while (iterator.HasMoreResults)
            {
                ids.AddRange((await iterator.ReadNextAsync(ct)).Select(x => x.Id));
            }
        }

        foreach (var chunk in ids.Chunk(DeleteBatchSize))
        {
            await DeleteMessagesAsync(pk, chunk, ct);
        }

        try
        {
            await Container.DeleteItemAsync<SessionDoc>(sessionId, pk, cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone (concurrent resume).
        }

        return DeleteSessionResult.Deleted;
    }

    public async Task<StartTurnResult> TryStartTurnAsync(string userId, string sessionId, MessageRecord userMessage, CancellationToken ct)
    {
        TurnRules.EnsureUserMessage(sessionId, userMessage);
        var pk = new PartitionKey(userId);
        var read = await ReadAsync(userId, sessionId, ct);
        if (read is not { Doc.State: DocumentTypes.Active } current)
        {
            return new StartTurnResult.NotFound();
        }

        var (doc, etag) = current;
        var now = timeProvider.GetUtcNow();
        if (doc.ActiveTurn is { } active && !TurnRules.IsStale(active.StartedAt, now, Options.TurnTimeout))
        {
            return new StartTurnResult.Busy();
        }

        var lease = new TurnLease(sessionId, TurnRules.NewId(), TurnRules.NewId(), now);
        doc.ActiveTurn = new ActiveTurnDoc { TurnId = lease.LeaseId, StartedAt = now };
        doc.UpdatedAt = now;
        doc.MessageCount++;
        doc.Title ??= TurnRules.AutoTitle(userMessage.Content);
        doc.Ttl = TurnRules.TtlSeconds(Options.RetentionDays);

        using var response = await Container.CreateTransactionalBatch(pk)
            .CreateItem(MessageDoc.From(userId, userMessage, TurnRules.TtlSeconds(Options.RetentionDays)))
            .ReplaceItem(doc.Id, doc, new TransactionalBatchItemRequestOptions { IfMatchEtag = etag })
            .ExecuteAsync(ct);

        if (response.IsSuccessStatusCode)
        {
            return new StartTurnResult.Started(lease);
        }

        return response.StatusCode switch
        {
            HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict => new StartTurnResult.Busy(),
            HttpStatusCode.NotFound => new StartTurnResult.NotFound(),
            _ => throw new InvalidOperationException($"Starting the turn failed with Cosmos status {(int)response.StatusCode}."),
        };
    }

    public async Task<CompleteTurnResult> CompleteTurnAsync(string userId, TurnLease lease, MessageRecord? assistantMessage, CancellationToken ct)
    {
        if (assistantMessage is not null && assistantMessage.SessionId != lease.SessionId)
        {
            throw new ArgumentException("The message does not belong to the lease's session.", nameof(assistantMessage));
        }

        var pk = new PartitionKey(userId);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var read = await ReadAsync(userId, lease.SessionId, ct);
            if (read is not { } current)
            {
                return CompleteTurnResult.SessionGone;
            }

            var (doc, etag) = current;
            if (doc.ActiveTurn?.TurnId != lease.LeaseId)
            {
                return CompleteTurnResult.LeaseLost;
            }

            doc.ActiveTurn = null;
            doc.UpdatedAt = timeProvider.GetUtcNow();
            doc.Ttl = TurnRules.TtlSeconds(Options.RetentionDays);

            var batch = Container.CreateTransactionalBatch(pk);
            if (assistantMessage is not null)
            {
                doc.MessageCount++;
                batch.CreateItem(MessageDoc.From(userId, assistantMessage, TurnRules.TtlSeconds(Options.RetentionDays)));
            }

            batch.ReplaceItem(doc.Id, doc, new TransactionalBatchItemRequestOptions { IfMatchEtag = etag });
            using var response = await batch.ExecuteAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                return CompleteTurnResult.Completed;
            }

            if (response.StatusCode is not (HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound))
            {
                throw new InvalidOperationException($"Completing the turn failed with Cosmos status {(int)response.StatusCode}.");
            }

            // ETag conflict or vanished session: re-read once and re-evaluate.
        }

        throw new InvalidOperationException("Completing the turn failed after a repeated ETag conflict.");
    }

    private async Task<(SessionDoc Doc, string ETag)?> ReadAsync(string userId, string sessionId, CancellationToken ct)
    {
        try
        {
            var response = await Container.ReadItemAsync<SessionDoc>(sessionId, new PartitionKey(userId), cancellationToken: ct);
            var doc = response.Resource;
            return doc.Type == DocumentTypes.Session && doc.UserId == userId ? (doc, response.ETag) : null;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task DeleteMessagesAsync(PartitionKey pk, string[] ids, CancellationToken ct)
    {
        var batch = Container.CreateTransactionalBatch(pk);
        foreach (var id in ids)
        {
            batch.DeleteItem(id);
        }

        using var response = await batch.ExecuteAsync(ct);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // A missing item (404) fails the whole batch; fall back to per-item deletes and ignore 404.
        foreach (var id in ids)
        {
            try
            {
                await Container.DeleteItemAsync<MessageDoc>(id, pk, cancellationToken: ct);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }
    }

    private sealed class IdOnly
    {
        public required string Id { get; set; }
    }
}
