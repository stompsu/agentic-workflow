---
name: add-workflow-type
description: Checklist for adding a new workflow type (beyond SupportTicket) end to end. Use when the user asks for a new workflow.
---
1. architect: confirm no contract change is needed (workflow list is data-driven).
2. agent-workflows: add a class implementing the workflow definition (id, display name, agents, routing),
   prompts in prompt files, register it in the registry.
3. Knowledge: new `domain` value(s) in the shared index; update ingestion script.
4. Tests: classification/routing tests with a fake chat client.
5. Verify the UI dropdown shows it with ZERO frontend code changes. If not, the abstraction is broken: fix that.