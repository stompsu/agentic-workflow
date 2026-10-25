using Microsoft.Extensions.Logging;
using SupportChat.Core.Health;
using SupportChat.Infrastructure.Storage.Cosmos;

namespace SupportChat.Infrastructure.Health;

/// <summary>
/// Cosmos container reachability (api-contract 2: 2 s timeout, result cached 30 s). Returns only true/false;
/// failures are logged by exception type only (no endpoint, no message).
/// </summary>
public sealed class CosmosReadinessCheck(CosmosChatContainer chat, TimeProvider timeProvider, ILogger<CosmosReadinessCheck> logger) : IReadinessCheck
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _lastResult;
    private DateTimeOffset _validUntil = DateTimeOffset.MinValue;

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (timeProvider.GetUtcNow() < _validUntil)
        {
            return _lastResult;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (timeProvider.GetUtcNow() < _validUntil)
            {
                return _lastResult;
            }

            using var timeout = new CancellationTokenSource(Timeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await chat.Container.ReadContainerAsync(cancellationToken: linked.Token);
                _lastResult = true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Readiness check failed: {ExceptionType}", ex.GetType().Name);
                _lastResult = false;
            }

            _validUntil = timeProvider.GetUtcNow() + CacheDuration;
            return _lastResult;
        }
        finally
        {
            _gate.Release();
        }
    }
}
