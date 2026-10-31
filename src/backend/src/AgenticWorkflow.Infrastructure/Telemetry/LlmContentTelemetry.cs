using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;

namespace AgenticWorkflow.Infrastructure.Telemetry;

/// <summary>
/// Optional recording of everything sent to and received from the chat models (plan.md D14). Off by default and rejected
/// in Production by <see cref="Azure.AzureStartupValidator"/>. When off, nothing here is registered.
/// </summary>
public static class LlmContentTelemetry
{
    public const string SourceName = "AgenticWorkflow.Llm";
    public const string LogCategory = "AgenticWorkflow.LlmContent";
    public const string ConnectionStringKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    public static IChatClient Instrument(IChatClient client) =>
        client.AsBuilder()
            .UseOpenTelemetry(sourceName: SourceName, configure: telemetry => telemetry.EnableSensitiveData = true)
            .Build();

    public static IServiceCollection AddLlmContentTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue("Telemetry:LlmContentEnabled", false))
        {
            return services;
        }

        var connectionString = configuration[ConnectionStringKey];
        services.AddOpenTelemetry().WithTracing(tracing => tracing
            .AddSource(SourceName)
            // A logger of its own: the application's logs stay out of this export, and this content stays out of the console.
            .AddProcessor(_ => new LlmContentLogProcessor(LoggerFactory.Create(logging => logging.AddOpenTelemetry(otel =>
                otel.AddAzureMonitorLogExporter(exporter => Configure(exporter, connectionString))))))
            .AddAzureMonitorTraceExporter(exporter => Configure(exporter, connectionString)));
        return services;
    }

    private static void Configure(AzureMonitorExporterOptions exporter, string? connectionString)
    {
        exporter.ConnectionString = connectionString;
        exporter.SamplingRatio = 1f; // every call
        exporter.TracesPerSecond = null;
        exporter.DisableOfflineStorage = true; // never write prompts to the container's disk
    }
}

/// <summary>
/// Writes the content attributes of each model span as log records. Application Insights cuts a property at 8,192
/// characters, so a long prompt would otherwise be lost; parts keep all of it (join them on TraceId, SpanId, Kind, Part).
/// </summary>
internal sealed class LlmContentLogProcessor(ILoggerFactory loggerFactory) : BaseProcessor<Activity>
{
    internal const int MaxPartChars = 8000;

    private static readonly string[] ContentTags =
        ["gen_ai.system_instructions", "gen_ai.tool.definitions", "gen_ai.input.messages", "gen_ai.output.messages"];

    private readonly ILogger _logger = loggerFactory.CreateLogger(LlmContentTelemetry.LogCategory);

    public override void OnEnd(Activity activity)
    {
        if (activity.Source.Name != LlmContentTelemetry.SourceName)
        {
            return;
        }

        var deployment = activity.GetTagItem("gen_ai.request.model") as string;
        foreach (var kind in ContentTags)
        {
            if (activity.GetTagItem(kind) is not string { Length: > 0 } content)
            {
                continue;
            }

            var parts = Split(content);
            for (var i = 0; i < parts.Count; i++)
            {
#pragma warning disable CA1848, CA2254 // Low volume, non-production only.
                _logger.LogInformation(
                    "{Kind} part {Part}/{Parts} {Deployment} {TraceId} {SpanId} {Content}",
                    kind, i + 1, parts.Count, deployment, activity.TraceId.ToString(), activity.SpanId.ToString(), parts[i]);
#pragma warning restore CA1848, CA2254
            }
        }
    }

    internal static List<string> Split(string content)
    {
        var parts = new List<string>();
        for (var start = 0; start < content.Length;)
        {
            var length = Math.Min(MaxPartChars, content.Length - start);
            if (start + length < content.Length && char.IsHighSurrogate(content[start + length - 1]))
            {
                length--; // never split a surrogate pair
            }

            parts.Add(content.Substring(start, length));
            start += length;
        }

        return parts;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            loggerFactory.Dispose(); // flushes the log exporter
        }

        base.Dispose(disposing);
    }
}
