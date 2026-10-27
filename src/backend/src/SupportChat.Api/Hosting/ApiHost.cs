using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using SupportChat.Agents;
using SupportChat.Api.Auth;
using SupportChat.Api.Chat;
using SupportChat.Api.Endpoints;
using SupportChat.Api.Http;
using SupportChat.Core.Options;

namespace SupportChat.Api.Hosting;

/// <summary>
/// The Api composition, shared by the production entry point and other hosts (ADR 0005 section 1). The registration
/// and middleware order is the one the Api.Tests regression snapshots pin; the two host-specific steps are callbacks
/// invoked at the position the production calls occupy.
/// </summary>
public static class ApiHost
{
    /// <summary>
    /// Registration order: Kestrel, HTTP JSON options, AddAgentWorkflows, <paramref name="addPlatform"/>,
    /// <paramref name="addJwtBearer"/>, AddChatAuthorization, rate limiting, ChatLimitsOptions validation,
    /// ChatTurnRunner, response compression.
    /// </summary>
    public static void ConfigureServices(
        WebApplicationBuilder builder,
        Action<IServiceCollection, IConfiguration> addPlatform,
        Action<IServiceCollection, IConfiguration> addJwtBearer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(addPlatform);
        ArgumentNullException.ThrowIfNull(addJwtBearer);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = RequestBody.MaxBytes;
        });

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            var shared = ApiJson.Options;
            options.SerializerOptions.PropertyNamingPolicy = shared.PropertyNamingPolicy;
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
            foreach (var converter in shared.Converters)
            {
                options.SerializerOptions.Converters.Add(converter);
            }
        });

        // Workflow registrations live in AddAgentWorkflows (SupportTicket always; echo only when Workflows:Echo:Enabled).
        // Requires IChatClientFactory + IKnowledgeSearch, registered by the platform callback.
        builder.Services.AddAgentWorkflows(builder.Configuration);
        addPlatform(builder.Services, builder.Configuration);
        addJwtBearer(builder.Services, builder.Configuration);
        builder.Services.AddChatAuthorization(builder.Configuration);
        builder.Services.AddChatRateLimiting();
        builder.Services.AddSingleton<IValidateOptions<ChatLimitsOptions>, ChatOptionsValidator>();
        builder.Services.AddOptions<ChatLimitsOptions>().ValidateOnStart();
        builder.Services.AddSingleton<ChatTurnRunner>();

        builder.Services.AddResponseCompression(options =>
        {
            // The SSE route must never be compressed (api-contract 2.6 / 7).
            options.ExcludedMimeTypes = [.. (options.ExcludedMimeTypes ?? []), "text/event-stream"];
        });
    }

    /// <summary>Middleware pipeline and endpoints, in the order the regression probes pin.</summary>
    public static void MapApi(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = context => Problems.WriteAsync(context, StatusCodes.Status500InternalServerError, Problems.Codes.InternalError),
        });
        app.UseStatusCodePages(context =>
            Problems.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode, Problems.CodeForStatus(context.HttpContext.Response.StatusCode)));
        app.UseResponseCompression();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapHealthEndpoints();

        var api = app.MapGroup("/api").RequireAuthorization(AuthExtensions.ChatUserPolicy);
        api.MapWorkflowEndpoints();
        api.MapSessionEndpoints();
        api.MapMeEndpoints();
        api.MapChatEndpoints();
    }

    /// <summary>
    /// Runs the host. A failed host start (startup validation, ADR 0003 section 4) must end the process with exit code 1
    /// and key-name-only messages. Letting the exception escape Main hung the container on crash handling (100% CPU,
    /// no exit). <paramref name="hostAssembly"/> is the entry assembly of the calling host.
    /// </summary>
    public static async Task<int> RunAsync(WebApplication app, Assembly hostAssembly)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(hostAssembly);

        var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
        try
        {
            await app.RunAsync();
            return 0;
        }
        catch (Exception ex) when (ex is not Microsoft.Extensions.Hosting.HostAbortedException) // tooling/test hosts abort Run on purpose
        {
            var failures = StartupFailure.Messages(ex);
            try
            {
                startupLogger.LogCritical("Startup failed: {Failures}", string.Join(" | ", failures));
            }
            catch (ObjectDisposedException)
            {
                // The host (and its logger providers) is already disposed after a failed start; fall back to stderr.
                await Console.Error.WriteLineAsync("Startup failed: " + string.Join(" | ", failures));
            }

            // Hosted by a test/tooling host (WebApplicationFactory): let it observe the original exception.
            if (!string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, hostAssembly.GetName().Name, StringComparison.Ordinal))
            {
                throw;
            }

            return 1;
        }
    }
}
