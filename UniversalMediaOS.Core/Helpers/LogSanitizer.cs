using System;
using System.Collections.Generic;
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

        // Keep these rules in step with redact_log_urls in scraper.py;
        // tools/tests/fixtures/log-url-redaction.json is the shared contract
        // both implementations are tested against.
        private static readonly Regex TokenRunPattern = new(
            "[A-Za-z0-9_+-]+",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex HexTokenPattern = new(
            "^[a-f0-9]{16,}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex MediaExtensionPattern = new(
            "\\.(?:m3u8?|mpd|mp4|m4[sav]|ts|aac|vtt|srt|webm|key)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly HashSet<string> CredentialRouteKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "token", "tokens", "sig", "sign", "signature", "hmac", "hash", "auth", "key", "apikey", "api_key",
            "access_token", "secret", "expires", "expiry", "exp", "policy", "session", "sid", "jwt"
        };

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
            // A lone "?" or "#" is what an earlier pass left in front of "<redacted>".
            if (uri.Query.Length > 1)
            {
                redacted += "?<redacted>";
            }
            if (uri.Fragment.Length > 1)
            {
                redacted += "#<redacted>";
            }
            if (urlText.Length > 0 && urlText[^1] is '?' or '#' && !redacted.EndsWith(urlText[^1]))
            {
                redacted += urlText[^1];
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
            string previous = string.Empty;
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

                if (IsSensitiveSegment(segment, previous))
                {
                    Match extension = MediaExtensionPattern.Match(segment);
                    segments[index] = "<redacted>" + (extension.Success ? extension.Value : string.Empty);
                }
                previous = segment;
            }

            return string.Join('/', segments);
        }

        private static bool IsSensitiveSegment(string segment, string previousSegment)
        {
            if (segment.Length == 0)
            {
                return false;
            }

            if (segment.Contains("://", StringComparison.Ordinal) || segment.Contains('='))
            {
                return true;
            }

            if (CredentialRouteKeys.Contains(previousSegment))
            {
                return true;
            }

            return TokenRunPattern.Matches(segment).Any(run => IsOpaqueTokenRun(run.Value));
        }

        private static bool IsOpaqueTokenRun(string run)
        {
            if (HexTokenPattern.IsMatch(run) &&
                (run.Length >= 32 || (run.Any(char.IsDigit) && run.Any(char.IsLetter))))
            {
                return true;
            }

            if (run.StartsWith("eyJ", StringComparison.Ordinal) && run.Length >= 16)
            {
                return true;
            }

            if (run.Length >= 40 && HasUpperLowerAndDigit(run) && run.Distinct().Take(16).Count() >= 16)
            {
                return true;
            }

            foreach (string word in run.Split('_', '+', '-'))
            {
                // Random text switches between upper, lower and digit far more often
                // than camel-case titles such as NightOfTheLivingDead1968 do.
                if (word.Length >= 20 && HasUpperLowerAndDigit(word) && ClassChanges(word) * 2 >= word.Length - 1)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasUpperLowerAndDigit(string text) =>
            text.Any(char.IsUpper) && text.Any(char.IsLower) && text.Any(char.IsDigit);

        private static int ClassChanges(string word)
        {
            static int Kind(char value) => char.IsUpper(value) ? 0 : char.IsLower(value) ? 1 : 2;

            int changes = 0;
            for (int index = 1; index < word.Length; index++)
            {
                if (Kind(word[index]) != Kind(word[index - 1]))
                {
                    changes++;
                }
            }

            return changes;
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
