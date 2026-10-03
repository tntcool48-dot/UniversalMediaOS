using System.Text.Json;
using UniversalMediaOS.Core.Configuration;

namespace UniversalMediaOS.Core.OtherMedia;

public sealed record AudiovisualOptions
{
    public const string ProviderIndexesConfigKey = "OtherMediaProviderIndexes";
    public const string LegacyProviderManifestsConfigKey = "OtherMediaProviderManifests";
    public const string TmdbApiKeyConfigKey = "TmdbApiKey";
    public const string TmdbApiUrlConfigKey = "TmdbApiUrl";
    public const string TmdbLanguageConfigKey = "TmdbLanguage";
    public const string InternetArchiveEnabledConfigKey = "OtherMediaEnableInternetArchive";
    public const string InternetArchiveUrlConfigKey = "InternetArchiveUrl";

    public string TmdbApiKey { get; init; } = string.Empty;

    public Uri TmdbBaseUri { get; init; } = new("https://api.themoviedb.org/3/");

    public string TmdbLanguage { get; init; } = "en-US";

    public bool EnableInternetArchive { get; init; } = true;

    public Uri InternetArchiveBaseUri { get; init; } = new("https://archive.org/");

    public IReadOnlyList<string> ProviderIndexLocations { get; init; } = Array.Empty<string>();

    public static AudiovisualOptions FromConfiguration(DomainHotSwapper? config)
    {
        if (config is null)
        {
            return new AudiovisualOptions();
        }

        string tmdbBase = config.GetSetting(TmdbApiUrlConfigKey);
        string archiveBase = config.GetSetting(InternetArchiveUrlConfigKey);
        string manifestSetting = config.GetSetting(ProviderIndexesConfigKey);
        if (string.IsNullOrWhiteSpace(manifestSetting))
        {
            manifestSetting = config.GetSetting(LegacyProviderManifestsConfigKey);
        }

        return new AudiovisualOptions
        {
            TmdbApiKey = config.GetSetting(TmdbApiKeyConfigKey).Trim(),
            TmdbBaseUri = ParseBaseUri(tmdbBase, "https://api.themoviedb.org/3/"),
            TmdbLanguage = NormalizeLanguage(config.GetSetting(TmdbLanguageConfigKey), "en-US"),
            EnableInternetArchive = ParseBoolean(
                config.GetSetting(InternetArchiveEnabledConfigKey),
                defaultValue: true),
            InternetArchiveBaseUri = ParseBaseUri(archiveBase, "https://archive.org/"),
            ProviderIndexLocations = ParseManifestLocations(manifestSetting)
        };
    }

    internal static IReadOnlyList<string> ParseManifestLocations(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        string trimmed = raw.Trim();
        if (trimmed.StartsWith('{'))
        {
            return [trimmed];
        }

        if (trimmed.StartsWith('['))
        {
            try
            {
                string[]? values = JsonSerializer.Deserialize<string[]>(trimmed);
                if (values is not null)
                {
                    return values
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }
            catch (JsonException)
            {
                // Fall through to the user-friendly line/semicolon format.
            }
        }

        return trimmed
            .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Uri ParseBaseUri(string? value, string fallback)
    {
        string candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            uri = new Uri(fallback);
        }

        string normalized = uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri.AbsoluteUri
            : uri.AbsoluteUri + "/";
        return new Uri(normalized);
    }

    private static string NormalizeLanguage(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static bool ParseBoolean(string? value, bool defaultValue)
    {
        return bool.TryParse(value, out bool parsed) ? parsed : defaultValue;
    }
}
