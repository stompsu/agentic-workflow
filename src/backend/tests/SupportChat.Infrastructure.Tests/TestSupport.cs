using Azure.Core;
using Microsoft.Extensions.Configuration;
using SupportChat.Infrastructure.Azure;

namespace SupportChat.Infrastructure.Tests;

internal static class TestConfig
{
    public static IConfiguration Build(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    /// <summary>A valid Development configuration (Key mode).</summary>
    public static Dictionary<string, string?> Development() => new()
    {
        ["Azure:AuthMode"] = "Key",
        ["Chat:RetentionDays"] = "30",
        ["Cosmos:Endpoint"] = "https://localhost:8081/",
        ["Cosmos:Key"] = "dGVzdC1rZXk=",
        ["Search:Endpoint"] = "https://example.search.windows.net",
        ["Search:Key"] = "query-key-value",
        ["AzureAd:Instance"] = "https://tenant.ciamlogin.com/",
        ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000001",
        ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000002",
    };

    /// <summary>A valid Production configuration (ManagedIdentity, no keys).</summary>
    public static Dictionary<string, string?> Production()
    {
        var c = Development();
        c["Azure:AuthMode"] = "ManagedIdentity";
        c["Cosmos:Endpoint"] = "https://account.documents.azure.com:443/";
        c.Remove("Cosmos:Key");
        c.Remove("Search:Key");
        c["AZURE_CLIENT_ID"] = "00000000-0000-0000-0000-000000000003";
        return c;
    }
}

/// <summary>A credential that fails the test if anything asks it for a token (proves no network use).</summary>
internal sealed class ThrowingCredential : TokenCredential
{
    public int Calls { get; private set; }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("No token requests are expected in this test.");
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("No token requests are expected in this test.");
    }
}

internal sealed class StubCredentialProvider(TokenCredential credential) : IAzureCredentialProvider
{
    public TokenCredential Credential => credential;
}
