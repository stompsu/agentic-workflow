# ADR 0004: Chat data retention and user data deletion

Status: Accepted (2026-10-25, decision D7 by the user). Related: ADR 0001 (identity), data-model 1.3 and 1.7,
api-contract 2.8, auth-setup sections 4.1 and 9.

## Context

Chat history holds personal data keyed by `oid`. The user decided: retention per environment plus a self-service
"delete my data" feature. Facebook Live mode requires a User Data Deletion URL (callback or instructions page).
Cosmos is one container partitioned by `/userId` with `defaultTtl = -1`.

## Decision

1. Retention by Cosmos per-document TTL, configured by `Chat:RetentionDays` (defaults dev 30, test 30, prod 90; user
   can change them; required outside local `Development`, ADR 0003 rule 9).
   - Messages: `ttl` set at write, never rewritten -> expire `RetentionDays` after they were written.
   - Session documents: `ttl` refreshed on every write (each turn) -> expire `RetentionDays` after last activity.
   - A session never expires before its newest message (same transactional batch); a long-lived session loses its
     oldest messages first (data-model 1.3 E1). Guarantee: no message older than `RetentionDays`.
2. Self-service deletion: `DELETE /api/me/data`, synchronous `204`, idempotent, stateless and resumable
   (sessions marked `deleting` first), 60 s budget with `503 deletion_incomplete` + `Retry-After` when exceeded,
   never `409`: live turn locks are taken over and the in-flight turn persists nothing (lease lost).
3. Facebook: "Data deletion instructions URL" -> `https://<prod-frontend-fqdn>/data-deletion.html`, a static page
   rendered at container start (no JavaScript) that explains "Delete my data", the retention period and how to request
   account deletion.
4. The Entra External ID account is separate: deleted manually by the tenant admin on request (auth-setup section 9).
   No admin chat-data purge tool in v1.

## Alternatives rejected

- 202 Accepted + background deletion + status endpoint: needs job state and a worker for data that deletes in seconds.
- Cosmos "delete items by partition key": still public preview, asynchronous, needs an account capability.
- 409 when a turn is in progress: makes erasure fail because of a stream the user may not see (another tab).
- Rewriting every message's `ttl` on each turn (whole session expires together): RU cost grows with session length.
- Facebook data deletion callback: requires a signed-request endpoint, confirmation codes and a status page; the
  instructions page is sufficient because users can delete their data themselves.

## Consequences

- Deleting chat data does not delete the identity; signing in again keeps the same `oid` (empty history). Deleting
  the identity does not delete chat data; if the person cannot run "Delete my data" first, retention removes it within
  `RetentionDays` (prod default 90 days). If faster erasure for such cases is required (for example a one-month
  GDPR response deadline), an admin purge tool is a future task (plan.md section 6, D7 note).
- Changing `RetentionDays` affects only documents written afterwards.
- `/data-deletion.html` and backend `Chat:RetentionDays` must state the same number: one Bicep param feeds both.
