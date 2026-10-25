using System.Net;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SupportChat.Agents;

namespace SupportChat.Api.Tests;

public class HealthTests
{
    [Theory]
    [InlineData("/healthz")]
    [InlineData("/api/healthz")]
    public async Task Liveness_is_anonymous_and_returns_only_status_ok(string path)
    {
        using var factory = new ApiFactory();
        var response = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/readyz")]
    [InlineData("/api/readyz")]
    public async Task Readiness_reflects_the_dependency_check_without_details(string path)
    {
        using var factory = new ApiFactory();
        var client = factory.CreateClient();

        var ready = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await ready.Content.ReadAsStringAsync());

        factory.Readiness.Ready = false;
        var down = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Equal("""{"status":"unavailable"}""", await down.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readiness_check_failures_are_reported_as_unavailable_not_as_errors()
    {
        using var factory = new ApiFactory(services: s => s.AddSingleton<SupportChat.Core.Health.IReadinessCheck, ThrowingReadiness>());
        var response = await factory.CreateClient().GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"status":"unavailable"}""", await response.Content.ReadAsStringAsync());
    }

    private sealed class ThrowingReadiness : SupportChat.Core.Health.IReadinessCheck
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("SECRET");
    }

    [Fact]
    public void Response_compression_excludes_the_SSE_content_type()
    {
        using var factory = new ApiFactory();
        var options = factory.Services.GetRequiredService<IOptions<ResponseCompressionOptions>>().Value;
        Assert.Contains("text/event-stream", options.ExcludedMimeTypes);
        Assert.DoesNotContain("text/event-stream", options.MimeTypes);
    }
}

/// <summary>The real host refuses to start with invalid configuration (ADR 0003 section 4).</summary>
public class StartupTests
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

    [Theory]
    [InlineData("Production")]
    [InlineData("production")]
    public async Task Production_with_Key_mode_does_not_start(string env)
    {
        var message = await StartFailureAsync(new ApiFactory(env));
        Assert.Contains("ManagedIdentity in Production", message);
    }

    [Fact]
    public async Task Production_with_a_configured_key_does_not_start_even_in_ManagedIdentity_mode()
    {
        var message = await StartFailureAsync(new ApiFactory("Production", c =>
        {
            c["Azure:AuthMode"] = "ManagedIdentity";
            c.Remove("Search:Key");
        }));
        Assert.Contains("Cosmos:Key must not be configured in Production", message);
        Assert.DoesNotContain("dGVzdC1rZXk=", message);
    }

    [Fact]
    public async Task Echo_enabled_in_Production_does_not_start()
    {
        var message = await StartFailureAsync(new ApiFactory("Production", c =>
        {
            c["Azure:AuthMode"] = "ManagedIdentity";
            c["Cosmos:Endpoint"] = "https://account.documents.azure.com:443/";
            c.Remove("Cosmos:Key");
            c.Remove("Search:Key");
            c["Workflows:Echo:Enabled"] = "true";
        }));
        Assert.Contains("Workflows:Echo:Enabled", message);
    }

    [Fact]
    public async Task A_non_echo_workflow_without_a_Foundry_endpoint_does_not_start()
    {
        var message = await StartFailureAsync(new ApiFactory(services: s => s.AddWorkflow<ThrowawayWorkflow>()));
        Assert.Contains("Foundry:ProjectEndpoint", message);
    }

    [Fact]
    public async Task Missing_AuthMode_does_not_start()
    {
        var message = await StartFailureAsync(new ApiFactory(configure: c => c["Azure:AuthMode"] = ""));
        Assert.Contains("Azure:AuthMode", message);
    }

    [Fact]
    public async Task Valid_Production_ManagedIdentity_configuration_starts_without_building_Azure_clients()
    {
        using var factory = new ApiFactory("Production", c =>
        {
            c["Azure:AuthMode"] = "ManagedIdentity";
            c["Cosmos:Endpoint"] = "https://account.documents.azure.com:443/";
            c.Remove("Cosmos:Key");
            c.Remove("Search:Key");
        });
        var response = await factory.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Echo_only_registry_starts_without_a_Foundry_endpoint()
    {
        using var factory = new ApiFactory(services: s => s.AddWorkflow<FakeEcho>());
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/healthz")).StatusCode);
    }

    private sealed class FakeEcho : SupportChat.Core.Workflows.IWorkflowDefinition
    {
        public string Id => "echo";

        public string DisplayName => "Echo";

        public string Description => "";

        public IAsyncEnumerable<SupportChat.Core.Workflows.ChatStreamEvent> RunAsync(
            SupportChat.Core.Workflows.WorkflowRunRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
