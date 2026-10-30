using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Infrastructure.Azure;
using AgenticWorkflow.Ingestion;

// Usage: dotnet run --project src/AgenticWorkflow.Ingestion -- [--dry-run] [--allow-empty]
// Configuration: appsettings.json / user-secrets / env (Ingestion__*, Search__Endpoint, Search__IndexName, Azure__AuthMode).
// Nothing here talks to Azure with --dry-run.
var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddUserSecrets<IngestionOptions>(optional: true);
using var host = builder.Build();

var configuration = host.Services.GetRequiredService<IConfiguration>();
var environment = host.Services.GetRequiredService<IHostEnvironment>();
var dryRun = args.Contains("--dry-run");
var allowEmpty = args.Contains("--allow-empty");

var options = configuration.GetSection(IngestionOptions.SectionName).Get<IngestionOptions>() ?? new IngestionOptions();
var indexName = configuration["Search:IndexName"] ?? "agentwf-knowledge";
Action<string> log = Console.WriteLine;

if (dryRun)
{
    await new IngestionRunner(options, indexName, null, null, log).RunAsync(true, allowEmpty, CancellationToken.None);
    return 0;
}

var failures = IngestionStartupValidator.Validate(configuration, environment.EnvironmentName, options, out var mode);
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
    new Uri(options.VectorizerResourceUri!), options.EmbeddingDeployment!, credentialProvider.Credential);

await new IngestionRunner(options, indexName, clients, embeddings, log).RunAsync(false, allowEmpty, CancellationToken.None);
return 0;
