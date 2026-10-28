using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.ResponseCompression;
using AgenticWorkflow.Agents;
using Microsoft.Extensions.Options;
using AgenticWorkflow.Api.Auth;
using AgenticWorkflow.Api.Chat;
using AgenticWorkflow.Api.Endpoints;
using AgenticWorkflow.Api.Guests;
using AgenticWorkflow.Api.Http;
using AgenticWorkflow.Core.Options;
using AgenticWorkflow.Infrastructure;
using AgenticWorkflow.Infrastructure.Smoke;

var builder = WebApplication.CreateBuilder(args);

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
// Requires IChatClientFactory + IKnowledgeSearch, registered by AddInfrastructure.
builder.Services.AddAgentWorkflows(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddChatAuth(builder.Configuration);
builder.Services.AddGuestAccess();
builder.Services.AddChatRateLimiting();
builder.Services.AddSingleton<IValidateOptions<ChatLimitsOptions>, ChatOptionsValidator>();
builder.Services.AddOptions<ChatLimitsOptions>().ValidateOnStart();
builder.Services.AddSingleton<ChatTurnRunner>();

builder.Services.AddResponseCompression(options =>
{
    // The SSE route must never be compressed (api-contract 2.6 / 7).
    options.ExcludedMimeTypes = [.. (options.ExcludedMimeTypes ?? []), "text/event-stream"];
});

var app = builder.Build();

// ADR 0003 section 1a: run the managed-identity checks and exit; never serve HTTP.
if (args.Contains(SmokeCheckCommand.Flag))
{
    return await SmokeCheckCommand.RunAsync(app.Services);
}

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
app.MapGuestEndpoints(); // POST /api/guest/token: anonymous, 404 guest_access_disabled when guests are off

var api = app.MapGroup("/api").RequireAuthorization(AuthExtensions.ChatUserPolicy);
api.AddEndpointFilter<GuestActivityFilter>(); // ended guest -> 401 guest_expired (skipped for DELETE /api/me/data)
api.MapWorkflowEndpoints();
api.MapSessionEndpoints();
api.MapMeEndpoints();
api.MapChatEndpoints();

// A failed host start (startup validation, ADR 0003 section 4) must end the process with exit code 1 and key-name-only
// messages. Letting the exception escape Main hung the container on crash handling (100% CPU, no exit).
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
    if (!string.Equals(System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name, typeof(Program).Assembly.GetName().Name, StringComparison.Ordinal))
    {
        throw;
    }

    return 1;
}

public partial class Program;
