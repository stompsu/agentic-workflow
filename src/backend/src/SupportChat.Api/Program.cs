using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.ResponseCompression;
using SupportChat.Agents;
using SupportChat.Api.Auth;
using SupportChat.Api.Endpoints;
using SupportChat.Api.Http;
using SupportChat.Infrastructure;

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

// Workflow registrations go here: services.AddWorkflow<SomeWorkflow>() (ADR 0002). The echo workflow
// (Workflows:Echo:Enabled, ADR 0002 section 10) is registered by agent-workflows.
builder.Services.AddAgentWorkflows();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddChatAuth(builder.Configuration);

builder.Services.AddResponseCompression(options =>
{
    // The SSE route must never be compressed (api-contract 2.6 / 7).
    options.ExcludedMimeTypes = [.. (options.ExcludedMimeTypes ?? []), "text/event-stream"];
});

var app = builder.Build();

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    ExceptionHandler = context => Problems.WriteAsync(context, StatusCodes.Status500InternalServerError, Problems.Codes.InternalError),
});
app.UseStatusCodePages(context =>
    Problems.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode, Problems.CodeForStatus(context.HttpContext.Response.StatusCode)));
app.UseResponseCompression();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();

var api = app.MapGroup("/api").RequireAuthorization(AuthExtensions.ChatUserPolicy);
api.MapWorkflowEndpoints();
api.MapSessionEndpoints();

app.Run();

public partial class Program;
