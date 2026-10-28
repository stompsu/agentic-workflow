namespace AgenticWorkflow.Core.Options;

public enum GuestSigningMode
{
    KeyVault,
    Ephemeral,
}

/// <summary>
/// Section "Guest" (ADR 0006 section 11; env form Guest__*). When <see cref="Enabled"/> is false the other keys are
/// ignored, except <see cref="DataTtl"/>, which RetentionPolicy always applies to leftover guest data.
/// Startup validation reads the raw configuration (AzureStartupValidator-style), so invalid values are reported by key.
/// </summary>
public sealed class GuestOptions
{
    public const string SectionName = "Guest";

    public const string IdPrefix = "guest-";

    public static readonly string[] DefaultWorkflows = ["support-ticket"];

    public bool Enabled { get; set; }

    public GuestSigningMode SigningMode { get; set; } = GuestSigningMode.KeyVault;

    /// <summary>Versionless Key Vault key URI (https://&lt;vault&gt;.vault.azure.net/keys/&lt;name&gt;).</summary>
    public string? SigningKeyUri { get; set; }

    public TimeSpan SigningKeyRefreshInterval { get; set; } = TimeSpan.FromHours(1);

    public string? Issuer { get; set; }

    public string Audience { get; set; } = "agentwf-api-guest";

    /// <summary>Token lifetime = end of the guest identity.</summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromHours(4);

    /// <summary>Guest data ttl (4 h 15 min). Applies even when Enabled is false.</summary>
    public TimeSpan DataTtl { get; set; } = TimeSpan.FromMinutes(255);

    public TimeSpan ActiveCheckCacheDuration { get; set; } = TimeSpan.FromMinutes(1);

    public int TurnsPerMinute { get; set; } = 5;

    public int MaxTurns { get; set; } = 30;

    public int MaxSessions { get; set; } = 5;

    public int CreationsPerClientPerHour { get; set; } = 10;

    public int MaxCreationsPerHour { get; set; } = 200;

    public int MaxTurnsPerHour { get; set; } = 300;

    public int MaxTurnsPerDay { get; set; } = 2000;

    public bool TrustClientIpHeader { get; set; }

    /// <summary>Null = default (<see cref="DefaultWorkflows"/>). Kept null by default so that binding never appends to a default array.</summary>
    public string[]? Workflows { get; set; }

    public IReadOnlyList<string> EffectiveWorkflows => Workflows is { Length: > 0 } ? Workflows : DefaultWorkflows;

    public static bool IsGuestId(string userId) => userId.StartsWith(IdPrefix, StringComparison.Ordinal);
}
