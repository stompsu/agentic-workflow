using AgenticWorkflow.Core.Health;

namespace AgenticWorkflow.Api.Endpoints;

/// <summary>
/// Liveness / readiness. Bodies never contain config, versions or errors. /healthz and /readyz are aliases for the
/// Container Apps probes on the backend port (not proxied by nginx).
/// </summary>
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var prefix in new[] { string.Empty, "/api" })
        {
            app.MapGet(prefix + "/healthz", () => Results.Json(new { status = "ok" })).AllowAnonymous();
            app.MapGet(prefix + "/readyz", async (IReadinessCheck check, CancellationToken ct) =>
            {
                var ready = false;
                try
                {
                    ready = await check.IsReadyAsync(ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                }

                return Results.Json(new { status = ready ? "ok" : "unavailable" }, statusCode: ready ? 200 : 503);
            }).AllowAnonymous();
        }
    }
}
