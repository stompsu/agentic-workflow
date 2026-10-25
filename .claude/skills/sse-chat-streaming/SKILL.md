---
name: sse-chat-streaming
description: The SSE event contract between backend and frontend for streaming chat. Use when touching chat endpoints or the chat UI.
---
Events (JSON in `data:`): `session` {sessionId}, `agent` {name}, `token` {text}, `citation` {title, url|docId},
`done` {messageId}, `error` {code, message}.
Backend: flush per token, honor cancellation, persist the full assistant message to Cosmos when done.
Frontend: fetch + ReadableStream with Authorization header, parse SSE frames, append tokens, abort on stop.
The source of truth is /docs/api-contract.md.