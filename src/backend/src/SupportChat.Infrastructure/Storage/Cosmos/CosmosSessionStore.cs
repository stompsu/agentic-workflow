using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportChat.Core.Options;
using SupportChat.Core.Storage;

namespace SupportChat.Infrastructure.Storage.Cosmos;

/// <summary>
/// Cosmos session store (data-model 1.5). Every operation passes PartitionKey(userId) in the request options AND
/// filters on userId; there is no query without it.
/// </summary>
public sealed class CosmosSessionStore(
    CosmosChatContainer chat,
    IOptions<ChatLimitsOptions> options,
    TimeProvider timeProvider,
    RetentionPolicy retention,
    ILogger<CosmosSessionStore>? logger = null) : ISessionStore
{
    private const int DeleteBatchSize = 100;
    private Container Container => chat.Container;
    private ChatLimitsOptions Options => options.Value;

    public async Task<CreateSessionResult> CreateAsync(
        string userId, string workflowId, string? title, bool enforceGuestQuota, CancellationToken ct)
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
            Ttl = retention.TtlSeconds(userId),
        };
        if (!enforceGuestQuota)
        {
            await Container.CreateItemAsync(doc, new PartitionKey(userId), cancellationToken: ct);
            return new CreateSessionResult.Created(doc.ToRecord());
        }

        // Guest: session create + conditional patch of the guest document in ONE transaction (ADR 0006 section 6.2).
        using var response = await Container.CreateTransactionalBatch(new PartitionKey(userId))
            .CreateItem(doc)
            .PatchItem(GuestDoc.DocumentId, GuestQuota.PatchOperations("/sessionCount", retention.GuestTtlSeconds), GuestQuota.Predicate("sessionCount", "maxSessions"))
            .ExecuteAsync(ct);
        if (response.IsSuccessStatusCode)
        {
            return new CreateSessionResult.Created(doc.ToRecord());
        }

        return GuestQuota.PatchStatus(response, patchIndex: 1) switch
        {
            HttpStatusCode.NotFound => new CreateSessionResult.GuestExpired(),
            HttpStatusCode.PreconditionFailed => new CreateSessionResult.GuestQuotaExceeded(),
            _ => throw new InvalidOperationException($"Creating the guest session failed with Cosmos status {(int)response.StatusCode}."),
        };
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
            doc.Ttl = retention.TtlSeconds(userId);
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

    public async Task<DeleteUserDataResult> DeleteAllUserDataAsync(string userId, TimeSpan budget, CancellationToken ct)
    {
        var pk = new PartitionKey(userId);
        var deadline = timeProvider.GetUtcNow() + budget;
        bool OutOfBudget() => timeProvider.GetUtcNow() >= deadline;
        var marked = 0;
        var messagesDeleted = 0;
        var sessionsDeleted = 0;

        DeleteUserDataResult Finish(DeleteUserDataResult result)
        {
            logger?.LogInformation(
                "Delete user data {Result}: sessions marked {SessionsMarked}, messages deleted {MessagesDeleted}, sessions deleted {SessionsDeleted}, enduser.id {EndUserId}",
                result, marked, messagesDeleted, sessionsDeleted, userId);
            return result;
        }

        // 1. Mark every active session deleting, taking over any lock (live or stale).
        var sessions = new List<SessionStub>();
        var sessionsQuery = new QueryDefinition(
                "SELECT c.id, c.state FROM c WHERE c.userId = @userId AND c.type = 'session'")
            .WithParameter("@userId", userId);
        using (var iterator = Container.GetItemQueryIterator<SessionStub>(
                   sessionsQuery, requestOptions: new QueryRequestOptions { PartitionKey = pk, MaxItemCount = 500 }))
        {
            while (iterator.HasMoreResults)
            {
                sessions.AddRange(await iterator.ReadNextAsync(ct));
            }
        }

        foreach (var stub in sessions.Where(x => x.State == DocumentTypes.Active))
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (OutOfBudget())
                {
                    return Finish(DeleteUserDataResult.Incomplete);
                }

                var read = await ReadAsync(userId, stub.Id, ct);
                if (read is not { } current || current.Doc.State == DocumentTypes.Deleting)
                {
                    break; // gone or already marked: counts as done
                }

                var (doc, etag) = current;
                var now = timeProvider.GetUtcNow();
                doc.State = DocumentTypes.Deleting;
                doc.ActiveTurn = new ActiveTurnDoc { TurnId = TurnRules.NewId(), StartedAt = now };
                doc.Ttl = retention.TtlSeconds(userId);
                try
                {
                    await Container.ReplaceItemAsync(doc, doc.Id, pk, new ItemRequestOptions { IfMatchEtag = etag }, ct);
                    marked++;
                    break;
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
                {
                    // ETag conflict: re-read and retry.
                }
                catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    break;
                }
            }
        }

        // 2. All message documents of the user (orphans included), in chunks.
        var messageIds = new List<string>();
        var messagesQuery = new QueryDefinition(
                "SELECT c.id FROM c WHERE c.userId = @userId AND c.type = 'message'")
            .WithParameter("@userId", userId);
        using (var iterator = Container.GetItemQueryIterator<IdOnly>(
                   messagesQuery, requestOptions: new QueryRequestOptions { PartitionKey = pk, MaxItemCount = 500 }))
        {
            while (iterator.HasMoreResults)
            {
                messageIds.AddRange((await iterator.ReadNextAsync(ct)).Select(x => x.Id));
            }
        }

        foreach (var chunk in messageIds.Chunk(DeleteBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            if (OutOfBudget())
            {
                return Finish(DeleteUserDataResult.Incomplete);
            }

            await DeleteMessagesAsync(pk, chunk, ct);
            messagesDeleted += chunk.Length;
        }

        // 3. The session documents listed in step 1.
        foreach (var stub in sessions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await Container.DeleteItemAsync<SessionDoc>(stub.Id, pk, cancellationToken: ct);
                sessionsDeleted++;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }

        // 3a. A guest's identity ends with its data: the guest document goes LAST (data-model 1.7).
        if (GuestOptions.IsGuestId(userId))
        {
            try
            {
                await Container.DeleteItemAsync<GuestDoc>(GuestDoc.DocumentId, pk, cancellationToken: ct);
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
            }
        }

        return Finish(DeleteUserDataResult.Completed);
    }

    public async Task<StartTurnResult> TryStartTurnAsync(
        string userId, string sessionId, MessageRecord userMessage, bool enforceGuestQuota, CancellationToken ct)
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
        doc.Ttl = retention.TtlSeconds(userId);

        var batch = Container.CreateTransactionalBatch(pk)
            .CreateItem(MessageDoc.From(userId, userMessage, retention.TtlSeconds(userId)))
            .ReplaceItem(doc.Id, doc, new TransactionalBatchItemRequestOptions { IfMatchEtag = etag });
        if (enforceGuestQuota)
        {
            // Last on purpose: a session conflict (ETag) reports on the replace and stays "Busy"; the patch then reports 424.
            batch.PatchItem(GuestDoc.DocumentId, GuestQuota.PatchOperations("/turnCount", retention.GuestTtlSeconds), GuestQuota.Predicate("turnCount", "maxTurns"));
        }

        using var response = await batch.ExecuteAsync(ct);

        if (response.IsSuccessStatusCode)
        {
            return new StartTurnResult.Started(lease);
        }

        if (enforceGuestQuota)
        {
            switch (GuestQuota.PatchStatus(response, patchIndex: 2))
            {
                case HttpStatusCode.NotFound:
                    return new StartTurnResult.GuestExpired();
                case HttpStatusCode.PreconditionFailed:
                    return new StartTurnResult.GuestQuotaExceeded();
            }
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
            doc.Ttl = retention.TtlSeconds(userId);

            var batch = Container.CreateTransactionalBatch(pk);
            if (assistantMessage is not null)
            {
                doc.MessageCount++;
                batch.CreateItem(MessageDoc.From(userId, assistantMessage, retention.TtlSeconds(userId)));
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

        // A missing item (404, for example expired by TTL or deleted concurrently) fails the whole atomic batch.
        // Only then redo the chunk as point deletes ignoring 404; any other failure propagates.
        if (!(response.StatusCode == HttpStatusCode.NotFound || response.Any(r => r.StatusCode == HttpStatusCode.NotFound)))
        {
            throw new InvalidOperationException($"Deleting messages failed with Cosmos status {(int)response.StatusCode}.");
        }

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

    private sealed class SessionStub
    {
        public required string Id { get; set; }

        public string? State { get; set; }
    }

    private sealed class IdOnly
    {
        public required string Id { get; set; }
    }
}
