---
name: ai-search-knowledge
description: Azure AI Search index design, ingestion and the retrieval tool used by specialist agents. Use for knowledge/RAG changes.
---
One index with fields: id, domain (open set of strings; SupportTicket uses policy|claims|health, new workflow types add values), title, content, contentVector, sourceUrl.
Hybrid search (keyword + vector) with semantic ranking; each specialist filters by its `domain`.
The retrieval tool returns top-k chunks with titles so the agent can cite them. Agents must say when nothing relevant is found instead of guessing.