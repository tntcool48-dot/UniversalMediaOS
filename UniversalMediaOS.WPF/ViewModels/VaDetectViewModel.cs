using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.Helpers;

namespace UniversalMediaOS.WPF.ViewModels
{
    public sealed partial class VaTargetCastItemViewModel : ObservableObject
    {
        public string CharacterName { get; init; } = string.Empty;
        public string VoiceActorName { get; init; } = string.Empty;
        public string RoleType { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
    }

    public sealed partial class VaKnownRoleViewModel : ObservableObject
    {
        public string AnimeTitle { get; init; } = string.Empty;
        public string CharacterName { get; init; } = string.Empty;
        public string ListStatus { get; init; } = string.Empty;
        public int UserScore { get; init; }
        public bool IsTopRated { get; init; }
        public string ScoreText => UserScore > 0 ? $"Score {UserScore}" : "Unrated";
        public string AccentText => IsTopRated ? "Top rated" : ListStatus;
    }

    public sealed partial class VaMatchViewModel : ObservableObject
    {
        public string VoiceActorName { get; init; } = string.Empty;
        public string TargetCharacterName { get; init; } = string.Empty;
        public string RoleType { get; init; } = string.Empty;
        public string Group { get; init; } = string.Empty;
        public ObservableRangeCollection<VaKnownRoleViewModel> KnownFrom { get; } = new();
        public string KnownSummary => $"{KnownFrom.Count} known role{(KnownFrom.Count == 1 ? "" : "s")}";
    }

    public partial class VaDetectViewModel : ObservableObject, IDisposable
    {
        private readonly DomainHotSwapper _config;
        private readonly FuzzyShieldSearch _searchService;
        private readonly MalLibrarySyncService _librarySyncService;
        private readonly VoiceActorIndexService _indexService;
        private readonly BackgroundVoiceCastPrefetchService _prefetchService;
        private readonly CancellationTokenSource _lifecycleCts = new();
        private CancellationTokenSource? _malSyncCts;
        private bool _malSyncCancelledByUser;
        private bool _isDisposed;
        internal TimeSpan MalSyncTimeout { get; init; } = TimeSpan.FromMinutes(2);
        internal TimeSpan CastSearchTimeout { get; init; } = TimeSpan.FromSeconds(45);
        private CancellationTokenSource? _castSearchCts;
        private long _castSearchGeneration;
        private bool _castSearchCancelledByUser;
        private MediaResult? _seedMedia;

        [ObservableProperty] private string _searchQuery = string.Empty;
        [ObservableProperty] private string _manualUrl = string.Empty;
        [ObservableProperty] private string _publicFallbackUsername = string.Empty;
        [ObservableProperty] private string _selectedMode = "Dub";
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SyncLibraryCommand), nameof(SyncPublicFallbackCommand), nameof(SearchCommand), nameof(CancelMalSyncCommand), nameof(CancelCastSearchCommand))]
        private bool _isBusy;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartPrefetchCommand), nameof(StopPrefetchCommand))]
        private bool _isPrefetching;
        [ObservableProperty] private string _prefetchStatusText = string.Empty;
        [ObservableProperty] private string _statusText = "Sync your MAL library or search an anime to begin.";
        [ObservableProperty] private string _warningText = string.Empty;
        [ObservableProperty] private string _targetTitle = "No target selected";
        [ObservableProperty] private string _sourceText = "Source: none";
        [ObservableProperty] private string _librarySummary = "Library not synced in this session.";

        public ObservableRangeCollection<VaTargetCastItemViewModel> TargetCast { get; } = new();
        public ObservableRangeCollection<VaMatchViewModel> Matches { get; } = new();
        public bool IsSyncing => _malSyncCts != null;
        public bool IsSearching => _castSearchCts != null;
        private bool CanStartOperation() => !_isDisposed && !IsBusy;
        private bool CanCancelMalSync() => _malSyncCts is { IsCancellationRequested: false };
        private bool CanCancelCastSearch() => _castSearchCts is { IsCancellationRequested: false };
        private bool CanStartPrefetch() => !_isDisposed && !IsPrefetching;
        private bool CanStopPrefetch() => !_isDisposed && IsPrefetching;

        [RelayCommand(CanExecute = nameof(CanCancelCastSearch))]
        private void CancelCastSearch()
        {
            if (_castSearchCts == null) return;
            _castSearchCancelledByUser = true;
            _castSearchCts.Cancel();
            CancelCastSearchCommand.NotifyCanExecuteChanged();
        }

        private void InvalidateCastSearch()
        {
            _castSearchGeneration++;
            _castSearchCts?.Cancel();
            if (_castSearchCts != null) StatusText = "Selection changed. Use Find VAs to search again.";
            CancelCastSearchCommand.NotifyCanExecuteChanged();
            TargetCast.Clear();
            Matches.Clear();
            SourceText = "Source: none";
            WarningText = string.Empty;
        }

        partial void OnSearchQueryChanged(string value) => InvalidateCastSearch();
        partial void OnManualUrlChanged(string value) => InvalidateCastSearch();

        [RelayCommand(CanExecute = nameof(CanCancelMalSync))]
        private void CancelMalSync()
        {
            if (_malSyncCts == null) return;
            _malSyncCancelledByUser = true;
            _malSyncCts?.Cancel();
            CancelMalSyncCommand.NotifyCanExecuteChanged();
        }

        private CancellationTokenSource BeginMalSync()
        {
            var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
            operation.CancelAfter(MalSyncTimeout);
            _malSyncCts = operation;
            _malSyncCancelledByUser = false;
            IsBusy = true;
            WarningText = string.Empty;
            OnPropertyChanged(nameof(IsSyncing));
            return operation;
        }

        private void EndMalSync(CancellationTokenSource operation)
        {
            if (!ReferenceEquals(_malSyncCts, operation)) return;
            _malSyncCts = null;
            IsBusy = false;
            OnPropertyChanged(nameof(IsSyncing));
        }

        private void MalSyncFailed(string action, string retryAction, Exception error)
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log($"{action} failed: {error.GetType().Name}", "WARNING");
            StatusText = $"{action} failed.";
            WarningText = $"Your saved library was kept. Use {retryAction} to retry.";
            if (action == "MAL sync" && error is System.Net.Http.HttpRequestException
                { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden })
                WarningText = "Your saved library was kept. Reconnect MyAnimeList in Settings, then use Sync OAuth to retry.";
        }

        public bool IsSubSelected
        {
            get => SelectedMode.Equals("Sub", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (value)
                {
                    SelectedMode = "Sub";
                }
            }
        }

        public bool IsDubSelected
        {
            get => SelectedMode.Equals("Dub", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (value)
                {
                    SelectedMode = "Dub";
                }
            }
        }

        public VaDetectViewModel(
            DomainHotSwapper config,
            FuzzyShieldSearch searchService,
            MalLibrarySyncService librarySyncService,
            VoiceActorIndexService indexService,
            BackgroundVoiceCastPrefetchService prefetchService)
        {
            _config = config;
            _searchService = searchService;
            _librarySyncService = librarySyncService;
            _indexService = indexService;
            _prefetchService = prefetchService;
            PublicFallbackUsername = _config.GetSetting("VaDetectPublicMalFallbackUsername");
            SelectedMode = _config.GetSetting("VaDetectDefaultMode").Equals("Sub", StringComparison.OrdinalIgnoreCase)
                ? "Sub"
                : "Dub";
            _prefetchService.ProgressChanged += PrefetchService_ProgressChanged;
            _ = RefreshLibrarySummaryAsync();
        }

        public void LoadMedia(MediaResult media)
        {
            InvalidateCastSearch();
            _seedMedia = media;
            SearchQuery = media.OfficialTitle;
            TargetTitle = media.OfficialTitle;
        }

        partial void OnSelectedModeChanged(string value)
        {
            if (!value.Equals("Sub", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("Dub", StringComparison.OrdinalIgnoreCase))
            {
                SelectedMode = "Dub";
                return;
            }

            _config.SetSetting("VaDetectDefaultMode", value.Equals("Sub", StringComparison.OrdinalIgnoreCase) ? "Sub" : "Dub");
            InvalidateCastSearch();
            OnPropertyChanged(nameof(IsSubSelected));
            OnPropertyChanged(nameof(IsDubSelected));
            StatusText = $"VA Detect mode: {SelectedMode}. Search again to refresh matches.";
        }

        [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanStartOperation))]
        private async Task SyncLibraryAsync()
        {
            if (!CanStartOperation()) return;
            using var operation = BeginMalSync();
            StatusText = "Syncing MAL library with OAuth...";
            try
            {
                var result = await _librarySyncService.SyncOAuthAsync(operation.Token);
                WarningText = result.Warning;
                StatusText = string.IsNullOrWhiteSpace(result.Warning)
                    ? $"OAuth sync complete: {result.ImportedCount} anime imported."
                    : result.Warning;
                await RefreshLibrarySummaryAsync();
            }
            catch (OperationCanceledException)
            {
                StatusText = _malSyncCancelledByUser || _lifecycleCts.IsCancellationRequested
                    ? "MAL sync cancelled." : "MAL sync timed out.";
                WarningText = "Your saved library was kept. Use Sync OAuth to retry.";
            }
            catch (Exception ex)
            {
                MalSyncFailed("MAL sync", "Sync OAuth", ex);
            }
            finally
            {
                EndMalSync(operation);
            }
        }

        [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanStartOperation))]
        private async Task SyncPublicFallbackAsync()
        {
            if (!CanStartOperation()) return;
            using var operation = BeginMalSync();
            StatusText = "Syncing MAL public list fallback...";
            try
            {
                var result = await _librarySyncService.SyncPublicFallbackAsync(PublicFallbackUsername, operation.Token);
                WarningText = result.Warning;
                StatusText = result.ImportedCount > 0
                    ? $"Public fallback sync complete: {result.ImportedCount} anime imported."
                    : result.Warning;
                await RefreshLibrarySummaryAsync();
            }
            catch (OperationCanceledException)
            {
                StatusText = _malSyncCancelledByUser || _lifecycleCts.IsCancellationRequested
                    ? "Public fallback sync cancelled." : "Public fallback sync timed out.";
                WarningText = "Your saved library was kept. Use Public fallback to retry.";
            }
            catch (Exception ex)
            {
                MalSyncFailed("Public fallback sync", "Public fallback", ex);
            }
            finally
            {
                EndMalSync(operation);
            }
        }

        [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanStartOperation))]
        private async Task SearchAsync()
        {
            if (!CanStartOperation()) return;
            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                StatusText = "Enter an anime title first.";
                return;
            }

            IsBusy = true;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
            operation.CancelAfter(CastSearchTimeout);
            _castSearchCts = operation;
            _castSearchCancelledByUser = false;
            long generation = _castSearchGeneration;
            string query = SearchQuery.Trim();
            string manual = ManualUrl.Trim();
            var mode = ResolveMode();
            bool IsCurrent() => !_isDisposed && generation == _castSearchGeneration && ReferenceEquals(_castSearchCts, operation);
            OnPropertyChanged(nameof(IsSearching));
            CancelCastSearchCommand.NotifyCanExecuteChanged();
            WarningText = string.Empty;
            StatusText = $"Searching {mode} VAs for {query}...";

            try
            {
                MediaResult? media = ResolveSeedMedia();
                if (media == null)
                {
                    var page = await _searchService.SearchAnimePageAsync(query, 1, 5, AnimeSearchFilters.Default, operation.Token);
                    media = page.Results.FirstOrDefault();
                }

                operation.Token.ThrowIfCancellationRequested();
                if (!IsCurrent()) return;

                if (media == null)
                {
                    StatusText = "No anime found for that search.";
                    return;
                }

                TargetTitle = media.OfficialTitle;
                var result = await _indexService.FindMatchesAsync(
                    VoiceCastMedia.FromMediaResult(media),
                    mode,
                    manual,
                    operation.Token);

                operation.Token.ThrowIfCancellationRequested();
                if (!IsCurrent()) return;

                TargetCast.ReplaceRange(result.TargetCast.Select(item => new VaTargetCastItemViewModel
                {
                    CharacterName = item.CharacterName,
                    VoiceActorName = item.VoiceActorName,
                    RoleType = item.RoleType,
                    Source = item.Source
                }));

                Matches.ReplaceRange(result.Matches.Select(match =>
                {
                    var vm = new VaMatchViewModel
                    {
                        VoiceActorName = match.VoiceActorName,
                        TargetCharacterName = match.TargetCharacterName,
                        RoleType = match.RoleType,
                        Group = match.Group
                    };
                    vm.KnownFrom.ReplaceRange(match.KnownFrom.Select(role => new VaKnownRoleViewModel
                    {
                        AnimeTitle = role.AnimeTitle,
                        CharacterName = role.CharacterName,
                        ListStatus = role.ListStatus,
                        UserScore = role.UserScore,
                        IsTopRated = role.IsTopRated
                    }));
                    return vm;
                }));

                SourceText = $"Source: {result.Source}";
                WarningText = result.Warning;
                StatusText = result.NotFound
                    ? $"No {mode} cast data found for {media.OfficialTitle}."
                    : $"Found {TargetCast.Count} {mode} cast roles and {Matches.Count} familiar VA matches.";
            }
            catch (OperationCanceledException)
            {
                if (IsCurrent())
                {
                    StatusText = _castSearchCancelledByUser || _lifecycleCts.IsCancellationRequested
                        ? "VA search cancelled." : "VA search timed out.";
                    WarningText = "Your saved cast was kept. Use Find VAs to retry.";
                }
            }
            catch (Exception ex)
            {
                if (IsCurrent())
                {
                    UniversalMediaOS.Core.Helpers.AppLogger.Log($"VA search failed: {ex.GetType().Name}", "WARNING");
                    StatusText = "VA search failed.";
                    WarningText = "Your saved cast was kept. Use Find VAs to retry.";
                }
            }
            finally
            {
                if (ReferenceEquals(_castSearchCts, operation))
                {
                    _castSearchCts = null;
                    IsBusy = false;
                    OnPropertyChanged(nameof(IsSearching));
                    CancelCastSearchCommand.NotifyCanExecuteChanged();
                }
            }
        }

        [RelayCommand(CanExecute = nameof(CanStartPrefetch))]
        private async Task StartPrefetchAsync()
        {
            if (!CanStartPrefetch() || _prefetchService.IsRunning)
            {
                return;
            }

            IsPrefetching = true;
            await _prefetchService.StartAsync(ResolveMode());
        }

        [RelayCommand(CanExecute = nameof(CanStopPrefetch))]
        private void StopPrefetch()
        {
            _prefetchService.Stop();
        }

        private MediaResult? ResolveSeedMedia()
        {
            if (_seedMedia == null)
            {
                return null;
            }

            return SearchQuery.Trim().Equals(_seedMedia.OfficialTitle, StringComparison.OrdinalIgnoreCase) ||
                   SearchQuery.Trim().Equals(_seedMedia.EnglishTitle, StringComparison.OrdinalIgnoreCase) ||
                   SearchQuery.Trim().Equals(_seedMedia.RomajiTitle, StringComparison.OrdinalIgnoreCase)
                ? _seedMedia
                : null;
        }

        private VoiceLanguageMode ResolveMode()
        {
            return SelectedMode.Equals("Sub", StringComparison.OrdinalIgnoreCase)
                ? VoiceLanguageMode.Sub
                : VoiceLanguageMode.Dub;
        }

        private async Task RefreshLibrarySummaryAsync()
        {
            try
            {
                var library = await _librarySyncService.GetLibraryAsync(_lifecycleCts.Token);
                LibrarySummary = library.Count == 0
                    ? "No MAL library entries cached yet."
                    : $"{library.Count} MAL library entries cached.";
            }
            catch
            {
                LibrarySummary = "Library cache unavailable.";
            }
        }

        private void PrefetchService_ProgressChanged(object? sender, VoiceCastPrefetchProgressEventArgs e)
        {
            void Apply()
            {
                if (_isDisposed) return;
                IsPrefetching = e.IsRunning;
                PrefetchStatusText = e.Message;
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.InvokeAsync(Apply);
            }
            else
            {
                Apply();
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _castSearchGeneration++;
            _castSearchCts?.Cancel();
            _prefetchService.ProgressChanged -= PrefetchService_ProgressChanged;
            _lifecycleCts.Cancel();
            _lifecycleCts.Dispose();
        }
    }
}
