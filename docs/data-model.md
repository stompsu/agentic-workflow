# Data model

Owners: architect (schema), backend-dotnet (Cosmos stores), agent-workflows (history use, retrieval), azure-infra (provisioning).
Revised 2026-10-25 with decisions D7 (retention + delete my data), D8 (embeddings) and D9 (knowledge source, analyzer).
Revised 2026-10-27 with ADR 0006 (guest access, accepted): guest partitions, the `guest` document, the reserved
counter partition `system-guest-counter`, retention by user (1.3) and the guest steps in 1.5 and 1.7.

## 1. Cosmos DB

### 1.1 Decision: one container, type discriminator

Database `agentwf`, container `chat`, partition key `/userId` (value = `oid` claim).
Documents have `type` = `session` | `message` | `guest` | `guestCounter` (the last two: 1.8).

Partition key values (ADR 0006 section 6): an Entra `oid` (lowercase GUID), a guest id `guest-<lowercase GUID v4>`
(the `oid` of a guest token), or the reserved value `system-guest-counter` (global guest counters, 1.8). The three
forms are disjoint, so no user id can address another form's partition. The `guest-` prefix is used ONLY by the
retention policy (1.3) and never for an authorization decision; the validating token scheme decides whether a caller
is a guest (ADR 0006 section 5.3).

Why one container instead of `sessions` + `messages`:
- A session and its messages share the logical partition `userId`, so a turn's writes
  (create message + update session `updatedAt`/turn lock) go into ONE transactional batch. Two containers cannot do that.
- Every query is single-partition by construction (all keyed by `userId`); no cross-partition fan-out.
- One set of RBAC, throughput and TTL settings.
- "Delete my data" (1.7) is a single-partition operation.

Trade-off: a logical partition is limited to 20 GB per user. Chat text per user is far below that.
If that ever changes, move to a hierarchical partition key (`/userId`, `/sessionId`); batches still work because a
session doc and its messages share the full key. Not needed for v1.

Throughput: serverless in dev/test, autoscale in prod (azure-infra decides the max RU/s).

### 1.2 Documents

Session:
```json
{
  "id": "0192f0c1-7a3e-7cc1-9a51-3f1e2b9d4c10",
  "type": "session",
  "userId": "8f3d2b1e-0000-4c4b-9a0e-1234567890ab",
  "sessionId": "0192f0c1-7a3e-7cc1-9a51-3f1e2b9d4c10",
  "workflowId": "support-ticket",
  "title": "My claim from May",
  "state": "active",
  "createdAt": "2026-10-25T10:15:30.123Z",
  "updatedAt": "2026-10-25T10:17:02.001Z",
  "messageCount": 4,
  "activeTurn": { "turnId": "0192f0c1-dddd-...", "startedAt": "2026-10-25T10:17:00.000Z" },
  "schemaVersion": 1,
  "ttl": 7776000
}
```
Message:
```json
{
  "id": "0192f0c1-bbbb-7000-8000-000000000001",
  "type": "message",
  "userId": "8f3d2b1e-0000-4c4b-9a0e-1234567890ab",
  "sessionId": "0192f0c1-7a3e-7cc1-9a51-3f1e2b9d4c10",
  "role": "assistant",
  "content": "Most claims are settled within 10 working days [S1].",
  "createdAt": "2026-10-25T10:16:01.500Z",
  "status": "complete",
  "kind": "answer",
  "agentName": "Claims specialist",
  "citations": [ { "ref": "S1", "title": "Claims handling times", "docId": "claims-handling-0003", "url": "https://kb.example.com/claims/handling" } ],
  "errorCode": null,
  "workflowMetadata": { "category": "claims", "confidence": 0.91, "prompts": "orchestrator.v1,claims.v1" },
  "schemaVersion": 1,
  "ttl": 7776000
}
```
(`ttl` 7776000 = 90 days, the prod default.)

Guest session and message documents are identical to the ones above, with `userId = "guest-<guid>"` and the guest
`ttl` (1.3). The guest-only documents are in 1.8.

Rules:
- `id`: GUID v7 (`Guid.CreateVersion7()`), server-generated. Session doc: `id == sessionId`.
- `userId`: ONLY from the validated token. Stores take `userId` as the first parameter of every method.
- Enums are stored as camelCase strings (same `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` as the API).
- `title`: nullable; set in the turn-start batch from the first user message (first 60 chars) when still `null`.
- `state` (session): `active` | `deleting`; always written (`active` on create). A `deleting` session is invisible:
  `GetAsync` returns null, `ListAsync` omits it, `TryStartTurnAsync` returns `NotFound`; only `DeleteAsync` and
  `DeleteAllUserDataAsync` see it (and resume the deletion, 1.5 / 1.7).
- `status` (message): `complete` | `cancelled` | `failed` (see api-contract 4.5). User messages are always `complete`.
- `kind`: `answer` | `clarification` (assistant only; readers default a missing value to `answer`).
- `workflowMetadata`: free-form JSON object produced by the workflow (`TurnCompleted.Metadata`). The API stores it
  and never interprets it. Keeps the store workflow-agnostic.
- `activeTurn`: turn lock; `turnId` = `TurnLease.LeaseId`. Set with an ETag-conditional replace at turn start (or at
  DELETE start, with a fresh delete lease id); cleared at turn end only if `activeTurn.turnId` equals the lease's id,
  otherwise the store returns `LeaseLost` and writes nothing. A lock older than `Chat:TurnTimeout + 60 s` is stale and
  may be taken over (by a new turn or a DELETE session). `DELETE /api/me/data` takes over live locks as well (1.7).
- `messageCount`: number of messages ever written to the session (incremented at turn start and turn end). Internal,
  never returned; it is NOT decremented when messages expire (1.3 E1), so it may exceed the number of stored messages.
- `ttl`: integer seconds, rules in 1.3. The property is OMITTED (not `null`) when the retention policy returns no
  value (an Entra user's document while `Chat:RetentionDays` is unset): Cosmos rejects `"ttl": null`. The document
  classes serialize `ttl` with "ignore when null". Guest documents always carry `ttl`.
- `schemaVersion`: bump on breaking document changes; readers tolerate unknown fields.
- DTO mappers never expose `userId`, `ttl`, `activeTurn`, `state`, `workflowMetadata`, `schemaVersion`, `messageCount`
  or `type` (mapper tests assert it).

### 1.3 TTL / retention (decided, D7; by user since ADR 0006)

Config `Chat:RetentionDays` (integer, 1..3650). Defaults, chosen by the user on 2026-10-25 and changeable by the user
at any time (set per environment by azure-infra through the Bicep param `chatRetentionDays`):

| Environment | `Chat:RetentionDays` (default) |
|---|---|
| dev | 30 |
| test | 30 |
| prod | 90 |
| local `Development` | unset allowed (no `ttl`, documents never expire) |

Startup fails when `Chat:RetentionDays` is unset or outside 1..3650 in any environment other than `Development`
(ADR 0003 rule 9). The same value is passed to the frontend as `APP_RETENTION_DAYS` for the data deletion page
(auth-setup section 4.1).

Retention by user (ADR 0006 section 6.3): the `ttl` of every session and message document comes from
`RetentionPolicy.TtlSeconds(userId)` (Infrastructure, injected into both stores; replaces `TurnRules.TtlSeconds(int?)`):
- `userId` starts with `guest-`: ALWAYS `Guest:DataTtl` in seconds (default `04:15:00` = 15,300 s), even when
  `Chat:RetentionDays` is unset (local `Development`) and even when `Guest:Enabled` is false, so leftover guest data
  still expires (its range `00:35:00`..`7.00:00:00` is therefore validated at startup even when guests are disabled).
- Otherwise (Entra users): `Chat:RetentionDays * 86400`, or no `ttl` when `Chat:RetentionDays` is unset.
The guest document and the counter documents use their own TTLs (1.8). In this section "the retention period" means
the value the policy returns for the document's `userId` (`RetentionDays` for Entra users, `Guest:DataTtl` for guests);
R1-R6 and E1-E7 apply to guests with that substitution. Guest TTL guarantee: `Guest:DataTtl >= Guest:MaxLifetime +
00:05:00` (startup rule, ADR 0003 section 4), and every document of a guest is written at or after the guest's
creation, so nothing expires while the guest's token can still read it. All guest data is gone at most `Guest:DataTtl`
after the guest's last write (worst case about 8 h 15 min after "Continue as guest" at the defaults).

Mechanism: container `defaultTtl = -1` (TTL enabled, no default expiry; already in Bicep). Every document the API
writes carries `ttl` from the retention policy above (`Chat:RetentionDays * 86400` for Entra users). Cosmos counts
`ttl` from the document's LAST WRITE (system property `_ts`, Cosmos server time), not from `createdAt`.

Rules:
- R1 Message documents get `ttl` when created and are never updated afterwards. A message expires `RetentionDays`
  after it was written.
- R2 The session document gets `ttl` on EVERY write: create, turn start (batch with the user message), turn end
  (batch with the assistant message, or the lock-only release), DELETE-session mark, delete-my-data mark. A session
  therefore expires `RetentionDays` after its last activity (`ttl` is refreshed on each turn).
- R3 Turn start and turn end write the session document in the same transactional batch as the message, so the
  session's `_ts` is always at or after the `_ts` of its newest message. A session never expires before its newest
  message; it can outlive its older messages (E1).
- R4 Resulting guarantee: no message is kept longer than `RetentionDays` after it was written. A session document
  (including its `title`, derived from the first user message) is kept until `RetentionDays` after the session's last
  activity.
- R5 Expired documents are invisible to point reads and queries immediately, even before Cosmos deletes them in the
  background. Background deletion is charged as delete RUs on serverless (dev/test) and uses leftover RU/s on
  provisioned/autoscale (prod).
- R6 A config change applies only to documents written after it: each document keeps the `ttl` it was written with.
  Lowering `RetentionDays` does not shorten existing messages; a session picks up the new value at its next write.
  Documents written while the value was unset (local `Development` only) never expire.

Edge cases:
- E1 Message expires before its session: a session in use for longer than `RetentionDays` loses its oldest messages
  one by one while it stays in the list. `GET /api/sessions/{id}` returns only the remaining messages; the first one may
  be an assistant message whose user message expired a moment earlier (user and assistant messages of a turn are
  written seconds apart), and the array may be empty. History for agents drops such a leading assistant message
  (1.6 step 2). `messageCount` is not adjusted.
- E2 A session created but never used expires `RetentionDays` after creation.
- E3 A session cannot expire during a turn in practice (turn start refreshes its `ttl`; `RetentionDays >= 1 day`
  is far above `Chat:TurnTimeout`). If it ever happens, turn end gets `SessionGone` (api-contract 4.5).
- E4 A session left in `deleting` by an interrupted delete keeps the `ttl` from its mark write; it and its remaining
  messages expire within `RetentionDays` at the latest, unless a repeated delete removes them first.
- E5 Orphaned messages (session document already gone after an interrupted delete) are unreachable through the API
  (every message read is scoped to a session the caller can read) and are removed by TTL or by `DELETE /api/me/data`.
- E6 The session `title` lives as long as the session document (R2), even after the first user message expired.
  Accepted: the title belongs to an active session; deleting the session or the user's data removes it.
- E7 `ttl` uses Cosmos server time; `createdAt`/`updatedAt` are application time. Skew is irrelevant at day granularity.
  The API never computes expiry itself and never filters by date.

Rejected: rewriting every message's `ttl` on each turn so that a whole session expires together (RU cost grows with
session length on every turn), and a scheduled cleanup job (extra component; TTL already does it).

### 1.4 Indexing policy

```json
{
  "indexingMode": "consistent",
  "automatic": true,
  "includedPaths": [
    { "path": "/userId/?" }, { "path": "/type/?" }, { "path": "/sessionId/?" }, { "path": "/state/?" },
    { "path": "/status/?" }, { "path": "/createdAt/?" }, { "path": "/updatedAt/?" }
  ],
  "excludedPaths": [ { "path": "/*" } ],
  "compositeIndexes": [
    [ { "path": "/userId", "order": "ascending" }, { "path": "/type", "order": "ascending" }, { "path": "/state", "order": "ascending" }, { "path": "/updatedAt", "order": "descending" } ],
    [ { "path": "/userId", "order": "ascending" }, { "path": "/type", "order": "ascending" }, { "path": "/sessionId", "order": "ascending" }, { "path": "/createdAt", "order": "descending" } ]
  ]
}
```
`content`, `citations`, `workflowMetadata` are not indexed (write cost, no queries on them). The delete-my-data
queries (1.7) filter on `userId` + `type` only and need no new index. Guest access needs no index change: the guest
document and the counter documents are only read and patched by id (1.8); patch filter predicates are evaluated on
the target document and need no index.

### 1.5 Query patterns (all pass `PartitionKey(userId)` in request options AND filter on `userId`)

| Use | Operation |
|---|---|
| Get session | `ReadItemAsync<SessionDoc>(sessionId, new PartitionKey(userId))`; not found if missing, `type != "session"` or `state = "deleting"` |
| List sessions | `SELECT c.id, c.workflowId, c.title, c.createdAt, c.updatedAt, c.messageCount FROM c WHERE c.userId = @userId AND c.type = 'session' AND c.state = 'active' ORDER BY c.updatedAt DESC` (`MaxItemCount = pageSize`, continuation token) |
| Messages for display | `SELECT TOP 101 * FROM c WHERE c.userId = @userId AND c.type = 'message' AND c.sessionId = @sessionId ORDER BY c.createdAt DESC` (101 to compute `hasMoreMessages`; reverse in memory) |
| History for agents | same as display with `TOP @historyMaxMessages` and `AND c.id != @currentUserMessageId`, all statuses (pairing filter in memory, 1.6) |
| Start turn | Read session; `NotFound` if missing or `deleting`; `Busy` if a non-stale `activeTurn` exists. Else transactional batch: create user message (with `ttl`) + replace session (ETag condition; set `activeTurn`, `updatedAt`, `messageCount`, `title` if null, `ttl`). ETag conflict -> `Busy` |
| End turn | Read session. Missing -> `SessionGone`; `activeTurn.turnId != lease.LeaseId` (taken over, incl. by a DELETE or delete-my-data) -> `LeaseLost`. Both: nothing written, no exception. Else transactional batch: create assistant message (if any, with `ttl`) + replace session with ETag condition (clear `activeTurn`, `updatedAt`, `ttl`); ETag conflict -> re-read once and re-evaluate |
| Delete session | 1. Read session; `NotFound` if missing. 2. If `state = active`: `Busy` if a non-stale `activeTurn`; else ETag-conditional replace setting `state = deleting`, `activeTurn = {turnId: <new delete lease id>, startedAt}` and `ttl` (ETag conflict -> `Busy`). If already `deleting`, skip to 3 (resume). 3. Query message ids for `(userId, sessionId)` and delete them in chunks of up to 100 (chunk rule below). 4. Delete the session document (404 ignored). Return `Deleted` |
| Delete all user data | 1.7 |
| Guest: create guest document | `CreateItemAsync(guestDoc, new PartitionKey(guestUserId))` with `id = "guest"`, BEFORE the token is signed (1.8) |
| Guest: activity check | `ReadItemAsync<GuestDoc>("guest", new PartitionKey(guestUserId))`; 404 = guest ended. Cached per replica by `GuestActivityCache` (ADR 0006 section 6.2) |
| Guest: start turn (`enforceGuestQuota = true`) | As "Start turn", plus in the SAME transactional batch `PatchItem("guest", [Increment("/turnCount", 1)])` with `FilterPredicate = "FROM c WHERE c.turnCount < c.maxTurns"` and `ttl` refresh. Per-operation result of the guest operation: 404 -> `GuestExpired`, 412 -> `GuestQuotaExceeded`; the other operations report 424 and nothing is written |
| Guest: create session (`enforceGuestQuota = true`) | Transactional batch: create session document + `PatchItem("guest", [Increment("/sessionCount", 1)])` with `FilterPredicate = "FROM c WHERE c.sessionCount < c.maxSessions"` and `ttl` refresh; 404 / 412 mapped as above |
| Guest: global counter | `PatchItemAsync(counterId, new PartitionKey("system-guest-counter"), [Increment("/count", 1)])`; on 404 create the document with `count = 1` (409 on that create -> patch again). Returns the new count (1.8) |

Chunk delete rule (both deletes): a transactional batch is atomic, so ONE already-missing document (404, for example
expired by TTL or deleted by a concurrent request) fails the whole batch. On a batch failure caused by 404 the store
deletes that chunk again as individual point deletes, ignoring 404. Any other failure propagates.

A crash between steps 2 and 4 leaves the session `deleting` (invisible); a repeated DELETE resumes it, and TTL
eventually removes the leftovers (1.3 E4). There is no query without `userId`. qa-reviewer treats any
`Container.GetItemQueryIterator` without a partition key as a blocker.

Guest quota patch verification (ADR 0006 V3, blocking for the quota implementation): 412 on a failed predicate, 404 on
a missing guest document, 424 on the other batch operations with nothing written, the property-to-property predicate
(fallback: a literal from the snapshot read at creation), and the same behavior in the Cosmos emulator.

Isolation tests (backend-dotnet, Phase 1): an in-memory store (used by Api.Tests) that enforces `userId` the same way,
plus Cosmos emulator integration tests (skipped when the emulator is not reachable) proving user B cannot read, list,
delete or lock user A's sessions/messages. Extended for 1.7: user B's delete-my-data leaves user A's data untouched.
Extended for guests: guest A, guest B and a social user cannot see or touch each other's data, in both directions.

### 1.6 History loading for agents

The chat endpoint loads history once per turn, after the turn started, and passes it to the workflow
(`WorkflowRunRequest.History`, see ADR 0002). Rule: the current user message is excluded by id (it is passed separately
as `WorkflowRunRequest.UserMessage`). Steps 1-3 run in the store (`IMessageStore.GetHistoryAsync`); steps 4-5 run in
the workflow-agnostic `ChatHistoryBuilder` in the Api project (the store never trims by tokens, workflows never trim).
1. Load the last `Chat:HistoryMaxMessages` (default 20) messages of the session, excluding the current user message, oldest first.
2. Pairing filter: keep an assistant message only if `status = complete` AND the message directly before it is a user
   message; keep a user message only if the next message is a kept assistant message. Orphaned user messages (failed
   turn, cancelled turn, cancelled before any token) are therefore shown in the UI but never sent to agents, and an
   assistant message whose user message expired by TTL (1.3 E1) or fell outside the loaded window is dropped, so the
   history always starts with a user message.
3. Strip citation markers (`\[S\d+\]`) from assistant text: refs are per turn and would collide with new refs.
4. Trim oldest-first (in user/assistant pairs) to `Chat:HistoryMaxTokens` (default 6000; estimate = chars / 4) so the
   current user message always fits.
5. Map to `HistoryMessage` (role, content, `agentName`, `kind`, `workflowMetadata`, `createdAt`).

Agents run with this explicit history; no server-side (Foundry/Responses) conversation state is used, so replicas
stay stateless and the Cosmos record is the only history.

### 1.7 Delete all user data (`DELETE /api/me/data`, D7)

Store method `ISessionStore.DeleteAllUserDataAsync(userId, budget, ct)` (plan.md section 2). Stateless and resumable:
the only progress state is each session's own `state = deleting`. A `TimeProvider`-based deadline = start + `budget`
(`Chat:DeleteUserDataBudget`, default `00:01:00`) is checked before every replace and every chunk; when it has passed
the method returns `Incomplete` (endpoint: 503 `deletion_incomplete`). `ct` is `RequestAborted`: on abort the method
throws `OperationCanceledException`; nothing is rolled back.

1. Mark. Query `SELECT * FROM c WHERE c.userId = @userId AND c.type = 'session'` (all states, all pages). For each
   session with `state = active`: ETag-conditional replace setting `state = deleting`,
   `activeTurn = {turnId: <new delete lease id>, startedAt: now}` and `ttl`, WITHOUT checking the existing lock (live
   locks are taken over, unlike DELETE session). ETag conflict -> re-read that session and retry (a re-read that finds
   the document missing or already `deleting` counts as done). Sessions already `deleting` are skipped.
   All sessions are marked before anything is deleted, so they all become invisible first and no new turn can start
   in them (`TryStartTurnAsync` -> `NotFound`; an in-flight turn's `CompleteTurnAsync` -> `LeaseLost`, writes nothing).
2. Messages. Query `SELECT c.id FROM c WHERE c.userId = @userId AND c.type = 'message'` (all sessions, including
   orphans) and delete in chunks of up to 100 (chunk delete rule, 1.5). This query runs after step 1 completed, so it
   also sees user messages written by turns that started just before their session was marked.
3. Sessions. Delete every session document from step 1 by id (404 ignored).
3a. Guests only (caller is a guest per `ICurrentUser.IsGuest`): delete the guest document (`id = "guest"`, 404
   ignored) LAST, then invalidate this replica's `GuestActivityCache` entry. This ends the guest identity: later writes
   fail at once (the guest operation in the batch returns 404 -> 401 `guest_expired`), other requests within the
   cache window (ADR 0006 section 6.5). The endpoint skips the guest-activity check, so a repeated call for an ended
   guest runs the (empty) sequence and returns `Completed`.
4. Return `Completed` (endpoint: 204). A caller with no documents gets `Completed` immediately.

Concurrency: documents created after step 1's query (a session created concurrently in another tab) are not deleted by
this call; a repeated call deletes them. Two concurrent calls for the same user interleave safely (ETag re-read,
404 ignored). Scale: per-user data is small; at ~100 documents per chunk, thousands of documents finish well inside the
budget. RU cost is one delete per document (serverless dev/test is billed per request).
Logging: counts of marked sessions and deleted documents plus `enduser.id`; never titles or content.

### 1.8 Guest documents (ADR 0006 section 6)

Guest document (one per guest partition, `id = "guest"`, `type = "guest"`). Created at `POST /api/guest/token` BEFORE
the token is signed, so a token never exists without its document; if signing then fails, the orphan document expires
by TTL.
```json
{
  "id": "guest",
  "type": "guest",
  "userId": "guest-3f0c2a7e-5b1d-4c8e-9a2f-6d7e8f901234",
  "createdAt": "2026-10-27T10:15:00.000Z",
  "expiresAt": "2026-10-27T14:15:00.000Z",
  "turnCount": 0,
  "maxTurns": 30,
  "sessionCount": 0,
  "maxSessions": 5,
  "schemaVersion": 1,
  "ttl": 15300
}
```
Rules:
- `expiresAt` = the token's `exp` = the end of the guest identity (`Guest:MaxLifetime` after creation).
- `maxTurns` / `maxSessions` are snapshots of `Guest:MaxTurns` / `Guest:MaxSessions` at creation; a config change
  never changes the rules for a live guest.
- `turnCount` counts accepted turns (incremented in the turn-start batch, 1.5); `sessionCount` counts sessions CREATED
  (incremented in the session-create batch, never decremented by deletes). Both are only changed by conditional patch
  `Increment` inside the existing transactions, so a failed turn start or session create never consumes quota.
- `ttl` = `Guest:DataTtl` in seconds, set at create and refreshed by every quota patch (Cosmos counts TTL from the
  last write), so the guest document never expires before the guest's newest session or message.
- Deleted last by `DELETE /api/me/data` (1.7 step 3a). Its absence means "guest ended" (revocation anchor).
- Never returned by the API; no DTO maps it.

Global guest counters (reserved partition `userId = "system-guest-counter"`, `type = "guestCounter"`):
```json
{ "id": "turn-h-2026102710", "type": "guestCounter", "userId": "system-guest-counter", "count": 17, "ttl": 172800 }
```
- Ids (UTC windows): `create-h-yyyyMMddHH` (guest creations per hour), `turn-h-yyyyMMddHH` (guest turns per hour),
  `turn-d-yyyyMMdd` (guest turns per day).
- `count` is changed only by patch `Increment` (atomic); the document is created on the first 404 (1.5). `ttl` 48 h.
- No user id can equal `system-guest-counter` (Entra ids are GUIDs, guest ids start with `guest-`). No API endpoint
  reads this partition; only the guest limiters in the Api do (ADR 0006 sections 8, 9.2).
- Load: one patch per guest creation and two per guest turn into one logical partition; at the default caps (a few
  hundred per hour) far below a logical partition's throughput limit.
- Counters store counts only: no ids, no IP addresses.

## 2. Azure AI Search

### 2.1 Index `agentwf-knowledge` (`Search:IndexName`)

One index for all domains; specialists filter by `domain`.

| Field | Type | key | searchable | filterable | facetable | sortable | retrievable | Notes |
|---|---|---|---|---|---|---|---|---|
| `id` | `Edm.String` | yes | no | yes | no | no | yes | Chunk id: lowercase hex(SHA-256(sourcePath + "#" + chunkIndex)), deterministic so re-ingestion upserts. Not base64url: a Search document key cannot start with `_` |
| `domain` | `Edm.String` | | no | yes | yes | no | yes | Open set of lowercase values (`^[a-z0-9-]+$`); current values in 2.5 |
| `title` | `Edm.String` | | yes (`en.microsoft`) | no | no | no | yes | Document title (+ section heading) |
| `content` | `Edm.String` | | yes (`en.microsoft`) | no | no | no | yes | Chunk text, ~500-800 tokens, ~10% overlap |
| `contentVector` | `Collection(Edm.Single)` | | yes (vector) | no | no | no | no (`stored: false`) | 1536 dimensions (text-embedding-3-small, decided D8). Profile `vector-profile` |
| `sourceUrl` | `Edm.String` | | no | no | no | no | yes | Link to the source; may be empty. Only absolute `http(s)` values are passed on (ADR 0002 section 9) |

Analyzer: `en.microsoft` (English content, decided D9). A non-English knowledge base later needs a new index
definition (analyzers cannot be changed on an existing field) and a re-ingestion.

### 2.2 Vector search

```json
"vectorSearch": {
  "algorithms": [ { "name": "hnsw-cosine", "kind": "hnsw",
                    "hnswParameters": { "metric": "cosine", "m": 4, "efConstruction": 400, "efSearch": 500 } } ],
  "vectorizers": [ { "name": "aoai-vectorizer", "kind": "azureOpenAI",
                     "azureOpenAIParameters": { "resourceUri": "https://<foundry-or-aoai-resource>.openai.azure.com",
                                                "deploymentId": "<embedding deployment>", "modelName": "text-embedding-3-small" } } ],
  "profiles": [ { "name": "vector-profile", "algorithm": "hnsw-cosine", "vectorizer": "aoai-vectorizer" } ]
}
```
Model `text-embedding-3-small` at 1536 dimensions (D8). The model resource (`modelResourceId`) and the deployment name
(`embeddingDeployment`, default `text-embedding-3-small`) are supplied by the user; the deployment must exist in that
resource. The integrated vectorizer embeds the query inside Search (the backend sends text via a vectorizable text
query), so the backend needs no embedding client. The Search service's managed identity needs
`Cognitive Services OpenAI User` on the model resource (azure-infra). Ingestion computes document embeddings itself
(TokenCredential) or uses the same vectorizer through an indexer skillset; ingestion owner decides in Phase 2.
Document and query embeddings MUST come from the same model and dimensions; changing either requires a new index and
re-ingestion.

### 2.3 Semantic configuration

```json
"semantic": { "defaultConfiguration": "semantic-default",
  "configurations": [ { "name": "semantic-default",
    "prioritizedFields": { "titleField": { "fieldName": "title" },
                           "prioritizedContentFields": [ { "fieldName": "content" } ] } } ] }
```
Requires a Search SKU with semantic ranker enabled (Basic or higher; azure-infra confirms the plan setting).

### 2.4 Query used by the retrieval tool

Hybrid + semantic, single domain:
- `search` = the tool's `query` argument; vector query on `contentVector`, `kNearestNeighbors = 50`, text vectorized by `aoai-vectorizer`.
- `filter` = `domain eq '<domain>'`. The value comes from a code constant (2.5), never from model or user input;
  `IKnowledgeSearch` additionally rejects values not matching `^[a-z0-9-]+$`.
- `queryType = semantic`, `semanticConfiguration = semantic-default`, `top = Search:TopK` (default 5),
  `select = id, domain, title, content, sourceUrl`.
- Drop results with reranker score below `Search:MinRerankerScore` (default 1.5 on the 0-4 scale); if none remain, the
  tool returns "no results" and the agent says so.

### 2.5 Domain mapping (SupportTicket)

| Classification category | `domain` value (constant) |
|---|---|
| `Policy` | `policy` |
| `Claims` | `claims` |
| `HealthAssessments` | `health` |

The mapping is a constant inside the SupportTicket workflow. A new workflow type adds its own new domain values
(add-workflow-type skill; document them here); the index schema does not change.

### 2.6 Ingestion

Separate from the web app (console app run locally or as a Container Apps Job). Source (decided D9, v1): the repo
folder `knowledge/<domain>/*.md` (`knowledge/policy`, `knowledge/claims`, `knowledge/health`), English content. The
folder is copied into the ingestion image (`Ingestion:SourcePath`, `/app/knowledge` in the job). Other sources (blob,
SharePoint) are out of scope for v1. Upsert by deterministic `id`; delete chunks whose source disappeared.
Keys: in Key mode the API's `Search:Key` is a QUERY key (read-only), created by `infra/scripts/ensure-search-query-key.sh`
(run by the user, plan.md section 3) and stored in Key Vault as `search-query-key`. The admin key is used only by
ingestion / index creation (`Ingestion:SearchAdminKey`, user-secrets or Key Vault) and is never given to the API.
RBAC: only the ingestion identity gets `Search Index Data Contributor` (+ `Search Service Contributor` for index
management); the backend gets `Search Index Data Reader`.
