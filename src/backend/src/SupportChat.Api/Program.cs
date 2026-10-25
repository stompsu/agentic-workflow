using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.ResponseCompression;
using SupportChat.Agents;
using Microsoft.Extensions.Options;
using SupportChat.Api.Auth;
using SupportChat.Api.Chat;
using SupportChat.Api.Endpoints;
using SupportChat.Api.Http;
using SupportChat.Core.Options;
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

// Workflow registrations live in AddAgentWorkflows (SupportTicket always; echo only when Workflows:Echo:Enabled).
// Requires IChatClientFactory + IKnowledgeSearch, registered by AddInfrastructure.
builder.Services.AddAgentWorkflows(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddChatAuth(builder.Configuration);
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
api.MapChatEndpoints();

app.Run();

public partial class Program;
