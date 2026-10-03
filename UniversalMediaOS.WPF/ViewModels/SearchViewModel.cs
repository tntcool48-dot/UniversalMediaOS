using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using System;
using UniversalMediaOS.WPF.Helpers;
using CommunityToolkit.Mvvm.Messaging;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class SearchViewModel : ObservableObject, IDisposable
    {
        private const int DubCheckConcurrencyLimit = 4;
        private const int MaxAutomaticDubAnnotationsPerSearch = 12;
        internal const double SearchCardFootprint = 266;
        private readonly FuzzyShieldSearch _searchService;
        private readonly DubAvailabilityService _dubAvailabilityService;
        private readonly FavoriteMediaService _favoriteService;
        private readonly CancellationTokenSource _lifecycleCts = new();
        private readonly SemaphoreSlim _dubAnnotationSemaphore = new(2, 2);
        private readonly HashSet<string> _automaticDubAnnotationKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _automaticDubAnnotationLock = new();
        private readonly object _searchIdleLock = new();
        private TaskCompletionSource<bool> _searchIdleSignal = CreateCompletedSearchIdleSignal();
        private CancellationTokenSource? _filterRefreshCts;
        private CancellationTokenSource? _dubAnnotationCts;
        private int _searchGeneration;
        private bool _isInitialized;
        private bool _isDisposed;

        [ObservableProperty]
        private string _searchQuery = string.Empty;

        [ObservableProperty]
        private bool _isSearching;

        [ObservableProperty]
        private string _resultsDescription = "Loading AniList recommendations";

        private bool _isLoadingMore;

        [ObservableProperty]
        private string _selectedStatus = "Any";

        [ObservableProperty]
        private string _selectedSort = "Trending";

        [ObservableProperty]
        private string _selectedAudio = "Any";

        [ObservableProperty]
        private string _tagSummary = "All tags";

        [ObservableProperty]
        private bool _isLoadingTags;

        [ObservableProperty]
        private bool _hasNoResults;

        public ObservableRangeCollection<MediaResult> SearchResults { get; } = new();

        public ObservableRangeCollection<SearchResultRow> SearchResultRows { get; } = new();

        public ObservableRangeCollection<AnimeTagFilterViewModel> TagFilters { get; } = new();

        public ObservableCollection<string> StatusFilters { get; } = new(new[]
        {
            "Any", "Airing", "Finished", "Upcoming"
        });

        public ObservableCollection<string> SortFilters { get; } = new(new[]
        {
            "Trending", "Popular", "Top Rated", "Newest", "Relevance"
        });

        public ObservableCollection<string> AudioFilters { get; } = new(new[]
        {
            "Any", "Sub", "Dub"
        });

        private int _currentPage;
        private bool _hasNextPage = true;
        private string _activeQuery = string.Empty;
        private int _resultColumns = 1;

        public SearchViewModel(
            FuzzyShieldSearch searchService,
            DubAvailabilityService dubAvailabilityService,
            FavoriteMediaService favoriteService)
        {
            _searchService = searchService;
            _dubAvailabilityService = dubAvailabilityService;
            _favoriteService = favoriteService;
            SearchResults.CollectionChanged += SearchResults_CollectionChanged;
        }

        public void SetResultsViewportWidth(double viewportWidth)
        {
            int columns = CalculateResultColumns(viewportWidth);
            if (columns == _resultColumns)
            {
                return;
            }

            _resultColumns = columns;
            RebuildSearchResultRows(preserveExistingPrefix: false);
        }

        internal static int CalculateResultColumns(double viewportWidth)
        {
            if (!double.IsFinite(viewportWidth) || viewportWidth <= 0)
            {
                return 1;
            }

            return Math.Max(1, (int)Math.Floor(Math.Max(0, viewportWidth - 20) / SearchCardFootprint));
        }

        private void SearchResults_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RebuildSearchResultRows(preserveExistingPrefix: true);
        }

        private void RebuildSearchResultRows(bool preserveExistingPrefix)
        {
            var rows = SearchResults
                .Select((result, index) => (result, index))
                .GroupBy(item => item.index / _resultColumns)
                .Select(group => new SearchResultRow(group.Select(item => item.result).ToArray()))
                .ToArray();

            MediaResult[] previouslyRendered = SearchResultRows
                .SelectMany(row => row.Results)
                .ToArray();
            bool existingItemsArePrefix = preserveExistingPrefix &&
                previouslyRendered.Length <= SearchResults.Count &&
                previouslyRendered
                    .Select((result, index) => ReferenceEquals(result, SearchResults[index]))
                    .All(isSame => isSame);
            if (!existingItemsArePrefix)
            {
                SearchResultRows.ReplaceRange(rows);
                return;
            }

            // Keep completed row objects stable so a load-more append does not reset the
            // virtualizing panel or jump the user's scroll position. Only the last partial
            // row and newly appended rows can change.
            int firstChangedRow = previouslyRendered.Length / _resultColumns;
            int overlap = Math.Min(SearchResultRows.Count, rows.Length);
            for (int i = firstChangedRow; i < overlap; i++)
            {
                SearchResultRows[i] = rows[i];
            }

            while (SearchResultRows.Count > rows.Length)
            {
                SearchResultRows.RemoveAt(SearchResultRows.Count - 1);
            }

            for (int i = SearchResultRows.Count; i < rows.Length; i++)
            {
                SearchResultRows.Add(rows[i]);
            }
        }

        public void Initialize()
        {
            if (_isInitialized || _isDisposed)
            {
                return;
            }

            _isInitialized = true;
            _ = InitializeAsync(_lifecycleCts.Token);
        }

        public void CancelActiveWork()
        {
            if (!_lifecycleCts.IsCancellationRequested)
            {
                _lifecycleCts.Cancel();
            }

            _filterRefreshCts?.Cancel();
            _dubAnnotationCts?.Cancel();
        }

        private async Task InitializeAsync(CancellationToken token)
        {
            try
            {
                await Task.WhenAll(
                    LoadTagFiltersAsync(token),
                    LoadRecommendationsAsync(token));
            }
            catch (OperationCanceledException)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log("Anime search initialization cancelled.");
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Anime search initialization failed: {ex.Message}", "WARNING");
            }
        }

        private async Task LoadTagFiltersAsync(CancellationToken token)
        {
            IsLoadingTags = true;
            try
            {
                var options = await _searchService.GetAnimeTagOptionsAsync(token);
                TagFilters.ReplaceRange(options.Select(option => new AnimeTagFilterViewModel(option)));
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Loaded {TagFilters.Count} AniList genre/tag filters.");
            }
            catch (OperationCanceledException)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log("AniList genre/tag filter load cancelled.");
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Failed to load AniList genre/tag filters: {ex.Message}", "WARNING");
                TagFilters.ReplaceRange(CreateFallbackTagOptions().Select(option => new AnimeTagFilterViewModel(option)));
            }
            finally
            {
                IsLoadingTags = false;
                RefreshTagSummary();
            }
        }

        private async Task LoadRecommendationsAsync(CancellationToken token)
        {
            int generation = NextSearchGeneration();
            IsSearching = true;
            ResultsDescription = "Trending anime from AniList";
            _currentPage = 1;
            _activeQuery = string.Empty;
            _hasNextPage = true;

            try
            {
                var page = await _searchService.SearchAnimePageAsync(string.Empty, _currentPage, 36, BuildFilters(), token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentSearchGeneration(generation))
                {
                    return;
                }

                _hasNextPage = page.HasNextPage;
                var filtered = await ApplyAudioFilterAsync(page.Results, token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentSearchGeneration(generation))
                {
                    return;
                }

                if (filtered.Count > 0)
                {
                    _favoriteService.ApplyFavorites(filtered);
                    SearchResults.ReplaceRange(filtered);
                    UpdateNoResultsState();
                    return;
                }

                ShowNoAnimeFound("No anime found. You may be offline or the anime provider returned no results.");
            }
            catch (OperationCanceledException)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log("Anime recommendation load cancelled.");
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Anime recommendation load failed: {ex.Message}", "WARNING");
                ShowNoAnimeFound("No anime found. You may be offline or the anime provider returned no results.");
            }
            finally
            {
                if (IsCurrentSearchGeneration(generation))
                {
                    IsSearching = false;
                    UpdateNoResultsState();
                }
            }
        }

        private void ShowNoAnimeFound(string message)
        {
            ResultsDescription = message;
            SearchResults.ReplaceRange(Array.Empty<MediaResult>());
            UpdateNoResultsState();
        }

        [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
        private async Task SearchAsync(CancellationToken token)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, _lifecycleCts.Token);
            token = linkedCts.Token;

            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                await LoadRecommendationsAsync(token);
                return;
            }

            int generation = NextSearchGeneration();
            UniversalMediaOS.Core.Helpers.AppLogger.Log($"SearchAsync invoked. Query: '{SearchQuery}'");
            IsSearching = true;
            ResultsDescription = $"AniList results for \"{SearchQuery.Trim()}\"";
            _currentPage = 1;
            _activeQuery = SearchQuery.Trim();
            _hasNextPage = true;
            try
            {
                var page = await _searchService.SearchAnimePageAsync(_activeQuery, _currentPage, 36, BuildFilters(), token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentSearchGeneration(generation))
                {
                    return;
                }

                _hasNextPage = page.HasNextPage;
                var filtered = await ApplyAudioFilterAsync(page.Results, token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentSearchGeneration(generation))
                {
                    return;
                }

                _favoriteService.ApplyFavorites(filtered);
                SearchResults.ReplaceRange(filtered);
                UpdateNoResultsState();
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"SearchAsync complete. Found {filtered.Count} visible results from {page.Results.Count} provider results.");
            }
            catch (OperationCanceledException)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log("SearchAsync cancelled by user.");
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"SearchAsync failed. Error: {ex.Message}", "ERROR");
                ResultsDescription = "Search failed; showing previous results";
            }
            finally
            {
                if (IsCurrentSearchGeneration(generation))
                {
                    IsSearching = false;
                    UpdateNoResultsState();
                }
            }
        }

        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task LoadMoreAnimeAsync()
        {
            if (IsSearching || _isLoadingMore || !_hasNextPage)
            {
                return;
            }

            _isLoadingMore = true;
            int generation = Volatile.Read(ref _searchGeneration);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
            var token = linkedCts.Token;
            try
            {
                int nextPage = _currentPage + 1;
                var page = await _searchService.SearchAnimePageAsync(_activeQuery, nextPage, 36, BuildFilters(), token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentSearchGeneration(generation))
                {
                    return;
                }

                var filtered = await ApplyAudioFilterAsync(page.Results, token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentSearchGeneration(generation))
                {
                    return;
                }

                if (page.Results.Count > 0)
                {
                    if (filtered.Count > 0)
                    {
                        _favoriteService.ApplyFavorites(filtered);
                        SearchResults.AddRange(filtered);
                        UpdateNoResultsState();
                    }
                    _currentPage = nextPage;
                    _hasNextPage = page.HasNextPage;
                    ResultsDescription = string.IsNullOrWhiteSpace(_activeQuery)
                        ? $"Trending anime from AniList - page {_currentPage}"
                        : $"AniList results for \"{_activeQuery}\" - page {_currentPage}";
                }
                else
                {
                    _hasNextPage = false;
                }
            }
            catch (OperationCanceledException)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log("LoadMoreAnimeAsync cancelled.");
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"LoadMoreAnimeAsync failed: {ex.Message}", "WARNING");
            }
            finally
            {
                _isLoadingMore = false;
                if (IsCurrentSearchGeneration(generation))
                {
                    UpdateNoResultsState();
                }
            }
        }

        [RelayCommand]
        private void SelectAnime(MediaResult result)
        {
            if (result != null)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"SelectAnime details requested for: '{result.OfficialTitle}' (ID: {result.Id})");
                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new NavigateToDetailsMessage(result));
            }
        }

        [RelayCommand]
        private void ToggleFavorite(MediaResult result)
        {
            if (result == null)
            {
                return;
            }

            bool isFavorite = _favoriteService.Toggle(result);
            WeakReferenceMessenger.Default.Send(new ToastNotificationMessage(isFavorite
                ? $"Added to My List: {result.OfficialTitle}"
                : $"Removed from My List: {result.OfficialTitle}"));
        }

        private AnimeSearchFilters BuildFilters()
        {
            var selectedTags = TagFilters.Where(tag => tag.IsSelected).ToList();

            return new AnimeSearchFilters(
                Genres: selectedTags.Where(tag => tag.IsGenre).Select(tag => tag.Name).ToArray(),
                Tags: selectedTags.Where(tag => !tag.IsGenre).Select(tag => tag.Name).ToArray(),
                Status: SelectedStatus,
                Sort: SelectedSort,
                Audio: SelectedAudio);
        }

        private async Task<List<MediaResult>> ApplyAudioFilterAsync(List<MediaResult> results, CancellationToken token)
        {
            if (SelectedAudio.Equals("Sub", StringComparison.OrdinalIgnoreCase))
            {
                return results.Where(result => result.AvailableSubEpisodes > 0).ToList();
            }

            if (!SelectedAudio.Equals("Dub", StringComparison.OrdinalIgnoreCase))
            {
                return results;
            }

            var checkedResults = new List<MediaResult>();
            using var semaphore = new SemaphoreSlim(DubCheckConcurrencyLimit);
            var tasks = results.Select(async result =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var availability = await _dubAvailabilityService.CheckAsync(
                        result,
                        token,
                        bypassCache: false,
                        mode: DubAvailabilityCheckMode.Summary);
                    if (availability.Checked)
                    {
                        ApplyDubAvailability(result, availability);
                    }

                    return result.AvailableDubEpisodes > 0 || !availability.Checked
                        ? result
                        : null;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var checkedItems = await Task.WhenAll(tasks);
            checkedResults.AddRange(checkedItems.Where(result => result != null).Cast<MediaResult>());

            return checkedResults;
        }

        private void UpdateNoResultsState()
        {
            HasNoResults = !IsSearching && SearchResults.Count == 0;
        }

        public void RequestDubAvailabilityForVisibleResults(IReadOnlyCollection<MediaResult> results)
        {
            if (_isDisposed || results.Count == 0)
            {
                return;
            }

            if (_dubAnnotationCts == null || _dubAnnotationCts.IsCancellationRequested)
            {
                _dubAnnotationCts?.Dispose();
                _dubAnnotationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
            }

            var boundedVisibleResults = new List<MediaResult>();
            lock (_automaticDubAnnotationLock)
            {
                foreach (MediaResult result in results)
                {
                    if (result.DubAvailabilityChecked ||
                        _automaticDubAnnotationKeys.Count >= MaxAutomaticDubAnnotationsPerSearch)
                    {
                        continue;
                    }

                    string key = result.Id > 0
                        ? "anilist:" + result.Id
                        : result.OfficialTitle.Trim();
                    if (_automaticDubAnnotationKeys.Add(key))
                    {
                        boundedVisibleResults.Add(result);
                    }
                }
            }

            if (boundedVisibleResults.Count == 0)
            {
                return;
            }

            int generation = Volatile.Read(ref _searchGeneration);
            _ = AnnotateDubAvailabilityAsync(boundedVisibleResults, generation, _dubAnnotationCts.Token);
        }

        private async Task AnnotateDubAvailabilityAsync(IReadOnlyCollection<MediaResult> results, int generation, CancellationToken lifecycleToken)
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifecycleToken, timeoutCts.Token);
            var tasks = results
                .Where(result => !result.DubAvailabilityChecked)
                .Select(async result =>
                {
                    await _dubAnnotationSemaphore.WaitAsync(cts.Token);
                    try
                    {
                        if (!IsCurrentSearchGeneration(generation))
                        {
                            return;
                        }

                        var availability = await _dubAvailabilityService.CheckAsync(
                            result,
                            cts.Token,
                            bypassCache: false,
                            mode: DubAvailabilityCheckMode.Summary);
                        if (availability.Checked && IsCurrentSearchGeneration(generation))
                        {
                            ApplyDubAvailability(result, availability);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        UniversalMediaOS.Core.Helpers.AppLogger.Log($"Dub annotation failed for '{result.OfficialTitle}': {ex.Message}", "WARNING");
                    }
                    finally
                    {
                        _dubAnnotationSemaphore.Release();
                    }
                });

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static void ApplyDubAvailability(
            MediaResult media,
            DubAvailabilityResult availability)
        {
            if (!availability.Checked ||
                (media.IsDubAvailabilityVerified && !availability.Verified))
            {
                return;
            }

            media.AvailableDubEpisodes = Math.Max(0, availability.DubEpisodes);
            media.HighestContiguousDubEpisode = availability.Verified
                ? Math.Max(0, availability.HighestContiguousDubEpisode)
                : 0;
            media.DubbedEpisodeNumbers = availability.Verified
                ? availability.DubbedEpisodeNumbers
                : Array.Empty<decimal>();
            media.DubAvailabilityState = availability.Verified
                ? MediaDubAvailabilityState.Verified
                : MediaDubAvailabilityState.Summary;
            media.DubAvailabilityChecked = true;
        }

        private int NextSearchGeneration()
        {
            _dubAnnotationCts?.Cancel();
            _dubAnnotationCts?.Dispose();
            _dubAnnotationCts = null;
            lock (_automaticDubAnnotationLock)
            {
                _automaticDubAnnotationKeys.Clear();
            }
            return Interlocked.Increment(ref _searchGeneration);
        }

        private bool IsCurrentSearchGeneration(int generation)
        {
            return !_lifecycleCts.IsCancellationRequested &&
                   generation == Volatile.Read(ref _searchGeneration);
        }

        partial void OnIsSearchingChanged(bool value)
        {
            lock (_searchIdleLock)
            {
                if (value)
                {
                    if (_searchIdleSignal.Task.IsCompleted)
                    {
                        _searchIdleSignal = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    return;
                }

                _searchIdleSignal.TrySetResult(true);
            }
        }

        partial void OnSelectedStatusChanged(string value) => QueueFilterRefresh();
        partial void OnSelectedSortChanged(string value) => QueueFilterRefresh();
        partial void OnSelectedAudioChanged(string value) => QueueFilterRefresh();

        [RelayCommand]
        private void ToggleTag(AnimeTagFilterViewModel tag)
        {
            if (tag == null)
            {
                return;
            }

            tag.IsSelected = !tag.IsSelected;
            RefreshTagSummary();
            QueueFilterRefresh();
        }

        [RelayCommand]
        private void ClearTags()
        {
            bool changed = false;
            foreach (var tag in TagFilters.Where(tag => tag.IsSelected))
            {
                tag.IsSelected = false;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            RefreshTagSummary();
            QueueFilterRefresh();
        }

        private void RefreshTagSummary()
        {
            var selected = TagFilters.Where(tag => tag.IsSelected).Select(tag => tag.Name).ToList();
            TagSummary = selected.Count switch
            {
                0 => "All tags",
                <= 3 => string.Join(", ", selected),
                _ => $"{selected.Count} tags selected"
            };
        }

        private static IEnumerable<AnimeTagOption> CreateFallbackTagOptions()
        {
            string[] genres =
            [
                "Action", "Adventure", "Comedy", "Drama", "Fantasy", "Horror",
                "Mahou Shoujo", "Mecha", "Music", "Mystery", "Psychological",
                "Romance", "Sci-Fi", "Slice of Life", "Sports", "Supernatural", "Thriller"
            ];

            foreach (string genre in genres)
            {
                yield return new AnimeTagOption(genre, "Genre", false, true);
            }

            string[] tags =
            [
                "Aliens", "Anti-Hero", "Coming of Age", "Demons", "Female Protagonist",
                "Found Family", "Isekai", "Magic", "Male Protagonist", "Military",
                "Reincarnation", "School", "Shounen", "Time Manipulation", "Urban Fantasy"
            ];

            foreach (string tag in tags)
            {
                yield return new AnimeTagOption(tag, "Tag", false, false);
            }
        }

        private void QueueFilterRefresh()
        {
            _filterRefreshCts?.Cancel();
            _filterRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
            var token = _filterRefreshCts.Token;

            _ = RefreshFiltersAfterDelayAsync(token);
        }

        private async Task RefreshFiltersAfterDelayAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(250, token);
                await WaitForSearchIdleAsync(token);
                token.ThrowIfCancellationRequested();
                await SearchAsync(token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Filter refresh failed: {ex.Message}", "WARNING");
            }
        }

        private async Task WaitForSearchIdleAsync(CancellationToken token)
        {
            while (true)
            {
                Task idleTask;
                lock (_searchIdleLock)
                {
                    if (!IsSearching)
                    {
                        return;
                    }

                    idleTask = _searchIdleSignal.Task;
                }

                await idleTask.WaitAsync(token);
            }
        }

        private static TaskCompletionSource<bool> CreateCompletedSearchIdleSignal()
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.TrySetResult(true);
            return signal;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            CancelActiveWork();
            SearchResults.CollectionChanged -= SearchResults_CollectionChanged;
            _filterRefreshCts?.Dispose();
            _dubAnnotationCts?.Dispose();
            _lifecycleCts.Dispose();
        }
    }

    public sealed record SearchResultRow(IReadOnlyList<MediaResult> Results);

    public partial class AnimeTagFilterViewModel : ObservableObject
    {
        public AnimeTagFilterViewModel(AnimeTagOption option)
        {
            Name = option.Name;
            Category = option.Category;
            IsAdult = option.IsAdult;
            IsGenre = option.IsGenre;
        }

        public string Name { get; }
        public string Category { get; }
        public bool IsAdult { get; }
        public bool IsGenre { get; }
        public string KindLabel => IsGenre ? "Genre" : Category;
        public string ToolTipText => IsGenre
            ? $"Genre: {Name}"
            : IsAdult
                ? $"{Category}: {Name} (adult tag; hidden while NSFW content is off)"
                : $"{Category}: {Name}";

        [ObservableProperty]
        private bool _isSelected;
    }
}
