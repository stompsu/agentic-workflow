using SupportChat.Core.Workflows;
using SupportChat.Infrastructure.Azure;

namespace SupportChat.Infrastructure.Tests.Azure;

public class AzureStartupValidatorTests
{
    private static readonly WorkflowDescriptor Echo = new("echo", "Echo", "Echo");
    private static readonly WorkflowDescriptor Real = new("support-ticket", "Support ticket", "Real");

    private static IReadOnlyList<string> Run(Dictionary<string, string?> config, string env, params WorkflowDescriptor[] workflows) =>
        AzureStartupValidator.Validate(TestConfig.Build(config), env, workflows);

    [Theory]
    [InlineData("Development")]
    [InlineData("Dev")]
    [InlineData("Test")]
    [InlineData("test")]
    public void Key_mode_is_valid_in_non_production_environments(string env)
    {
        var config = TestConfig.Development();
        config["AZURE_CLIENT_ID"] = "id";
        Assert.Empty(Run(config, env));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData("PRODUCTION")]
    public void ManagedIdentity_without_keys_is_valid_in_production(string env) =>
        Assert.Empty(Run(TestConfig.Production(), env));

    [Theory]
    [InlineData("key")]
    [InlineData("KEY")]
    [InlineData("managedidentity")]
    public void AuthMode_is_case_insensitive(string mode)
    {
        var config = TestConfig.Development();
        config["AZURE_CLIENT_ID"] = "id";
        config["Azure:AuthMode"] = mode;
        Assert.Empty(Run(config, "Dev"));
    }

    // Rule 1
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Certificate")]
    public void Missing_or_invalid_AuthMode_fails(string? mode)
    {
        var config = TestConfig.Development();
        config["Azure:AuthMode"] = mode;
        Assert.Contains(Run(config, "Development"), f => f.Contains("Azure:AuthMode"));
    }

    // Rule 2
    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    public void Production_with_Key_mode_fails(string env)
    {
        var config = TestConfig.Development();
        config["AZURE_CLIENT_ID"] = "id";
        Assert.Contains(Run(config, env), f => f.Contains("ManagedIdentity in Production"));
    }

    // Rule 3
    [Theory]
    [InlineData("Prod")]
    [InlineData("Staging")]
    [InlineData("")]
    public void Key_mode_outside_the_allow_list_fails(string env)
    {
        var config = TestConfig.Development();
        config["AZURE_CLIENT_ID"] = "id";
        Assert.Contains(Run(config, env), f => f.Contains("allowed only in Development, Dev and Test"));
    }

    // Rule 4
    [Theory]
    [InlineData("Cosmos:Key")]
    [InlineData("Search:Key")]
    public void Key_mode_requires_both_keys(string missing)
    {
        var config = TestConfig.Development();
        config[missing] = "";
        Assert.Contains(Run(config, "Development"), f => f.Contains(missing));
    }

    // Rule 5
    [Theory]
    [InlineData("Cosmos:Key")]
    [InlineData("Search:Key")]
    public void Any_key_present_in_production_fails_even_in_ManagedIdentity_mode(string key)
    {
        var config = TestConfig.Production();
        config[key] = "something";
        Assert.Contains(Run(config, "Production"), f => f.Contains($"{key} must not be configured in Production"));
    }

    // Rule 6
    [Theory]
    [InlineData("Dev")]
    [InlineData("Test")]
    [InlineData("Production")]
    public void AZURE_CLIENT_ID_is_required_outside_Development(string env)
    {
        var config = env == "Production" ? TestConfig.Production() : TestConfig.Development();
        config.Remove("AZURE_CLIENT_ID");
        Assert.Contains(Run(config, env), f => f.Contains("AZURE_CLIENT_ID"));
    }

    [Fact]
    public void AZURE_CLIENT_ID_is_not_required_in_Development() =>
        Assert.DoesNotContain(Run(TestConfig.Development(), "development"), f => f.Contains("AZURE_CLIENT_ID"));

    // Rule 7
    [Theory]
    [InlineData("Cosmos:Endpoint")]
    [InlineData("Search:Endpoint")]
    [InlineData("AzureAd:Instance")]
    [InlineData("AzureAd:TenantId")]
    [InlineData("AzureAd:ClientId")]
    public void Required_endpoints_and_AzureAd_settings_must_exist(string key)
    {
        var config = TestConfig.Development();
        config.Remove(key);
        Assert.Contains(Run(config, "Development"), f => f.Contains(key));
    }

    [Theory]
    [InlineData("AzureAd:TenantId", "<tenant-guid>")]
    [InlineData("AzureAd:ClientId", "<api-app-client-id>")]
    [InlineData("AzureAd:TenantId", "not-a-guid")]
    [InlineData("AzureAd:ClientId", "REPLACE_ME")]
    public void Placeholder_or_malformed_GUIDs_fail_without_echoing_the_value(string key, string value)
    {
        var config = TestConfig.Development();
        config[key] = value;
        var failures = Run(config, "Development");
        Assert.Contains(failures, f => f.Contains(key));
        Assert.DoesNotContain(failures, f => f.Contains(value));
    }

    [Theory]
    [InlineData("AzureAd:TenantId")]
    [InlineData("AzureAd:ClientId")]
    public void All_zero_GUIDs_fail(string key)
    {
        var config = TestConfig.Development();
        config[key] = "00000000-0000-0000-0000-000000000000";
        Assert.Contains(Run(config, "Development"), f => f.Contains(key));
    }

    [Theory]
    [InlineData("AzureAd:Instance", "https://REPLACE_ME.ciamlogin.com/")]
    [InlineData("AzureAd:Instance", "https://<tenant-subdomain>.ciamlogin.com/")]
    [InlineData("Cosmos:Endpoint", "https://replace_me.documents.azure.com:443/")]
    [InlineData("Search:Endpoint", "https://<service>.search.windows.net")]
    public void Placeholder_URIs_fail(string key, string value)
    {
        var config = TestConfig.Development();
        config[key] = value;
        var failures = Run(config, "Development");
        Assert.Contains(failures, f => f.Contains(key));
        Assert.DoesNotContain(failures, f => f.Contains(value));
    }

    [Theory]
    [InlineData("AzureAd:Instance", "http://tenant.ciamlogin.com/")]
    [InlineData("Search:Endpoint", "http://example.search.windows.net")]
    [InlineData("Cosmos:Endpoint", "http://account.documents.azure.com:443/")]
    [InlineData("Search:Endpoint", "example.search.windows.net")]
    [InlineData("Search:Endpoint", "https://localhost:9200")]
    public void Non_https_or_relative_URIs_fail(string key, string value)
    {
        var config = TestConfig.Development();
        config[key] = value;
        Assert.Contains(Run(config, "Development"), f => f.Contains(key));
    }

    [Fact]
    public void Cosmos_emulator_https_localhost_is_allowed_only_outside_production()
    {
        Assert.Empty(Run(TestConfig.Development(), "Development"));
        var prod = TestConfig.Production();
        prod["Cosmos:Endpoint"] = "https://localhost:8081/";
        Assert.Contains(Run(prod, "Production"), f => f.Contains("Cosmos:Endpoint"));
    }

    [Fact]
    public void Placeholder_Foundry_endpoint_fails_when_required()
    {
        var config = TestConfig.Development();
        config["Foundry:ProjectEndpoint"] = "https://<resource>.services.ai.azure.com/api/projects/<p>";
        Assert.Contains(Run(config, "Development", Real), f => f.Contains("Foundry:ProjectEndpoint"));
        Assert.Empty(Run(config, "Development", Echo));
    }

    [Fact]
    public void Fully_valid_configuration_passes() =>
        Assert.Empty(Run(TestConfig.Production(), "Production", Echo));

    [Fact]
    public void Echo_only_registry_starts_without_a_Foundry_endpoint() =>
        Assert.Empty(Run(TestConfig.Development(), "Development", Echo));

    [Fact]
    public void Empty_registry_starts_without_a_Foundry_endpoint() =>
        Assert.Empty(Run(TestConfig.Development(), "Development"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Any_non_echo_workflow_requires_the_Foundry_endpoint(bool withEcho)
    {
        var workflows = withEcho ? new[] { Echo, Real } : [Real];
        Assert.Contains(Run(TestConfig.Development(), "Development", workflows), f => f.Contains("Foundry:ProjectEndpoint"));
    }

    [Fact]
    public void Non_echo_workflow_with_a_Foundry_endpoint_starts()
    {
        var config = TestConfig.Development();
        config["Foundry:ProjectEndpoint"] = "https://res.services.ai.azure.com/api/projects/p";
        Assert.Empty(Run(config, "Development", Real));
    }

    // Rule 8
    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    [InlineData("Staging")]
    public void Echo_enabled_outside_the_allow_list_fails(string env)
    {
        var config = env == "Production" || env == "production" ? TestConfig.Production() : TestConfig.Development();
        config["AZURE_CLIENT_ID"] = "id";
        config["Workflows:Echo:Enabled"] = "true";
        Assert.Contains(Run(config, env), f => f.Contains("Workflows:Echo:Enabled"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Dev")]
    [InlineData("Test")]
    public void Echo_enabled_is_allowed_in_non_production_environments(string env)
    {
        var config = TestConfig.Development();
        config["AZURE_CLIENT_ID"] = "id";
        config["Workflows:Echo:Enabled"] = "true";
        Assert.Empty(Run(config, env, Echo));
    }

    [Fact]
    public void Failure_messages_never_contain_secret_values()
    {
        var config = TestConfig.Development();
        config["Cosmos:Key"] = "SECRET-COSMOS-VALUE";
        config["Search:Key"] = "SECRET-SEARCH-VALUE";
        var failures = Run(config, "Production");
        Assert.NotEmpty(failures);
        Assert.DoesNotContain(failures, f => f.Contains("SECRET"));
    }
}
