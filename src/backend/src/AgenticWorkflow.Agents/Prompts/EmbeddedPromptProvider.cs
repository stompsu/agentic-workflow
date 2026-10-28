using System.Reflection;
using AgenticWorkflow.Core.Prompts;

namespace AgenticWorkflow.Agents.Prompts;

/// <summary>
/// Reads versioned prompt files that are embedded in this assembly (ADR 0002 section 7). Paths look like
/// "Workflows/SupportTicket/Prompts/orchestrator.v1.md" (forward slashes, case-sensitive).
/// </summary>
public sealed class EmbeddedPromptProvider : IPromptProvider
{
    private readonly Assembly _assembly;
    private readonly Dictionary<string, string> _resourceByPath;

    public EmbeddedPromptProvider()
        : this(typeof(EmbeddedPromptProvider).Assembly)
    {
    }

    public EmbeddedPromptProvider(Assembly assembly)
    {
        _assembly = assembly;
        _resourceByPath = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".md", StringComparison.Ordinal))
            .ToDictionary(Normalize, n => n, StringComparer.Ordinal);
    }

    /// <summary>All prompt paths available (used by tests to verify that every prompt loads).</summary>
    public IReadOnlyCollection<string> Paths => _resourceByPath.Keys;

    public string Get(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!_resourceByPath.TryGetValue(Normalize(path), out var resource))
        {
            throw new FileNotFoundException($"Prompt '{path}' is not an embedded resource.", path);
        }

        using var stream = _assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException($"Prompt '{path}' could not be opened.", path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
