namespace SupportChat.Core.Options;

public enum AzureAuthMode
{
    Key,
    ManagedIdentity,
}

/// <summary>Section "Azure". AuthMode is kept as a string so that invalid values are reported by startup validation.</summary>
public sealed class AzureOptions
{
    public const string SectionName = "Azure";

    public string? AuthMode { get; set; }
}

public sealed class CosmosOptions
{
    public const string SectionName = "Cosmos";

    public string? Endpoint { get; set; }

    /// <summary>Key mode only. Only AzureClientFactory and the startup validator read it.</summary>
    public string? Key { get; set; }

    public string DatabaseName { get; set; } = "supportchat";

    public string ContainerName { get; set; } = "chat";
}

public sealed class SearchServiceOptions
{
    public const string SectionName = "Search";

    public string? Endpoint { get; set; }

    /// <summary>Key mode only; a QUERY key (read-only). Only AzureClientFactory and the startup validator read it.</summary>
    public string? Key { get; set; }

    public string IndexName { get; set; } = "support-knowledge";

    public int TopK { get; set; } = 5;

    public double MinRerankerScore { get; set; } = 1.5;
}

public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    public string? ProjectEndpoint { get; set; }
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string[] RequiredScopes { get; set; } = ["access_as_user"];
}

public sealed class ChatLimitsOptions
{
    public const string SectionName = "Chat";

    public int HistoryMaxMessages { get; set; } = 20;

    public int HistoryMaxTokens { get; set; } = 6000;

    public int MaxMessageChars { get; set; } = 4000;

    public TimeSpan TurnTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan PersistTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public int TurnsPerMinute { get; set; } = 10;

    /// <summary>
    /// Retention in days (1..3650, D7). ttl = days * 86400 on every message and session write. Unset = no ttl field
    /// (allowed only in Development, ADR 0003 rule 9).
    /// </summary>
    public int? RetentionDays { get; set; }

    /// <summary>Time budget of DELETE /api/me/data (00:00:05..00:03:00, ADR 0003 rule 10).</summary>
    public TimeSpan DeleteUserDataBudget { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>A lock older than TurnTimeout + this grace is stale and may be taken over (data-model 1.2).</summary>
    public static readonly TimeSpan StaleLockGrace = TimeSpan.FromSeconds(60);
}

public sealed class EchoWorkflowOptions
{
    public const string SectionName = "Workflows:Echo";

    public bool Enabled { get; set; }
}
