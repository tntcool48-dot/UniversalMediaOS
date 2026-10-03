using UniversalMediaOS.Core.Configuration;

namespace UniversalMediaOS.Core.OtherMedia;

public sealed class MovieService : IPagedAudiovisualCatalogService, IAudiovisualSourceUpdates, IAudiovisualEpisodeMetadataClient
{
    private readonly AudiovisualCatalogMetadataRouter _metadata;
    private readonly AudiovisualSourceResolver _sources;

    public MovieService(
        DomainHotSwapper? config = null,
        HttpClient? httpClient = null)
    {
        HttpClient client = httpClient ?? AudiovisualHttp.SharedClient;
        var requests = new ProviderRequestCoordinator(client);
        _metadata = new AudiovisualCatalogMetadataRouter(
            config,
            requests,
            config == null ? new ImdbMetadataClient(client) : null);
        _sources = new AudiovisualSourceResolver(config, client);
    }

    public MovieService(
        DomainHotSwapper config,
        ProviderRequestCoordinator requests,
        AudiovisualSourceResolver sources)
    {
        _metadata = new AudiovisualCatalogMetadataRouter(
            config ?? throw new ArgumentNullException(nameof(config)),
            requests ?? throw new ArgumentNullException(nameof(requests)));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    public AudiovisualMediaKind Kind => AudiovisualMediaKind.Movie;

    public IAsyncEnumerable<AudiovisualSourceUpdate> FindSourceUpdatesAsync(SourceSearchRequest request, CancellationToken token = default)
    {
        if (request.Identity.Kind != Kind) throw new ArgumentException("Request kind must match the selected catalog.", nameof(request));
        return _sources.FindSourceUpdatesAsync(request, token);
    }

    public Task<AudiovisualUnitsResult> GetUnitsAsync(AudiovisualIdentity identity,
        int? season = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Kind != Kind) throw new ArgumentException("Identity kind must match the selected catalog.", nameof(identity));
        return _metadata.GetUnitsAsync(identity, season, token);
    }

    public AudiovisualCatalogCapabilities Capabilities => _metadata.GetCapabilities(Kind);

    public Task<AudiovisualCatalogPage> GetPageAsync(AudiovisualCatalogRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (request.Kind != Kind) throw new ArgumentException("Request kind must match the selected catalog.", nameof(request));
        return _metadata.GetPageAsync(request, token);
    }

    public async Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(
        string query,
        CancellationToken token = default)
    {
        return await _metadata.SearchAsync(Kind, query, token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(
        CancellationToken token = default)
    {
        return await _metadata.GetPopularAsync(Kind, token).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(
        AudiovisualIdentity identity,
        AudiovisualUnit? unit = null,
        string? preferredLanguage = null,
        CancellationToken token = default)
    {
        return identity.Kind != Kind
            ? Task.FromResult<IReadOnlyList<AudiovisualSource>>(Array.Empty<AudiovisualSource>())
            : _sources.FindSourcesAsync(identity, unit, preferredLanguage, false, token);
    }
}

public sealed class TvService : IPagedAudiovisualCatalogService, IAudiovisualSourceUpdates, IAudiovisualEpisodeMetadataClient
{
    private readonly AudiovisualCatalogMetadataRouter _metadata;
    private readonly AudiovisualSourceResolver _sources;

    public TvService(
        DomainHotSwapper? config = null,
        HttpClient? httpClient = null)
    {
        HttpClient client = httpClient ?? AudiovisualHttp.SharedClient;
        var requests = new ProviderRequestCoordinator(client);
        _metadata = new AudiovisualCatalogMetadataRouter(
            config,
            requests,
            config == null ? new ImdbMetadataClient(client) : null);
        _sources = new AudiovisualSourceResolver(config, client);
    }

    public TvService(
        DomainHotSwapper config,
        ProviderRequestCoordinator requests,
        AudiovisualSourceResolver sources)
    {
        _metadata = new AudiovisualCatalogMetadataRouter(
            config ?? throw new ArgumentNullException(nameof(config)),
            requests ?? throw new ArgumentNullException(nameof(requests)));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    public AudiovisualMediaKind Kind => AudiovisualMediaKind.Television;

    public IAsyncEnumerable<AudiovisualSourceUpdate> FindSourceUpdatesAsync(SourceSearchRequest request, CancellationToken token = default)
    {
        if (request.Identity.Kind != Kind) throw new ArgumentException("Request kind must match the selected catalog.", nameof(request));
        return _sources.FindSourceUpdatesAsync(request, token);
    }

    public Task<AudiovisualUnitsResult> GetUnitsAsync(AudiovisualIdentity identity,
        int? season = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Kind != Kind) throw new ArgumentException("Identity kind must match the selected catalog.", nameof(identity));
        return _metadata.GetUnitsAsync(identity, season, token);
    }

    public AudiovisualCatalogCapabilities Capabilities => _metadata.GetCapabilities(Kind);

    public Task<AudiovisualCatalogPage> GetPageAsync(AudiovisualCatalogRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (request.Kind != Kind) throw new ArgumentException("Request kind must match the selected catalog.", nameof(request));
        return _metadata.GetPageAsync(request, token);
    }

    public async Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(
        string query,
        CancellationToken token = default)
    {
        return await _metadata.SearchAsync(Kind, query, token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(
        CancellationToken token = default)
    {
        return await _metadata.GetPopularAsync(Kind, token).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(
        AudiovisualIdentity identity,
        AudiovisualUnit? unit = null,
        string? preferredLanguage = null,
        CancellationToken token = default)
    {
        return identity.Kind != Kind
            ? Task.FromResult<IReadOnlyList<AudiovisualSource>>(Array.Empty<AudiovisualSource>())
            : _sources.FindSourcesAsync(identity, unit, preferredLanguage, false, token);
    }
}

public sealed class CartoonService : IPagedAudiovisualCatalogService, IAudiovisualSourceUpdates, IAudiovisualEpisodeMetadataClient
{
    private readonly AudiovisualCatalogMetadataRouter _metadata;
    private readonly AudiovisualSourceResolver _sources;

    public CartoonService(
        DomainHotSwapper? config = null,
        HttpClient? httpClient = null)
    {
        HttpClient client = httpClient ?? AudiovisualHttp.SharedClient;
        var requests = new ProviderRequestCoordinator(client);
        _metadata = new AudiovisualCatalogMetadataRouter(
            config,
            requests,
            config == null ? new ImdbMetadataClient(client) : null);
        _sources = new AudiovisualSourceResolver(config, client);
    }

    public CartoonService(
        DomainHotSwapper config,
        ProviderRequestCoordinator requests,
        AudiovisualSourceResolver sources)
    {
        _metadata = new AudiovisualCatalogMetadataRouter(
            config ?? throw new ArgumentNullException(nameof(config)),
            requests ?? throw new ArgumentNullException(nameof(requests)));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
    }

    public AudiovisualMediaKind Kind => AudiovisualMediaKind.Cartoon;

    public IAsyncEnumerable<AudiovisualSourceUpdate> FindSourceUpdatesAsync(SourceSearchRequest request, CancellationToken token = default)
    {
        if (request.Identity.Kind != Kind) throw new ArgumentException("Request kind must match the selected catalog.", nameof(request));
        return _sources.FindSourceUpdatesAsync(request, token);
    }

    public Task<AudiovisualUnitsResult> GetUnitsAsync(AudiovisualIdentity identity,
        int? season = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Kind != Kind) throw new ArgumentException("Identity kind must match the selected catalog.", nameof(identity));
        return _metadata.GetUnitsAsync(identity, season, token);
    }

    public AudiovisualCatalogCapabilities Capabilities => _metadata.GetCapabilities(Kind);

    public Task<AudiovisualCatalogPage> GetPageAsync(AudiovisualCatalogRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (request.Kind != Kind) throw new ArgumentException("Request kind must match the selected catalog.", nameof(request));
        return _metadata.GetPageAsync(request, token);
    }

    public async Task<IReadOnlyList<AudiovisualMediaItem>> SearchAsync(
        string query,
        CancellationToken token = default)
    {
        return await _metadata.SearchAsync(Kind, query, token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AudiovisualMediaItem>> GetPopularAsync(
        CancellationToken token = default)
    {
        return await _metadata.GetPopularAsync(Kind, token).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(
        AudiovisualIdentity identity,
        AudiovisualUnit? unit = null,
        string? preferredLanguage = null,
        CancellationToken token = default)
    {
        return identity.Kind != Kind
            ? Task.FromResult<IReadOnlyList<AudiovisualSource>>(Array.Empty<AudiovisualSource>())
            : _sources.FindSourcesAsync(identity, unit, preferredLanguage, false, token);
    }
    public Task<IReadOnlyList<AudiovisualSource>> FindArabicSourcesAsync(
        AudiovisualIdentity identity,
        AudiovisualUnit? unit = null,
        CancellationToken token = default)
    {
        return identity.Kind != Kind
            ? Task.FromResult<IReadOnlyList<AudiovisualSource>>(Array.Empty<AudiovisualSource>())
            : _sources.FindSourcesAsync(
                identity,
                unit,
                "ar",
                requireArabicCartoonVerification: true,
                token);
    }
}
