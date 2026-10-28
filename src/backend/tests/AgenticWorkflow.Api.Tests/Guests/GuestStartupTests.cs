using System.Net;
using System.Net.Http.Headers;
using AgenticWorkflow.Api.Tests;

namespace AgenticWorkflow.Api.Tests.Guests;

/// <summary>The real host and guest configuration (ADR 0003 rules 12-17; ADR 0006 section 5.5).</summary>
[Collection(StartupFailureCollection.Name)]
public class GuestStartupFailureTests
{
    private static async Task<string> StartFailureAsync(ApiFactory factory)
    {
        using (factory)
        {
            var ex = await Record.ExceptionAsync(() => Task.Run(() => factory.CreateClient()));
            Assert.NotNull(ex);
            var messages = new List<string>();
            for (var e = ex; e is not null; e = e.InnerException)
            {
                messages.Add(e.Message);
            }

            return string.Join(" | ", messages);
        }
    }

    [Fact]
    public async Task Guests_enabled_without_an_issuer_do_not_start()
    {
        var message = await StartFailureAsync(new ApiFactory("Test", c =>
        {
            c["Guest:Enabled"] = "true";
            c["Guest:SigningKeyUri"] = "https://kv.vault.azure.net/keys/guest-token-signing";
        }));
        Assert.Contains("Guest:Issuer", message);
    }

    [Theory]
    [InlineData("Test")]
    [InlineData("Dev")]
    public async Task Ephemeral_signing_outside_Development_does_not_start(string env)
    {
        var message = await StartFailureAsync(new ApiFactory(env, c =>
        {
            c["Guest:Enabled"] = "true";
            c["Guest:Issuer"] = "agentwf-guest-test";
            c["Guest:SigningMode"] = "Ephemeral";
        }));
        Assert.Contains("Guest:SigningMode=Ephemeral", message);
    }

    [Fact]
    public async Task A_versioned_signing_key_uri_does_not_start()
    {
        var message = await StartFailureAsync(new ApiFactory("Test", c =>
        {
            c["Guest:Enabled"] = "true";
            c["Guest:Issuer"] = "agentwf-guest-test";
            c["Guest:SigningKeyUri"] = "https://kv.vault.azure.net/keys/guest-token-signing/0123456789abcdef0123456789abcdef";
        }));
        Assert.Contains("Guest:SigningKeyUri", message);
    }

    [Fact]
    public async Task An_invalid_DataTtl_does_not_start_even_with_guests_disabled()
    {
        var message = await StartFailureAsync(new ApiFactory("Test", c => c["Guest:DataTtl"] = "8.00:00:00"));
        Assert.Contains("Guest:DataTtl", message);
    }

    [Fact]
    public async Task The_unregistered_guest_workflow_list_does_not_start()
    {
        var message = await StartFailureAsync(new ApiFactory("Test", c =>
        {
            c["Guest:Enabled"] = "true";
            c["Guest:Issuer"] = "agentwf-guest-test";
            c["Guest:SigningKeyUri"] = "https://kv.vault.azure.net/keys/guest-token-signing";
            c["Guest:Workflows:0"] = "nope";
        }));
        Assert.Contains("Guest:Workflows", message);
    }

    [Fact]
    public async Task Guest_audience_equal_to_the_Entra_client_id_does_not_start()
    {
        var message = await StartFailureAsync(new ApiFactory("Test", c =>
        {
            c["Guest:Enabled"] = "true";
            c["Guest:Issuer"] = "agentwf-guest-test";
            c["Guest:SigningKeyUri"] = "https://kv.vault.azure.net/keys/guest-token-signing";
            c["Guest:Audience"] = TestTokens.Audience;
        }));
        Assert.Contains("Guest:Audience", message);
    }
}

/// <summary>Local Development: the in-process ephemeral signer end to end, with no fake signer registered.</summary>
public class GuestEphemeralDevelopmentTests
{
    [Fact]
    public async Task Development_with_the_ephemeral_signer_issues_and_accepts_guest_tokens()
    {
        using var factory = new ApiFactory("Development", c =>
        {
            c["Guest:Enabled"] = "true";
            c["Guest:SigningMode"] = "Ephemeral";
            c["Guest:Issuer"] = "agentwf-guest-local";
        });
        var client = factory.CreateClient();

        var response = await client.PostAsync("/api/guest/token", Helpers.Json("{}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.ReadJsonAsync())["accessToken"]!.GetValue<string>();
        Assert.Equal("ES256", Jws.Header(token)["alg"]!.GetValue<string>());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/workflows")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sessions")).StatusCode);
    }
}
