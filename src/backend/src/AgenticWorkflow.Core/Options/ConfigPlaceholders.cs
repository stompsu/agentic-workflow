namespace AgenticWorkflow.Core.Options;

/// <summary>Detects unfilled template values such as "&lt;chat-deployment&gt;" or "REPLACE_ME".</summary>
public static class ConfigPlaceholders
{
    public static bool HasPlaceholder(string? value) =>
        value is not null
        && (value.Contains("REPLACE_ME", StringComparison.OrdinalIgnoreCase) || value.Contains('<') || value.Contains('>'));
}
