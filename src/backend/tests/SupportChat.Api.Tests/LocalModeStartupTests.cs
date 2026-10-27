using SupportChat.Api.Http;

namespace SupportChat.Api.Tests;

/// <summary>ADR 0003 rule 11 / ADR 0005 G5: the production API refuses LocalMode:Enabled in every environment.</summary>
[Collection(StartupFailureCollection.Name)]
public class LocalModeStartupTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    [InlineData("Production")]
    public async Task LocalMode_enabled_does_not_start(string environment)
    {
        using var factory = environment == "Production"
            ? new ApiFactory(environment, c =>
            {
                c["Azure:AuthMode"] = "ManagedIdentity";
                c["Cosmos:Endpoint"] = "https://account.documents.azure.com:443/";
                c.Remove("Cosmos:Key");
                c.Remove("Search:Key");
                c["LocalMode:Enabled"] = "true";
            })
            : new ApiFactory(environment, c => c["LocalMode:Enabled"] = "true");

        var ex = await Record.ExceptionAsync(() => Task.Run(() => factory.CreateClient()));

        Assert.NotNull(ex);
        Assert.Contains(StartupFailure.Messages(ex), m => m.Contains("LocalMode:Enabled"));
    }
}
