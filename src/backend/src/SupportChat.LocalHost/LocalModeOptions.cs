using System.Globalization;

namespace SupportChat.LocalHost;

public enum LocalAuthMode
{
    Local,
    Entra,
}

public enum LocalModelsMode
{
    Scripted,
    Foundry,
}

/// <summary>
/// The "LocalMode" section, parsed leniently (an invalid value falls back to the default). Rejecting invalid values is
/// the job of <see cref="LocalModeValidator"/>, which reads the raw configuration so its messages name keys only.
/// </summary>
public sealed class LocalModeOptions
{
    public const string SectionName = "LocalMode";

    public static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan DefaultTokenDelay = TimeSpan.FromMilliseconds(30);

    public bool Enabled { get; set; }

    public LocalAuthMode Auth { get; set; } = LocalAuthMode.Local;

    public LocalModelsMode Models { get; set; } = LocalModelsMode.Scripted;

    public TimeSpan TokenLifetime { get; set; } = DefaultTokenLifetime;

    public TimeSpan TokenDelay { get; set; } = DefaultTokenDelay;

    public string KnowledgeSourcePath { get; set; } = "knowledge";

    public static LocalModeOptions From(IConfiguration configuration)
    {
        var options = new LocalModeOptions
        {
            Enabled = bool.TryParse(configuration[$"{SectionName}:Enabled"]?.Trim(), out var enabled) && enabled,
        };

        if (TryParseName<LocalAuthMode>(configuration[$"{SectionName}:Auth"], out var auth))
        {
            options.Auth = auth;
        }

        if (TryParseName<LocalModelsMode>(configuration[$"{SectionName}:Models"], out var models))
        {
            options.Models = models;
        }

        if (TimeSpan.TryParse(configuration[$"{SectionName}:LocalAuth:TokenLifetime"]?.Trim(), CultureInfo.InvariantCulture, out var lifetime))
        {
            options.TokenLifetime = lifetime;
        }

        if (TimeSpan.TryParse(configuration[$"{SectionName}:Scripted:TokenDelay"]?.Trim(), CultureInfo.InvariantCulture, out var delay))
        {
            options.TokenDelay = delay;
        }

        if (configuration[$"{SectionName}:Knowledge:SourcePath"] is { Length: > 0 } path && !string.IsNullOrWhiteSpace(path))
        {
            options.KnowledgeSourcePath = path.Trim();
        }

        return options;
    }

    /// <summary>Parses an enum by name only: numeric strings ("5", "1", "-1") and undefined values are rejected.</summary>
    public static bool TryParseName<TEnum>(string? raw, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text) || char.IsAsciiDigit(text[0]) || text[0] is '-' or '+')
        {
            return false;
        }

        return Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    /// <summary>A relative source path is resolved against <see cref="AppContext.BaseDirectory"/> (ADR 0005 section 3).</summary>
    public string ResolveKnowledgePath() =>
        Path.IsPathRooted(KnowledgeSourcePath) ? KnowledgeSourcePath : Path.Combine(AppContext.BaseDirectory, KnowledgeSourcePath);
}
