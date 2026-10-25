# Data model

Owners: architect (schema), backend-dotnet (Cosmos stores), agent-workflows (history use, retrieval), azure-infra (provisioning).

## 1. Cosmos DB

### 1.1 Decision: one container, type discriminator

Database `supportchat`, container `chat`, partition key `/userId` (value = `oid` claim).
Documents have `type` = `session` | `message`.

Why one container instead of `sessions` + `messages`:
- A session and its messages share the logical partition `userId`, so a turn's writes
  (create message + update session `updatedAt`/turn lock) go into ONE transactional batch. Two containers cannot do that.
- Every query is single-partition by construction (all keyed by `userId`); no cross-partition fan-out.
- One set of RBAC, throughput and TTL settings.

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
Rules:
- `id`: GUID v7 (`Guid.CreateVersion7()`), server-generated. Session doc: `id == sessionId`.
- `userId`: ONLY from the validated token. Stores take `userId` as the first parameter of every method.
- Enums are stored as camelCase strings (same `JsonStringEnumConverter(JsonNamingPolicy.CamelCase)` as the API).
- `title`: nullable; set in the turn-start batch from the first user message (first 60 chars) when still `null`.
- `state` (session): `active` | `deleting`; always written (`active` on create). A `deleting` session is invisible:
  `GetAsync` returns null, `ListAsync` omits it, `TryStartTurnAsync` returns `NotFound`; only `DeleteAsync` sees it
  (and resumes the deletion, 1.5).
- `status` (message): `complete` | `cancelled` | `failed` (see api-contract 4.5). User messages are always `complete`.
- `kind`: `answer` | `clarification` (assistant only; readers default a missing value to `answer`).
- `workflowMetadata`: free-form JSON object produced by the workflow (`TurnCompleted.Metadata`). The API stores it
  and never interprets it. Keeps the store workflow-agnostic.
- `activeTurn`: turn lock; `turnId` = `TurnLease.LeaseId`. Set with an ETag-conditional replace at turn start (or at
  DELETE start, with a fresh delete lease id); cleared at turn end only if `activeTurn.turnId` equals the lease's id,
  otherwise the store returns `LeaseLost` and writes nothing. A lock older than `Chat:TurnTimeout + 60 s` is stale and
  may be taken over (by a new turn or a DELETE).
- `schemaVersion`: bump on breaking document changes; readers tolerate unknown fields.
- DTO mappers never expose `userId`, `ttl`, `activeTurn`, `state`, `workflowMetadata`, `schemaVersion` or `type`
  (mapper tests assert it).

### 1.3 TTL / retention

Container `defaultTtl = -1` (TTL enabled, no default expiry). Each document gets `ttl` = `Chat:RetentionDays * 86400`
when `Chat:RetentionDays` is set; no `ttl` field (keep forever) when unset. The session doc is replaced on every turn,
which restarts its TTL, so a session expires N days after its last activity; each message expires N days after creation.
Retention per environment is an open decision (D7 in /docs/plan.md).

### 1.4 Indexing policy

```json
{
  "indexingMode": "consistent",
  "automatic": true,
  "includedPaths": [
    { "path": "/userId/?" }, { "path": "/id/?" }, { "path": "/type/?" }, { "path": "/sessionId/?" }, { "path": "/state/?" },
    { "path": "/status/?" }, { "path": "/createdAt/?" }, { "path": "/updatedAt/?" }
  ],
  "excludedPaths": [ { "path": "/*" } ],
  "compositeIndexes": [
    [ { "path": "/userId", "order": "ascending" }, { "path": "/type", "order": "ascending" }, { "path": "/state", "order": "ascending" }, { "path": "/updatedAt", "order": "descending" } ],
    [ { "path": "/userId", "order": "ascending" }, { "path": "/type", "order": "ascending" }, { "path": "/sessionId", "order": "ascending" }, { "path": "/createdAt", "order": "descending" } ]
  ]
}
```
`content`, `citations`, `workflowMetadata` are not indexed (write cost, no queries on them).

### 1.5 Query patterns (all pass `PartitionKey(userId)` in request options AND filter on `userId`)

| Use | Operation |
|---|---|
| Get session | `ReadItemAsync<SessionDoc>(sessionId, new PartitionKey(userId))`; not found if missing, `type != "session"` or `state = "deleting"` |
| List sessions | `SELECT c.id, c.workflowId, c.title, c.createdAt, c.updatedAt, c.messageCount FROM c WHERE c.userId = @userId AND c.type = 'session' AND c.state = 'active' ORDER BY c.updatedAt DESC` (`MaxItemCount = pageSize`, continuation token) |
| Messages for display | `SELECT TOP 101 * FROM c WHERE c.userId = @userId AND c.type = 'message' AND c.sessionId = @sessionId ORDER BY c.createdAt DESC` (101 to compute `hasMoreMessages`; reverse in memory) |
| History for agents | same as display with `TOP @historyMaxMessages` and `AND c.id != @currentUserMessageId`, all statuses (pairing filter in memory, 1.6) |
| Start turn | Read session; `NotFound` if missing or `deleting`; `Busy` if a non-stale `activeTurn` exists. Else transactional batch: create user message + replace session (ETag condition; set `activeTurn`, `updatedAt`, `messageCount`, `title` if null). ETag conflict -> `Busy` |
| End turn | Read session. Missing -> `SessionGone`; `activeTurn.turnId != lease.LeaseId` (taken over, incl. by a DELETE) -> `LeaseLost`. Both: nothing written, no exception. Else transactional batch: create assistant message (if any) + replace session with ETag condition (clear `activeTurn`, `updatedAt`); ETag conflict -> re-read once and re-evaluate |
| Delete session | 1. Read session; `NotFound` if missing. 2. If `state = active`: `Busy` if a non-stale `activeTurn`; else ETag-conditional replace setting `state = deleting` and `activeTurn = {turnId: <new delete lease id>, startedAt}` (ETag conflict -> `Busy`). If already `deleting`, skip to 3 (resume). 3. Query message ids for `(userId, sessionId)` and delete them in transactional batches of up to 100 (404 per item ignored). 4. Delete the session document. Return `Deleted` |

A crash between steps 2 and 4 leaves the session `deleting` (invisible); a repeated DELETE resumes it, and TTL (if set)
eventually removes the leftovers. There is no query without `userId`. qa-reviewer treats any
`Container.GetItemQueryIterator` without a partition key as a blocker.

Isolation tests (backend-dotnet, Phase 1): an in-memory store (used by Api.Tests) that enforces `userId` the same way,
plus Cosmos emulator integration tests (skipped when the emulator is not reachable) proving user B cannot read, list,
delete or lock user A's sessions/messages.

### 1.6 History loading for agents

The chat endpoint loads history once per turn, after the turn started, and passes it to the workflow
(`WorkflowRunRequest.History`, see ADR 0002). Rule: the current user message is excluded by id (it is passed separately
as `WorkflowRunRequest.UserMessage`). Steps 1-3 run in the store (`IMessageStore.GetHistoryAsync`); steps 4-5 run in
the workflow-agnostic `ChatHistoryBuilder` in the Api project (the store never trims by tokens, workflows never trim).
1. Load the last `Chat:HistoryMaxMessages` (default 20) messages of the session, excluding the current user message, oldest first.
2. Pairing filter: keep an assistant message only if `status = complete`; keep a user message only if the next message
   is a kept assistant message. Orphaned user messages (failed turn, cancelled turn, cancelled before any token) are
   therefore shown in the UI but never sent to agents.
3. Strip citation markers (`\[S\d+\]`) from assistant text: refs are per turn and would collide with new refs.
4. Trim oldest-first (in user/assistant pairs) to `Chat:HistoryMaxTokens` (default 6000; estimate = chars / 4) so the
   current user message always fits.
5. Map to `HistoryMessage` (role, content, `agentName`, `kind`, `workflowMetadata`, `createdAt`).

Agents run with this explicit history; no server-side (Foundry/Responses) conversation state is used, so replicas
stay stateless and the Cosmos record is the only history.

## 2. Azure AI Search

### 2.1 Index `support-knowledge` (`Search:IndexName`)

One index for all domains; specialists filter by `domain`.

| Field | Type | key | searchable | filterable | facetable | sortable | retrievable | Notes |
|---|---|---|---|---|---|---|---|---|
| `id` | `Edm.String` | yes | no | yes | no | no | yes | Chunk id: base64url(SHA-256(sourcePath + "#" + chunkIndex)), deterministic so re-ingestion upserts |
| `domain` | `Edm.String` | | no | yes | yes | no | yes | Open set of lowercase values (`^[a-z0-9-]+$`); current values in 2.5 |
| `title` | `Edm.String` | | yes (`en.microsoft`) | no | no | no | yes | Document title (+ section heading) |
| `content` | `Edm.String` | | yes (`en.microsoft`) | no | no | no | yes | Chunk text, ~500-800 tokens, ~10% overlap |
| `contentVector` | `Collection(Edm.Single)` | | yes (vector) | no | no | no | no (`stored: false`) | Dimensions = embedding model dims (1536 for text-embedding-3-small; D8). Profile `vector-profile` |
| `sourceUrl` | `Edm.String` | | no | no | no | no | yes | Link to the source; may be empty. Only absolute `http(s)` values are passed on (ADR 0002 section 9) |

Analyzer language is an assumption (English content); change before first ingestion if the knowledge base is not English.

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
The integrated vectorizer embeds the query inside Search (the backend sends text via a vectorizable text query), so the
backend needs no embedding client. The Search service's managed identity needs `Cognitive Services OpenAI User` on
the model resource (azure-infra). Ingestion computes document embeddings itself (TokenCredential) or uses the same
vectorizer through an indexer skillset; ingestion owner decides in Phase 2.

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

Separate from the web app (console app run locally or as a Container Apps Job). Source: `knowledge/<domain>/*.md`
(or another source the user names, D9). Upsert by deterministic `id`; delete chunks whose source disappeared.
Keys: in Key mode the API's `Search:Key` is a QUERY key (read-only), created by `infra/scripts/ensure-search-query-key.sh`
(run by the user, plan.md section 3) and stored in Key Vault as `search-query-key`. The admin key is used only by
ingestion / index creation (`Ingestion:SearchAdminKey`, user-secrets or Key Vault) and is never given to the API.
RBAC: only the ingestion identity gets `Search Index Data Contributor` (+ `Search Service Contributor` for index
management); the backend gets `Search Index Data Reader`.
