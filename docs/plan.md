# Delivery plan

Phase 0 (this) produced: /docs/api-contract.md, /docs/data-model.md, /docs/auth-setup.md,
/docs/adr/0001-identity-flow.md, /docs/adr/0002-workflow-abstraction.md, /docs/adr/0003-azure-auth-mode.md.
Revised 2026-10-25 with user decisions (section 6) and qa-reviewer findings (second review: S1-S6 + nits applied).
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
    nginx/default.conf.template         # /api proxy (proxy_buffering off etc., api-contract section 7)
    docker-entrypoint.d/                # writes /config.json from env vars at start
    src/ ...                            # Vite + React + TS strict (Phase 3)
    package.json                        # scripts: dev, lint, typecheck, test (vitest run), build, e2e
knowledge/{policy,claims,health}/       # source docs for ingestion (or another source, D9)
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
| SPA -> API | REST + SSE, rendering rules | /docs/api-contract.md (section 8 for rendering) |
| SPA -> Entra | MSAL config from `/config.json` | /docs/auth-setup.md section 7 |
| API endpoints -> workflows | `IWorkflowRegistry`, `IWorkflowDefinition.RunAsync` -> `IAsyncEnumerable<ChatStreamEvent>` (v1 event protocol) | ADR 0002 sections 2-3 |
| API -> user identity | `ICurrentUser.UserId` (= `oid`) | ADR 0001 |
| API -> Cosmos | `ISessionStore`, `IMessageStore` (every method starts with `string userId`) | below + data-model 1.5 |
| Agents -> models | `IChatClientFactory.Create(deploymentName)` (Core; impl Infrastructure) | ADR 0002 section 4 |
| Agents -> knowledge | `IKnowledgeSearch.SearchAsync(domain, query, top, ct)` (Core; impl Infrastructure) | ADR 0002 section 8 |
| Agents -> prompts | `IPromptProvider.Get(path)` (Core; impl Agents) | ADR 0002 section 7 |
| Infrastructure -> Azure | `IAzureCredentialProvider`, `IAzureClientFactory` | ADR 0003 |
| infra -> pipelines | Bicep outputs: resource group, ACR name, backend app, frontend app, MI smoke job name; GitHub env vars `AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_SUBSCRIPTION_ID, AZURE_RESOURCE_GROUP, ACR_NAME, BACKEND_APP, FRONTEND_APP` (+ `MI_SMOKE_JOB` in test) | github-actions-pipelines skill (templates/) |
| infra -> backend | env vars in section 5 | this file |

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
    LeaseLost,     // session exists but activeTurn.turnId != lease.LeaseId (stale lock taken over by a turn or a DELETE):
                   // nothing written (no message, lock untouched), warning logged (sessionId, leaseId only)
}
public enum DeleteSessionResult { Deleted, NotFound, Busy } // Busy -> 409 turn_in_progress

public interface ISessionStore
{
    Task<SessionRecord> CreateAsync(string userId, string workflowId, string? title, CancellationToken ct);
    Task<SessionRecord?> GetAsync(string userId, string sessionId, CancellationToken ct);           // null for deleting
    Task<Page<SessionRecord>> ListAsync(string userId, int pageSize, string? continuationToken, CancellationToken ct); // excludes deleting
    // Lock-then-delete sequence in data-model 1.5. Resumes a session already in state deleting.
    Task<DeleteSessionResult> DeleteAsync(string userId, string sessionId, CancellationToken ct);
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
Turn sequence (chat endpoint): validate -> `GetAsync` (404) -> registry `TryGet` (409 `workflow_unavailable`) ->
`TryStartTurnAsync` (404/409) -> `GetHistoryAsync` + `ChatHistoryBuilder` (token trim) -> send `session` -> run workflow
-> persist -> `done`/`error`; `finally`: ensure `CompleteTurnAsync` ran (lock released).
A failure after `TryStartTurnAsync` succeeded but before `session` was written (history load, builder) returns 500
ProblemDetails `internal_error`; the `finally` releases the lock (`CompleteTurnAsync` with no message); the user
message stays as an orphan (shown in UI, never sent to agents).
`CompleteTurnResult.SessionGone` / `LeaseLost`: if the stream is still open, send `error internal_error` instead of `done`.

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
- Runbooks: rollback (redeploy previous tag), key-free prod verification, retention/deletion procedure.
- qa-reviewer final review: blockers closed.

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
| `AzureAd:Instance` | `https://<sub>.ciamlogin.com/` | no | |
| `AzureAd:TenantId` | `<tid>` | no | |
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
| `Foundry:ProjectEndpoint` | `https://<res>.services.ai.azure.com/api/projects/<project>` | no | TokenCredential only; required only when a non-echo workflow is registered (ADR 0003 rule 7) |
| `Workflows:Echo:Enabled` | `false` | no | `true` allowed only in Development/Dev/Test (ADR 0003 rule 8) |
| `Workflows:SupportTicket:ConfidenceThreshold` | `0.7` | no | |
| `Workflows:SupportTicket:MaxConsecutiveClarifications` | `2` | no | |
| `Workflows:SupportTicket:MaxToolCallsPerTurn` | `3` | no | 1..10; `search_knowledge` calls per specialist turn. At the cap the model gets one final call with no tool execution; if it still produces no text the turn fails with `workflow_error`. |
| `Workflows:SupportTicket:Agents:Orchestrator:Deployment` | e.g. `gpt-4.1-mini` | no | names are placeholders (D8) |
| `Workflows:SupportTicket:Agents:Policy:Deployment` | e.g. `gpt-4.1` | no | |
| `Workflows:SupportTicket:Agents:Claims:Deployment` | e.g. `gpt-4.1` | no | |
| `Workflows:SupportTicket:Agents:HealthAssessments:Deployment` | e.g. `gpt-4.1` | no | |
| `Chat:HistoryMaxMessages` | `20` | no | |
| `Chat:HistoryMaxTokens` | `6000` | no | applied by `ChatHistoryBuilder` (Api), not the store |
| `Chat:MaxMessageChars` | `4000` | no | |
| `Chat:TurnTimeout` | `00:03:00` | no | < 240 s ingress limit |
| `Chat:HeartbeatInterval` | `00:00:15` | no | |
| `Chat:PersistTimeout` | `00:00:05` | no | failure/cancel persistence, lock release |
| `Chat:TurnsPerMinute` | `10` | no | per `oid` per replica |
| `Chat:RetentionDays` | unset / e.g. `90` | no | open decision D7 |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | from infra | treat as config | |

Ingestion adds `Ingestion:EmbeddingDeployment`, `Ingestion:SourcePath`, `Ingestion:ChunkTokens` (800),
`Ingestion:ChunkOverlapTokens` (80), `Ingestion:SearchAdminKey` (Key mode only, secret; never set on the API).

Frontend container env: `AUTH_CLIENT_ID`, `AUTH_AUTHORITY`, `AUTH_API_SCOPE`, `AUTH_PROVIDERS` (optional),
`BACKEND_UPSTREAM` (backend internal FQDN for nginx `proxy_pass` and `Host`).

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

Policy consequence recorded (qa finding 1): keys stay allowed in Test per CLAUDE.md, and a pre-prod MI smoke job in
test gates prod (ADR 0003 section 1a).

### Open
D4. Entra tenants: 2 (nonprod for dev+test, prod) recommended vs 3 (one per stage).
D6. Offer email one-time passcode sign-in in prod, or social only? (Nonprod: recommended for test users.)
D7. Data retention per environment (`Chat:RetentionDays`), and whether users need a "delete my data" feature
    (Facebook requires a User Data Deletion URL for Live mode anyway).
D8. Embedding model and dimensions (text-embedding-3-small, 1536, assumed), chat model deployments per agent, and
    whether the Foundry project / model deployments already exist (azure-infra references, does not create).
D9. Knowledge source: where do policy/claims/health documents come from (repo folder, blob, SharePoint)? Content language
    (analyzer assumes English)?
D10. Custom domain for the frontend now or later (affects SPA redirect URIs; Learn Q&A reports social providers may
    not work through External ID custom URL domains, so keep ciamlogin.com for auth).

Q1. External ID issuer: community reports tokens with `iss = https://<tid>.ciamlogin.com/<tid>/v2.0`; Phase 1 must
    validate a real token through Microsoft.Identity.Web before relying on defaults.
Q4. Local Node is v20.12.2. Current Vite (8.x) requires Node 20.19+ or 22.12+, so `npm create vite` / `vite build`
    will fail locally. Upgrade local Node (recommend Node 22 LTS or newer; Node 20 reached end of life in April 2026)
    and use the same major in the frontend Dockerfile build stage. (Needed by Phase 3.)
Q5. Container Apps HTTP ingress has a 240 s request timeout; the 180 s turn timeout assumes no answer needs longer.
    Confirm acceptable; long-running workflow types would need a different pattern (background job + polling).
Q6. `domain_hint=google|facebook` is documented on Learn, but community threads report inconsistent behavior; verify in
    the nonprod tenant (fallback: hosted provider chooser).

### Resolved
Q2. Pipeline templates now exist in `.claude/skills/github-actions-pipelines/templates/`; devops-pipelines uses them.
Q3. actionlint is being installed locally; devops-pipelines lints with it.
(Q7, agent file names, fixed and removed.)
