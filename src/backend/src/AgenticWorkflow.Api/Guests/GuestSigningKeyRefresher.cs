using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using AgenticWorkflow.Core.Options;

namespace AgenticWorkflow.Api.Guests;

/// <summary>
/// Keeps <see cref="GuestSigningKeyCache"/> loaded (ADR 0006 section 5.4). Active only when guests are enabled and the
/// signing mode is KeyVault. Prefill in StartAsync (3 attempts within 20 s; on failure it logs an Error and lets the host
/// start: guest issuance and validation fail until a load succeeds, signed-in users are unaffected, readiness is NOT tied
/// to Key Vault). Then a refresh every SigningKeyRefreshInterval plus on-demand refreshes; a failed refresh keeps the last
/// good snapshot and retries with backoff 5 s, doubling to 5 min.
/// </summary>
public sealed class GuestSigningKeyRefresher(
    IGuestKeySource source,
    GuestSigningKeyCache cache,
    IOptions<GuestOptions> options,
    TimeProvider time,
    ILogger<GuestSigningKeyRefresher> logger) : BackgroundService
{
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PrefillBudget = TimeSpan.FromSeconds(20);
    public const int PrefillAttempts = 3;

    /// <summary>Consecutive failed refreshes before the alertable Error; then again every <see cref="RepeatEvery"/> further failures.</summary>
    public const int AlertThreshold = 3;

    public const int RepeatEvery = 12;

    /// <summary>Name of the event an infra alert matches (the message also starts with "GuestSigningKeyRefreshFailing:").</summary>
    public const string FailingEventName = "GuestSigningKeyRefreshFailing";

    private static readonly EventId FailingEvent = new(4101, FailingEventName);

    private int _consecutiveFailures;

    private bool Active => options.Value is { Enabled: true, SigningMode: GuestSigningMode.KeyVault };

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Active)
        {
            return;
        }

        using var deadline = new CancellationTokenSource(PrefillBudget, time);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        for (var attempt = 1; attempt <= PrefillAttempts; attempt++)
        {
            if (await TryRefreshAsync(budget.Token, "prefill"))
            {
                break;
            }

            if (attempt == PrefillAttempts || budget.IsCancellationRequested)
            {
                LogPrefillFailed();
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), time, budget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                LogPrefillFailed();
                break;
            }
        }

        await base.StartAsync(cancellationToken);
    }

    private void LogPrefillFailed()
    {
        logger.LogError(
            FailingEvent,
            "{Event}: guest signing keys could not be loaded at startup (consecutive failures {Failures}); guest sign-in is unavailable until a refresh succeeds",
            FailingEventName, _consecutiveFailures);
        _consecutiveFailures = 0; // the next AlertThreshold failures of the background loop alert again
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = backoff > TimeSpan.Zero ? backoff : options.Value.SigningKeyRefreshInterval;
            using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                var signal = cache.WaitForRefreshRequestAsync(waitCts.Token);
                var delay = Task.Delay(wait, time, waitCts.Token);
                try
                {
                    await Task.WhenAny(signal, delay);
                }
                finally
                {
                    await waitCts.CancelAsync();
                }

                try
                {
                    await Task.WhenAll(signal, delay);
                }
                catch (OperationCanceledException)
                {
                    // The loser of the race was cancelled on purpose.
                }
            }

            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (await TryRefreshAsync(stoppingToken, "refresh"))
            {
                backoff = TimeSpan.Zero;
            }
            else
            {
                backoff = backoff == TimeSpan.Zero ? InitialBackoff : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
    }

    /// <summary>Loads and swaps the snapshot; keeps the last good one on failure.</summary>
    internal async Task<bool> TryRefreshAsync(CancellationToken ct, string reason)
    {
        try
        {
            cache.Update(await GuestSigningKeyLoader.LoadAsync(source, options.Value, time, logger, ct));
            Volatile.Write(ref _consecutiveFailures, 0);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Type (and status for Key Vault errors) only: never a message that could carry endpoints.
            var type = ex.GetType().Name;
            var status = ex is Azure.RequestFailedException rfe ? $", status {rfe.Status}" : string.Empty;
            logger.LogWarning("Guest signing key {Reason} failed ({ExceptionType}{Status}); keeping the last good keys", reason, type, status);

            var failures = Interlocked.Increment(ref _consecutiveFailures);
            if (reason != "prefill" && failures >= AlertThreshold && (failures - AlertThreshold) % RepeatEvery == 0)
            {
                logger.LogError(
                    FailingEvent,
                    "{Event}: {Failures} consecutive guest signing key refreshes failed ({ExceptionType}{Status}); the last good keys are still in use but will go stale (lost Key Vault role or 403?)",
                    FailingEventName, failures, type, status);
            }

            return false;
        }
    }
}
