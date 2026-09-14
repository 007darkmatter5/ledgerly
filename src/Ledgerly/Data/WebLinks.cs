namespace Ledgerly.Data;

/// <summary>Cleans up web addresses people type in, so only safe http(s) links are stored and shown.</summary>
public static class WebLinks
{
    public const int MaxLength = 500;

    /// <summary>
    /// Returns a normalized absolute http(s) address, or null for blank input. Adds "https://" when no
    /// scheme is given ("mybank.com" → "https://mybank.com"). Returns false for anything else, including
    /// other schemes such as <c>javascript:</c>.
    /// </summary>
    public static bool TryNormalize(string? input, out string? url)
    {
        url = null;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
            return true;
        if (text.Length > MaxLength || text.Any(char.IsWhiteSpace))
            return false;

        // A bare host or path ("mybank.com/pay") gets https. Anything with its own scheme must be http(s).
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            if (text.Contains(':') && !LooksLikeHostWithPort(text))
                return false; // e.g. "javascript:alert(1)" or "mailto:x"
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host)
            || !(uri.Host.Contains('.') || uri.IsLoopback))
            return false;

        url = uri.AbsoluteUri;
        return url.Length <= MaxLength;
    }

    /// <summary>A short label for a link, e.g. "www.mybank.com".</summary>
    public static string DisplayHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static bool LooksLikeHostWithPort(string text)
    {
        // "mybank.com:8443/pay" - the part after the first colon starts with digits.
        var afterColon = text[(text.IndexOf(':') + 1)..];
        return afterColon.Length > 0 && char.IsDigit(afterColon[0]);
    }
}
