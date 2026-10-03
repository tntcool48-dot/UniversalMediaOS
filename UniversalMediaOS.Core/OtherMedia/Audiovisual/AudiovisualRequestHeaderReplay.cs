namespace UniversalMediaOS.Core.OtherMedia;

internal static class AudiovisualRequestHeaderReplay
{
    public static void Apply(HttpRequestMessage request, AudiovisualSource? source)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (source == null)
        {
            Apply(request, string.Empty, string.Empty, string.Empty, null);
            return;
        }

        Apply(
            request,
            source.UserAgent,
            source.Cookie,
            source.Referer,
            source.RequestHeaders);
    }

    public static void Apply(
        HttpRequestMessage request,
        string? userAgent,
        string? cookie,
        string? referer,
        IReadOnlyDictionary<string, string>? headers)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (headers != null)
        {
            foreach ((string name, string value) in headers)
            {
                string normalizedName = name?.Trim() ?? string.Empty;
                string normalizedValue = value?.Trim() ?? string.Empty;
                if (normalizedName.Length == 0 ||
                    normalizedValue.Length == 0 ||
                    IsManagedHeader(normalizedName) ||
                    IsConnectionSpecificHeader(normalizedName))
                {
                    continue;
                }

                request.Headers.TryAddWithoutValidation(normalizedName, normalizedValue);
            }
        }

        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            string.IsNullOrWhiteSpace(userAgent)
                ? "UniversalMediaOS/1.0 (media client)"
                : userAgent.Trim());

        if (!string.IsNullOrWhiteSpace(cookie))
        {
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", cookie.Trim());
        }

        if (Uri.TryCreate(referer?.Trim(), UriKind.Absolute, out Uri? referrer) &&
            referrer.Scheme is "http" or "https")
        {
            request.Headers.Referrer = referrer;
        }
    }

    private static bool IsManagedHeader(string name)
    {
        return name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Referer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConnectionSpecificHeader(string name)
    {
        return name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);
    }
}
