using System.Text.Json.Serialization;
using UniversalMediaOS.Core.Services;

namespace UniversalMediaOS.Core.OtherMedia;

[JsonConverter(typeof(JsonStringEnumConverter<AudiovisualMediaKind>))]
public enum AudiovisualMediaKind
{
    Movie,
    Television,
    Cartoon
}

[JsonConverter(typeof(JsonStringEnumConverter<AudiovisualContentForm>))]
public enum AudiovisualContentForm
{
    Unknown,
    Feature,
    Series
}

[JsonConverter(typeof(JsonStringEnumConverter<AudiovisualSourceAccessMode>))]
public enum AudiovisualSourceAccessMode
{
    DirectMedia,
    WebPage,
    InternetArchiveItem
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderAuthorization>))]
public enum ProviderAuthorization
{
    Unspecified,
    Licensed,
    PublicDomain,
    CreativeCommons,
    UserAuthorized
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderHealthState>))]
public enum ProviderHealthState
{
    Unknown,
    Healthy,
    Degraded,
    BackingOff
}

public enum AudiovisualSourceProvenance
{
    ConfiguredProvider,
    BuiltInInternetArchive,
    BuiltInScraper
}

public sealed record AudiovisualIdentity
{
    public AudiovisualExternalId? PrimaryId { get; init; }

    public IReadOnlyList<AudiovisualExternalId> ExternalIds { get; init; } = Array.Empty<AudiovisualExternalId>();

    public bool? IsAnimated { get; init; }

    public AudiovisualMediaKind Kind { get; init; }

    public AudiovisualContentForm ContentForm { get; init; }

    public string Title { get; init; } = string.Empty;

    public string OriginalTitle { get; init; } = string.Empty;

    public IReadOnlyList<string> AlternateTitles { get; init; } = Array.Empty<string>();

    public int? Year { get; init; }

    public int? TmdbId { get; init; }

    public string ImdbId { get; init; } = string.Empty;
}

public sealed record AudiovisualUnit
{
    public static AudiovisualUnit Feature { get; } = new();

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public string Title { get; init; } = string.Empty;

    [JsonIgnore]
    public bool IsFeature => SeasonNumber is null && EpisodeNumber is null;
}

public sealed record AudiovisualMediaItem
{
    // Set only when reconstructing an already persisted library work.
    public string? PersistedWorkKey { get; init; }
    public AudiovisualIdentity Identity { get; init; } = new();

    public string Title { get; init; } = string.Empty;

    public string Overview { get; init; } = string.Empty;

    public string PosterUrl { get; init; } = string.Empty;

    public string BackdropUrl { get; init; } = string.Empty;

    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();

    public double Rating { get; init; }

    public string OriginalLanguage { get; init; } = string.Empty;
}

public sealed record AudiovisualSource
{
    public AudiovisualSourceEvidence? Evidence { get; init; }

    [JsonIgnore]
    public AudiovisualSourceProvenance Provenance { get; init; }

    public string ProviderId { get; init; } = string.Empty;

    public string ProviderName { get; init; } = string.Empty;

    public Uri Location { get; init; } = new("about:blank");

    public AudiovisualSourceAccessMode AccessMode { get; init; }

    public ProviderAuthorization Authorization { get; init; }

    public string License { get; init; } = string.Empty;

    public string Rights { get; init; } = string.Empty;

    public string ContentType { get; init; } = string.Empty;

    // Advertised rendition whose bytes passed the scraper's media check.
    public string ValidatedHlsVariant { get; init; } = string.Empty;
    public bool MediaValidated { get; init; }
    [JsonIgnore]
    public IReadOnlyList<MediaSubtitleTrack> Subtitles { get; init; } = Array.Empty<MediaSubtitleTrack>();

    [JsonIgnore]
    public string UserAgent { get; init; } = string.Empty;

    [JsonIgnore]
    public string Cookie { get; init; } = string.Empty;

    [JsonIgnore]
    public string Referer { get; init; } = string.Empty;

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> RequestHeaders { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Languages { get; init; } = Array.Empty<string>();

    public AudiovisualIdentity Identity { get; init; } = new();

    public AudiovisualUnit Unit { get; init; } = AudiovisualUnit.Feature;

    public string Lane { get; init; } = string.Empty;

    public bool IsArabicCartoonVerified { get; init; }

    public DateTimeOffset VerifiedAtUtc { get; init; }
}

public sealed record ProviderHealthSnapshot
{
    public string ProviderId { get; init; } = string.Empty;

    public ProviderHealthState State { get; init; }

    public int ConsecutiveFailures { get; init; }

    public DateTimeOffset? LastSuccessUtc { get; init; }

    public DateTimeOffset? LastFailureUtc { get; init; }

    public DateTimeOffset? BackoffUntilUtc { get; init; }

    public int CachedResponseCount { get; init; }
}

public interface IAudiovisualCatalogService
{
    AudiovisualMediaKind Kind { get; }

    Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(
        string query,
        CancellationToken token = default);

    Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(
        CancellationToken token = default);

    Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(
        AudiovisualIdentity identity,
        AudiovisualUnit? unit = null,
        string? preferredLanguage = null,
        CancellationToken token = default);
}
