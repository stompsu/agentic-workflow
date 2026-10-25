namespace SupportChat.Core.Health;

/// <summary>Dependency readiness for /readyz. Implementations never throw and never expose details.</summary>
public interface IReadinessCheck
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}
