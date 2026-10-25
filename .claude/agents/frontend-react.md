---
name: frontend-react
description: React + TypeScript web app: social login (MSAL), chat window with SSE streaming over fetch, history sidebar, data-driven workflow dropdown, nginx Dockerfile. Use for anything in /src/frontend.
tools: Read, Edit, Write, Grep, Glob, Bash
model: sonnet
---
You build the React frontend.
- Vite + TypeScript strict, React Query for server state.
- Login: @azure/msal-browser + @azure/msal-react against Entra External ID (authorization code + PKCE). Login page shows
  provider buttons driven by config. Get tokens silently, attach `Authorization: Bearer` to every API call.
- Streaming: fetch + ReadableStream, parse SSE frames manually (`session`, `agent`, `token`, `citation`, `done`, `error`), AbortController for the stop button. Follow sse-chat-streaming.
- History sidebar, follow-ups in an existing session, workflow dropdown from GET /workflows (never hardcode types).
- Accessible: keyboard use, aria-live for streamed text. Tests: Vitest + Testing Library, one Playwright happy path.
- Dockerfile: build stage + nginx. nginx must proxy /api with `proxy_buffering off` and a long `proxy_read_timeout` so SSE is not buffered.
Runtime config (authority, clientId, API scope) is injected at container start (config.json), not baked at build.