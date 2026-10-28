using System.Net;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AgenticWorkflow.Agents;

namespace AgenticWorkflow.Api.Tests;

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
        using var factory = new ApiFactory(services: s => s.AddSingleton<AgenticWorkflow.Core.Health.IReadinessCheck, ThrowingReadiness>());
        var response = await factory.CreateClient().GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"status":"unavailable"}""", await response.Content.ReadAsStringAsync());
    }

    private sealed class ThrowingReadiness : AgenticWorkflow.Core.Health.IReadinessCheck
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

/// <summary>
/// Tests whose host is expected to fail during start run alone: WebApplicationFactory resolves the host through
/// process-wide diagnostic hooks, and a failed start tears the host down (disposing its services) while other
/// factories are being built in parallel. Serialising these removes that cross-talk (observed once as
/// "Cannot access a disposed object" instead of the validation message; startup itself is deterministic).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StartupFailureCollection
{
    public const string Name = "Startup failures";
}

/// <summary>The real host refuses to start with invalid configuration (ADR 0003 section 4).</summary>
[Collection(StartupFailureCollection.Name)]
public class StartupTests
{
    private static async Task<string> StartFailureAsync(ApiFactory factory)
    {
        using (factory)
        {
            var ex = await Record.ExceptionAsync(() => Task.Run(() => factory.CreateClient()));
            Assert.NotNull(ex);
            var messages = new List<string>();
            Collect(ex, messages);

            return string.Join(" | ", messages);
        }
    }

    private static void Collect(Exception? e, List<string> messages)
    {
        for (; e is not null; e = e.InnerException)
        {
            messages.Add(e.Message);
            if (e is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    Collect(inner, messages);
                }
            }
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
    public async Task SupportTicket_is_always_registered_so_a_missing_a_Foundry_endpoint_does_not_start()
    {
        var message = await StartFailureAsync(new ApiFactory(configure: c => c.Remove("Foundry:ProjectEndpoint")));
        Assert.Contains("Foundry:ProjectEndpoint", message);
    }

    [Fact]
    public async Task Missing_RetentionDays_outside_Development_does_not_start()
    {
        var message = await StartFailureAsync(new ApiFactory(configure: c => c.Remove("Chat:RetentionDays")));
        Assert.Contains("Chat:RetentionDays", message);
    }

    [Fact]
    public async Task Out_of_range_DeleteUserDataBudget_does_not_start()
    {
        var message = await StartFailureAsync(new ApiFactory(configure: c => c["Chat:DeleteUserDataBudget"] = "00:00:01"));
        Assert.Contains("Chat:DeleteUserDataBudget", message);
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
    public async Task Missing_SupportTicket_deployment_does_not_start()
    {
        var message = await StartFailureAsync(new ApiFactory(configure: c => c["Workflows:SupportTicket:Agents:Claims:Deployment"] = ""));
        Assert.Contains("Claims:Deployment", message);
    }

    [Fact]
    public async Task Appsettings_placeholder_deployments_without_overrides_do_not_start()
    {
        var message = await StartFailureAsync(new ApiFactory(configure: c =>
        {
            foreach (var key in new[] { "Orchestrator", "Policy", "Claims", "HealthAssessments" })
            {
                c.Remove($"Workflows:SupportTicket:Agents:{key}:Deployment");
            }
        }));
        Assert.Contains("Orchestrator:Deployment", message);
        Assert.Contains("HealthAssessments:Deployment", message);
        Assert.DoesNotContain("chat-deployment", message);
    }

    private sealed class FakeEcho : AgenticWorkflow.Core.Workflows.IWorkflowDefinition
    {
        public string Id => "echo";

        public string DisplayName => "Echo";

        public string Description => "";

        public IAsyncEnumerable<AgenticWorkflow.Core.Workflows.ChatStreamEvent> RunAsync(
            AgenticWorkflow.Core.Workflows.WorkflowRunRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
