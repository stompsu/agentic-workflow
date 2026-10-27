using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration.Json;
using SupportChat.Api.Auth;
using SupportChat.Api.Hosting;
using SupportChat.Infrastructure.Smoke;
using SupportChat.LocalHost.Auth;
using SupportChat.LocalHost.Guards;
using SupportChat.LocalHost.Platform;

namespace SupportChat.LocalHost;

/// <summary>
/// Entry point of the local-only executable (ADR 0005). A class with a static <c>Main</c> rather than a static class, so
/// it can be the entry-point type argument of WebApplicationFactory in tests. Order: --smoke-check refusal (G7), G1-G3,
/// G4(a), ConfigureServices, Build, token endpoint (Auth=Local), MapApi, G4(b) hook, RunAsync.
/// </summary>
public sealed class LocalProgram
{
    private LocalProgram()
    {
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains(SmokeCheckCommand.Flag, StringComparer.Ordinal))
        {
            await Console.Error.WriteLineAsync("The LocalHost does not support --smoke-check.");
            return 1;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        InsertApiBaseConfiguration(builder.Configuration);

        var failures = LocalModeGuards.CheckEnvironment(builder.Configuration, builder.Environment.EnvironmentName)
            .Concat(LocalModeGuards.CheckUrls(builder.Configuration))
            .ToList();
        if (failures.Count > 0)
        {
            await Console.Error.WriteLineAsync("Startup failed: " + string.Join(" | ", failures));
            return 1;
        }

        ApiHost.ConfigureServices(
            builder,
            (services, config) => services.AddLocalPlatform(config),
            AddJwtBearer);

        var app = builder.Build();
        app.MapLocalAuth();
        ApiHost.MapApi(app);
        var binding = LocalModeGuards.AttachBindingGuard(app);

        var exitCode = await ApiHost.RunAsync(app, typeof(LocalProgram).Assembly);
        return binding.Violated ? 1 : exitCode;
    }

    /// <summary>Auth=Entra: exactly the production scheme; Auth=Local (default): the local issuer's key.</summary>
    internal static void AddJwtBearer(IServiceCollection services, IConfiguration configuration)
    {
        if (LocalModeOptions.From(configuration).Auth == LocalAuthMode.Entra)
        {
            services.AddEntraJwtBearer(configuration);
        }
        else
        {
            services.AddLocalJwtBearer(configuration);
        }
    }

    /// <summary>The Api appsettings.json (linked as appsettings.Api.json) becomes the first, lowest-priority JSON source.</summary>
    private static void InsertApiBaseConfiguration(ConfigurationManager configuration)
    {
        var sources = ((IConfigurationBuilder)configuration).Sources;
        var firstJson = sources.Select((s, i) => (s, i)).FirstOrDefault(x => x.s is JsonConfigurationSource);
        var index = firstJson.s is null ? sources.Count : firstJson.i;
        var apiBase = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.Api.json", optional: false, reloadOnChange: false);
        sources.Insert(index, apiBase.Sources[0]);
    }
}
