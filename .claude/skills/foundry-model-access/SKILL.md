---
name: foundry-model-access
description: How to get IChatClient instances from AIProjectClient for MAF agents. Use when creating or changing model clients.
---
Pattern from current docs (verify against installed package versions first, the names have been changing):
`aiProjectClient.GetProjectOpenAIClient().GetProjectResponsesClient().AsIChatClient(deploymentName)`
(AsIChatClient comes from Microsoft.Extensions.AI.OpenAI.)
Wrap it in ONE registered factory `IChatClientFactory.Create(string deploymentName)`; nothing else touches AIProjectClient.
Agents are `ChatClientAgent` over that IChatClient. If a one-call Foundry chat client type exists in the installed
Microsoft.Agents.AI.Foundry version, prefer it inside the factory.
Tests use a fake IChatClient. Never call real models in unit tests.