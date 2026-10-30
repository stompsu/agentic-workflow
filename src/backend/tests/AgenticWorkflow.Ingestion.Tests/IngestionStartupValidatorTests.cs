using Microsoft.Extensions.Configuration;
using AgenticWorkflow.Core.Options;

namespace AgenticWorkflow.Ingestion.Tests;

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

    [Fact]
    public void Foundry_project_endpoint_is_not_required()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Azure:AuthMode"] = "ManagedIdentity",
            ["Search:Endpoint"] = "https://search.example.net",
            ["Ingestion:VectorizerResourceUri"] = "https://foundry.example.net",
        }).Build();
        var options = new IngestionOptions { EmbeddingDeployment = "embed" };

        Assert.Empty(IngestionStartupValidator.Validate(configuration, "Production", options, out _));
    }

    [Theory]
    [InlineData("<embedding-deployment>")]
    [InlineData("REPLACE_ME")]
    [InlineData("replace_me-x")]
    public void Placeholder_deployments_are_rejected(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Azure:AuthMode"] = "ManagedIdentity",
            ["Search:Endpoint"] = "https://search.example.net",
            ["Foundry:ProjectEndpoint"] = "https://foundry.example.net/api/projects/p",
            ["Ingestion:VectorizerResourceUri"] = "https://foundry.example.net",
        }).Build();

        var embedding = IngestionStartupValidator.Validate(
            configuration, "Production", new IngestionOptions { EmbeddingDeployment = value }, out _);
        Assert.Contains(embedding, f => f.StartsWith("Ingestion:EmbeddingDeployment") && !f.Contains(value));

        var vectorizer = IngestionStartupValidator.Validate(
            configuration, "Production", new IngestionOptions { EmbeddingDeployment = "embed", VectorizerDeployment = value }, out _);
        Assert.Contains(vectorizer, f => f.StartsWith("Ingestion:VectorizerDeployment") && !f.Contains(value));
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
