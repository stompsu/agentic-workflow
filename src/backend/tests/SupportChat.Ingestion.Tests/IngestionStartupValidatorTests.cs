using Microsoft.Extensions.Configuration;
using SupportChat.Core.Options;

namespace SupportChat.Ingestion.Tests;

public class IngestionStartupValidatorTests
{
    private const string KeyMessage = "Azure:AuthMode=Key is allowed only in Development, Dev and Test.";

    private static List<string> Run(string mode, string env, string? adminKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Azure:AuthMode"] = mode,
            ["Search:Endpoint"] = "https://search.example.net",
            ["Foundry:ProjectEndpoint"] = "https://foundry.example.net/api/projects/p",
            ["Ingestion:VectorizerResourceUri"] = "https://foundry.example.net",
        }).Build();
        var options = new IngestionOptions { SearchAdminKey = adminKey, EmbeddingDeployment = "embed" };
        return IngestionStartupValidator.Validate(configuration, env, options, out _);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData("")]
    public void Key_mode_outside_allow_list_is_rejected(string env) =>
        Assert.Contains(KeyMessage, Run("Key", env, "k"));

    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    [InlineData("Dev")]
    [InlineData("DEV")]
    [InlineData("Test")]
    [InlineData("TEST")]
    public void Key_mode_in_allowed_environment_passes(string env) =>
        Assert.Empty(Run("Key", env, "k"));

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Dev")]
    [InlineData("Test")]
    [InlineData("Development")]
    public void Managed_identity_is_allowed_anywhere(string env) =>
        Assert.Empty(Run("ManagedIdentity", env, null));

    [Fact]
    public void Admin_key_in_managed_identity_mode_is_rejected()
    {
        var failures = Run("ManagedIdentity", "Production", "k");
        Assert.Contains("Ingestion:SearchAdminKey must not be set when Azure:AuthMode=ManagedIdentity.", failures);
    }
}
