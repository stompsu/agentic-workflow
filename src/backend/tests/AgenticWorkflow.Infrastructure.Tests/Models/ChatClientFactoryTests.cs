using System.Reflection;
using Azure;
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Extensions.Options;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Infrastructure.Models;

namespace AgenticWorkflow.Infrastructure.Tests.Models;

public class ChatClientFactoryTests
{
    private const string Endpoint = "https://example.invalid/api/projects/p";

    private static ChatClientFactory Create(string? endpoint, ThrowingCredential credential, Func<Uri, TokenCredential, AIProjectClient>? build = null) =>
        new(
            Options.Create(new FoundryOptions { ProjectEndpoint = endpoint }),
            new StubCredentialProvider(credential),
            build ?? ((uri, cred) => new AIProjectClient(uri, cred)));

    [Fact]
    public void Project_client_is_not_built_before_the_first_Create()
    {
        var built = 0;
        var factory = Create(Endpoint, new ThrowingCredential(), (uri, cred) =>
        {
            built++;
            return new AIProjectClient(uri, cred);
        });

        Assert.Equal(0, built);
        Assert.False(factory.ProjectClientCreated);

        factory.Create("gpt-test");
        factory.Create("gpt-other");

        Assert.Equal(1, built);
        Assert.True(factory.ProjectClientCreated);
    }

    [Fact]
    public void Create_builds_a_chat_client_with_the_token_credential_and_no_network_call()
    {
        var credential = new ThrowingCredential();
        TokenCredential? received = null;
        var factory = Create(Endpoint, credential, (uri, cred) =>
        {
            received = cred;
            return new AIProjectClient(uri, cred);
        });

        var client = factory.Create("gpt-test");

        Assert.NotNull(client);
        Assert.Same(credential, received);
        Assert.Equal(0, credential.Calls);
    }

    [Fact]
    public void Chat_clients_are_cached_per_deployment()
    {
        var factory = Create(Endpoint, new ThrowingCredential());
        Assert.Same(factory.Create("a"), factory.Create("a"));
        Assert.NotSame(factory.Create("a"), factory.Create("b"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a uri")]
    public void Missing_endpoint_throws_on_first_Create_only(string? endpoint)
    {
        var factory = Create(endpoint, new ThrowingCredential());
        Assert.Throws<InvalidOperationException>(() => factory.Create("a"));
    }

    [Fact]
    public void AIProjectClient_has_no_public_constructor_taking_a_key_credential()
    {
        var keyTaking = typeof(AIProjectClient).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Any(p => p.ParameterType == typeof(AzureKeyCredential)));
        Assert.Empty(keyTaking);
    }
}
