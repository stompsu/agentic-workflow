#pragma warning disable OPENAI001
using Azure.AI.Projects;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportChat.Core.Options;
using SupportChat.Infrastructure.Azure;

namespace SupportChat.Infrastructure.Smoke;

/// <summary>One read-only connectivity/authorization probe for the managed-identity smoke check (ADR 0003 section 1a).</summary>
public interface ISmokeProbe
{
    string Name { get; }

    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>Cosmos: reads the configured container's metadata (proves endpoint + data-plane auth). Writes nothing.</summary>
public sealed class CosmosSmokeProbe(IAzureClientFactory factory, IOptions<CosmosOptions> options) : ISmokeProbe
{
    public string Name => "Cosmos";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var client = factory.CreateCosmosClient();
        await client.GetContainer(options.Value.DatabaseName, options.Value.ContainerName)
            .ReadContainerAsync(cancellationToken: cancellationToken);
    }
}

/// <summary>Search: document count of the configured index (read-only; allowed by "Search Index Data Reader").</summary>
public sealed class SearchSmokeProbe(IAzureClientFactory factory, IOptions<SearchServiceOptions> options) : ISmokeProbe
{
    public string Name => "Search";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var client = factory.CreateSearchClient(options.Value.IndexName);
        await client.GetDocumentCountAsync(cancellationToken);
    }
}

/// <summary>
/// Foundry: acquires a token for the Foundry scope with the app's TokenCredential and, when Foundry:ProjectEndpoint is set,
/// lists deployments (project metadata; no model is invoked). Azure.AI.Projects 3.0.0-beta.3 has this cheap call.
/// </summary>
public sealed class FoundrySmokeProbe : ISmokeProbe
{
    public const string Scope = "https://ai.azure.com/.default";

    private readonly IAzureCredentialProvider _credentials;
    private readonly IOptions<FoundryOptions> _options;
    private readonly Func<Uri, TokenCredential, CancellationToken, Task> _metadataCall;

    public FoundrySmokeProbe(IAzureCredentialProvider credentials, IOptions<FoundryOptions> options)
        : this(credentials, options, ListDeploymentsAsync)
    {
    }

    internal FoundrySmokeProbe(
        IAzureCredentialProvider credentials, IOptions<FoundryOptions> options, Func<Uri, TokenCredential, CancellationToken, Task> metadataCall)
    {
        _credentials = credentials;
        _options = options;
        _metadataCall = metadataCall;
    }

    public string Name => "Foundry";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var credential = _credentials.Credential;
        await credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken);

        var endpoint = _options.Value.ProjectEndpoint;
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            await _metadataCall(new Uri(endpoint), credential, cancellationToken);
        }
    }

    private static async Task ListDeploymentsAsync(Uri endpoint, TokenCredential credential, CancellationToken cancellationToken)
    {
        var client = new AIProjectClient(endpoint, credential);
        await foreach (var _ in client.Deployments.GetDeploymentsAsync(cancellationToken: cancellationToken))
        {
            break; // first page is enough
        }
    }
}

/// <summary>
/// Runs every probe with its own timeout. Logs the check name and the exception TYPE only (never a message, which may
/// carry endpoints or secrets). Returns the process exit code: 0 only when every probe passed.
/// </summary>
public sealed class SmokeCheckRunner(IEnumerable<ISmokeProbe> probes, ILogger<SmokeCheckRunner> logger)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public async Task<int> RunAsync(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var failed = 0;
        foreach (var probe in probes)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout ?? DefaultTimeout);
            try
            {
                await probe.RunAsync(cts.Token);
                logger.LogInformation("Smoke check {Check}: PASS", probe.Name);
            }
            catch (Exception ex)
            {
                failed++;
                logger.LogError("Smoke check {Check}: FAIL ({ExceptionType})", probe.Name, ex.GetType().Name);
            }
        }

        if (failed == 0)
        {
            logger.LogInformation("Smoke check: all checks passed");
            return 0;
        }

        logger.LogError("Smoke check: {Failed} check(s) failed", failed);
        return 1;
    }
}
