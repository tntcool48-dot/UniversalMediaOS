using System.Net;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UniversalMediaOS.Core.OtherMedia;

public sealed record AudiovisualProviderManifest
{
    public int Version { get; init; } = 1;

    public IReadOnlyList<string> Includes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<AudiovisualProviderDefinition> Providers { get; init; } =
        Array.Empty<AudiovisualProviderDefinition>();
}

public sealed record AudiovisualProviderDefinition
{
    private static readonly Regex ProviderIdPattern = new(
        "^[a-z0-9][a-z0-9._-]{1,63}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public string BaseUrl { get; init; } = string.Empty;

    public string SearchUrlTemplate { get; init; } = string.Empty;

    public string Adapter { get; init; } = "json-index-v1";

    public IReadOnlyList<AudiovisualMediaKind> MediaKinds { get; init; } =
        Array.Empty<AudiovisualMediaKind>();

    public IReadOnlyList<string> Languages { get; init; } = Array.Empty<string>();

    public ProviderAuthorization Authorization { get; init; }

    public string RightsStatement { get; init; } = string.Empty;

    public string Attribution { get; init; } = string.Empty;

    public int RequestsPerMinute { get; init; } = 30;

    public int CacheSeconds { get; init; } = 300;

    public int TimeoutSeconds { get; init; } = 10;

    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;

    public bool ArabicCartoonLane { get; init; }

    public bool AllowPrivateNetwork { get; init; }

    public IReadOnlyList<string> AllowedHosts { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();

        if (!ProviderIdPattern.IsMatch(Id.Trim()))
        {
            errors.Add("Id must contain 2-64 letters, digits, dots, underscores, or hyphens.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Name is required.");
        }

        if (!Adapter.Equals("json-index-v1", StringComparison.OrdinalIgnoreCase) &&
            !Adapter.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Adapter must be 'json-index-v1'.");
        }

        if (MediaKinds.Count == 0)
        {
            errors.Add("At least one media kind is required.");
        }



        if (RequestsPerMinute is < 0 or > 600)
        {
            errors.Add("RequestsPerMinute must be between 0 and 600.");
        }

        if (CacheSeconds is < 0 or > 86_400)
        {
            errors.Add("CacheSeconds must be between 0 and 86400.");
        }

        if (TimeoutSeconds is < 1 or > 60)
        {
            errors.Add("TimeoutSeconds must be between 1 and 60.");
        }

        if (MaxResponseBytes is < 1024 or > 16 * 1024 * 1024)
        {
            errors.Add("MaxResponseBytes must be between 1024 and 16777216.");
        }

        if (!TryBuildProbeUri(out Uri? probeUri))
        {
            errors.Add("SearchUrlTemplate must resolve to an absolute HTTP(S) URL.");
        }
        else if (probeUri is null || !IsUriAllowed(probeUri))
        {
            errors.Add("SearchUrlTemplate resolves outside the provider's permitted hosts/network boundary.");
        }

        if (ArabicCartoonLane &&
            (!MediaKinds.Contains(AudiovisualMediaKind.Cartoon) ||
             !Languages.Any(IsArabicLanguage)))
        {
            errors.Add("ArabicCartoonLane providers must support Cartoon and declare Arabic in Languages.");
        }

        return errors;
    }

    public Uri BuildSearchUri(
        AudiovisualIdentity identity,
        AudiovisualUnit? unit,
        string? preferredLanguage)
    {
        ArgumentNullException.ThrowIfNull(identity);

        string template = SearchUrlTemplate.Trim();
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{query}"] = identity.Title,
            ["{title}"] = identity.Title,
            ["{year}"] = identity.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            ["{tmdbId}"] = identity.TmdbId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            ["{season}"] = unit?.SeasonNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            ["{episode}"] = unit?.EpisodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            ["{kind}"] = identity.Kind.ToString().ToLowerInvariant(),
            ["{language}"] = preferredLanguage ?? string.Empty
        };

        foreach ((string placeholder, string value) in replacements)
        {
            template = template.Replace(
                placeholder,
                Uri.EscapeDataString(value),
                StringComparison.OrdinalIgnoreCase);
        }

        Uri? baseUri = null;
        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            _ = Uri.TryCreate(EnsureTrailingSlash(BaseUrl.Trim()), UriKind.Absolute, out baseUri);
        }

        Uri result = Uri.TryCreate(template, UriKind.Absolute, out Uri? absolute)
            ? absolute
            : baseUri is not null && Uri.TryCreate(baseUri, template, out Uri? relative)
                ? relative
                : throw new InvalidOperationException($"Provider '{Id}' produced an invalid search URL.");

        if (!IsUriAllowed(result))
        {
            throw new InvalidOperationException($"Provider '{Id}' produced a URL outside its permitted boundary.");
        }

        return result;
    }

    public bool Supports(AudiovisualMediaKind kind, string? preferredLanguage)
    {
        if (!Enabled || !MediaKinds.Contains(kind))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(preferredLanguage) || Languages.Count == 0)
        {
            return true;
        }

        return Languages.Any(language => LanguageMatches(language, preferredLanguage));
    }

    internal bool IsUriAllowed(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.IdnHost) ||
            !string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            return false;
        }

        string host = uri.IdnHost.TrimEnd('.');
        var permittedHosts = new HashSet<string>(AllowedHosts, StringComparer.OrdinalIgnoreCase);
        if (Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            permittedHosts.Add(baseUri.IdnHost.TrimEnd('.'));
        }

        if (Uri.TryCreate(SearchUrlTemplate.Replace("{query}", "probe", StringComparison.OrdinalIgnoreCase),
                UriKind.Absolute,
                out Uri? templateUri))
        {
            permittedHosts.Add(templateUri.IdnHost.TrimEnd('.'));
        }

        if (permittedHosts.Count > 0 && !permittedHosts.Contains(host))
        {
            return false;
        }

        return AllowPrivateNetwork || !IsPrivateHost(host);
    }

    internal static bool LanguageMatches(string? declared, string? requested)
    {
        string left = NormalizeLanguage(declared);
        string right = NormalizeLanguage(requested);
        if (left.Length == 0 || right.Length == 0)
        {
            return false;
        }

        return left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
               left.StartsWith(right + "-", StringComparison.OrdinalIgnoreCase) ||
               right.StartsWith(left + "-", StringComparison.OrdinalIgnoreCase) ||
               (IsArabicLanguage(left) && IsArabicLanguage(right));
    }

    internal static bool IsArabicLanguage(string? language)
    {
        string normalized = NormalizeLanguage(language);
        return normalized.Equals("ar", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("ar-", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("ara", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("arabic", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("العربية", StringComparison.Ordinal);
    }

    private bool TryBuildProbeUri(out Uri? uri)
    {
        try
        {
            uri = BuildSearchUri(
                new AudiovisualIdentity
                {
                    Kind = MediaKinds.FirstOrDefault(),
                    Title = "probe",
                    Year = 2000,
                    TmdbId = 1
                },
                new AudiovisualUnit { SeasonNumber = 1, EpisodeNumber = 1 },
                "en");
            return true;
        }
        catch (InvalidOperationException)
        {
            uri = null;
            return false;
        }
    }

    private static readonly CultureInfo[] NeutralLanguages = CultureInfo.GetCultures(CultureTypes.NeutralCultures);

    private static string NormalizeLanguage(string? language)
    {
        string normalized = (language ?? string.Empty).Trim().Replace('_', '-');
        string[] parts = normalized.Split('-', 2);
        string code = parts[0].ToLowerInvariant();
        string? canonical = NeutralLanguages
            .FirstOrDefault(culture => culture.ThreeLetterISOLanguageName == code)?.TwoLetterISOLanguageName;
        return (canonical ?? code) + (parts.Length > 1 ? "-" + parts[1] : string.Empty);
    }

    private static string EnsureTrailingSlash(string value)
    {
        return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
    }

    private static bool IsPrivateHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out IPAddress? address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        }

        return address.IsIPv6LinkLocal ||
               address.IsIPv6SiteLocal ||
               address.IsIPv6Multicast ||
               (bytes[0] & 0xFE) == 0xFC;
    }
}
