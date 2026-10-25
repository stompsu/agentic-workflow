---
name: azure-auth-switch
description: How to build Azure clients: keys or Managed Identity for Cosmos/AI Search by config, TokenCredential-only for AIProjectClient. Use whenever adding or changing an Azure client.
---
1. Config: `Azure:AuthMode` = `Key` | `ManagedIdentity`. Per-service endpoint, optional key (from Key Vault reference).
2. One `IAzureClientFactory` creates CosmosClient and SearchClient. Services never read keys.
   - Key: endpoint + key.
   - ManagedIdentity: `DefaultAzureCredential` (pass the user-assigned identity client id via `AZURE_CLIENT_ID`).
3. AIProjectClient is ALWAYS built with a TokenCredential, in every environment.
4. Startup validation: AuthMode=Key with ASPNETCORE_ENVIRONMENT=Production -> throw.
5. Test: each factory path builds a client with no network calls.