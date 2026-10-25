using System.Text.RegularExpressions;

namespace SupportChat.Infrastructure.Search;

/// <summary>
/// The only way a domain reaches an OData filter or an index document: a lowercase value matching
/// <c>^[a-z0-9-]+$</c> (data-model 2.1 / 2.4). Anything else is rejected, never escaped or concatenated.
/// </summary>
public static partial class DomainValue
{
    public const int MaxLength = 64;

    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaxLength } && Pattern().IsMatch(value);

    public static string Require(string? value, string paramName) =>
        IsValid(value) ? value! : throw new ArgumentException("The domain must match ^[a-z0-9-]+$.", paramName);

    /// <summary>Filter for a VALIDATED domain.</summary>
    public static string ToFilter(string domain) => $"{KnowledgeIndexSchema.DomainField} eq '{Require(domain, nameof(domain))}'";

    // \z, not $: "$" would also match before a trailing newline.
    [GeneratedRegex(@"^[a-z0-9-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
