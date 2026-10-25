using Azure.Identity;
using Microsoft.Extensions.Options;
using SupportChat.Core.Options;
using SupportChat.Infrastructure.Azure;

namespace SupportChat.Infrastructure.Tests.Azure;

public class AzureClientFactoryTests
{
    private static AzureClientFactory Create(string mode, ThrowingCredential credential, string? cosmosKey = "dGVzdC1rZXk=", string? searchKey = "query-key") =>
        new(
            Options.Create(new AzureOptions { AuthMode = mode }),
            Options.Create(new CosmosOptions { Endpoint = "https://acct.documents.azure.com:443/", Key = cosmosKey }),
            Options.Create(new SearchServiceOptions { Endpoint = "https://svc.search.windows.net", Key = searchKey }),
            new StubCredentialProvider(credential));

    [Theory]
    [InlineData("Key")]
    [InlineData("key")]
    public void Key_mode_builds_both_clients_without_network_or_token_requests(string mode)
    {
        var credential = new ThrowingCredential();
        var factory = Create(mode, credential);

        using var cosmos = factory.CreateCosmosClient();
        var search = factory.CreateSearchClient("support-knowledge");

        Assert.Equal(new Uri("https://acct.documents.azure.com:443/"), cosmos.Endpoint);
        Assert.Equal("support-knowledge", search.IndexName);
        Assert.Equal(new Uri("https://svc.search.windows.net"), search.Endpoint);
        Assert.Equal(0, credential.Calls);
    }

    [Fact]
    public void ManagedIdentity_mode_builds_both_clients_with_the_token_credential_and_no_keys()
    {
        var credential = new ThrowingCredential();
        var factory = Create("ManagedIdentity", credential, cosmosKey: null, searchKey: null);

        using var cosmos = factory.CreateCosmosClient();
        var search = factory.CreateSearchClient("support-knowledge");

        Assert.NotNull(cosmos);
        Assert.NotNull(search);
        Assert.Equal(0, credential.Calls);
    }

    [Fact]
    public void Key_mode_without_keys_throws_without_echoing_values()
    {
        var factory = Create("Key", new ThrowingCredential(), cosmosKey: null, searchKey: null);
        Assert.Throws<InvalidOperationException>(factory.CreateCosmosClient);
        Assert.Throws<InvalidOperationException>(() => factory.CreateSearchClient("i"));
    }

    [Fact]
    public void Invalid_mode_throws()
    {
        var factory = Create("Certificate", new ThrowingCredential());
        Assert.Throws<InvalidOperationException>(factory.CreateCosmosClient);
    }

    [Fact]
    public void Development_credential_excludes_managed_identity()
    {
        var o = AzureCredentialProvider.BuildOptions("development", "client");
        Assert.True(o.ExcludeManagedIdentityCredential);
        Assert.True(o.ExcludeWorkloadIdentityCredential);
        Assert.False(o.ExcludeAzureCliCredential);
    }

    [Theory]
    [InlineData("Dev")]
    [InlineData("Test")]
    [InlineData("Production")]
    public void Deployed_credential_is_reduced_to_the_user_assigned_managed_identity(string env)
    {
        var o = AzureCredentialProvider.BuildOptions(env, "the-client-id");
        Assert.Equal("the-client-id", o.ManagedIdentityClientId);
        Assert.False(o.ExcludeManagedIdentityCredential);
        Assert.True(o.ExcludeEnvironmentCredential);
        Assert.True(o.ExcludeWorkloadIdentityCredential);
        Assert.True(o.ExcludeVisualStudioCredential);
        Assert.True(o.ExcludeVisualStudioCodeCredential);
        Assert.True(o.ExcludeAzureCliCredential);
        Assert.True(o.ExcludeAzurePowerShellCredential);
        Assert.True(o.ExcludeAzureDeveloperCliCredential);
        Assert.True(o.ExcludeInteractiveBrowserCredential);
    }

    [Fact]
    public void Provider_returns_one_DefaultAzureCredential_instance_without_requesting_tokens()
    {
        var config = TestConfig.Build(new Dictionary<string, string?> { ["AZURE_CLIENT_ID"] = "id" });
        var provider = new AzureCredentialProvider(new FakeEnvironment("Test"), config);
        Assert.IsType<DefaultAzureCredential>(provider.Credential);
        Assert.Same(provider.Credential, provider.Credential);
    }
}

internal sealed class FakeEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;

    public string ApplicationName { get; set; } = "tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
}
