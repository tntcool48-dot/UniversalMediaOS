using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace UniversalMediaOS.Core.Helpers
{
    /// <summary>
    /// Makes diagnostic text safe to share by removing URL credentials, query
    /// values, fragments, and token-like path segments while retaining useful
    /// scheme, host, port, and stable route information.
    /// </summary>
    internal static class LogSanitizer
    {
        private static readonly Regex HttpUrlPattern = new(
            "https?://[^\\s'\\\"<>]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex HexTokenPattern = new(
            "^[a-f0-9]{32,}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex Base64UrlTokenPattern = new(
            "^[a-z0-9_-]{40,}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly char[] TrailingPunctuation = [')', ']', '}', ',', ';', '.', '!'];

        internal static string RedactSensitiveUrls(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return message;
            }

            return HttpUrlPattern.Replace(message, match => RedactMatch(match.Value));
        }

        private static string RedactMatch(string candidate)
        {
            int contentLength = candidate.Length;
            while (contentLength > 0 && Array.IndexOf(TrailingPunctuation, candidate[contentLength - 1]) >= 0)
            {
                contentLength--;
            }

            string urlText = candidate[..contentLength];
            string suffix = candidate[contentLength..];
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return RedactMalformedUrl(urlText) + suffix;
            }

            string host = uri.HostNameType == UriHostNameType.IPv6
                ? $"[{uri.IdnHost}]"
                : uri.IdnHost;
            string authority = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
            string redacted = $"{uri.Scheme}://{authority}{RedactSensitivePath(uri.AbsolutePath)}";
            if (!string.IsNullOrEmpty(uri.Query))
            {
                redacted += "?<redacted>";
            }
            if (!string.IsNullOrEmpty(uri.Fragment))
            {
                redacted += "#<redacted>";
            }

            return redacted + suffix;
        }

        private static string RedactSensitivePath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath))
            {
                return absolutePath;
            }

            string[] segments = absolutePath.Split('/');
            for (int index = 0; index < segments.Length; index++)
            {
                string segment;
                try
                {
                    segment = Uri.UnescapeDataString(segments[index]);
                }
                catch
                {
                    segment = segments[index];
                }

                if (LooksLikeOpaqueToken(segment))
                {
                    segments[index] = "<redacted>";
                }
            }

            return string.Join('/', segments);
        }

        private static bool LooksLikeOpaqueToken(string segment)
        {
            if (HexTokenPattern.IsMatch(segment))
            {
                return true;
            }

            if (!Base64UrlTokenPattern.IsMatch(segment))
            {
                return false;
            }

            bool hasLower = segment.Any(char.IsLower);
            bool hasUpper = segment.Any(char.IsUpper);
            bool hasDigit = segment.Any(char.IsDigit);
            int distinctCharacters = segment.Distinct().Take(16).Count();
            return hasLower && hasUpper && hasDigit && distinctCharacters >= 16;
        }

        private static string RedactMalformedUrl(string value)
        {
            int queryIndex = value.IndexOf('?');
            int fragmentIndex = value.IndexOf('#');
            int sensitiveIndex = queryIndex < 0
                ? fragmentIndex
                : fragmentIndex < 0
                    ? queryIndex
                    : Math.Min(queryIndex, fragmentIndex);
            char sensitiveMarker = sensitiveIndex >= 0 ? value[sensitiveIndex] : '\0';
            string visible = sensitiveIndex >= 0 ? value[..sensitiveIndex] : value;

            int schemeEnd = visible.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd >= 0)
            {
                int authorityStart = schemeEnd + 3;
                int authorityEnd = visible.IndexOf('/', authorityStart);
                if (authorityEnd < 0)
                {
                    authorityEnd = visible.Length;
                }

                int at = authorityEnd > authorityStart
                    ? visible.LastIndexOf('@', authorityEnd - 1)
                    : -1;
                if (at >= authorityStart)
                {
                    visible = visible[..authorityStart] + visible[(at + 1)..];
                }
            }

            return sensitiveMarker switch
            {
                '?' => visible + "?<redacted>",
                '#' => visible + "#<redacted>",
                _ => visible
            };
        }
    }
}
