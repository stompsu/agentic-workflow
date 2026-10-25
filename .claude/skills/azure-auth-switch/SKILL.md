---
name: azure-auth-switch
description: How to create Azure clients (Cosmos, AI Search, model endpoint) that use keys in lower environments and Managed Identity in production. Use whenever adding or changing an Azure client.
---
1. Config: `Azure:AuthMode` = `Key` | `ManagedIdentity`; per-service endpoint + optional key.
2. Create one `ICredentialFactory` / client factory; services never read keys directly.
3. Key mode: build the client from endpoint + key. ManagedIdentity mode: use `DefaultAzureCredential`
   (or `ManagedIdentityCredential` with client id) and no key.
4. Fail startup if AuthMode=Key in Production.
5. Add a test that each factory path builds a client without network calls.