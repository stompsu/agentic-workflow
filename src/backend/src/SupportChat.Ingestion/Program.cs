using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SupportChat.Core.Options;
using SupportChat.Infrastructure.Azure;
using SupportChat.Ingestion;

// Usage: dotnet run --project src/SupportChat.Ingestion -- [--dry-run] [--allow-empty]
// Configuration: appsettings.json / user-secrets / env (Ingestion__*, Search__Endpoint, Search__IndexName, Azure__AuthMode,
// Foundry__ProjectEndpoint). Nothing here talks to Azure with --dry-run.
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddUserSecrets<IngestionOptions>(optional: true);
using var host = builder.Build();

var configuration = host.Services.GetRequiredService<IConfiguration>();
var environment = host.Services.GetRequiredService<IHostEnvironment>();
var dryRun = args.Contains("--dry-run");
var allowEmpty = args.Contains("--allow-empty");

var options = configuration.GetSection(IngestionOptions.SectionName).Get<IngestionOptions>() ?? new IngestionOptions();
var indexName = configuration["Search:IndexName"] ?? "support-knowledge";
Action<string> log = Console.WriteLine;

if (dryRun)
{
    await new IngestionRunner(options, indexName, null, null, log).RunAsync(true, allowEmpty, CancellationToken.None);
    return 0;
}

var failures = Validate(configuration, environment.EnvironmentName, options, out var mode);
if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 2;
}

var credentialProvider = new AzureCredentialProvider(environment, configuration);
var clients = new IngestionClients(
    mode, new Uri(configuration["Search:Endpoint"]!), indexName, options.SearchAdminKey, () => credentialProvider.Credential);
var embeddings = IngestionClients.CreateEmbeddingGenerator(
    new Uri(configuration["Foundry:ProjectEndpoint"]!), options.EmbeddingDeployment!, credentialProvider.Credential);

await new IngestionRunner(options, indexName, clients, embeddings, log).RunAsync(false, allowEmpty, CancellationToken.None);
return 0;

static List<string> Validate(IConfiguration configuration, string environmentName, IngestionOptions options, out AzureAuthMode mode)
{
    var failures = new List<string>();
    mode = AzureAuthMode.ManagedIdentity;
    if (!Enum.TryParse(configuration["Azure:AuthMode"], ignoreCase: true, out AzureAuthMode parsed) || !Enum.IsDefined(parsed))
    {
        failures.Add("Azure:AuthMode must be Key or ManagedIdentity.");
    }
    else
    {
        mode = parsed;
    }

    // Same rule as the API: keys never in Production.
    if (mode == AzureAuthMode.Key && string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
    {
        failures.Add("Azure:AuthMode=Key is not allowed in Production.");
    }

    if (mode == AzureAuthMode.Key && string.IsNullOrWhiteSpace(options.SearchAdminKey))
    {
        failures.Add("Ingestion:SearchAdminKey is required when Azure:AuthMode=Key.");
    }

    if (mode == AzureAuthMode.ManagedIdentity && !string.IsNullOrWhiteSpace(options.SearchAdminKey))
    {
        failures.Add("Ingestion:SearchAdminKey must not be set when Azure:AuthMode=ManagedIdentity.");
    }

    foreach (var key in new[] { "Search:Endpoint", "Foundry:ProjectEndpoint", "Ingestion:VectorizerResourceUri" })
    {
        if (!Uri.TryCreate(configuration[key], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || (configuration[key]?.Contains('<') ?? true))
        {
            failures.Add($"{key} must be an absolute https URI.");
        }
    }

    if (string.IsNullOrWhiteSpace(options.EmbeddingDeployment))
    {
        failures.Add("Ingestion:EmbeddingDeployment is required.");
    }

    return failures;
}
