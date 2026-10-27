# API contract (source of truth)

Status: v1 (Phase 0, revised 2026-10-25 after the second qa-reviewer pass; D7 additions 2026-10-25: `DELETE /api/me/data`,
503 `deletion_incomplete`, retention notes). All D7 additions are additive (no version bump).
2026-10-27: local-mode note in section 1 (ADR 0005); informational only, no contract change, no version bump.
Change this file FIRST, then backend and frontend.
Owners: architect (contract), backend-dotnet (server), frontend-react (client), qa-reviewer (compliance).

## 1. General rules

- Base path: `/api`. The SPA calls the same origin; nginx proxies `/api/*` unchanged to the backend
  (no path rewrite, no CORS). The backend maps every route under `/api` itself. Local SPA development uses the Vite
  dev server proxy for `/api` (same origin as well).
- JSON: UTF-8, `camelCase` property names, enums as camelCase strings (`"answer"`, `"healthAssessments"`), timestamps
  ISO 8601 UTC (`2026-10-25T10:15:30.123Z`), ids are lowercase GUID strings (server-generated, version 7 so they sort by time).
- Clients MUST ignore unknown JSON properties (additive changes are non-breaking). Removing or renaming a field,
  event or code is breaking and needs a contract version bump.
- Auth: every endpoint except health requires `Authorization: Bearer <access token>` for scope
  `api://<api-client-id>/access_as_user` issued by the Entra External ID tenant (see /docs/adr/0001-identity-flow.md).
- The user is ALWAYS the `oid` claim of the validated token. No endpoint accepts a user id in the route, query or body.
  No response contains `userId`, `ttl`, `activeTurn`, `state` or `workflowMetadata`.
- Workflow-agnostic: no endpoint, field or event is specific to a workflow type. Workflows are data (`GET /api/workflows`).
- Retention: chat data expires automatically (`Chat:RetentionDays`, data-model 1.3). The API exposes no retention
  value or expiry time; clients must tolerate sessions disappearing from the list and old messages disappearing from
  a session (2.5).
- Local mode (/docs/adr/0005-local-mode.md): the local-only `SupportChat.LocalHost` executable serves the identical
  `/api` contract (same endpoints, SSE events, status codes and ProblemDetails, same `ChatUser` policy and `oid`
  scoping); with `LocalMode:Auth=Local` the bearer token comes from a local issuer instead of Entra External ID and is
  still a validated, signed JWT. The `/local-auth/*` endpoints (e.g. `POST /local-auth/token`) exist ONLY in the
  LocalHost executable, are outside `/api`, and are NOT part of this contract (ADR 0005 section 5). Local mode is
  never available in any Azure environment (ADR 0003 rule 11).

## 2. Endpoints

| Method | Path | Auth | Success | Notes |
|---|---|---|---|---|
| GET | `/api/healthz` | none | 200 | Liveness. Process up. No dependency calls. |
| GET | `/api/readyz` | none | 200 / 503 | Readiness: config valid + Cosmos container reachable (2 s timeout, result cached 30 s). |
| GET | `/api/workflows` | user | 200 | Registered workflow types. |
| POST | `/api/sessions` | user | 201 | Create a session bound to one workflow. |
| GET | `/api/sessions` | user | 200 | Current user's sessions, paged, newest activity first. |
| GET | `/api/sessions/{sessionId}` | user | 200 | Session + most recent messages. |
| POST | `/api/sessions/{sessionId}/messages` | user | 200 `text/event-stream` | Send a user message, stream the assistant turn (SSE). |
| DELETE | `/api/sessions/{sessionId}` | user | 204 | Hard-delete session and all its messages. |
| DELETE | `/api/me/data` | user | 204 | Delete ALL chat data (every session and message) of the caller. Idempotent. Section 2.8. |

`/api/healthz` is the pipeline smoke-test target (`https://<frontend fqdn>/api/healthz`). Health responses:
`{"status":"ok"}` or `{"status":"unavailable"}` (503). They never include config, dependency versions or errors.
Aliases `/healthz` and `/readyz` exist only for Container Apps probes on the backend container port; nginx proxies
only `/api/`, so the backend aliases are not reachable through the frontend. The frontend nginx answers its own
`/healthz` and `/readyz` locally (plain-text 200, for the frontend container's probes); these are separate endpoints
and say nothing about the backend.

### 2.1 Session creation decision

Sessions are created EXPLICITLY with `POST /api/sessions`. There is no implicit creation on first message.
Reasons: the session is bound to a `workflowId` once, ownership/404 checks happen before any stream starts,
and the messages endpoint has one shape. The SPA creates the session when the user sends the first message
of a new chat (two calls: create, then post message); it does not create empty sessions on "New chat" click.

The `session` SSE event is still always the FIRST event of every stream (acknowledges the turn and echoes the
`sessionId`), so the client has one code path and the event stays available if implicit creation is ever added.

### 2.2 GET /api/workflows

```json
200
{
  "items": [
    { "id": "support-ticket", "displayName": "Support ticket", "description": "Questions about policies, claims and health assessments." }
  ]
}
```
Order is registry order. `id` is stable forever (stored in sessions). The SPA renders the dropdown only from this list.
The list may contain `echo` in Development/Dev/Test when `Workflows:Echo:Enabled = true` (never in Production).

### 2.3 POST /api/sessions

Request:
```json
{ "workflowId": "support-ticket", "title": "My claim from May" }
```
- `workflowId` required, must be a registered id, else 400 `validation_failed`.
- `title` optional, 1..200 chars after trim. If absent, `title` is `null` until the first turn starts; the turn-start
  write sets it from the first user message (first 60 chars, trimmed). Clients render `null` as a placeholder ("New chat").

Response `201`, header `Location: /api/sessions/{id}`:
```json
{ "id": "0192f0c1-7a3e-7cc1-9a51-3f1e2b9d4c10", "workflowId": "support-ticket", "title": "My claim from May",
  "createdAt": "2026-10-25T10:15:30.123Z", "updatedAt": "2026-10-25T10:15:30.123Z" }
```
`title` is `string | null` in every session representation.

### 2.4 GET /api/sessions?pageSize=20&continuationToken=...

- `pageSize` 1..50, default 20. `continuationToken` opaque string from the previous page (base64url); never parse it.
- Only sessions of the current user. Ordered by `updatedAt` desc. Sessions being deleted are omitted.
- Expired sessions (no activity for `Chat:RetentionDays`) are omitted; they are gone for good.

```json
200
{
  "items": [
    { "id": "0192f0c1-...", "workflowId": "support-ticket", "title": "My claim from May",
      "createdAt": "2026-10-25T10:15:30.123Z", "updatedAt": "2026-10-25T10:17:02.001Z" }
  ],
  "continuationToken": "eyJ0b2tlbiI6Ii4uLiJ9"
}
```
`continuationToken` is `null` on the last page. An invalid token returns 400 `validation_failed`.

### 2.5 GET /api/sessions/{sessionId}

Returns the session and its most recent 100 messages in ascending `createdAt` order.

```json
200
{
  "id": "0192f0c1-...", "workflowId": "support-ticket", "title": "My claim from May",
  "createdAt": "2026-10-25T10:15:30.123Z", "updatedAt": "2026-10-25T10:17:02.001Z",
  "hasMoreMessages": false,
  "messages": [
    { "id": "0192f0c1-aaaa-...", "role": "user", "content": "How long does a claim take?",
      "createdAt": "2026-10-25T10:16:00.000Z", "status": "complete" },
    { "id": "0192f0c1-bbbb-...", "role": "assistant", "content": "Most claims are settled within 10 working days [S1].",
      "createdAt": "2026-10-25T10:16:01.500Z", "status": "complete", "kind": "answer",
      "agentName": "Claims specialist",
      "citations": [ { "ref": "S1", "title": "Claims handling times", "docId": "claims-handling-0003", "url": "https://kb.example.com/claims/handling" } ] }
  ]
}
```
Message fields:
- `role`: `user` | `assistant`.
- `status`: `complete` | `cancelled` (client aborted, partial text) | `failed` (server error, text may be partial or empty).
- `kind` (assistant only): `answer` | `clarification` (the assistant asked the user a question instead of answering).
  Always sent by the server; a reader that finds it missing treats it as `answer`.
- `agentName` (assistant only): display name of the agent that produced the text (one agent per message in v1).
- `citations` (assistant only, may be empty): same shape as the `citation` event data.
- `errorCode` (only when `status = failed`): one of the stream error codes in 4.4.

A user message whose turn failed or was cancelled stays visible (it may have no assistant reply).
Older-message paging is out of scope for v1 (`hasMoreMessages` signals it; a paging parameter can be added later).
A session being deleted returns 404. An expired session returns 404.
Retention: each message expires `Chat:RetentionDays` after it was written, while the session lives until
`Chat:RetentionDays` after its last activity (data-model 1.3). A session used over a period longer than the retention
period therefore loses its oldest messages first. Clients must render whatever is returned, including a list whose
first message is an `assistant` message, and an empty `messages` array.

### 2.6 POST /api/sessions/{sessionId}/messages (SSE)

Request headers: `Authorization`, `Content-Type: application/json`, `Accept: text/event-stream`.
```json
{ "content": "How long does a claim take?" }
```
- `content` required, 1..4000 chars after trim (`Chat:MaxMessageChars`). Request body limit 32 KB.
- All validation happens BEFORE the response starts. Failures return ProblemDetails (section 5), never SSE.
- At most one active turn per session: a concurrent POST returns 409 `turn_in_progress`. A session being deleted
  (including by `DELETE /api/me/data`) returns 404 `not_found`.
- If the turn was accepted (user message persisted, lock taken) but a failure occurs before the `session` event is
  written, the response is 500 ProblemDetails `internal_error`; the server releases the lock and the user message
  stays visible without a reply.
- Stop-then-send: the server releases the turn lock in a `finally` after the aborted turn's persistence, which can
  lag the client's abort by up to `Chat:PersistTimeout`. On 409 `turn_in_progress` the client retries ONCE after 1 s,
  then shows "A reply is still being finished, try again".

Response headers on success:
```
HTTP/1.1 200 OK
Content-Type: text/event-stream; charset=utf-8
Cache-Control: no-cache, no-transform
X-Accel-Buffering: no
```
Body: SSE stream (section 4). Server closes the stream after the terminal event. Response compression is disabled
for this route.

### 2.7 DELETE /api/sessions/{sessionId}

`204` on success. `404` if it does not exist, belongs to another user, or was already deleted.
`409 turn_in_progress` while a turn is active (a stale lock, see data-model 1.2, does not block). The client may retry
after the stream ends.

Server sequence (data-model 1.5):
1. Take the turn lock with an ETag-conditional replace that also marks the session `deleting` (a live lock or an ETag
   conflict -> 409). From this point the session is invisible: GET returns 404, the list omits it, POST messages
   returns 404. A turn whose stale lock was taken over cannot write anything afterwards (lease lost, 4.5).
2. Delete the session's messages in transactional batches (up to 100 per batch).
3. Delete the session document, then return 204.

If the request fails after step 1 (500), the session stays invisible; repeating the DELETE resumes steps 2-3 and
returns 204. Once step 3 completed, DELETE returns 404.

### 2.8 DELETE /api/me/data (delete my data)

Deletes ALL chat data of the caller: every session document (any state) and every message document in the caller's
partition (`oid`), including orphaned messages left by an interrupted session delete. It does NOT delete the Entra
External ID user account; that is a separate manual admin step (/docs/auth-setup.md section 9).

Request:
- `DELETE /api/me/data`, no query parameters, no body (a body, if sent, is ignored; no `Content-Type` required).
- Auth exactly like every other endpoint: bearer token with scope `access_as_user` and an `oid` claim
  (401 `unauthorized` / 403 `forbidden`, section 3). The data deleted is ALWAYS the token's `oid` partition; `me` is
  not an id and no other user can be addressed.

Responses:

| status | body | Meaning |
|---|---|---|
| 204 | none | Everything of the caller that existed when the request started is deleted. Also returned when there was nothing to delete. Repeating the call returns 204 again (idempotent). |
| 503 | ProblemDetails `code: deletion_incomplete`, header `Retry-After: 1` | The server time budget (`Chat:DeleteUserDataBudget`, default `00:01:00`) ran out before everything was deleted. Progress is kept: sessions already marked stay invisible, deleted documents stay deleted; some data may still be visible until the request is repeated. The client repeats the same request. |
| 401 / 403 | ProblemDetails `unauthorized` / `forbidden` | Section 3. |
| 500 | ProblemDetails `internal_error` | Unexpected failure. Progress is kept; repeating the request resumes. |

Never 404 (a caller with no data gets 204) and never 409.

Decision: synchronous 204 (not 202). Reasons: per-user data is small (single logical partition, at most a few
thousand documents; about 100-document chunks complete in well under the budget), the call stays stateless (no job
record, no status endpoint, no worker), and the client gets a definite "done". The deletion is resumable without
server-side job state: sessions are first marked `deleting` (persisted in their own documents), so a request that hit
the budget, failed, or was aborted by the client is simply repeated and continues where the previous one stopped.
The budget (60 s) keeps the request far below the 240 s Container Apps ingress timeout and nginx
`proxy_read_timeout 300s`. Rejected: 202 + polling (needs deletion state and a status endpoint); Cosmos
"delete items by partition key" (still preview, asynchronous, needs an account capability).

In-flight turns do not block (no 409): the deletion takes over every session's turn lock, live or stale. Reason: the
user's intent is erasure, and a stream in another tab must not make it fail.
- A turn streaming at that moment loses its lease: at turn end it persists nothing (no assistant message) and, if its
  stream is still open, ends with `error internal_error` (section 4.5). Its user message is deleted with the rest.
- A POST messages to an already-marked session returns 404 `not_found`; one racing the marking may return 409
  `turn_in_progress`, and its single client retry then gets 404.
- Data created after the request started (for example a new session created in another tab during the deletion) can
  survive; calling the endpoint again deletes it. Two concurrent calls by the same user are safe.

Server sequence (data-model 1.7):
1. Mark: list every session document of the caller (all states). For each `active` session, an ETag-conditional
   replace sets `state = deleting` and `activeTurn = {turnId: <new delete lease id>, startedAt}` regardless of any
   existing lock (ETag conflict -> re-read and retry). Sessions already `deleting` are skipped. From here each session
   is invisible: GET returns 404, the list omits it, POST messages returns 404.
2. Delete every message document of the caller in chunks of up to 100 (already-missing documents are ignored).
3. Delete every session document listed in step 1 (already-missing documents are ignored).
4. Return 204.
The budget is checked before each replace and each chunk; when it is exceeded the server stops and returns 503
`deletion_incomplete`. On client abort (`RequestAborted`) the server stops; nothing is rolled back.
Not covered by the POST messages rate limiter. Logged: deleted document counts and `enduser.id` only.

Client (SPA):
- Called only from an explicit "Delete my data" action (account menu) after a confirmation dialog that says all chat
  history in this app will be permanently deleted, that this cannot be undone, and that the sign-in account is not
  deleted.
- Before the call, abort any in-flight stream in this tab (AbortController).
- On 503 `deletion_incomplete`: repeat after `Retry-After` seconds, at most 5 attempts in total; then show
  "Deleting your data is taking longer than expected. Please try again in a minute."
- On 204: drop all cached sessions and messages (React Query cache), show the empty new-chat view and the notice
  "Your chat history was deleted. Your sign-in account was not deleted." plus `app.supportContact` from `/config.json`
  if configured (auth-setup section 7). The user stays signed in.
- Other errors: the generic error handling (401 retry/login, 5xx message).

## 3. Status codes for ownership

- 401: missing/invalid/expired token. Header `WWW-Authenticate: Bearer`, body ProblemDetails `code: unauthorized`
  (custom JwtBearer challenge). Client: acquire token silently with force refresh, retry once, then redirect to login.
- 403: valid token but missing the `access_as_user` scope or missing `oid` (for example an app-only token).
  Body ProblemDetails `code: forbidden`. 403 is never used for resources.
- 404: session id unknown OR owned by another user (OR being deleted, OR expired). Responses are identical except
  `traceId`. Never 403. (Implementation: every read is `(id, partitionKey = oid)`; another user's session is simply
  not found.) `DELETE /api/me/data` never returns 404.

## 4. SSE stream

### 4.1 Framing

Each event:
```
id: <n>
event: <type>
data: <single-line JSON>

```
- `id` is a per-stream integer starting at 1, incremented per event (ordering/debugging only). No resume:
  the server ignores `Last-Event-ID`; the client never auto-reconnects (fetch, not EventSource). No `retry:` field.
- `data` is exactly one line of JSON (the serializer escapes newlines). Lines end with `\n`; an empty line ends the event.
- Heartbeat: a comment line `: hb` followed by an empty line, sent every `Chat:HeartbeatInterval` (15 s) while no
  other event was written. It has no `id`. The client must ignore all lines starting with `:`.
- The server flushes after every event and every heartbeat.

### 4.2 Events

| event | data | Meaning |
|---|---|---|
| `session` | `{ "sessionId": string }` | First event, exactly once. Turn accepted; user message persisted. |
| `agent` | `{ "name": string }` | The agent producing the following tokens. Display name; render as text. |
| `token` | `{ "text": string }` | Text delta to append to the current assistant message. Never empty. |
| `citation` | `{ "ref": string, "title": string, "docId": string, "url": string \| null }` | A source the answer cites. `ref` matches the marker in text, e.g. `[S1]`. `url` is an absolute `http(s)` URL or `null` (the backend nulls any other scheme). `title` is sent RAW (not HTML-encoded; the encoding in ADR 0002 section 9 applies only to text sent to the model); the client renders it as plain text. |
| `done` | `{ "messageId": string }` | Terminal. Assistant message persisted with this id. |
| `error` | `{ "code": string, "message": string }` | Terminal. `message` comes from the server-side table in 4.4 (user-safe). |

Note on `citation`: the skill lists `{title, url|docId}`. This contract always sends `docId`, sends `url` when known,
and adds `ref` so the UI can link `[S1]` markers to sources. Additive, justified by the marker format.

### 4.3 Order (grammar)

v1: exactly one agent segment per turn (orchestrator clarification OR specialist answer).
```
stream   := session [ agent token* citation* ] terminal?
terminal := done | error
```
- `session` is always first. `token` and `citation` only appear after `agent`; `agent` appears at most once.
- `done` only follows a segment with at least one `token`. `error` may follow `session` directly or any event.
- Citations are sent after the last token (the server resolves `[Sn]` markers on the full text).
- Exactly one terminal event, then the server closes. No terminal event when the client aborted.
- A clarifying question is a normal segment: `agent` (orchestrator display name) + `token`(s) + `done`.
  The persisted message has `kind: "clarification"`; the next user message in the same session is the answer.
- Forward compatibility: multiple segments per turn would be a contract change. Clients should still start a new
  visual segment if a second `agent` arrives rather than fail.

### 4.4 Stream error codes

The backend owns the `message` text; workflows only supply a code. Codes not in this table become `internal_error`.

| code | When | `message` (user-safe) |
|---|---|---|
| `model_error` | Model call failed (non-throttling) | "The assistant could not answer. Please try again." |
| `upstream_throttled` | Model or dependency throttled (429). Distinct from HTTP 429 `rate_limited` (section 5, our own per-user limiter). | "The service is busy. Please try again in a moment." |
| `content_filtered` | Model content filter blocked input or output | "This request could not be processed. Please rephrase." |
| `timeout` | Turn exceeded `Chat:TurnTimeout` (default 180 s, below the 240 s Container Apps ingress request timeout): an `OperationCanceledException` while the turn-timeout token is cancelled and `RequestAborted` is not | "The answer took too long. Please try again." |
| `workflow_error` | Workflow logic failed (invalid structured output after retries, `TurnCompleted` with no tokens, protocol violation) | "Something went wrong. Please try again." |
| `internal_error` | Anything else, including a workflow that ends without a terminal event, an `OperationCanceledException` that is neither client abort nor turn timeout, and a lost lease / vanished session at turn end (4.5, incl. a concurrent `DELETE /api/me/data`) | "Something went wrong. Please try again." |

### 4.5 Cancellation and persistence

Persisted per turn (Cosmos, see /docs/data-model.md):
1. Before `session` is sent (one batch): the user message (`status: complete`), session `updatedAt`, turn lock, and
   `title` if it is still `null`.
2. On `done`: the assistant message (`status: complete`) with full text, `agentName`, `kind`, citations. Then `done` is sent.
3. On server error: the assistant message with `status: failed`, `errorCode`, partial text (possibly empty). Then `error` is sent.
4. On client abort (AbortController / tab closed / network drop): the server observes `RequestAborted`, cancels the
   model call, and persists the partial assistant message with `status: cancelled` IF at least one token was produced
   (no message otherwise). Nothing more is written to the stream.
- Writes in 3 and 4 use `CancellationToken.None` bounded by a separate `Chat:PersistTimeout` (5 s), never the
  aborted/timed-out request token.
- The turn lock is released in a `finally` in all cases (the end-turn batch, or a lock-only release if no message is written).
- Session gone or lease lost at turn end: if the session no longer exists (TTL expiry, deletion) or its lock now
  belongs to someone else (the turn's lock went stale and was taken over by another turn or a DELETE, or it was taken
  over by `DELETE /api/me/data`, which takes over live locks too), the server persists NOTHING (no assistant message,
  lock untouched), logs a warning with session id and lease id only, and, if the stream is still open, sends
  `error internal_error` instead of `done`.

Partial (`cancelled`/`failed`) messages and user messages without a complete reply are shown in history but are NOT
fed back to agents (data-model 1.6). The `messageId` in `done` is generated at turn start, so the partial message of a
cancelled turn uses the same id.

### 4.6 Example: routed answer

```
id: 1
event: session
data: {"sessionId":"0192f0c1-7a3e-7cc1-9a51-3f1e2b9d4c10"}

id: 2
event: agent
data: {"name":"Claims specialist"}

id: 3
event: token
data: {"text":"Most claims are settled "}

: hb

id: 4
event: token
data: {"text":"within 10 working days [S1]."}

id: 5
event: citation
data: {"ref":"S1","title":"Claims handling times","docId":"claims-handling-0003","url":"https://kb.example.com/claims/handling"}

id: 6
event: done
data: {"messageId":"0192f0c1-bbbb-7000-8000-000000000001"}

```

### 4.7 Example: clarifying question

```
id: 1
event: session
data: {"sessionId":"0192f0c1-7a3e-7cc1-9a51-3f1e2b9d4c10"}

id: 2
event: agent
data: {"name":"Support assistant"}

id: 3
event: token
data: {"text":"Is your question about your policy coverage, an existing claim, or a health assessment?"}

id: 4
event: done
data: {"messageId":"0192f0c1-cccc-7000-8000-000000000002"}

```

### 4.8 Example: error mid-stream

```
id: 1
event: session
data: {"sessionId":"0192f0c1-..."}

id: 2
event: agent
data: {"name":"Policy specialist"}

id: 3
event: token
data: {"text":"Your policy covers "}

id: 4
event: error
data: {"code":"upstream_throttled","message":"The service is busy. Please try again in a moment."}

```

## 5. Non-stream errors (ProblemDetails, RFC 9457)

`Content-Type: application/problem+json`. Extension members `code` (stable, machine-readable) and `traceId`.
Clients switch on `code` only; `type`/`title` are informational (ASP.NET Core defaults are fine).

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Validation failed",
  "status": 400,
  "code": "validation_failed",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "errors": { "content": ["Content must be between 1 and 4000 characters."] }
}
```

| status | code | When |
|---|---|---|
| 400 | `validation_failed` | Bad body/query, unknown `workflowId`, bad continuation token. `errors` lists fields. |
| 401 | `unauthorized` | No/invalid token. |
| 403 | `forbidden` | Token lacks scope or `oid`. |
| 404 | `not_found` | Unknown, foreign, being-deleted or expired session. |
| 409 | `turn_in_progress` | Another turn is active for this session (POST messages, DELETE session). Never returned by `DELETE /api/me/data`. |
| 409 | `workflow_unavailable` | Session's `workflowId` is no longer registered. Client: show the session read-only with a notice and offer a new chat. |
| 413 | `payload_too_large` | Body > 32 KB (backend). |
| 415 | `unsupported_media_type` | Not `application/json`. |
| 429 | `rate_limited` | Our per-user limiter on POST messages (keyed by `oid`, per replica; header `Retry-After` in seconds). Client: disable sending until `Retry-After` elapses. |
| 500 | `internal_error` | Unhandled, incl. a failure after turn start but before the `session` event (lock released, 2.6). No details in production. |
| 503 | `deletion_incomplete` | `DELETE /api/me/data` ran out of its time budget; progress kept; header `Retry-After: 1`. Client repeats the request (2.8). |

Responses produced by nginx or the ingress are NOT ProblemDetails: nginx returns an HTML 413 for bodies > 64 KB, and
502/503/504 may come from nginx/ingress. Clients map by status when the body is not `application/problem+json`
(413 -> `payload_too_large`, 5xx -> `internal_error`). A 503 is `deletion_incomplete` ONLY when the body is
ProblemDetails with that `code`.

## 6. Limits (config keys in parentheses; durations are `TimeSpan` strings)

| Limit | Default |
|---|---|
| Message content | 1..4000 chars (`Chat:MaxMessageChars`) |
| Request body | 32 KB (endpoint-level Kestrel limit; nginx `client_max_body_size 64k`) |
| Title | 1..200 chars; auto title 60 chars |
| Sessions page size | 1..50, default 20 |
| Messages returned by GET session | 100 most recent |
| Turn duration | `00:03:00` (`Chat:TurnTimeout`) |
| Heartbeat interval | `00:00:15` (`Chat:HeartbeatInterval`) |
| Persistence after abort/failure | `00:00:05` (`Chat:PersistTimeout`) |
| Per-user turns | 10 per minute per replica (`Chat:TurnsPerMinute`); effective limit scales with replica count |
| Delete-my-data time budget per request | `00:01:00` (`Chat:DeleteUserDataBudget`, 00:00:05..00:03:00) |
| Retention | `Chat:RetentionDays` per environment; defaults dev 30, test 30, prod 90 (data-model 1.3) |

## 7. Infrastructure requirements implied by this contract

- nginx `location /api/`: `proxy_pass` to the backend internal FQDN over HTTPS with `proxy_ssl_server_name on` and
  `proxy_set_header Host <backend internal fqdn>` (Container Apps routes by Host), `proxy_http_version 1.1`,
  `proxy_set_header Connection ""`, `proxy_buffering off`, `proxy_cache off`, `gzip off` for `text/event-stream`,
  `proxy_read_timeout 300s`, pass `Authorization` through. No other backend paths are proxied.
- Vite dev server (local only): `server.proxy['/api']` to the local backend, no buffering of `text/event-stream`.
- Backend: no response compression for `text/event-stream` (exclude the SSE route if compression middleware is added).
- Container Apps HTTP ingress request timeout is 240 s; the turn timeout (180 s) and the delete-my-data budget (60 s)
  keep requests below it. Workflows that need longer than the turn timeout require a different pattern (background job
  + polling), which would be a contract change (Q5, decided).
- `config.json` (frontend runtime config) is served with `Cache-Control: no-store`.
- `/data-deletion.html` (Facebook "Data deletion instructions URL", auth-setup section 4): a static, public page served
  by the frontend nginx (no auth, no JavaScript, `Cache-Control: no-cache`, normal security headers). It is not an API
  endpoint and is not proxied. Content requirements: auth-setup section 4.1.

## 8. Client rendering rules (assistant content)

- Markdown with raw HTML disabled; images disabled (rendered as their alt text or not at all).
- Links: only `http:`/`https:` hrefs are rendered as links (others as plain text), with `target="_blank"` and
  `rel="noopener noreferrer"`.
- `[Sn]` markers link to the matching `citation` (`url` if present, else show `title`/`docId`).
- `agent.name`, `citation.title` (raw from the server), session `title` and user messages are rendered as plain text,
  never as markdown/HTML.
- Accepted risk: http(s) links written by the model in assistant markdown (not only citation URLs) are rendered as
  links; they are not validated against retrieved sources. Mitigations: scheme allow-list, `noopener noreferrer`,
  new tab, prompt rules (ADR 0002 section 9). Revisit if a link allow-list or "external link" warning is needed.
