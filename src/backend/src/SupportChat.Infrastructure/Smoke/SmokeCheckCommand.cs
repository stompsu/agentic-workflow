using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SupportChat.Infrastructure.Azure;

namespace SupportChat.Infrastructure.Smoke;

/// <summary>
/// <c>--smoke-check</c> entry point (ADR 0003 section 1a). Order: all startup validation, then the managed-identity-only
/// guard, then the probes. Never serves HTTP. Returns the process exit code (0 = all passed, 1 otherwise).
/// </summary>
public static class SmokeCheckCommand
{
    public const string Flag = "--smoke-check";

    public static void AddSmokeChecks(this IServiceCollection services)
    {
        services.AddSingleton<ISmokeProbe, CosmosSmokeProbe>();
        services.AddSingleton<ISmokeProbe, SearchSmokeProbe>();
        services.AddSingleton<ISmokeProbe, FoundrySmokeProbe>();
        services.AddSingleton<SmokeCheckRunner>();
    }

    public static async Task<int> RunAsync(IServiceProvider services, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SmokeCheck");
        try
        {
            // Same fail-fast rules as a normal start (ValidateOnStart is not run because the host is never started).
            services.GetRequiredService<IStartupValidator>().Validate();
        }
        catch (Exception ex)
        {
            logger.LogError("Smoke check refused: startup validation failed ({ExceptionType})", ex.GetType().Name);
            // StartupValidator.Validate aggregates one OptionsValidationException per options class.
            var validations = ex is AggregateException aggregate
                ? aggregate.InnerExceptions.OfType<OptionsValidationException>()
                : ex is OptionsValidationException single ? [single] : [];
            foreach (var failure in validations.SelectMany(v => v.Failures))
            {
                logger.LogError("Startup validation: {Failure}", failure); // names configuration keys only
            }

            return 1;
        }

        var refusals = AzureStartupValidator.ValidateSmokeCheck(services.GetRequiredService<IConfiguration>());
        if (refusals.Count > 0)
        {
            foreach (var refusal in refusals)
            {
                logger.LogError("Smoke check refused: {Reason}", refusal);
            }

            return 1;
        }

        return await services.GetRequiredService<SmokeCheckRunner>().RunAsync(timeout, cancellationToken);
    }
}
