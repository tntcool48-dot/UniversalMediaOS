using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.WPF.Helpers;

namespace UniversalMediaOS.WPF.ViewModels;

public sealed partial class AudiovisualCardViewModel : ObservableObject
{
    public AudiovisualCardViewModel(AudiovisualMediaItem item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        WorkKey = AudiovisualIdentityKeys.CreateWorkKey(item.Identity, item.PersistedWorkKey);
    }

    internal string WorkKey { get; private set; }
    internal string CatalogKey => Identity.ContentForm + ":" + WorkKey;
    internal void UsePersistedWorkKey(string? key) { if (!string.IsNullOrWhiteSpace(key)) WorkKey = key; }
    public AudiovisualMediaItem Item { get; }
    public AudiovisualIdentity Identity => Item.Identity;
    public string Title => string.IsNullOrWhiteSpace(Item.Title) ? Item.Identity.Title : Item.Title;
    public string Overview => string.IsNullOrWhiteSpace(Item.Overview)
        ? "No overview is available from the current metadata provider."
        : Item.Overview;
    public string PosterUrl => Item.PosterUrl ?? string.Empty;
    public string YearText => Item.Identity.Year?.ToString() ?? "Year unknown";
    public string RatingText => Item.Rating > 0 ? $"\u2605 {Item.Rating:0.0}" : "Not rated";
    public string GenreText => Item.Genres.Count > 0
        ? string.Join(" \u00B7 ", Item.Genres.Take(3))
        : "Genre unavailable";
    public string LanguageText => string.IsNullOrWhiteSpace(Item.OriginalLanguage)
        ? "Language unknown"
        : Item.OriginalLanguage.ToUpperInvariant();
    public string? MetadataAttributionUrl
    {
        get
        {
            var filmIds = AudiovisualIdentityKeys.GetIds(Identity).Where(id => id.Provider == "wikidata" && id.Namespace == "item").ToArray();
            if (filmIds.Length == 1 && System.Text.RegularExpressions.Regex.IsMatch(filmIds[0].Value, @"\AQ[1-9][0-9]*\z"))
                return "https://www.wikidata.org/wiki/" + filmIds[0].Value;
            var ids = AudiovisualIdentityKeys.GetIds(Identity).Where(id => id.Provider == "tvmaze" && id.Namespace == "show").ToArray();
            return ids.Length == 1 && int.TryParse(ids[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int show) && show > 0
                ? "https://www.tvmaze.com/shows/" + show.ToString(CultureInfo.InvariantCulture) : null;
        }
    }
    public bool HasMetadataAttribution => MetadataAttributionUrl != null;
    public string MetadataAttributionText => MetadataAttributionUrl?.Contains("wikidata.org", StringComparison.Ordinal) == true
        ? "Film data: Wikidata" : "Series data: TVmaze (CC BY-SA)";
    public string? PosterAttributionUrl => Uri.TryCreate(PosterUrl, UriKind.Absolute, out var poster) &&
        poster.Host == "m.media-amazon.com" && System.Text.RegularExpressions.Regex.IsMatch(Identity.ImdbId, @"\Att[0-9]+\z")
            ? "https://www.imdb.com/title/" + Identity.ImdbId + "/" : null;
    public bool HasPosterAttribution => PosterAttributionUrl != null;

    [ObservableProperty]
    private bool _isFavorite;
}

public sealed record AudiovisualSeasonChoice(int? Number)
{
    public string DisplayName => Number.HasValue ? $"Season {Number}" : "Unassigned season";
}

public sealed record AudiovisualEpisodeChoice(AudiovisualEpisodeMetadata Metadata)
{
    public string DisplayName => Metadata.Unit?.EpisodeNumber is { } number
        ? $"{number}. {Metadata.Title}" : $"{(Metadata.IsSpecial ? "Special" : "Unnumbered")}: {Metadata.Title}";
}

public sealed class AudiovisualSourceViewModel
{
    public AudiovisualSourceViewModel(AudiovisualSource source, SourceVerification? verification = null)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Verification = verification;
    }

    public AudiovisualSource Source { get; }
    public SourceVerification? Verification { get; }
    public bool IsVerified => Verification?.Status == SourceVerificationStatus.Verified;
    public string VerificationText => IsVerified ? "MATCH VERIFIED" : "MATCH UNVERIFIED";
    public string StreamActionText => IsVerified ? "Stream" : "Try stream";
    public string ProviderName => string.IsNullOrWhiteSpace(Source.ProviderName)
        ? Source.ProviderId
        : Source.ProviderName;
    public string AccessModeText => Source.AccessMode switch
    {
        AudiovisualSourceAccessMode.DirectMedia => "DIRECT",
        AudiovisualSourceAccessMode.WebPage => "WEB",
        AudiovisualSourceAccessMode.InternetArchiveItem => "ARCHIVE ITEM",
        _ => Source.AccessMode.ToString().ToUpperInvariant()
    };
    public string LanguageText => !IsVerified || Source.Languages.Count == 0
        ? "Audio language unverified"
        : $"Audio: {string.Join(", ", Source.Languages.Select(language => language.ToUpperInvariant()))}";
    public string PlaybackNotice => string.Join(" ", new[] {
        IsVerified ? string.Empty : "Film/episode match unverified.",
        Source.Evidence?.Audio is { Origin: SourceEvidenceOrigin.ProviderItem or SourceEvidenceOrigin.ObservedStream, Languages.Count: > 0 }
            ? string.Empty : "Audio language unverified." }.Where(text => text.Length > 0));
    public string RightsText
    {
        get
        {
            string authorization = Source.Authorization switch
            {
                ProviderAuthorization.PublicDomain => "Public domain",
                ProviderAuthorization.CreativeCommons => "Creative Commons",
                ProviderAuthorization.Licensed => "Licensed",
                ProviderAuthorization.UserAuthorized => "User-authorized",
                _ => "Authorization not declared"
            };
            string rights = !string.IsNullOrWhiteSpace(Source.Rights)
                ? Source.Rights
                : Source.License;
            return string.IsNullOrWhiteSpace(rights) ? authorization : $"{authorization} \u00B7 {rights}";
        }
    }
    public string UnitText => !IsVerified ? "Film/episode match unverified" : Source.Unit.IsFeature
        ? "Feature"
        : $"Season {Source.Unit.SeasonNumber?.ToString() ?? "?"}, episode {Source.Unit.EpisodeNumber?.ToString() ?? "?"}" +
          (string.IsNullOrWhiteSpace(Source.Unit.Title) ? string.Empty : $" \u00B7 {Source.Unit.Title}");
    public bool IsArabicVerified => Source.IsArabicCartoonVerified;
    public bool CanPlay => Source.AccessMode == AudiovisualSourceAccessMode.DirectMedia;
    public bool CanOpenWebsite => Source.AccessMode == AudiovisualSourceAccessMode.WebPage;
    public bool CanDownload => Source.AccessMode == AudiovisualSourceAccessMode.DirectMedia;
    public bool CanWatchViaDownload => Source.AccessMode == AudiovisualSourceAccessMode.DirectMedia;
    public string LibraryDownloadText => Source.Unit.IsFeature ? "Download to library" : "Download episode to library";

    private static bool IsStreamingPlaylist(AudiovisualSource source)
    {
        string extension = Path.GetExtension(source.Location.AbsolutePath);
        return extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mpd", StringComparison.OrdinalIgnoreCase) ||
               source.ContentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
               source.ContentType.Contains("dash+xml", StringComparison.OrdinalIgnoreCase);
    }
}

public abstract partial class AudiovisualCatalogViewModel : ObservableObject, IDisposable
{
    private readonly IAudiovisualCatalogService _catalogService;
    private readonly CartoonService? _cartoonService;
    private readonly AudiovisualLibraryService _libraryService;
    private readonly AuthorizedMediaDownloadService _downloadService;
    private readonly OtherMediaSourceSafetyService _sourceSafetyService;
    private readonly IDialogService _dialogService;
    private readonly IExternalLauncher _externalLauncher;
    private readonly CancellationTokenSource _lifecycleCts = new();
    private CancellationTokenSource? _requestCts;
    private CancellationTokenSource? _catalogCts;
    private CancellationTokenSource? _episodesCts;
    private IReadOnlyList<AudiovisualEpisodeMetadata> _episodeMetadata = [];
    private AudiovisualCatalogRequest? _catalogRequest;
    private string? _nextCatalogToken;
    private CancellationTokenSource? _downloadCts;
    private bool _isTemporaryDownload;
    private bool _isSeasonDownload;
    private bool _hasCompleteEpisodeList;
    private bool _isInitialized;
    private bool _isDisposed;
    private long _sourceSelectionVersion;
    private long _resolvedSelectionVersion = -1;

    protected AudiovisualCatalogViewModel(
        IAudiovisualCatalogService catalogService,
        AudiovisualLibraryService libraryService,
        AuthorizedMediaDownloadService downloadService,
        OtherMediaSourceSafetyService sourceSafetyService,
        IDialogService dialogService,
        IExternalLauncher externalLauncher)
    {
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _cartoonService = catalogService as CartoonService;
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _libraryService.LibraryChanged += LibraryService_LibraryChanged;
        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        _sourceSafetyService = sourceSafetyService ??
            throw new ArgumentNullException(nameof(sourceSafetyService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _externalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));

        Kind = catalogService.Kind;
        HeaderTitle = Kind switch
        {
            AudiovisualMediaKind.Movie => "Movies",
            AudiovisualMediaKind.Television => "TV Shows",
            AudiovisualMediaKind.Cartoon => "Cartoons",
            _ => "Media"
        };
        HeaderSubtitle = Kind switch
        {
            AudiovisualMediaKind.Movie => "Find films, compare versions and keep your watchlist. No metadata key required.",
            AudiovisualMediaKind.Television => "Browse series and choose episodes with TVmaze. No metadata key required.",
            AudiovisualMediaKind.Cartoon => "Browse feature and episodic animation, including the Arabic-dub source lane.",
            _ => "Browse media."
        };
        SearchPlaceholder = $"Search {HeaderTitle.ToLowerInvariant()}...";
        SearchAutomationName = $"{HeaderTitle} search";
        SearchButtonAutomationName = $"Search {HeaderTitle}";
        EmptyTitle = $"No {HeaderTitle.ToLowerInvariant()} found";
        EmptyMessage = "Try another title or review the enabled metadata and scraper sites in Settings.";
    }

    public AudiovisualMediaKind Kind { get; }
    public string HeaderTitle { get; }
    public string HeaderSubtitle { get; }
    public string SearchPlaceholder { get; }
    public string SearchAutomationName { get; }
    public string SearchButtonAutomationName { get; }
    public string EmptyTitle { get; }
    public string EmptyMessage { get; }
    public bool IsCartoon => Kind == AudiovisualMediaKind.Cartoon;
    public bool HasCatalogAttribution => Kind is AudiovisualMediaKind.Television or AudiovisualMediaKind.Movie;
    public string CatalogAttributionText => Kind == AudiovisualMediaKind.Movie ? "Film data: Wikidata" : "Series data: TVmaze (CC BY-SA)";
    public string BrowseButtonText => Kind switch
    { AudiovisualMediaKind.Television => "Browse series", AudiovisualMediaKind.Movie => "Browse films", _ => "Popular" };
    public bool ShowEpisodePicker => SupportsEpisodes && _episodeMetadata.Count > 0;
    public bool ShowManualEpisodeNumbers => SupportsEpisodes && !IsLoadingEpisodes && !ShowEpisodePicker;
    public bool SupportsEpisodes =>
        SelectedItem?.Identity.ContentForm == AudiovisualContentForm.Series ||
        SelectedItem == null && Kind == AudiovisualMediaKind.Television;
    public ObservableRangeCollection<AudiovisualCardViewModel> Items { get; } = new();
    public ObservableCollection<AudiovisualSourceViewModel> Sources { get; } = new();
    public ObservableCollection<AudiovisualSeasonChoice> Seasons { get; } = new();
    public ObservableCollection<AudiovisualEpisodeChoice> Episodes { get; } = new();
    public ObservableCollection<string> LanguageOptions { get; } = new(new[]
    {
        "Any", "Arabic", "English", "French", "Japanese"
    });
    public ObservableCollection<AudiovisualLibraryStatus> LibraryStatuses { get; } =
        new(Enum.GetValues<AudiovisualLibraryStatus>());

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _hasMore;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _isLoadingMore;

    [ObservableProperty]
    private bool _isResolvingSources;

    [ObservableProperty]
    private bool _hasNoResults;

    [ObservableProperty]
    private string _statusText = "Loading popular titles";

    [ObservableProperty]
    private bool _isDetailsOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteButtonText))]
    [NotifyPropertyChangedFor(nameof(SupportsEpisodes))]
    [NotifyPropertyChangedFor(nameof(ShowEpisodePicker))]
    [NotifyPropertyChangedFor(nameof(ShowManualEpisodeNumbers))]
    [NotifyPropertyChangedFor(nameof(ShowSeasonDownload))]
    private AudiovisualCardViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowManualEpisodeNumbers))]
    [NotifyCanExecuteChangedFor(nameof(DownloadSeasonCommand))]
    private bool _isLoadingEpisodes;

    [ObservableProperty]
    private string _episodeStatusText = string.Empty;

    [ObservableProperty]
    private AudiovisualSeasonChoice? _selectedSeason;

    [ObservableProperty]
    private AudiovisualEpisodeChoice? _selectedEpisode;

    [ObservableProperty]
    private bool _arabicOnly;

    [ObservableProperty]
    private string _seasonNumberText = "1";

    [ObservableProperty]
    private string _episodeNumberText = "1";

    [ObservableProperty]
    private string _preferredLanguage = "Any";

    [ObservableProperty]
    private string _sourceStatusText = "Choose an exact episode if needed, then refresh sources.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteButtonText))]
    private bool _selectedIsFavorite;

    [ObservableProperty]
    private AudiovisualLibraryStatus _selectedLibraryStatus;

    [ObservableProperty]
    private string _resumeText = "Not opened yet.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadSeasonCommand))]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadPercentage;

    [ObservableProperty]
    private string _downloadStatusText = string.Empty;

    public string FavoriteButtonText => SelectedIsFavorite ? "\u2605 Favorited" : "\u2606 Add favorite";

    public void Initialize()
    {
        if (_isInitialized || _isDisposed)
        {
            return;
        }

        _isInitialized = true;
        _ = LoadPopularAsync();
    }

    public async Task OpenItemAsync(AudiovisualMediaItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_isDisposed || item.Identity.Kind != Kind)
        {
            return;
        }

        _isInitialized = true;
        var requestedCard = new AudiovisualCardViewModel(item);
        AudiovisualCardViewModel? card = Items.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Item, item) || candidate.CatalogKey == requestedCard.CatalogKey);
        if (card == null)
        {
            card = requestedCard;
            Items.Insert(0, card);
        }

        HasNoResults = false;
        await OpenDetailsAsync(card);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            await LoadPopularAsync();
            return;
        }

        await LoadCatalogPageAsync(new(Kind, AudiovisualCatalogMode.Search, SearchQuery.Trim()), append: false);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task LoadPopularAsync()
    {
        return LoadCatalogPageAsync(new(Kind), append: false);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task ShowDiscoverAsync()
    {
        SearchQuery = string.Empty;
        return LoadPopularAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task ShowLibraryAsync()
    {
        return LoadLibraryAsync(favoritesOnly: false);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private Task ShowFavoritesAsync()
    {
        return LoadLibraryAsync(favoritesOnly: true);
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            await LoadPopularAsync();
        }
        else
        {
            await SearchAsync();
        }
    }

    private bool CanLoadMore() => HasMore && !IsBusy && !IsLoadingMore && !_isDisposed;

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private Task LoadMoreAsync() => _catalogRequest == null || _nextCatalogToken == null
        ? Task.CompletedTask : LoadCatalogPageAsync(_catalogRequest with { ContinuationToken = _nextCatalogToken }, append: true);

    partial void OnSearchQueryChanged(string value) => InvalidateCatalogSequence();
    partial void OnArabicOnlyChanged(bool value)
    {
        InvalidateCatalogSequence();
        InvalidateSourceSelection();
    }

    partial void OnSelectedItemChanged(AudiovisualCardViewModel? value)
    {
        InvalidateSourceSelection();
        ResetEpisodeSelection();
        DownloadStatusText = string.Empty;
    }
    partial void OnSeasonNumberTextChanged(string value) => InvalidateSourceSelection();
    partial void OnEpisodeNumberTextChanged(string value) => InvalidateSourceSelection();
    partial void OnPreferredLanguageChanged(string value) => InvalidateSourceSelection();

    partial void OnSelectedSeasonChanged(AudiovisualSeasonChoice? value)
    {
        SelectedEpisode = null;
        Episodes.Clear();
        if (value != null)
            foreach (var episode in _episodeMetadata.Where(e => e.SeasonNumber == value.Number))
                Episodes.Add(new(episode));
        InvalidateSourceSelection();
    }

    partial void OnSelectedEpisodeChanged(AudiovisualEpisodeChoice? value) => InvalidateSourceSelection();

    private void ResetEpisodeSelection()
    {
        _hasCompleteEpisodeList = false;
        _episodesCts?.Cancel();
        _episodesCts = null;
        _episodeMetadata = [];
        SelectedSeason = null;
        SelectedEpisode = null;
        Seasons.Clear();
        Episodes.Clear();
        IsLoadingEpisodes = false;
        EpisodeStatusText = string.Empty;
        SeasonNumberText = "1";
        EpisodeNumberText = "1";
        OnPropertyChanged(nameof(ShowEpisodePicker));
        OnPropertyChanged(nameof(ShowManualEpisodeNumbers));
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task LoadEpisodesAsync()
    {
        var item = SelectedItem;
        if (_isDisposed || item == null || !SupportsEpisodes) return;
        ResetEpisodeSelection();
        InvalidateSourceSelection();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
        _episodesCts = operation;
        var token = operation.Token;
        bool Current() => !_isDisposed && ReferenceEquals(_episodesCts, operation) &&
            ReferenceEquals(SelectedItem, item) && !token.IsCancellationRequested;
        IsLoadingEpisodes = true;
        EpisodeStatusText = "Loading seasons and episodes...";
        try
        {
            if (_catalogService is not IAudiovisualEpisodeMetadataClient client)
            {
                EpisodeStatusText = "Episode lists are unavailable for this catalog. Enter the numbers below.";
                return;
            }
            var result = await client.GetUnitsAsync(item.Identity, token: token);
            if (!Current()) return;
            var saved = await _libraryService.GetAsync(CreateLibraryKey(item), token);
            if (!Current()) return;
            _episodeMetadata = result.Items;
            _hasCompleteEpisodeList = !result.IsPartial && result.Outcomes.All(outcome => outcome.Status == ProviderOutcomeStatus.Success);
            foreach (var season in result.Items.Select(e => e.SeasonNumber).Distinct().OrderBy(s => s))
                Seasons.Add(new(season));
            SelectedSeason = saved?.LastSeasonNumber is { } savedSeason
                ? Seasons.FirstOrDefault(s => s.Number == savedSeason) ?? Seasons.FirstOrDefault(s => s.Number > 0) ?? Seasons.FirstOrDefault()
                : Seasons.FirstOrDefault(s => s.Number > 0) ?? Seasons.FirstOrDefault();
            if (saved?.LastSeasonNumber != null && saved.LastSeasonNumber == SelectedSeason?.Number &&
                saved.LastEpisodeNumber is { } savedEpisode)
            {
                var matches = Episodes.Where(e => e.Metadata.Unit is { } unit &&
                    unit.SeasonNumber == saved.LastSeasonNumber && unit.EpisodeNumber == savedEpisode).Take(2).ToArray();
                if (matches.Length == 1) SelectedEpisode = matches[0];
            }
            var failure = result.Outcomes.FirstOrDefault(o => o.Status != ProviderOutcomeStatus.Success);
            EpisodeStatusText = result.Items.Count > 0
                ? result.IsPartial || failure != null
                    ? "Some episode details are unavailable. Choose a numbered episode to find sources."
                    : "Choose an episode, then find sources. Specials without exact numbering cannot be matched yet."
                : failure != null
                    ? "Episode list unavailable. " + CatalogFailureText(failure.Status) + " Enter the numbers below or retry."
                    : "No episode list was returned. Enter the numbers below.";
            OnPropertyChanged(nameof(ShowEpisodePicker));
            OnPropertyChanged(nameof(ShowManualEpisodeNumbers));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!Current()) return;
            AppLogger.Log($"{HeaderTitle} episode lookup failed: {ex.Message}", "WARNING");
            EpisodeStatusText = "Episode list unavailable. Enter the numbers below or retry.";
        }
        finally
        {
            if (ReferenceEquals(_episodesCts, operation))
            {
                _episodesCts = null;
                IsLoadingEpisodes = false;
            }
            operation.Dispose();
        }
    }

    [RelayCommand]
    private void OpenMetadataAttribution()
    {
        if (_isDisposed) return;
        string? url = SelectedItem?.MetadataAttributionUrl ?? (Kind == AudiovisualMediaKind.Movie
            ? "https://www.wikidata.org" : HasCatalogAttribution ? "https://www.tvmaze.com" : null);
        if (url != null) _externalLauncher.OpenUrl(url);
    }

    [RelayCommand]
    private void OpenPosterAttribution()
    {
        if (!_isDisposed && SelectedItem?.PosterAttributionUrl is { } url) _externalLauncher.OpenUrl(url);
    }

    private void InvalidateSourceSelection()
    {
        if (_isTemporaryDownload || _isSeasonDownload) _downloadCts?.Cancel();
        _sourceSelectionVersion++;
        _resolvedSelectionVersion = -1;
        _requestCts?.Cancel();
        _requestCts = null;
        IsResolvingSources = false;
        Sources.Clear();
        SourceStatusText = "Selection changed. Refresh sources for this title, episode and language.";
        DownloadSeasonCommand.NotifyCanExecuteChanged();
    }

    private bool IsCurrentSource(AudiovisualSourceViewModel? source) =>
        !_isDisposed && SelectedItem != null && source != null &&
        _resolvedSelectionVersion == _sourceSelectionVersion && Sources.Contains(source);

    private void InvalidateCatalogSequence()
    {
        _catalogCts?.Cancel();
        _catalogCts = null;
        _catalogRequest = null;
        _nextCatalogToken = null;
        HasMore = false;
        IsBusy = false;
        IsLoadingMore = false;
    }

    private CancellationTokenSource ReplaceCatalogToken()
    {
        _catalogCts?.Cancel();
        _catalogCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
        return _catalogCts;
    }

    private async Task LoadCatalogPageAsync(AudiovisualCatalogRequest request, bool append)
    {
        if (_isDisposed || append && !CanLoadMore()) return;
        if (!append)
        {
            InvalidateCatalogSequence();
            _catalogRequest = request;
            Items.ReplaceRange([]);
        }
        CancellationTokenSource operation = ReplaceCatalogToken();
        CancellationToken token = operation.Token;
        IsBusy = !append;
        IsLoadingMore = append;
        HasNoResults = false;
        StatusText = append ? "Loading more titles..." : "Loading...";
        bool Current() => !_isDisposed && ReferenceEquals(_catalogCts, operation) && !token.IsCancellationRequested;
        try
        {
            AudiovisualCatalogPage page;
            if (_catalogService is IPagedAudiovisualCatalogService paged)
                page = await paged.GetPageAsync(request, token);
            else
            {
                var legacy = request.Mode == AudiovisualCatalogMode.Search
                    ? await _catalogService.SearchAsync(request.Query, token)
                    : await _catalogService.GetPopularAsync(token);
                page = new(legacy, null, []);
            }
            if (!Current()) return;
            var failure = page.Outcomes.FirstOrDefault(o => o.Status != ProviderOutcomeStatus.Success);
            if (failure != null && page.Items.Count == 0)
            {
                StatusText = CatalogFailureText(failure.Status) + (append ? " Your loaded titles are still available; retry Load more." : "");
                return;
            }
            var library = await _libraryService.GetAllAsync(Kind, token);
            if (!Current()) return;
            var favorites = library.Where(entry => entry.IsFavorite).SelectMany(entry => entry.Aliases.Append(entry.Key.StableId))
                .ToHashSet(StringComparer.Ordinal);
            var seen = append ? Items.Select(card => card.CatalogKey).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            var cards = new List<AudiovisualCardViewModel>();
            foreach (var item in page.Items.Where(item => item.Identity.Kind == Kind))
            {
                var card = new AudiovisualCardViewModel(item);
                if (!seen.Add(card.CatalogKey)) continue;
                card.IsFavorite = favorites.Contains(CreateLibraryKey(card).StableId);
                cards.Add(card);
            }
            if (!Current()) return;
            if (append) Items.AddRange(cards); else Items.ReplaceRange(cards);
            _nextCatalogToken = page.NextToken;
            HasMore = page.NextToken != null;
            HasNoResults = Items.Count == 0 && !HasMore && failure == null;
            StatusText = failure != null ? CatalogFailureText(failure.Status) + " Some titles are available."
                : Items.Count == 0 ? HasMore ? "No matches on this page. Load more to continue." : "No matching titles"
                : $"{Items.Count} titles" + (page.IsPartial ? " · Some metadata is unavailable" : "") + (page.IsStale ? " · Cached results" : "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!Current()) return;
            AppLogger.Log($"{HeaderTitle} catalog load failed: {ex.GetType().Name}", "WARNING");
            StatusText = append ? "Couldn't load more. Your loaded titles are still available; retry Load more." : "Catalog unavailable";
        }
        finally
        {
            if (ReferenceEquals(_catalogCts, operation))
            {
                _catalogCts = null;
                IsBusy = false;
                IsLoadingMore = false;
            }
            operation.Dispose();
        }
    }

    private static string CatalogFailureText(ProviderOutcomeStatus status) => status switch
    {
        ProviderOutcomeStatus.RateLimited => "The catalog provider is busy. Try again shortly.",
        ProviderOutcomeStatus.Timeout => "The catalog request timed out.",
        ProviderOutcomeStatus.InvalidCredentials => "The catalog provider refused access.",
        ProviderOutcomeStatus.NotConfigured => "No catalog provider is configured.",
        ProviderOutcomeStatus.Unsupported => "This catalog operation is not supported by the current provider.",
        ProviderOutcomeStatus.Disabled => "The catalog provider is disabled.",
        _ => "Catalog unavailable."
    };

    private async Task LoadLibraryAsync(bool favoritesOnly)
    {
        if (_isDisposed) return;
        InvalidateCatalogSequence();
        CancellationTokenSource operation = ReplaceCatalogToken();
        CancellationToken token = operation.Token;
        IsBusy = true;
        HasNoResults = false;
        StatusText = favoritesOnly ? "Loading favorites..." : "Loading saved titles...";
        try
        {
            IReadOnlyList<AudiovisualLibraryEntry> entries =
                await _libraryService.GetAllAsync(Kind, token);
            token.ThrowIfCancellationRequested();
            Items.Clear();
            foreach (AudiovisualLibraryEntry entry in entries.Where(entry =>
                         !favoritesOnly || entry.IsFavorite))
            {
                string overview = entry.LastOpenedUtc != null
                    ? FormatResumeText(entry)
                    : entry.Status == AudiovisualLibraryStatus.None
                        ? "Saved in your independent media library."
                        : $"Library status: {entry.Status}.";
                var card = new AudiovisualCardViewModel(AudiovisualLibraryMapping.ToMediaItem(entry, Kind) with
                { Overview = overview }) { IsFavorite = entry.IsFavorite };
                Items.Add(card);
            }

            HasNoResults = Items.Count == 0;
            StatusText = Items.Count == 0
                ? favoritesOnly
                    ? "No favorites yet"
                    : "Your library is empty"
                : favoritesOnly
                    ? $"{Items.Count} favorite title{(Items.Count == 1 ? string.Empty : "s")}"
                    : $"{Items.Count} saved or recently opened title{(Items.Count == 1 ? string.Empty : "s")}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Log($"{HeaderTitle} library load failed: {ex.Message}", "WARNING");
            if (!ReferenceEquals(_catalogCts, operation) || token.IsCancellationRequested || _isDisposed) return;
            Items.Clear();
            HasNoResults = true;
            StatusText = "Library unavailable";
        }
        finally
        {
            if (ReferenceEquals(_catalogCts, operation))
            {
                IsBusy = false;
                _catalogCts = null;
            }

            operation.Dispose();
        }
    }

    [RelayCommand]
    private async Task OpenDetailsAsync(AudiovisualCardViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        SelectedItem = item;
        IsDetailsOpen = true;
        Sources.Clear();
        SourceStatusText = SupportsEpisodes ? "Choose an episode, then find sources." : "Choose Find sources when you are ready to watch.";
        await LoadLibraryStateAsync(item);
        if (_isDisposed || !ReferenceEquals(SelectedItem, item)) return;
        if (SupportsEpisodes) await LoadEpisodesAsync();
    }

    [RelayCommand]
    private void CloseDetails()
    {
        _requestCts?.Cancel();
        Sources.Clear();
        SelectedItem = null;
        IsDetailsOpen = false;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task FindSourcesAsync()
    {
        AudiovisualCardViewModel? item = SelectedItem;
        if (item == null || _isDisposed)
        {
            return;
        }

        if (!TryCreateUnit(out AudiovisualUnit unit, out string? validationError))
        {
            SourceStatusText = validationError!;
            Sources.Clear();
            return;
        }

        CancellationTokenSource operation = ReplaceRequestToken();
        CancellationToken token = operation.Token;
        long selectionVersion = _sourceSelectionVersion;
        bool Current() => !_isDisposed && selectionVersion == _sourceSelectionVersion &&
            ReferenceEquals(_requestCts, operation) && !token.IsCancellationRequested;
        _resolvedSelectionVersion = -1;
        Sources.Clear();
        IsResolvingSources = true;
        SourceStatusText = "Matching title, year, type, and episode...";
        try
        {
            string? language = ToLanguageCode(PreferredLanguage);
            int failedProviders = 0;
            if (_catalogService is IAudiovisualSourceUpdates updates)
            {
                var request = new SourceSearchRequest
                {
                    Identity = item.Identity,
                    Unit = unit,
                    AudioLanguage = language,
                    RequireArabicCartoonVerification = ArabicOnly && IsCartoon
                };
                var identityRequest = request with { AudioLanguage = null, SubtitleLanguage = null };
                int Preference(AudiovisualSourceViewModel card) =>
                    (card.Source.AccessMode == AudiovisualSourceAccessMode.DirectMedia ? 0 : 4) +
                    (ExactAudiovisualMatcher.VerifyEvidence(identityRequest, card.Source.Evidence).Status == SourceVerificationStatus.Verified ? 0 : 2) +
                    (card.IsVerified ? 0 : 1);
                void AddSource(AudiovisualSourceViewModel card)
                {
                    var previous = Sources.FirstOrDefault(source => source.Source.Location == card.Source.Location);
                    if (previous != null)
                    {
                        if (Preference(previous) <= Preference(card)) return;
                        Sources.Remove(previous);
                    }
                    int index = 0;
                    while (index < Sources.Count && Preference(Sources[index]) <= Preference(card)) index++;
                    Sources.Insert(index, card);
                }
                var unverified = new Dictionary<string, AudiovisualSource>(StringComparer.Ordinal);
                await foreach (AudiovisualSourceUpdate update in updates.FindSourceUpdatesAsync(request, token))
                {
                    if (!Current()) return;
                    if (update.OperationId != request.OperationId) continue;
                    if (update.Kind == AudiovisualSourceUpdateKind.VerificationChanged &&
                        update.Verification?.Status == SourceVerificationStatus.Unverified && update.Source != null &&
                        update.Source.Location.IsAbsoluteUri &&
                        update.Source.Location.Scheme is "http" or "https" &&
                        update.Source.Location.UserInfo.Length == 0 &&
                        update.Source.AccessMode is AudiovisualSourceAccessMode.DirectMedia or AudiovisualSourceAccessMode.WebPage)
                    {
                        unverified[update.CandidateId] = update.Source;
                        if (update.Source is { AccessMode: AudiovisualSourceAccessMode.DirectMedia, MediaValidated: true })
                        {
                            _resolvedSelectionVersion = selectionVersion;
                            AddSource(new AudiovisualSourceViewModel(update.Source, update.Verification));
                            SourceStatusText = $"{Sources.Count} source option{(Sources.Count == 1 ? string.Empty : "s")} available; checking other providers...";
                        }
                    }
                    else if (update.Kind == AudiovisualSourceUpdateKind.SourceReady &&
                             update.Verification?.Status == SourceVerificationStatus.Verified && update.Source != null)
                    {
                        _resolvedSelectionVersion = selectionVersion;
                        AddSource(new AudiovisualSourceViewModel(update.Source, update.Verification));
                        int ready = Sources.Count(source => source.IsVerified);
                        SourceStatusText = $"{ready} verified source{(ready == 1 ? string.Empty : "s")} ready; checking other providers...";
                    }
                    else if (update.Kind == AudiovisualSourceUpdateKind.ProviderFailed)
                        failedProviders++;
                }

                token.ThrowIfCancellationRequested();
                if (!Current()) return;
                foreach (AudiovisualSource candidate in unverified.Values
                             .OrderBy(source => source.AccessMode == AudiovisualSourceAccessMode.DirectMedia ? 0 : 1)
                             .ThenBy(source => source.ProviderName, StringComparer.OrdinalIgnoreCase))
                {
                    if (!Sources.Any(source => source.Source.Location == candidate.Location))
                        AddSource(new AudiovisualSourceViewModel(candidate));
                }
            }
            else
            {
                IReadOnlyList<AudiovisualSource> sources =
                    ArabicOnly && _cartoonService != null
                        ? await _cartoonService.FindArabicSourcesAsync(item.Identity, unit, token)
                        : await _catalogService.FindSourcesAsync(item.Identity, unit, language, token);
                token.ThrowIfCancellationRequested();
                if (!Current()) return;
                foreach (AudiovisualSource source in sources
                             .OrderByDescending(source => source.IsArabicCartoonVerified)
                             .ThenBy(source => source.AccessMode == AudiovisualSourceAccessMode.DirectMedia ? 0 : 1)
                             .ThenBy(source => source.ProviderName, StringComparer.OrdinalIgnoreCase))
                    Sources.Add(new AudiovisualSourceViewModel(source));
            }

            _resolvedSelectionVersion = selectionVersion;

            int streams = Sources.Count(source => source.CanPlay);
            int websites = Sources.Count(source => source.CanOpenWebsite);
            int verified = Sources.Count(source => source.IsVerified);
            SourceStatusText = Sources.Count == 0
                ? ArabicOnly && IsCartoon
                    ? "No source with explicit Arabic-dub evidence was found."
                    : failedProviders > 0 ? "No verified source found; one or more providers failed. Retry source search."
                    : "No source candidate was found for this title and episode."
                : $"{verified} verified, {Sources.Count - verified} unverified candidate{(Sources.Count - verified == 1 ? string.Empty : "s")}: " +
                  $"{streams} native stream option{(streams == 1 ? string.Empty : "s")}, " +
                  $"{websites} website fallback{(websites == 1 ? string.Empty : "s")}.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Log($"{HeaderTitle} source lookup failed: {ex.Message}", "WARNING");
            if (!Current()) return;
            if (Sources.Count > 0)
                SourceStatusText = $"{Sources.Count} source option{(Sources.Count == 1 ? string.Empty : "s")} remain available; another provider failed. Retry to refresh.";
            else
                SourceStatusText = "Source lookup failed. Check provider settings and try again.";
        }
        finally
        {
            if (ReferenceEquals(_requestCts, operation))
            {
                IsResolvingSources = false;
                _requestCts = null;
            }

            operation.Dispose();
        }
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        AudiovisualCardViewModel? item = SelectedItem;
        if (item == null)
        {
            return;
        }

        bool next = !SelectedIsFavorite;
        AudiovisualLibraryEntry entry = await _libraryService.SetFavoriteAsync(
            CreateLibraryKey(item),
            item.Title,
            item.PosterUrl,
            next,
            _lifecycleCts.Token);
        SelectedIsFavorite = entry.IsFavorite;
        item.IsFavorite = entry.IsFavorite;
    }

    [RelayCommand]
    private async Task SaveLibraryStatusAsync()
    {
        AudiovisualCardViewModel? item = SelectedItem;
        if (item == null)
        {
            return;
        }

        await _libraryService.SetStatusAsync(
            CreateLibraryKey(item),
            item.Title,
            item.PosterUrl,
            SelectedLibraryStatus,
            _lifecycleCts.Token);
        ResumeText = SelectedLibraryStatus == AudiovisualLibraryStatus.None
            ? "Not currently tracked."
            : $"Status: {SelectedLibraryStatus}.";
    }

    [RelayCommand]
    private Task PlaySourceAsync(AudiovisualSourceViewModel? sourceViewModel) =>
        OpenSourceInPlayerAsync(sourceViewModel, AudiovisualSourceAccessMode.DirectMedia);

    [RelayCommand]
    private Task OpenWebsiteSourceAsync(AudiovisualSourceViewModel? sourceViewModel) =>
        OpenSourceInPlayerAsync(sourceViewModel, AudiovisualSourceAccessMode.WebPage);

    private async Task OpenSourceInPlayerAsync(AudiovisualSourceViewModel? sourceViewModel,
        AudiovisualSourceAccessMode expectedMode)
    {
        AudiovisualCardViewModel? item = SelectedItem;
        if (item == null || sourceViewModel == null || sourceViewModel.Source.AccessMode != expectedMode ||
            !IsCurrentSource(sourceViewModel))
        {
            return;
        }

        AudiovisualSource source = sourceViewModel.Source;
        var playbackContext = new AudiovisualPlaybackContext(item.WorkKey, item.Identity, source.Unit,
            item.Title, item.PosterUrl, source);
        int? season = source.Unit.SeasonNumber;
        int? episode = source.Unit.EpisodeNumber;
        string playTitle = source.Unit.IsFeature
            ? item.Title
            : $"{item.Title} \u00B7 S{season:00}E{episode:00}";

        try
        {
            bool useWebView = source.AccessMode == AudiovisualSourceAccessMode.WebPage;
            Uri safeLocation = await _sourceSafetyService.ResolvePublicLocationAsync(
                source,
                _lifecycleCts.Token);
            if (!IsCurrentSource(sourceViewModel)) return;
            await _libraryService.RecordOpenedAsync(
                CreateLibraryKey(item),
                item.Title,
                item.PosterUrl,
                season,
                episode,
                _lifecycleCts.Token);
            if (!IsCurrentSource(sourceViewModel)) return;
            WeakReferenceMessenger.Default.Send(new PlayMediaMessage(
                safeLocation.AbsoluteUri,
                playTitle,
                isWebView: useWebView,
                referer: source.Referer,
                malId: 0,
                episodeNumber: episode?.ToString() ?? string.Empty,
                contentType: source.ContentType,
                userAgent: source.UserAgent,
                cookie: source.Cookie,
                requestHeaders: source.RequestHeaders,
                audiovisualContext: playbackContext,
                subtitles: source.Subtitles,
                audioNotice: sourceViewModel.PlaybackNotice,
                validatedHlsVariant: source.ValidatedHlsVariant));
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidDataException or OperationCanceledException)
        {
            AppLogger.Log($"{HeaderTitle} source safety check failed: {ex.Message}", "WARNING");
            _dialogService.ShowErrorDialog(
                "The source could not be verified as a public network location.",
                "Source Blocked");
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task OpenSourceAsync(AudiovisualSourceViewModel? sourceViewModel)
    {
        if (sourceViewModel == null || !IsCurrentSource(sourceViewModel))
        {
            return;
        }

        if (sourceViewModel.Source.Provenance ==
                AudiovisualSourceProvenance.ConfiguredProvider &&
            !_dialogService.ShowConfirmDialog(
                $"Open '{sourceViewModel.ProviderName}' in your system browser? " +
                "This leaves the app's protected network client, so continue only if you trust the configured provider.",
                "Open External Provider"))
        {
            return;
        }

        try
        {
            Uri safeLocation = await _sourceSafetyService.ResolvePublicLocationAsync(
                sourceViewModel.Source,
                _lifecycleCts.Token);
            if (!IsCurrentSource(sourceViewModel)) return;
            if (_externalLauncher.OpenUrl(safeLocation.AbsoluteUri))
            {
                return;
            }
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidDataException or OperationCanceledException)
        {
            AppLogger.Log($"{HeaderTitle} source open safety check failed: {ex.Message}", "WARNING");
        }

        _dialogService.ShowErrorDialog(
            "The source URL could not be verified or opened.",
            "Unable to Open Source");
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task DownloadSourceAsync(AudiovisualSourceViewModel? sourceViewModel) =>
        DownloadSelectedSourceAsync(sourceViewModel, temporary: false);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task WatchViaDownloadAsync(AudiovisualSourceViewModel? sourceViewModel) =>
        DownloadSelectedSourceAsync(sourceViewModel, temporary: true);

    private async Task DownloadSelectedSourceAsync(AudiovisualSourceViewModel? sourceViewModel, bool temporary)
    {
        AudiovisualCardViewModel? item = SelectedItem;
        if (item == null || sourceViewModel == null ||
            !(temporary ? sourceViewModel.CanWatchViaDownload : sourceViewModel.CanDownload) || !IsCurrentSource(sourceViewModel))
        {
            return;
        }

        _downloadCts?.Cancel();
        CancellationTokenSource operation =
            CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
        _downloadCts = operation;
        _isTemporaryDownload = temporary;
        _isSeasonDownload = false;
        var context = new AudiovisualPlaybackContext(item.WorkKey, item.Identity, sourceViewModel.Source.Unit,
            item.Title, item.PosterUrl, sourceViewModel.Source);
        bool Current() => !_isDisposed && ReferenceEquals(_downloadCts, operation) &&
            !operation.IsCancellationRequested && (!temporary || IsCurrentSource(sourceViewModel));
        TemporaryMediaWatchResult? watchResult = null;
        bool reportProgress = true;
        IsDownloading = true;
        DownloadPercentage = 0;
        DownloadStatusText = temporary ? "Downloading for temporary playback..." : "Starting library download...";
        try
        {
            var progress = new Progress<AuthorizedMediaDownloadProgress>(value =>
            {
                if (!reportProgress || !Current()) return;
                DownloadPercentage = value.Percentage ?? DownloadPercentage;
                DownloadStatusText = value.DisplayText;
            });
            if (temporary)
            {
                watchResult = await _downloadService.DownloadTemporaryAsync(sourceViewModel.Source, context,
                    progress, operation.Token);
                reportProgress = false;
                if (!Current()) return;
                await _libraryService.RecordOpenedAsync(CreateLibraryKey(item), item.Title, item.PosterUrl,
                    context.Unit.SeasonNumber, context.Unit.EpisodeNumber, operation.Token);
                if (!Current()) return;
                string title = context.Unit.IsFeature ? item.Title
                    : $"{item.Title} \u00B7 S{context.Unit.SeasonNumber:00}E{context.Unit.EpisodeNumber:00}";
                WeakReferenceMessenger.Default.Send(new PlayMediaMessage(watchResult.FilePath, title,
                    isWebView: false, episodeNumber: context.Unit.EpisodeNumber?.ToString() ?? string.Empty,
                    audiovisualContext: context, temporaryWatchLease: watchResult.Lease,
                    localCaptionPaths: watchResult.CaptionPaths,
                    audioNotice: (sourceViewModel.IsVerified ? string.Empty : "Film/episode match unverified. ") + watchResult.AudioNotice));
                watchResult = null; // The player owns the lease after a successful handoff.
                DownloadPercentage = 100;
                DownloadStatusText = "Opened downloaded video. It stays until its last player tab closes.";
            }
            else
            {
                var saved = await _downloadService.DownloadLibraryAsync(sourceViewModel.Source, context,
                    ToLanguageCode(PreferredLanguage), progress, operation.Token);
                reportProgress = false;
                if (!Current()) return;
                DownloadPercentage = 100;
                DownloadStatusText = $"Saved video and {saved.CaptionPaths.Count} caption file(s) to {saved.FilePath}";
                WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"{item.Title} finished downloading."));
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!_isDisposed && ReferenceEquals(_downloadCts, operation)) DownloadStatusText = "Download cancelled.";
        }
        catch (Exception ex)
        {
            AppLogger.Log($"{HeaderTitle} direct download failed: {ex.Message}", "WARNING");
            if (Current())
            {
                DownloadStatusText = "Download failed.";
                _dialogService.ShowErrorDialog(ex.Message, "Download Failed");
            }
        }
        finally
        {
            reportProgress = false;
            watchResult?.Lease.Dispose();
            if (ReferenceEquals(_downloadCts, operation))
            {
                _downloadCts = null;
                _isTemporaryDownload = false;
                _isSeasonDownload = false;
                IsDownloading = false;
                DownloadSeasonCommand.NotifyCanExecuteChanged();
            }

            operation.Dispose();
        }
    }

    [RelayCommand]
    private void CancelDownload()
    {
        _downloadCts?.Cancel();
    }

    public bool ShowSeasonDownload => Kind == AudiovisualMediaKind.Television && SupportsEpisodes;

    private bool CanDownloadSeason() => ShowSeasonDownload && !_isDisposed && !IsDownloading && !IsLoadingEpisodes &&
        _hasCompleteEpisodeList && SelectedItem != null && SelectedSeason?.Number is > 0 && Episodes.Count > 0 &&
        Episodes.Count <= 500 && Episodes.All(episode => episode.Metadata.Unit is { EpisodeNumber: > 0 } unit &&
            unit.SeasonNumber == SelectedSeason.Number) &&
        Episodes.Select(episode => episode.Metadata.Unit!.EpisodeNumber).Distinct().Count() == Episodes.Count;

    [RelayCommand(CanExecute = nameof(CanDownloadSeason), AllowConcurrentExecutions = true)]
    private async Task DownloadSeasonAsync()
    {
        if (!CanDownloadSeason()) return;
        var item = SelectedItem!;
        int season = SelectedSeason!.Number!.Value;
        var units = Episodes.Select(episode => episode.Metadata.Unit!).OrderBy(unit => unit.EpisodeNumber).ToArray();
        string? language = ToLanguageCode(PreferredLanguage);
        long selection = _sourceSelectionVersion;
        _downloadCts?.Cancel();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
        operation.CancelAfter(TimeSpan.FromHours(24));
        _downloadCts = operation;
        _isSeasonDownload = true;
        IsDownloading = true;
        DownloadSeasonCommand.NotifyCanExecuteChanged();
        bool Current() => !_isDisposed && selection == _sourceSelectionVersion &&
            ReferenceEquals(_downloadCts, operation) && !operation.IsCancellationRequested;
        int completed = 0;
        AudiovisualUnit? currentUnit = null;
        string lastPath = string.Empty;
        try
        {
            foreach (var unit in units)
            {
                operation.Token.ThrowIfCancellationRequested();
                if (!Current()) return;
                currentUnit = unit;
                var savedContext = new AudiovisualPlaybackContext(item.WorkKey, item.Identity, unit, item.Title, item.PosterUrl,
                    new AudiovisualSource { Identity = item.Identity, Unit = unit });
                DownloadStatusText = $"Checking saved S{season:00}E{unit.EpisodeNumber:00} · {completed}/{units.Length} ready";
                var existing = await _downloadService.FindLibraryDownloadAsync(savedContext, language, operation.Token);
                if (!Current()) return;
                if (existing != null)
                {
                    completed++;
                    lastPath = existing.FilePath;
                    DownloadPercentage = completed * 100d / units.Length;
                    continue;
                }
                DownloadStatusText = $"Finding a verified source for S{season:00}E{unit.EpisodeNumber:00} · {completed}/{units.Length} saved";
                var request = new SourceSearchRequest { Identity = item.Identity, Unit = unit, AudioLanguage = language };
                var source = await ResolveSeasonDownloadSourceAsync(request, operation.Token);
                if (!Current()) return;
                var context = new AudiovisualPlaybackContext(item.WorkKey, item.Identity, unit, item.Title, item.PosterUrl, source);
                bool reporting = true;
                var progress = new Progress<AuthorizedMediaDownloadProgress>(value =>
                {
                    if (!reporting || !Current()) return;
                    DownloadPercentage = 100d * (completed + (value.Percentage ?? 0) / 100) / units.Length;
                    DownloadStatusText = $"Saving S{season:00}E{unit.EpisodeNumber:00} · {value.DisplayText} · {completed}/{units.Length} saved";
                });
                LibraryMediaDownloadResult result;
                try { result = await _downloadService.DownloadLibraryAsync(source, context, language, progress, operation.Token); }
                finally { reporting = false; }
                completed++;
                lastPath = result.FilePath;
                if (!Current()) return;
            }
            DownloadPercentage = 100;
            DownloadStatusText = $"Saved {completed} episodes from Season {season}, including available captions. Last file: {lastPath}";
            WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"{item.Title} · Season {season}: {completed} episodes saved."));
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!_isDisposed && ReferenceEquals(_downloadCts, operation) && ReferenceEquals(SelectedItem, item))
                DownloadStatusText = $"Season download stopped. {completed}/{units.Length} completed episodes are kept in your library.";
        }
        catch (Exception ex)
        {
            AppLogger.Log($"TV season download stopped at S{season:00}E{currentUnit?.EpisodeNumber:00}: {ex.Message}", "WARNING");
            if (Current()) DownloadStatusText = $"Stopped at S{season:00}E{currentUnit?.EpisodeNumber:00}: {ex.Message} {completed}/{units.Length} completed episodes are kept.";
        }
        finally
        {
            if (ReferenceEquals(_downloadCts, operation))
            {
                _downloadCts = null;
                _isSeasonDownload = false;
                IsDownloading = false;
                DownloadSeasonCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private async Task<AudiovisualSource> ResolveSeasonDownloadSourceAsync(SourceSearchRequest request, CancellationToken token)
    {
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(token);
        lookup.CancelAfter(TimeSpan.FromMinutes(2));
        bool Usable(AudiovisualSource source) => source.AccessMode == AudiovisualSourceAccessMode.DirectMedia &&
            ExactAudiovisualMatcher.VerifyEvidence(request, source.Evidence).Status == SourceVerificationStatus.Verified;
        try
        {
            if (_catalogService is IAudiovisualSourceUpdates updates)
            {
                await foreach (var update in updates.FindSourceUpdatesAsync(request, lookup.Token))
                {
                    lookup.Token.ThrowIfCancellationRequested();
                    if (update.OperationId == request.OperationId && update.Kind == AudiovisualSourceUpdateKind.SourceReady &&
                        update.Source is { } source && Usable(source)) return source;
                }
            }
            else
            {
                var sources = await _catalogService.FindSourcesAsync(request.Identity, request.Unit, request.AudioLanguage, lookup.Token);
                lookup.Token.ThrowIfCancellationRequested();
                if (sources.FirstOrDefault(Usable) is { } source) return source;
            }
            throw new InvalidOperationException("No independently verified native source with the requested audio was found. Retry this season or choose Stream for the episode.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Episode source search exceeded two minutes."); }
        finally { await lookup.CancelAsync(); }
    }

    private async Task LoadLibraryStateAsync(AudiovisualCardViewModel item)
    {
        AudiovisualLibraryEntry? entry = await _libraryService.GetAsync(
            CreateLibraryKey(item),
            _lifecycleCts.Token);
        if (_isDisposed || !ReferenceEquals(SelectedItem, item)) return;
        item.UsePersistedWorkKey(entry?.Key.WorkKey);
        SelectedIsFavorite = entry?.IsFavorite == true;
        SelectedLibraryStatus = entry?.Status ?? AudiovisualLibraryStatus.None;
        item.IsFavorite = SelectedIsFavorite;
        ResumeText = FormatResumeText(entry);
    }

    private async void LibraryService_LibraryChanged(object? sender, EventArgs e)
    {
        var item = SelectedItem;
        if (_isDisposed || item == null) return;
        try
        {
            var entry = await _libraryService.GetAsync(CreateLibraryKey(item), _lifecycleCts.Token).ConfigureAwait(false);
            void Apply()
            {
                if (!_isDisposed && ReferenceEquals(SelectedItem, item)) ResumeText = FormatResumeText(entry);
            }
            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
                await dispatcher.InvokeAsync(Apply);
            else Apply();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLogger.Log($"{HeaderTitle} progress summary refresh failed: {ex.Message}", "WARNING"); }
    }

    internal static string FormatResumeText(AudiovisualLibraryEntry? entry)
    {
        if (entry?.LastOpenedUtc is not { } opened) return "Not opened yet.";
        string unit = entry.LastEpisodeNumber is { } episode
            ? entry.LastSeasonNumber is { } season ? $"S{season:00}E{episode:00}" : $"Episode {episode}"
            : "movie";
        if (entry.PositionSeconds > 5 && double.IsFinite(entry.PositionSeconds))
        {
            string position = TimeSpan.FromSeconds(entry.PositionSeconds).ToString(
                entry.PositionSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
            return $"Resume {unit} at {position} \u00B7 {opened.LocalDateTime:g}";
        }
        return entry.LastEpisodeNumber != null
            ? $"Last opened {unit} \u00B7 {opened.LocalDateTime:g}" : $"Last opened {opened.LocalDateTime:g}";
    }

    private bool TryCreateUnit(out AudiovisualUnit unit, out string? error)
    {
        if (!SupportsEpisodes)
        {
            unit = AudiovisualUnit.Feature;
            error = null;
            return true;
        }

        if (IsLoadingEpisodes || ShowEpisodePicker)
        {
            unit = SelectedEpisode?.Metadata.Unit ?? AudiovisualUnit.Feature;
            error = IsLoadingEpisodes ? "Wait for the episode list to load."
                : SelectedEpisode == null || !Episodes.Contains(SelectedEpisode) || SelectedSeason == null || !Seasons.Contains(SelectedSeason)
                    ? "Choose an episode from this season."
                    : SelectedEpisode.Metadata.Unit == null ? "This episode has no exact playback numbering. Source matching is unavailable for it." : null;
            return error == null;
        }

        if (!int.TryParse(SeasonNumberText, out int season) || season < 0)
        {
            unit = AudiovisualUnit.Feature;
            error = "Season must be zero or a positive number.";
            return false;
        }

        if (!int.TryParse(EpisodeNumberText, out int episode) || episode < 1)
        {
            unit = AudiovisualUnit.Feature;
            error = "Episode must be a positive number.";
            return false;
        }

        unit = new AudiovisualUnit
        {
            SeasonNumber = season,
            EpisodeNumber = episode
        };
        error = null;
        return true;
    }

    private CancellationTokenSource ReplaceRequestToken()
    {
        _requestCts?.Cancel();
        _requestCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
        return _requestCts;
    }

    private static string? ToLanguageCode(string value)
    {
        return value switch
        {
            "Arabic" => "ar",
            "English" => "en",
            "French" => "fr",
            "Japanese" => "ja",
            _ => null
        };
    }

    private AudiovisualLibraryKey CreateLibraryKey(AudiovisualCardViewModel item)
    {
        return AudiovisualLibraryKey.Create(item.Identity, item.WorkKey);
    }

    private static string BuildDownloadTitle(
        AudiovisualCardViewModel item,
        AudiovisualUnit unit)
    {
        return unit.IsFeature
            ? item.Title
            : $"{item.Title} S{unit.SeasonNumber ?? 0:00}E{unit.EpisodeNumber ?? 0:00}";
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _libraryService.LibraryChanged -= LibraryService_LibraryChanged;
        InvalidateSourceSelection();
        InvalidateCatalogSequence();
        ResetEpisodeSelection();
        _requestCts?.Cancel();
        _requestCts = null;
        _downloadCts?.Cancel();
        _downloadCts = null;
        _isTemporaryDownload = false;
        IsDownloading = false;
        _lifecycleCts.Cancel();
        _lifecycleCts.Dispose();
    }
}

public sealed class MovieCatalogViewModel : AudiovisualCatalogViewModel
{
    public MovieCatalogViewModel(
        MovieService service,
        AudiovisualLibraryService libraryService,
        AuthorizedMediaDownloadService downloadService,
        OtherMediaSourceSafetyService sourceSafetyService,
        IDialogService dialogService,
        IExternalLauncher externalLauncher)
        : base(
            service,
            libraryService,
            downloadService,
            sourceSafetyService,
            dialogService,
            externalLauncher)
    {
    }
}

public sealed class TvCatalogViewModel : AudiovisualCatalogViewModel
{
    public TvCatalogViewModel(
        TvService service,
        AudiovisualLibraryService libraryService,
        AuthorizedMediaDownloadService downloadService,
        OtherMediaSourceSafetyService sourceSafetyService,
        IDialogService dialogService,
        IExternalLauncher externalLauncher)
        : base(
            service,
            libraryService,
            downloadService,
            sourceSafetyService,
            dialogService,
            externalLauncher)
    {
    }
}

public sealed class CartoonCatalogViewModel : AudiovisualCatalogViewModel
{
    public CartoonCatalogViewModel(
        CartoonService service,
        AudiovisualLibraryService libraryService,
        AuthorizedMediaDownloadService downloadService,
        OtherMediaSourceSafetyService sourceSafetyService,
        IDialogService dialogService,
        IExternalLauncher externalLauncher)
        : base(
            service,
            libraryService,
            downloadService,
            sourceSafetyService,
            dialogService,
            externalLauncher)
    {
    }
}
