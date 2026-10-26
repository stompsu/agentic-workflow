# Delivery plan

Phase 0 (this) produced: /docs/api-contract.md, /docs/data-model.md, /docs/auth-setup.md,
/docs/adr/0001-identity-flow.md, /docs/adr/0002-workflow-abstraction.md, /docs/adr/0003-azure-auth-mode.md.
Revised 2026-10-25 with user decisions (section 6) and qa-reviewer findings (second review: S1-S6 + nits applied).
Revised again 2026-10-25: the user accepted all recommendations for D4, D6-D10 and Q5 (section 6); new
/docs/adr/0004-retention-and-user-data-deletion.md; follow-up work in Phase 5 (section 3).
Contract changes go through architect first. qa-reviewer gates the end of every phase.

## 1. Repo layout

```
src/
  backend/
    SupportChat.slnx                    # single solution: `dotnet build/test src/backend`
    Dockerfile                          # multi-stage, non-root, image support-backend
    Directory.Packages.props            # central package versions (exact pins, see section 4)
    src/
      SupportChat.Core/                 # contracts only: workflows, events, stores + records, ICurrentUser, IKnowledgeSearch,
                                        # IChatClientFactory, IPromptProvider, options
      SupportChat.Infrastructure/       # Cosmos stores, Search, IAzureCredentialProvider, IAzureClientFactory,
                                        # ChatClientFactory (implements Core IChatClientFactory; lazy AIProjectClient)
      SupportChat.Agents/               # workflow registry, EmbeddedPromptProvider (implements IPromptProvider),
                                        # Knowledge/ (search_knowledge, CitationCollector),
                                        # Workflows/SupportTicket (+ Prompts/*.md embedded), Workflows/Echo (config-gated)
      SupportChat.Api/                  # minimal APIs, auth, SSE writer, ChatHistoryBuilder (token trimming), ProblemDetails,
                                        # health, startup validation, --smoke-check
      SupportChat.Ingestion/            # console app: chunk + embed + upsert to Search (Phase 2)
    tests/
      SupportChat.Api.Tests/            # WebApplicationFactory, fake auth, in-memory stores, throwaway workflow, SSE golden files
      SupportChat.Agents.Tests/         # fake IChatClient: classification, threshold, routing, citations, injection
      SupportChat.Infrastructure.Tests/ # factory paths, auth-mode validation, query construction, Cosmos emulator isolation (optional)
  frontend/
    Dockerfile                          # build stage + nginx, image support-frontend
    nginx/default.conf.template         # /api proxy (proxy_buffering off etc., api-contract section 7), /data-deletion.html
    docker-entrypoint.d/                # writes /config.json and fills /data-deletion.html from env vars at start
    public/data-deletion.html           # Facebook data deletion instructions page with placeholders (auth-setup 4.1)
    src/ ...                            # Vite + React + TS strict (Phase 3)
    package.json                        # scripts: dev, lint, typecheck, test (vitest run), build, e2e
knowledge/{policy,claims,health}/       # source docs for ingestion (decided D9: repo folder, English)
infra/
  main.bicep, modules/*.bicep
  params/{dev,test,prod}.bicepparam
  scripts/ensure-search-query-key.sh    # post-deploy, run by the user (S4, section 3)
  README.md                             # deploy order, script usage
.github/                                # devops-pipelines only
docs/
```
Dependency direction: Api -> Agents -> Core; Api -> Infrastructure -> Core; Agents never references Infrastructure.
`IChatClientFactory`, `IPromptProvider` and `IKnowledgeSearch` are declared in Core. Implementations:
`ChatClientFactory` and the Search-backed `IKnowledgeSearch` in Infrastructure, `EmbeddedPromptProvider` in Agents.

## 2. Interfaces between components

| From -> To | Interface | Defined in |
|---|---|---|
| SPA -> API | REST + SSE, rendering rules | /docs/api-contract.md (section 8 for rendering, 2.8 for delete my data) |
| SPA -> Entra | MSAL config from `/config.json` | /docs/auth-setup.md section 7 |
| Facebook reviewers / users -> frontend | static `/data-deletion.html` | /docs/auth-setup.md section 4.1 |
| API endpoints -> workflows | `IWorkflowRegistry`, `IWorkflowDefinition.RunAsync` -> `IAsyncEnumerable<ChatStreamEvent>` (v1 event protocol) | ADR 0002 sections 2-3 |
| API -> user identity | `ICurrentUser.UserId` (= `oid`) | ADR 0001 |
| API -> Cosmos | `ISessionStore`, `IMessageStore` (every method starts with `string userId`) | below + data-model 1.3, 1.5, 1.7 |
| Agents -> models | `IChatClientFactory.Create(deploymentName)` (Core; impl Infrastructure) | ADR 0002 section 4 |
| Agents -> knowledge | `IKnowledgeSearch.SearchAsync(domain, query, top, ct)` (Core; impl Infrastructure) | ADR 0002 section 8 |
| Agents -> prompts | `IPromptProvider.Get(path)` (Core; impl Agents) | ADR 0002 section 7 |
| Infrastructure -> Azure | `IAzureCredentialProvider`, `IAzureClientFactory` | ADR 0003 |
| infra -> pipelines | Bicep outputs: resource group, ACR name, backend app, frontend app, MI smoke job name; GitHub env vars `AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID, AZURE_RESOURCE_GROUP, ACR_NAME, BACKEND_APP, FRONTEND_APP` (+ `MI_SMOKE_JOB` in test) | github-actions-pipelines skill (templates/) |
| infra -> backend / frontend | env vars in section 5 | this file |

Store contracts and records (Core, namespace `SupportChat.Core.Storage`):
```csharp
// Turn lock held by one in-flight turn. LeaseId is the turn id (= WorkflowRunRequest.TurnId), stored in the session
// document as activeTurn.turnId. AssistantMessageId is generated at turn start and used for done/failed/cancelled.
public sealed record TurnLease(string SessionId, string LeaseId, string AssistantMessageId, DateTimeOffset StartedAt);

// No userId, ttl, activeTurn, state, type, schemaVersion: callers already know the user; DTOs cannot leak what records lack.
public sealed record SessionRecord(string Id, string WorkflowId, string? Title,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount);

public enum MessageStatus { Complete, Cancelled, Failed }                    // camelCase in JSON
public sealed record CitationRecord(string Ref, string Title, string DocId, string? Url);
public sealed record MessageRecord(
    string Id, string SessionId, ChatRole Role, string Content, DateTimeOffset CreatedAt,
    MessageStatus Status,
    MessageKind? Kind,                       // assistant only
    string? AgentName,                       // assistant only
    IReadOnlyList<CitationRecord> Citations, // assistant only; empty otherwise
    string? ErrorCode,                       // only when Status = Failed
    JsonObject? WorkflowMetadata);           // stored, never returned by the API

public sealed record Page<T>(IReadOnlyList<T> Items, string? ContinuationToken);  // null token = last page

public abstract record StartTurnResult
{
    public sealed record Started(TurnLease Lease) : StartTurnResult;
    public sealed record NotFound : StartTurnResult;   // missing, foreign, or state = deleting -> 404 not_found
    public sealed record Busy : StartTurnResult;       // -> 409 turn_in_progress
}
public enum CompleteTurnResult
{
    Completed,
    SessionGone,   // session document missing: nothing written, no exception
    LeaseLost,     // session exists but activeTurn.turnId != lease.LeaseId (stale lock taken over by a turn or a DELETE,
                   // or any lock taken over by DeleteAllUserDataAsync): nothing written (no message, lock untouched),
                   // warning logged (sessionId, leaseId only)
}
public enum DeleteSessionResult { Deleted, NotFound, Busy } // Busy -> 409 turn_in_progress
public enum DeleteUserDataResult { Completed, Incomplete }  // Completed -> 204; Incomplete -> 503 deletion_incomplete

public interface ISessionStore
{
    Task<SessionRecord> CreateAsync(string userId, string workflowId, string? title, CancellationToken ct);
    Task<SessionRecord?> GetAsync(string userId, string sessionId, CancellationToken ct);           // null for deleting
    Task<Page<SessionRecord>> ListAsync(string userId, int pageSize, string? continuationToken, CancellationToken ct); // excludes deleting
    // Lock-then-delete sequence in data-model 1.5. Resumes a session already in state deleting.
    Task<DeleteSessionResult> DeleteAsync(string userId, string sessionId, CancellationToken ct);
    // Delete my data (data-model 1.7): marks every session of userId deleting (taking over live AND stale locks), then
    // deletes all message and session documents of userId. Idempotent, resumable, no job state.
    // Returns Incomplete when `budget` has elapsed (TimeProvider; checked before each replace and each chunk).
    // ct = RequestAborted: throws OperationCanceledException on abort; nothing is rolled back.
    Task<DeleteUserDataResult> DeleteAllUserDataAsync(string userId, TimeSpan budget, CancellationToken ct);
    // Persists the user message, sets the lock, sets title from the message if null (one batch).
    Task<StartTurnResult> TryStartTurnAsync(string userId, string sessionId, MessageRecord userMessage, CancellationToken ct);
    // Writes the assistant message (if any) and releases the lock. Called from finally with CancellationToken.None
    // + Chat:PersistTimeout on failure/cancel paths.
    Task<CompleteTurnResult> CompleteTurnAsync(string userId, TurnLease lease, MessageRecord? assistantMessage, CancellationToken ct);
}
public interface IMessageStore
{
    Task<(IReadOnlyList<MessageRecord> Items, bool HasMore)> GetRecentAsync(string userId, string sessionId, int max, CancellationToken ct);
    // Excludes excludeMessageId (the current user message); applies pairing + marker stripping (data-model 1.6 steps 1-3).
    // Does NOT trim by tokens: ChatHistoryBuilder in Api (workflow-agnostic) does step 4 and maps to HistoryMessage.
    Task<IReadOnlyList<MessageRecord>> GetHistoryAsync(string userId, string sessionId, string excludeMessageId, int maxMessages, CancellationToken ct);
}
```
Every write of a document sets `ttl` per data-model 1.3 (messages once at create; session documents on every write);
`ttl` is omitted, never `null`, when `Chat:RetentionDays` is unset.

Turn sequence (chat endpoint): validate -> `GetAsync` (404) -> registry `TryGet` (409 `workflow_unavailable`) ->
`TryStartTurnAsync` (404/409) -> `GetHistoryAsync` + `ChatHistoryBuilder` (token trim) -> send `session` -> run workflow
-> persist -> `done`/`error`; `finally`: ensure `CompleteTurnAsync` ran (lock released).
A failure after `TryStartTurnAsync` succeeded but before `session` was written (history load, builder) returns 500
ProblemDetails `internal_error`; the `finally` releases the lock (`CompleteTurnAsync` with no message); the user
message stays as an orphan (shown in UI, never sent to agents).
`CompleteTurnResult.SessionGone` / `LeaseLost`: if the stream is still open, send `error internal_error` instead of `done`.

Delete-my-data endpoint (`DELETE /api/me/data`): `ICurrentUser.UserId` -> `DeleteAllUserDataAsync(userId,
Chat:DeleteUserDataBudget, RequestAborted)` -> `Completed`: 204 no body; `Incomplete`: 503 ProblemDetails
`deletion_incomplete` + `Retry-After: 1`; exception: 500 `internal_error`. No body parsing, no rate limiter.

## 3. Phases and tasks

The phase split below follows the user's fixed brief (2026-10-25); task content is unchanged, only moved between phases.

### Phase 1: backend foundation + all Bicep (auth, sessions, stores, Azure clients; no chat endpoint, no SPA, no pipelines)
- azure-infra: Bicep (all modules) for dev: Log Analytics + App Insights, ACA environment, shared ACR, frontend app
  (external ingress, nginx proxy built in Phase 3) + backend app (internal ingress), Cosmos (serverless, container per
  data-model 1.x incl. indexing policy + `defaultTtl -1`), AI Search (semantic enabled, index created by
  ingestion/script, not Bicep), Key Vault (secrets: Cosmos key written by Bicep via `listKeys()`; Search QUERY key
  written by the script below; ACA secret references), user-assigned MI with AcrPull + "Key Vault Secrets User" on the
  vault (needed for the ACA Key Vault references in dev/test). Foundry: optional param `foundryProjectResourceId`
  (default empty); the "Azure AI User" role assignment is created only when it is set (D5 still holds once a project
  exists; D8). Probes use `/healthz` and `/readyz` on the backend container port. Outputs for pipelines.
  `infra/params/{dev,test,prod}.bicepparam`: prod: `disableLocalAuth` on Cosmos and Search, Cosmos SQL role
  assignment, Search Index Data Reader, min replicas 1, HTTP concurrency scale rule, autoscale Cosmos. Test: same data
  roles for the UAMI, Search `aadOrApiKey`. Document pipeline identity roles.
  Search query key (S4): Bicep cannot create query keys. Decision: azure-infra (owner) writes
  `infra/scripts/ensure-search-query-key.sh` (az CLI, idempotent: create query key `supportchat-api` if absent, write it
  to Key Vault secret `search-query-key` without printing it). The USER runs it once per dev/test environment after
  the first infra deploy (documented in `infra/README.md` with the required roles and the first-deploy order: the
  secret must exist before the backend revision references it). Not run by pipelines in v1; moving it into the deploy
  job later needs extra pipeline roles and the user's approval. Prod needs no query key (MI only).
- backend-dotnet: solution layout; Core contracts from ADR 0002 + section 2 (incl. `IWorkflowRegistry`,
  `IWorkflowDefinition`, `ChatStreamEvent`, `IChatClientFactory`, `IPromptProvider` declarations, the store
  interfaces and the records above); Microsoft.Identity.Web auth + `ChatUser` policy + ProblemDetails
  challenge/forbidden (ADR 0001) + `ICurrentUser`; ADR 0003 credential provider, client factory, startup validation
  (case-insensitive, incl. echo gating and conditional Foundry endpoint rule); `ChatClientFactory` with a lazily
  constructed `AIProjectClient` (Foundry placeholder approach, ADR 0003 section 4); Cosmos stores with result types
  incl. `LeaseLost` and lock-then-delete; session endpoints: `GET /api/workflows`, `POST /api/sessions` (workflowId
  validated against the registry), `GET /api/sessions`, `GET /api/sessions/{id}`, `DELETE /api/sessions/{id}` incl.
  409; ProblemDetails; health (`/healthz`, `/readyz`); Dockerfile.
  Minimal registry: a Core-level `IWorkflowRegistry` implementation in Api or Agents that only lists registered
  definitions, so `GET /api/workflows` and `POST /api/sessions` workflowId validation work. It may be empty; the final
  `WorkflowRegistry` / `AddWorkflow<T>` replaces it in Phase 2 without endpoint changes.
  Rule from Phase 1: no message content, prompts, tokens or emails in logs.
  Tests: fake JWT handler; 404 for foreign session identical except `traceId`; in-memory store enforcing `userId` +
  Cosmos emulator isolation tests (skipped when the emulator is absent: user B cannot read/list/delete/lock user A
  data); store-level `LeaseLost` writes nothing; throwaway workflow registered in Api.Tests proving extensibility
  (listed by `GET /api/workflows`, accepted by `POST /api/sessions`); DTO mappers never expose
  `userId`/`ttl`/`activeTurn`/`state`; factory paths and auth-mode validation.
  Phase 1 is exercised with tests and local runs (curl/REST client against the local backend).
- user (manual): /docs/auth-setup.md for the nonprod tenant (dev registrations; Q1 real-token check),
  `infra/scripts/ensure-search-query-key.sh` for dev; Facebook Live review started early (lead time).
- qa-reviewer: backend contract compliance, scoping and isolation tests, keys rule, log content rule, tests.

### Phase 2: workflows + SSE chat endpoint + knowledge
- agent-workflows: `WorkflowRegistry`, `AddWorkflow<T>` (final form, replaces the Phase 1 minimal registry),
  `EmbeddedPromptProvider`, `EchoWorkflow` registered only when `Workflows:Echo:Enabled = true` (default false;
  allowed in Development/Dev/Test, startup fails if enabled in Production; ADR 0002 section 10). It streams the input
  back word by word and proves zero-change addability. Fake `IChatClient` test helpers.
- agent-workflows: SupportTicket per ADR 0002 section 6 (plain C# orchestration, D2): structured classification,
  threshold, clarification, routing, specialists; prompt files v1; shared `SupportChat.Agents.Knowledge`
  (`search_knowledge` + `CitationCollector`) with `&<>` encoding and http(s)-only URLs; history handling; domain
  constants; rationale logging rule; verify `ChatClientFactory` against the pinned Foundry prerelease (D1) and
  `store=false` (D3); tests: routing per category, low confidence -> clarification, max consecutive clarifications,
  invalid JSON retry, citations resolution and fabricated-ref drop, injection fixtures (`</source>`, `javascript:`),
  cancellation, one segment per turn.
- backend-dotnet: SSE message endpoint `POST /api/sessions/{id}/messages` (turn sequence in section 2): SSE writer
  (framing, flush, heartbeat, abort, timeout, persistence rules 4.5, no compression) using `TimeProvider` and
  `TimeSpan` options; `ChatHistoryBuilder`; server-side error code -> message table; event-protocol enforcement and
  `OperationCanceledException` mapping (ADR 0002 section 3); rate limiter on POST messages partitioned by `oid` (per
  replica).
  Tests: SSE framing golden files; `FakeTimeProvider` for heartbeat/timeout; history excludes the current message and
  orphans; cancelled partial persisted after abort; stop-then-send releases the lock; `LeaseLost` yields
  `error internal_error`; deleting session -> POST messages 404; failure before `session` -> 500 with lock released;
  non-abort `OperationCanceledException` -> `internal_error`.
- backend-dotnet: `IKnowledgeSearch` over `SearchClient` (hybrid + semantic + domain filter, data-model 2.4, domain
  regex check); `SupportChat.Ingestion` console app skeleton (chunking, deterministic ids, upsert/delete, admin key or
  Contributor RBAC only here); index definition as code or JSON. Set `Foundry:ProjectEndpoint` (now required by
  validation because SupportTicket is registered).
- azure-infra: set `foundryProjectResourceId` (role assignment), Search MI -> "Cognitive Services OpenAI User" on the
  model resource (integrated vectorizer); embedding model deployment reference; ingestion identity with
  "Search Index Data Contributor".
- qa-reviewer: prompt-injection handling, sanitization, citation validation, no real model calls in tests; SSE
  contract compliance.

### Phase 3: SPA
- frontend-react: `Dockerfile` (build stage + nginx stage), `nginx/default.conf.template` (proxies only `/api/`,
  api-contract section 7), entrypoint writing `/config.json`. Required because the backend has internal ingress and
  the Phase 4 pipeline smoke test hits `https://<frontend>/api/healthz`.
- frontend-react: Vite + React + TS strict; Vite dev server proxy for `/api` to the local backend (same-origin, no
  CORS); runtime `config.json`; MSAL login page with provider buttons from config; token attach + 401 handling;
  React Query for workflows/sessions; workflow dropdown from `GET /api/workflows`; history sidebar (`title: null`
  placeholder); chat view; SSE parser (fetch + ReadableStream, ignores `:` comments, all 6 events, single segment),
  stop button (AbortController) + one retry on 409 `turn_in_progress` after 1 s; 409 `workflow_unavailable` (session
  read-only with a "This assistant is no longer available" notice, user can start a new chat); 429 `rate_limited`
  honoring `Retry-After` (disable send until it elapses); non-ProblemDetails error bodies mapped by status (nginx 413,
  5xx); rendering per api-contract section 8 (markdown, HTML and images disabled, http(s) links with
  `target="_blank" rel="noopener noreferrer"`, `[Sn]` linked to citations, plain-text titles), agent name badge;
  `aria-live`; Vitest tests incl. SSE parser against the contract examples; Playwright happy path with mocked auth +
  mocked SSE backend.
- qa-reviewer: client contract compliance and rendering/sanitization rules (api-contract section 8).

### Phase 4: pipelines, test environment, production readiness
- devops-pipelines: `.github` workflows from the templates in `.claude/skills/github-actions-pipelines/templates/`,
  linted with actionlint. ci.yml first; then build-push + deploy dev; deploy test/prod with approvals, same image tag
  promotion, smoke test `/api/healthz`; after test deploy run the MI smoke job (`--smoke-check`,
  `Azure__AuthMode=ManagedIdentity`, no keys) and block prod on failure (prod gate). `/docs/cicd-setup.md` + OIDC
  setup script (written, not run). Repo `README.md`. `dependabot.yml`.
- azure-infra: test-env MI smoke ACA Job (ADR 0003 section 1a); ingestion ACA Job with sample documents for the three
  domains (`knowledge/{policy,claims,health}/`).
- backend-dotnet: OpenTelemetry (traces per turn: classification, retrieval, model), load test of SSE through
  nginx + ACA ingress (heartbeat, 240 s ingress limit).
- agent-workflows + qa-reviewer: evaluation set (labelled questions per category) to tune `ConfidenceThreshold`.
- user (manual): /docs/cicd-setup.md commands; auth-setup for test registrations; prod tenant setup;
  `ensure-search-query-key.sh` for test.
- Prod deploy behind approval; prod auth smoke test with real Google + Facebook accounts (manual).
- Runbooks: rollback (redeploy previous tag), key-free prod verification, retention/deletion procedure
  (retention: data-model 1.3; self-service deletion: api-contract 2.8; account deletion: auth-setup section 9).
- qa-reviewer final review: blockers closed.

### Phase 5: decisions D4-D13 follow-up (2026-10-25; contract already updated, implement from it)
Order: backend-dotnet and frontend-react in parallel (the frontend codes against api-contract 2.8 with a mocked API),
then azure-infra (new params), then qa-reviewer. No pipeline changes.
- backend-dotnet:
  - Options: `Chat:RetentionDays` (int?, 1..3650) and `Chat:DeleteUserDataBudget` (TimeSpan, default `00:01:00`);
    startup validation rules 9-10 (ADR 0003 section 4). Test hosts that run in a non-`Development` environment must
    set `Chat:RetentionDays`.
  - Cosmos documents: `ttl` serialized with "ignore when null" (never `"ttl": null`); `ttl` written on every session
    write including both delete marks; messages get `ttl` only at create (data-model 1.3 R1-R2). Verify the existing
    `CosmosSessionStore` paths against this.
  - Chunk delete rule (data-model 1.5): on a transactional batch failing with 404, redo the chunk as point deletes
    ignoring 404. Applies to `DeleteAsync` and `DeleteAllUserDataAsync`.
  - `ISessionStore.DeleteAllUserDataAsync` (Cosmos + in-memory store), `DeleteUserDataResult`, data-model 1.7.
  - Endpoint `DELETE /api/me/data` per api-contract 2.8 and section 2 above (204 / 503 `deletion_incomplete` +
    `Retry-After: 1` / 500), `ChatUser` policy, no rate limiter, log counts only.
  - History pairing rule: drop an assistant message whose preceding message is not a user message (data-model 1.6
    step 2).
  - Tests: 204 for a user with no data and for a repeated call; user B's call leaves user A's data intact (in-memory
    and emulator); orphan messages deleted; session in `deleting` resumed; live turn lock taken over -> in-flight
    turn `CompleteTurnAsync` returns `LeaseLost`, writes nothing, stream ends `error internal_error`; POST messages to a
    marked session -> 404; budget exhaustion with `FakeTimeProvider` -> 503 + `Retry-After: 1`, a second call
    completes -> 204; batch-404 fallback; `ttl` absent when unset, `RetentionDays * 86400` when set, refreshed on
    session writes, unchanged on messages; history drops a leading assistant message; validation matrix rules 9-10.
- agent-workflows: no functional change. Confirm SupportTicket reads `Workflows:SupportTicket:Agents:Orchestrator:Deployment`
  for classification/clarification and the three specialist keys for answers (D8: orchestrator on a cheaper
  deployment); no deployment names in code.
- frontend-react:
  - "Delete my data" action in the account menu with a confirmation dialog; flow per api-contract 2.8 (abort the
    in-flight stream first; 503 `deletion_incomplete` -> retry after `Retry-After`, max 5 attempts; 204 -> clear React
    Query cache, empty new-chat view, notice incl. `app.supportContact`; user stays signed in).
  - Config parser: optional string `app.supportContact` (rendered as plain text) and optional integer
    `app.retentionDays` (1..3650; sidebar note "Chats are kept for N days after last activity").
  - `public/data-deletion.html` with placeholders `__APP_TITLE__`, `__RETENTION_TEXT__`, `__SUPPORT_CONTACT_TEXT__`
    and the content in auth-setup 4.1; `10-write-config.sh` fills them in place (HTML-escaped, temp file + rename),
    validates `APP_RETENTION_DAYS` (`^[1-9][0-9]{0,3}$`) and `APP_SUPPORT_CONTACT` (max 200 chars), writes
    `app.supportContact` and `app.retentionDays` to `config.json` only when set; nginx `location = /data-deletion.html` (security headers,
    `Cache-Control: no-cache`).
  - Tests (Vitest): delete flow (confirm/cancel, 204, 503 retry and give-up, stream aborted first, cache cleared);
    config parsing of `supportContact`; a provider entry without `domainHint` calls `login()` without a hint.
    Entrypoint: a script test or Docker build check that no placeholder remains and that `<`/`&` in the env values are
    escaped. Bundle stays a single chunk (D12).
- azure-infra:
  - New Bicep params: `chatRetentionDays` (int; dev 30, test 30, prod 90 in the bicepparams, marked as user-changeable
    defaults) -> backend and MI smoke job `Chat__RetentionDays`, frontend `APP_RETENTION_DAYS`;
    `authProviders` (string; dev/test = the nonprod JSON in auth-setup section 7, prod empty = unset) ->
    `AUTH_PROVIDERS`; `supportContact` (string; prod `REPLACE_ME`, user input) -> `APP_SUPPORT_CONTACT`;
    `orchestratorDeployment` and `specialistDeployment` (strings, user input, defaults empty = env vars not set) ->
    `Workflows__SupportTicket__Agents__Orchestrator__Deployment` and the `Policy`, `Claims`, `HealthAssessments`
    variants.
  - `infra/README.md`: list of user-supplied inputs per environment (D8: `foundryProjectResourceId`,
    `foundryProjectEndpoint`, `modelResourceId`, `embeddingDeployment` = `text-embedding-3-small` deployment name, the
    two chat deployment names; R1: real resource names incl. a globally unique `acrName`) and "infra is deployed from
    the user's machine in v1" (D11; `setup-github-oidc.sh` without `--infra-roles`).
  - Cosmos unchanged (`defaultTtl -1` already set).
- user (manual): auth-setup 5.1 (nonprod email OTP), 5.2 (prod social only), 4.1 (Facebook Data deletion
  instructions URL), section 9 procedure known; supply D8 inputs and R1 names; review the retention defaults.
- qa-reviewer: api-contract 2.8 compliance (status codes, no 404/409, `Retry-After`, ProblemDetails), partition
  scoping of every new query (no query without partition key), no content/titles in delete logs, `ttl` never `null`,
  data-deletion page escaping and no placeholder left, prod params do not enable email sign-in.

## 4. Package versions (verified 2026-10-25; exact pins in Directory.Packages.props)

Microsoft.Agents.AI 1.26.0; Microsoft.Agents.AI.Foundry `[1.26.0-preview.261023.1]` with Azure.AI.Projects
`[3.0.0-beta.3]` pinned exactly (D1; `CentralPackageTransitivePinningEnabled` also pins Azure.AI.Extensions.OpenAI
3.0.0-beta.3 and Microsoft.Extensions.AI.OpenAI 10.10.1); Microsoft.Identity.Web 4.16.1, Microsoft.Azure.Cosmos 3.63.2,
Azure.Search.Documents 12.0.0, Azure.Identity 1.21.0, Microsoft.Extensions.TimeProvider.Testing (tests).
Microsoft.Agents.AI.Workflows is not referenced in v1 (D2). Frontend: current @azure/msal-browser / @azure/msal-react,
Vite 8 (requires Node 20.19+ or 22.12+).

## 5. Configuration keys

Backend (env var form uses `__`; config keys contain no hyphens, so every key is a valid ACA env var name).
Durations are `TimeSpan` strings (`hh:mm:ss`).

| Key | Example / default | Secret | Notes |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Dev` / `Test` / `Production` | no | ADR 0003, compared case-insensitively |
| `Azure:AuthMode` | `Key` / `ManagedIdentity` | no | fail-fast rules |
| `AZURE_CLIENT_ID` | UAMI client id | no | required outside Development |
| `AZURE_TOKEN_CREDENTIALS` | `ManagedIdentityCredential` | no | deployed envs |
| `AzureAd:Instance` | `https://<sub>.ciamlogin.com/` | no | D10: ciamlogin.com, no custom domain |
| `AzureAd:TenantId` | `<tid>` | no | nonprod tenant for dev/test, prod tenant for prod (D4) |
| `AzureAd:ClientId` | `<api-id>` | no | |
| `Auth:RequiredScopes` | `access_as_user` | no | |
| `Cosmos:Endpoint` | `https://<acct>.documents.azure.com:443/` | no | |
| `Cosmos:Key` | KV reference | yes | Key mode only; must be absent in Production |
| `Cosmos:DatabaseName` | `supportchat` | no | |
| `Cosmos:ContainerName` | `chat` | no | |
| `Search:Endpoint` | `https://<svc>.search.windows.net` | no | |
| `Search:Key` | KV reference (`search-query-key`) | yes | QUERY key; Key mode only; must be absent in Production |
| `Search:IndexName` | `support-knowledge` | no | |
| `Search:TopK` | `5` | no | |
| `Search:MinRerankerScore` | `1.5` | no | |
| `Foundry:ProjectEndpoint` | `https://<res>.services.ai.azure.com/api/projects/<project>` | no | TokenCredential only; required only when a non-echo workflow is registered (ADR 0003 rule 7); value supplied by the user (D8) |
| `Workflows:Echo:Enabled` | `false` | no | `true` allowed only in Development/Dev/Test (ADR 0003 rule 8) |
| `Workflows:SupportTicket:ConfidenceThreshold` | `0.7` | no | |
| `Workflows:SupportTicket:MaxConsecutiveClarifications` | `2` | no | |
| `Workflows:SupportTicket:MaxToolCallsPerTurn` | `3` | no | 1..10; `search_knowledge` calls per specialist turn. At the cap the model gets one final call with no tool execution; if it still produces no text the turn fails with `workflow_error`. |
| `Workflows:SupportTicket:Agents:Orchestrator:Deployment` | e.g. `gpt-4.1-mini` | no | D8: a cheaper chat deployment than the specialists; name supplied by the user (Bicep `orchestratorDeployment`) |
| `Workflows:SupportTicket:Agents:Policy:Deployment` | e.g. `gpt-4.1` | no | D8: name supplied by the user (Bicep `specialistDeployment`) |
| `Workflows:SupportTicket:Agents:Claims:Deployment` | e.g. `gpt-4.1` | no | same |
| `Workflows:SupportTicket:Agents:HealthAssessments:Deployment` | e.g. `gpt-4.1` | no | same |
| `Chat:HistoryMaxMessages` | `20` | no | |
| `Chat:HistoryMaxTokens` | `6000` | no | applied by `ChatHistoryBuilder` (Api), not the store |
| `Chat:MaxMessageChars` | `4000` | no | |
| `Chat:TurnTimeout` | `00:03:00` | no | < 240 s ingress limit (Q5 decided) |
| `Chat:HeartbeatInterval` | `00:00:15` | no | |
| `Chat:PersistTimeout` | `00:00:05` | no | failure/cancel persistence, lock release |
| `Chat:TurnsPerMinute` | `10` | no | per `oid` per replica |
| `Chat:RetentionDays` | dev `30`, test `30`, prod `90` (user-changeable defaults) | no | D7; 1..3650; required outside Development (ADR 0003 rule 9); data-model 1.3 |
| `Chat:DeleteUserDataBudget` | `00:01:00` | no | D7; `00:00:05`..`00:03:00` (ADR 0003 rule 10); api-contract 2.8 |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | from infra | treat as config | |

Ingestion adds `Ingestion:EmbeddingDeployment` (deployment of `text-embedding-3-small`, 1536 dims, D8),
`Ingestion:SourcePath` (repo `knowledge/`, D9), `Ingestion:ChunkTokens` (800), `Ingestion:ChunkOverlapTokens` (80),
`Ingestion:SearchAdminKey` (Key mode only, secret; never set on the API).

Frontend container env (none secret; defaults and formats in /docs/auth-setup.md section 7): required
`BACKEND_UPSTREAM` (backend internal FQDN, bare host, for nginx `proxy_pass` and `Host`), `AUTH_CLIENT_ID`,
`AUTH_AUTHORITY`, `AUTH_API_SCOPE`; optional `AUTH_PROVIDERS` (nonprod adds the email entry, D6),
`AUTH_KNOWN_AUTHORITIES`, `AUTH_REDIRECT_URI` (default `/redirect.html`), `API_BASE_URL` (default `/api`), `APP_TITLE`,
`APP_TAGLINE`, `APP_RETENTION_DAYS` (same value as `Chat:RetentionDays`), `APP_SUPPORT_CONTACT` (set in prod).

## 6. Decisions and open questions

### Decided (2026-10-25, user)
D1. Accept the prerelease: Microsoft.Agents.AI.Foundry 1.26.0-preview.261023.1 + Azure.AI.Projects 3.0.0-beta.3
    (pulled by it), pinned exactly; upgrades are deliberate. Only `ChatClientFactory` depends on it. (ADR 0002 section 4)
D2. Plain C# orchestration over `ChatClientAgent`s behind `IWorkflowDefinition` for SupportTicket. MAF Workflows remains
    a possible future engine behind the same interface. (ADR 0002 section 5)
D5. Managed identity for Foundry in all deployed environments; no service-principal secret. `.claude/agents/azure-infra.md`
    updated accordingly. (ADR 0003) The role assignment is conditional on `foundryProjectResourceId` until the
    project exists (D8).
D3. Responses API storage is disabled (`store=false`) for every model call: history lives only in Cosmos, so nothing
    is retained in Foundry. Implemented once, in `ChatClientFactory` (ADR 0002 section 4); confirm in the first live
    smoke test.
D4. Two Entra External ID tenants: `nonprod` (dev + test, separate app registrations per environment) and `prod`.
    (auth-setup section 0, ADR 0001)
D6. Prod: social sign-in only (Google, Facebook). Nonprod: email one-time passcode enabled in addition, for test users;
    the nonprod SPA config adds a "Sign in with email" entry without `domainHint` (generic sign-in -> hosted chooser).
    No SPA code change. (auth-setup sections 5.1, 5.2, 7)
D7. Retention per environment via `Chat:RetentionDays`, defaults dev 30, test 30, prod 90 days (defaults the user can
    change), plus a self-service "delete my data" feature `DELETE /api/me/data` (synchronous 204, idempotent, no 409,
    503 `deletion_incomplete` on budget exhaustion). Session expires `RetentionDays` after last activity, each message
    `RetentionDays` after it was written. Facebook Live uses a Data Deletion Instructions URL `/data-deletion.html`.
    The Entra account is deleted manually by the tenant admin on request. (ADR 0004; data-model 1.3, 1.7;
    api-contract 2.8; auth-setup 4.1, 9)
    Known limitation: there is no admin purge of chat data in v1; for a person who cannot run "Delete my data",
    removal relies on retention (up to 90 days in prod). If a shorter guaranteed erasure time is required, an admin
    purge tool is a future task (user decision when needed).
D8. Embeddings: text-embedding-3-small at 1536 dimensions. The orchestrator runs on a cheaper chat model deployment
    than the specialists. Deployment names stay config (`Workflows:SupportTicket:Agents:*:Deployment`,
    `Ingestion:EmbeddingDeployment`). User inputs (per environment): Foundry project resource id
    (`foundryProjectResourceId`), project endpoint (`foundryProjectEndpoint`), model resource id (`modelResourceId`),
    and the deployment names. The Foundry project and model deployments are referenced, never created.
D9. Knowledge source for v1: the repo folder `knowledge/{policy,claims,health}/`; English content, analyzer
    `en.microsoft`. (data-model 2.1, 2.6)
D10. No custom domain for now; the frontend uses the Container App FQDN and auth stays on `<sub>.ciamlogin.com`.
    (auth-setup section 6)
Q5. Accepted: 180 s turn timeout under the 240 s Container Apps ingress limit. Workflow types that need longer would
    need a background job + polling pattern (a contract change). (api-contract 4.4, 7)
D11. Infra (Bicep) is deployed from the user's machine in v1; the OIDC setup script runs without `--infra-roles`, so
    pipelines deploy images only. (cicd-setup.md section 5)
D12. The frontend bundle is not code-split in v1 (single chunk; a Vite chunk-size warning is accepted).
D13. Playwright browsers may be installed locally (`npx playwright install chromium`) for `npm run e2e`.

Policy consequence recorded (qa finding 1): keys stay allowed in Test per CLAUDE.md, and a pre-prod MI smoke job in
test gates prod (ADR 0003 section 1a).

### Open
Q1. External ID issuer: community reports tokens with `iss = https://<tid>.ciamlogin.com/<tid>/v2.0`; Phase 1 must
    validate a real token through Microsoft.Identity.Web before relying on defaults. Frontend side: if the tenant
    uses the GUID host, add `<tid>.ciamlogin.com` to `AUTH_KNOWN_AUTHORITIES` (auth-setup sections 7-8).
Q6. `domain_hint=google|facebook` is documented on Learn, but community threads report inconsistent behavior; verify in
    the nonprod tenant (fallback: hosted provider chooser).
R1. Real resource names: the user supplies the actual names per environment, at least a globally unique `acrName`
    (the bicepparams contain a placeholder) and any naming-convention overrides (`namePrefix`, resource groups), plus
    `supportContact` for prod. Needed before the first infra deploy of each environment.

### Resolved
Q4. Local Node upgraded to v26.12.1 (2026-10-25); frontend Docker build stage and `engines` use Node 24 LTS.
Q2. Pipeline templates now exist in `.claude/skills/github-actions-pipelines/templates/`; devops-pipelines uses them.
Q3. actionlint is being installed locally; devops-pipelines lints with it.
(Q7, agent file names, fixed and removed.)
