using System.Text;

namespace SupportChat.Agents.Knowledge;

/// <summary>
/// Sanitization for retrieved content (ADR 0002 section 9). Retrieved text is data: it is stripped of control characters and
/// HTML-encoded (&amp; &lt; &gt;) before it reaches the model, so a "&lt;/source&gt;" in the index cannot close the wrapper.
/// </summary>
public static class KnowledgeSanitizer
{
    /// <summary>Removes control characters and invisible direction/format characters. Newlines and tabs are kept only if asked.</summary>
    public static string RemoveControlCharacters(string? value, bool keepLineBreaks = false)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (keepLineBreaks && (c == '\n' || c == '\t'))
            {
                sb.Append(c);
            }
            else if (!char.IsControl(c) && !IsInvisibleFormat(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>HTML-encodes only &amp;, &lt; and &gt; (the characters that matter for the &lt;source&gt; wrapper).</summary>
    public static string EncodeForModel(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>Control characters removed, then encoded. Used for titles and content sent to the model.</summary>
    public static string CleanAndEncode(string? value, bool keepLineBreaks) =>
        EncodeForModel(RemoveControlCharacters(value, keepLineBreaks));

    /// <summary>Returns the absolute URI string if it is http or https; otherwise null.</summary>
    public static string? SafeHttpUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim();
        if (trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return null;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host)
            ? uri.AbsoluteUri
            : null;
    }

    private static bool IsInvisibleFormat(char c) =>
        c is >= '​' and <= '‏'
            or >= '‪' and <= '‮'
            or >= '⁦' and <= '⁩'
            or '﻿';
}
