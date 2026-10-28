# Knowledge base (sample data)

Everything in this folder is **sample content for demonstration only**. "Contoso Assurance (sample data)" is a fictional
insurer; all figures, timeframes and policies are invented and nothing here is real insurance, legal or medical advice.
All `sourceUrl` values point to `https://example.com/kb/...` (a reserved domain), so the links do not resolve.

## Layout

```
knowledge/
  policy/   -> domain "policy"  (coverage, exclusions, premiums and grace period, cancellation)
  claims/   -> domain "claims"  (filing, required documents, processing times, appeals)
  health/   -> domain "health"  (assessments, booking, results, privacy)
```

The first folder name is the search `domain` and must match `^[a-z0-9-]+$` (see docs/data-model.md sections 2.1 and 2.5).
Sub-folders are allowed; every `*.md` below a domain folder is ingested.

## Front matter

```
---
title: Document title
sourceUrl: https://example.com/kb/<domain>/<slug>
---
```

- `title` is optional (falls back to the first `# ` heading, then the file name). Each chunk is titled
  "<title> - <section heading>".
- `sourceUrl` is optional and must be an absolute http(s) URL; anything else is dropped. It is returned with citations.
- Content is split at headings (`##`), then packed into chunks of about `Ingestion:ChunkTokens` with overlap, so keep each
  section self-contained.

## How ingestion picks it up

The console app in `src/backend/src/AgenticWorkflow.Ingestion` reads `Ingestion:SourcePath` (default `knowledge`, relative to
the working directory), chunks and embeds the files, and upserts them into the Azure AI Search index with deterministic
ids; chunks of removed files are deleted. See `src/backend/README.md` (ingestion keys) and `infra/README.md` (Search
service and roles). A local check that calls no Azure service:

```
dotnet run --project src/backend/src/AgenticWorkflow.Ingestion -- --dry-run
```
(Set `Ingestion__SourcePath` to the absolute path of this folder if you run from another directory.)
