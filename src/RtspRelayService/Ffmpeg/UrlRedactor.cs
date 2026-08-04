using System.Text.RegularExpressions;

namespace RtspRelayService.Ffmpeg;

public static class UrlRedactor
{
    private static readonly Regex SensitiveUrlPattern = new(
        @"\b(?:rtsp|rtsps|rtmp|rtmps)://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string RedactUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return value;

        var redacted = value;
        var authorityStart = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = value.IndexOfAny(['/', '?', '#'], authorityStart);
        if (authorityEnd < 0)
            authorityEnd = value.Length;

        var at = value.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
        if (at >= authorityStart)
        {
            redacted = string.Concat(
                value.AsSpan(0, authorityStart),
                "***:***@",
                value.AsSpan(at + 1));
        }

        if ((uri.Scheme.Equals("rtmp", StringComparison.OrdinalIgnoreCase)
             || uri.Scheme.Equals("rtmps", StringComparison.OrdinalIgnoreCase))
            && redacted.IndexOf('?') is var query && query >= 0)
            redacted = redacted[..query] + "?***";

        return redacted;
    }

    public static string RedactText(string text, IEnumerable<string> configuredUrls)
    {
        foreach (var raw in configuredUrls.Where(x => !string.IsNullOrEmpty(x)))
            text = text.Replace(raw, RedactUrl(raw), StringComparison.Ordinal);

        return SensitiveUrlPattern.Replace(text, match => RedactUrl(match.Value));
    }
}
