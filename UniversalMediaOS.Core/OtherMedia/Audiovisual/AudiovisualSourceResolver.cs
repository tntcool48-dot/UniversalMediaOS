using UniversalMediaOS.Core.Configuration;

namespace UniversalMediaOS.Core.OtherMedia;

public sealed class AudiovisualSourceResolver : IAudiovisualSourceUpdates
{
    private readonly DomainHotSwapper? _config;
    private readonly AudiovisualOptions _initialOptions;
    private readonly AudiovisualProviderManifestLoader _manifestLoader;
    private readonly ProviderRequestCoordinator _requests;
    private readonly InternetArchiveSourceProvider _internetArchive;
    private readonly ArabicCartoonLaneVerifier _arabicCartoonVerifier;
    private readonly ScraperAudiovisualSourceProvider? _scraperProvider;
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private string _loadedManifestKey = string.Empty;
    private ManifestAudiovisualSourceProvider? _manifestProvider;

    public AudiovisualSourceResolver(
        DomainHotSwapper? config = null,
        HttpClient? httpClient = null)
    {
        _config = config;
        _initialOptions = AudiovisualOptions.FromConfiguration(config);
        HttpClient client = httpClient ?? AudiovisualHttp.SharedClient;
        _requests = new ProviderRequestCoordinator(client);
        _manifestLoader = new AudiovisualProviderManifestLoader(client);
        _internetArchive = new InternetArchiveSourceProvider(_initialOptions, _requests);
        _arabicCartoonVerifier = new ArabicCartoonLaneVerifier(client);
    }

    public AudiovisualSourceResolver(
        DomainHotSwapper config,
        HttpClient httpClient,
        ProviderRequestCoordinator requests,
        ScraperAudiovisualSourceProvider scraperProvider)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _initialOptions = AudiovisualOptions.FromConfiguration(config);
        HttpClient client = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _manifestLoader = new AudiovisualProviderManifestLoader(client);
        _internetArchive = new InternetArchiveSourceProvider(_initialOptions, _requests);
        _arabicCartoonVerifier = new ArabicCartoonLaneVerifier(client);
        _scraperProvider = scraperProvider ?? throw new ArgumentNullException(nameof(scraperProvider));
    }

    internal AudiovisualSourceResolver(
        DomainHotSwapper? config,
        AudiovisualOptions options,
        AudiovisualProviderManifestLoader manifestLoader,
        ProviderRequestCoordinator requests,
        InternetArchiveSourceProvider internetArchive,
        ArabicCartoonLaneVerifier arabicCartoonVerifier,
        ScraperAudiovisualSourceProvider? scraperProvider = null)
    {
        _config = config;
        _initialOptions = options ?? throw new ArgumentNullException(nameof(options));
        _manifestLoader = manifestLoader ?? throw new ArgumentNullException(nameof(manifestLoader));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _internetArchive = internetArchive ?? throw new ArgumentNullException(nameof(internetArchive));
        _arabicCartoonVerifier = arabicCartoonVerifier ??
            throw new ArgumentNullException(nameof(arabicCartoonVerifier));
        _scraperProvider = scraperProvider;
    }

    public async Task<IReadOnlyList<AudiovisualSource>> FindSourcesAsync(
        AudiovisualIdentity identity,
        AudiovisualUnit? unit = null,
        string? preferredLanguage = null,
        bool requireArabicCartoonVerification = false,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Title))
        {
            return Array.Empty<AudiovisualSource>();
        }

        Task<IReadOnlyList<AudiovisualSource>> configuredProviders =
            FindConfiguredProviderSourcesAsync(identity, unit, preferredLanguage, token);
        InternetArchiveSourceProvider archiveProvider = _config == null
            ? _internetArchive
            : new InternetArchiveSourceProvider(
                AudiovisualOptions.FromConfiguration(_config),
                _requests);
        Task<IReadOnlyList<AudiovisualSource>> archive =
            archiveProvider.FindSourcesAsync(identity, unit, preferredLanguage, token);
        Task<IReadOnlyList<AudiovisualSource>> scraper = _scraperProvider != null
            ? _scraperProvider.FindSourcesAsync(identity, unit, preferredLanguage, token)
            : Task.FromResult<IReadOnlyList<AudiovisualSource>>(Array.Empty<AudiovisualSource>());

        var scraperSources = await scraper.ConfigureAwait(false);
        var configuredSources = await configuredProviders.ConfigureAwait(false);
        var archiveSources = await archive.ConfigureAwait(false);

        var strictSources = configuredSources.Concat(archiveSources)
            .Where(source =>
                ExactAudiovisualMatcher.IsExactMatch(
                    identity,
                    unit,
                    source.Identity,
                    source.Unit));

        AudiovisualSource[] exactSources = strictSources.Concat(scraperSources)
            .Where(source =>
                string.IsNullOrWhiteSpace(preferredLanguage) ||
                source.Languages.Any(language =>
                    AudiovisualProviderDefinition.LanguageMatches(language, preferredLanguage)))
            .DistinctBy(source => source.Location.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requireArabicCartoonVerification)
        {
            if (identity.Kind != AudiovisualMediaKind.Cartoon)
            {
                return Array.Empty<AudiovisualSource>();
            }

            return await _arabicCartoonVerifier.VerifyManyAsync(
                exactSources,
                identity,
                unit,
                token).ConfigureAwait(false);
        }

        return exactSources
            .OrderByDescending(source => source.AccessMode == AudiovisualSourceAccessMode.DirectMedia)
            .ThenBy(source => source.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async IAsyncEnumerable<AudiovisualSourceUpdate> FindSourceUpdatesAsync(SourceSearchRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Identity);
        token.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(request.ProviderId) && request.ProviderId != "internet-archive")
        {
            yield return new(request.OperationId, "", AudiovisualSourceUpdateKind.ProviderFailed,
                Outcome: new("source-selection", ProviderOutcomeStatus.Unsupported, TimeSpan.Zero, DiagnosticCode: "explicit_provider_selection_not_supported"));
            yield return new(request.OperationId, "", AudiovisualSourceUpdateKind.Completed, Completion: new(0, 0, 0, 0, 1));
            yield break;
        }
        var archive = _config == null ? _internetArchive : new InternetArchiveSourceProvider(AudiovisualOptions.FromConfiguration(_config), _requests);
        var providers = new List<SourceProviderBatch>
        {
            new("internet-archive", ct => archive.FindSourcesAsync(request.Identity, request.Unit, null, ct))
        };
        if (string.IsNullOrEmpty(request.ProviderId))
        {
            providers.Add(new("configured-providers", ct => FindConfiguredProviderSourcesAsync(request.Identity, request.Unit, null, ct)));
            if (_scraperProvider != null)
                providers.Add(new("scraper", ct => _scraperProvider.FindSourcesAsync(request.Identity, request.Unit, null, ct))
                { Stream = ct => _scraperProvider.FindSourceCandidatesAsync(request, ct) });
        }
        await foreach (var update in SourceUpdateStream.ReadAsync(request, providers,
            (source, ct) => _arabicCartoonVerifier.VerifyAsync(source, request.Identity, request.Unit, ct), token).ConfigureAwait(false))
            yield return update;
    }

    public IReadOnlyList<ProviderHealthSnapshot> GetProviderHealth()
    {
        return _requests.GetHealthSnapshots();
    }

    public void RefreshProviderManifests()
    {
        _manifestGate.Wait();
        try
        {
            _loadedManifestKey = string.Empty;
            _manifestProvider = null;
            _requests.Invalidate();
        }
        finally
        {
            _manifestGate.Release();
        }
    }

    private async Task<IReadOnlyList<AudiovisualSource>> FindConfiguredProviderSourcesAsync(
        AudiovisualIdentity identity,
        AudiovisualUnit? unit,
        string? preferredLanguage,
        CancellationToken token)
    {
        ManifestAudiovisualSourceProvider? provider = await GetManifestProviderAsync(token)
            .ConfigureAwait(false);
        return provider is null
            ? Array.Empty<AudiovisualSource>()
            : await provider.FindSourcesAsync(identity, unit, preferredLanguage, token)
                .ConfigureAwait(false);
    }

    private async Task<ManifestAudiovisualSourceProvider?> GetManifestProviderAsync(
        CancellationToken token)
    {
        AudiovisualOptions options = _config is null
            ? _initialOptions
            : AudiovisualOptions.FromConfiguration(_config);
        string key = string.Join(
            "\n",
            options.ProviderIndexLocations.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        if (key.Length == 0)
        {
            return null;
        }

        if (_manifestProvider is not null &&
            _loadedManifestKey.Equals(key, StringComparison.Ordinal))
        {
            return _manifestProvider;
        }

        await _manifestGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_manifestProvider is not null &&
                _loadedManifestKey.Equals(key, StringComparison.Ordinal))
            {
                return _manifestProvider;
            }

            try
            {
                AudiovisualProviderManifest manifest = await _manifestLoader.LoadManyAsync(
                    options.ProviderIndexLocations,
                    token).ConfigureAwait(false);
                _manifestProvider = new ManifestAudiovisualSourceProvider(manifest, _requests);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException)
            {
                _manifestProvider = null;
            }

            _loadedManifestKey = key;
            return _manifestProvider;
        }
        finally
        {
            _manifestGate.Release();
        }
    }
}
