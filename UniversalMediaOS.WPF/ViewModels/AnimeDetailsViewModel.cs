using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Routing;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Tracking;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class EpisodeOptionViewModel : ObservableObject
    {
        public EpisodeOptionViewModel(string number, bool isAvailable = true)
        {
            Number = number;
            IsAvailable = isAvailable;
        }

        public string Number { get; }

        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        private bool _isAvailable = true;

        public string AvailabilityText => IsAvailable
            ? $"Select episode {Number}"
            : $"Episode {Number} is not available yet";

        partial void OnIsAvailableChanged(bool value)
        {
            OnPropertyChanged(nameof(AvailabilityText));
        }
    }

    public class ScraperActivityItem
    {
        public string State { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
    }

    public partial class AnimeDetailsViewModel : ObservableObject, IDisposable
    {
        private const int EpisodePageSize = 24;

        private readonly DomainHotSwapper _config;
        private readonly TripleNetHandoff _routingEngine;
        private readonly DownloadQueueService _downloadQueue;
        private readonly TemporaryEpisodeWatchService _temporaryEpisodeWatch;
        private readonly Helpers.IDialogService _dialogService;
        private readonly DubAvailabilityService _dubAvailabilityService;
        private readonly MalOAuthService _malOAuthService;
        private readonly CancellationTokenSource _lifecycleCts = new();
        private int _episodePageStart = 1;
        private CancellationTokenSource? _routingCts;
        private CancellationTokenSource? _temporaryWatchCts;
        private CancellationTokenSource? _dubLookupCts;
        private int _dubLookupGeneration;
        private DownloadQueueJob? _downloadJob;
        private MediaResult? _observedMedia;
        private bool _isDisposed;

        [ObservableProperty]
        private MediaResult? _media;

        [ObservableProperty]
        private string _selectedEpisode = "1";

        [ObservableProperty]
        private string _selectedAudioMode = "Sub";

        [ObservableProperty]
        private bool _isRouting;

        [ObservableProperty]
        private bool _isTemporaryWatchDownloading;

        [ObservableProperty]
        private bool _isDownloading;

        [ObservableProperty]
        private string _downloadButtonText = "Season Download";

        [ObservableProperty]
        private string _malProgressText = "MAL status not loaded";

        [ObservableProperty]
        private bool _isMalStatusLoading;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CancelDubCheckCommand))]
        private bool _isDubChecking;

        [ObservableProperty]
        private string _dubCheckStatus = string.Empty;

        [ObservableProperty]
        private string _episodePageText = "Episodes";

        [ObservableProperty]
        private bool _canMoveEpisodePagePrevious;

        [ObservableProperty]
        private bool _canMoveEpisodePageNext;

        [ObservableProperty]
        private string _scraperStatusHeadline = "Ready";

        [ObservableProperty]
        private string _scraperStatusDetail = "Choose an episode and start playback to see resolver progress.";

        public ObservableCollection<EpisodeOptionViewModel> EpisodeOptions { get; } = new();
        public ObservableCollection<ScraperActivityItem> ScraperActivityItems { get; } = new();

        public string EpisodeProgressText
        {
            get
            {
                string episode = string.IsNullOrWhiteSpace(SelectedEpisode) ? "1" : SelectedEpisode.Trim();
                int totalEpisodes = Media?.TotalEpisodes ?? 0;
                return totalEpisodes > 0 ? $"Episode {episode} of {totalEpisodes}" : $"Episode {episode}";
            }
        }

        public bool IsSubSelected
        {
            get => SelectedAudioMode.Equals("Sub", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (value)
                {
                    SelectedAudioMode = "Sub";
                }
            }
        }

        public bool IsDubSelected
        {
            get => SelectedAudioMode.Equals("Dub", StringComparison.OrdinalIgnoreCase);
            set
            {
                if (value)
                {
                    SelectedAudioMode = "Dub";
                }
            }
        }

        public string AudioAvailabilityText
        {
            get
            {
                int sub = Media?.AvailableSubEpisodes ?? 0;
                int dub = Media?.AvailableDubEpisodes ?? 0;
                int total = Math.Max(Media?.TotalEpisodes ?? 0, sub);
                if (SelectedAudioMode.Equals("Dub", StringComparison.OrdinalIgnoreCase))
                {
                    if (Media?.IsDubAvailabilityVerified == true)
                    {
                        if (dub <= 0)
                        {
                            return "Dub mode - the provider episode list currently confirms no dubbed episodes.";
                        }

                        int through = Media.HighestContiguousDubEpisode;
                        return through == dub
                            ? $"Dub mode - episodes 1-{through} verified from per-episode audio flags."
                            : $"Dub mode - {dub} dubbed episode(s) verified; continuous playback is safe through episode {through}.";
                    }

                    if (Media?.DubAvailabilityState == MediaDubAvailabilityState.Summary && dub > 0)
                    {
                        return $"Dub mode - provider summary reports about {dub} dubbed episodes; opening Details verifies each episode.";
                    }

                    return "Dub mode - availability is unknown; playback can still search dub and dual-audio sources.";
                }

                return sub > 0
                    ? $"Sub mode - {sub} released episodes listed{(total > sub ? $" of {total}" : string.Empty)}."
                    : "Sub mode - no released episodes found yet.";
            }
        }

        public bool CanWatchSelectedEpisode
        {
            get
            {
                if (Media == null || IsRouting || IsTemporaryWatchDownloading)
                {
                    return false;
                }

                return int.TryParse(SelectedEpisode, out int episode) &&
                       episode > 0 &&
                       episode <= AvailableEpisodeCount;
            }
        }

        public bool CanDownloadSelectedEpisode =>
            Media != null && !IsRouting && !IsTemporaryWatchDownloading &&
            int.TryParse(SelectedEpisode, out int episode) && episode > 0 &&
            episode <= ResolveAvailableEpisodeCount(Media, "Sub");

        public AnimeDetailsViewModel(
            DomainHotSwapper config,
            TripleNetHandoff routingEngine,
            DownloadQueueService downloadQueue,
            TemporaryEpisodeWatchService temporaryEpisodeWatch,
            Helpers.IDialogService dialogService,
            DubAvailabilityService dubAvailabilityService,
            MalOAuthService malOAuthService)
        {
            _config = config;
            _routingEngine = routingEngine;
            _downloadQueue = downloadQueue;
            _temporaryEpisodeWatch = temporaryEpisodeWatch;
            _dialogService = dialogService;
            _dubAvailabilityService = dubAvailabilityService;
            _malOAuthService = malOAuthService;

            string defaultAudio = _config.GetSetting("DefaultAudioPref");
            SelectedAudioMode = defaultAudio.Equals("Dub", StringComparison.OrdinalIgnoreCase) ? "Dub" : "Sub";
        }

        partial void OnMediaChanged(MediaResult? value)
        {
            _temporaryWatchCts?.Cancel();
            int generation = Interlocked.Increment(ref _dubLookupGeneration);
            _dubLookupCts?.Cancel();
            _dubLookupCts = null;
            IsDubChecking = false;
            DubCheckStatus = string.Empty;

            if (_observedMedia != null)
            {
                _observedMedia.PropertyChanged -= Media_PropertyChanged;
            }

            _observedMedia = value;
            if (_observedMedia != null)
            {
                _observedMedia.PropertyChanged += Media_PropertyChanged;
            }

            if (value == null)
            {
                MalProgressText = "MAL status not loaded";
                EpisodeOptions.Clear();
                OnPropertyChanged(nameof(EpisodeProgressText));
                OnPropertyChanged(nameof(CanWatchSelectedEpisode));
                OnPropertyChanged(nameof(CanDownloadSelectedEpisode));
                WatchNowCommand.NotifyCanExecuteChanged();
                WatchViaDownloadCommand.NotifyCanExecuteChanged();
                return;
            }

            if (!string.IsNullOrWhiteSpace(value.TargetEpisode) && value.TargetEpisode != SelectedEpisode)
            {
                SelectedEpisode = value.TargetEpisode;
            }

            OnPropertyChanged(nameof(EpisodeProgressText));
            OnPropertyChanged(nameof(AudioAvailabilityText));
            OnPropertyChanged(nameof(CanWatchSelectedEpisode));
            OnPropertyChanged(nameof(CanDownloadSelectedEpisode));
            WatchNowCommand.NotifyCanExecuteChanged();
            WatchViaDownloadCommand.NotifyCanExecuteChanged();
            ConfigureEpisodeButtons();
            ResetScraperActivity();
            _ = LoadMalStatusAsync(value);
            _dubLookupCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
            _ = ResolveDubAvailabilityAsync(value, _dubLookupCts, generation);
        }

        private void Media_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MediaResult.AvailableDubEpisodes) or
                nameof(MediaResult.DubAvailabilityChecked) or
                nameof(MediaResult.DubAvailabilityState) or
                nameof(MediaResult.HighestContiguousDubEpisode) or
                nameof(MediaResult.DubbedEpisodeNumbers) or
                nameof(MediaResult.AvailableSubEpisodes))
            {
                void Refresh()
                {
                    OnPropertyChanged(nameof(AudioAvailabilityText));
                    OnPropertyChanged(nameof(CanWatchSelectedEpisode));
                    OnPropertyChanged(nameof(CanDownloadSelectedEpisode));
                    WatchNowCommand.NotifyCanExecuteChanged();
                    WatchViaDownloadCommand.NotifyCanExecuteChanged();
                    ConfigureEpisodeButtons();
                }

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.InvokeAsync(Refresh);
                }
                else
                {
                    Refresh();
                }
            }
        }

        partial void OnSelectedEpisodeChanged(string value)
        {
            _temporaryWatchCts?.Cancel();
            OnPropertyChanged(nameof(EpisodeProgressText));
            OnPropertyChanged(nameof(CanWatchSelectedEpisode));
            OnPropertyChanged(nameof(CanDownloadSelectedEpisode));
            WatchNowCommand.NotifyCanExecuteChanged();
            WatchViaDownloadCommand.NotifyCanExecuteChanged();
            RefreshSelectedEpisodeButton();
        }

        partial void OnSelectedAudioModeChanged(string value)
        {
            _temporaryWatchCts?.Cancel();
            OnPropertyChanged(nameof(IsSubSelected));
            OnPropertyChanged(nameof(IsDubSelected));
            OnPropertyChanged(nameof(AudioAvailabilityText));
            EnsureSelectedEpisodeIsAvailable();
            OnPropertyChanged(nameof(CanWatchSelectedEpisode));
            OnPropertyChanged(nameof(CanDownloadSelectedEpisode));
            WatchNowCommand.NotifyCanExecuteChanged();
            WatchViaDownloadCommand.NotifyCanExecuteChanged();
            ConfigureEpisodeButtons();
        }

        partial void OnIsRoutingChanged(bool value)
        {
            OnPropertyChanged(nameof(CanWatchSelectedEpisode));
            OnPropertyChanged(nameof(CanDownloadSelectedEpisode));
            WatchNowCommand.NotifyCanExecuteChanged();
            WatchViaDownloadCommand.NotifyCanExecuteChanged();
        }

        partial void OnIsTemporaryWatchDownloadingChanged(bool value)
        {
            OnPropertyChanged(nameof(CanWatchSelectedEpisode));
            OnPropertyChanged(nameof(CanDownloadSelectedEpisode));
            WatchNowCommand.NotifyCanExecuteChanged();
            WatchViaDownloadCommand.NotifyCanExecuteChanged();
        }

        private int AvailableEpisodeCount
            => ResolveAvailableEpisodeCount(Media, SelectedAudioMode);

        internal static int ResolveAvailableEpisodeCount(MediaResult? media, string selectedAudioMode)
        {
            int releasedSubEpisodes = Math.Max(0, media?.AvailableSubEpisodes ?? 0);
            if (selectedAudioMode.Equals("Dub", StringComparison.OrdinalIgnoreCase))
            {
                if (media?.IsDubAvailabilityVerified == true)
                {
                    // Exact provider episode flags can contain specials or gaps.
                    // Navigation is deliberately bounded to the verified contiguous
                    // run instead of treating a raw count as an episode ceiling.
                    return Math.Max(0, media.HighestContiguousDubEpisode);
                }

                // Unknown/summary data is advisory. Keep manual playback search
                // available because another source may still carry a dub.
                return releasedSubEpisodes;
            }

            return releasedSubEpisodes;
        }

        private int TotalEpisodeCount => Math.Max(Media?.TotalEpisodes ?? 0, AvailableEpisodeCount);

        private void ConfigureEpisodeButtons()
        {
            _episodePageStart = 1;
            if (int.TryParse(SelectedEpisode, out int selectedEpisode) && selectedEpisode > EpisodePageSize)
            {
                _episodePageStart = ((selectedEpisode - 1) / EpisodePageSize) * EpisodePageSize + 1;
            }

            RebuildEpisodeButtons();
        }

        private void RebuildEpisodeButtons()
        {
            int total = TotalEpisodeCount;
            if (total <= 0)
            {
                EpisodeOptions.Clear();
                EpisodePageText = SelectedAudioMode.Equals("Dub", StringComparison.OrdinalIgnoreCase)
                    ? "No verified dubbed episodes yet"
                    : "No released episodes yet";
                CanMoveEpisodePagePrevious = false;
                CanMoveEpisodePageNext = false;
                OnPropertyChanged(nameof(CanWatchSelectedEpisode));
                OnPropertyChanged(nameof(CanDownloadSelectedEpisode));
                WatchNowCommand.NotifyCanExecuteChanged();
                WatchViaDownloadCommand.NotifyCanExecuteChanged();
                return;
            }

            int maxPageStart = ((total - 1) / EpisodePageSize) * EpisodePageSize + 1;
            _episodePageStart = Math.Clamp(_episodePageStart, 1, maxPageStart);
            int pageEnd = Math.Min(total, _episodePageStart + EpisodePageSize - 1);
            int available = AvailableEpisodeCount;

            EpisodeOptions.Clear();
            for (int episode = _episodePageStart; episode <= pageEnd; episode++)
            {
                EpisodeOptions.Add(new EpisodeOptionViewModel(episode.ToString(), episode <= available)
                {
                    IsSelected = episode.ToString() == SelectedEpisode
                });
            }

            EpisodePageText = total > EpisodePageSize
                ? $"Episodes {_episodePageStart}-{pageEnd} of {total}"
                : $"Episodes 1-{total}";
            CanMoveEpisodePagePrevious = _episodePageStart > 1;
            CanMoveEpisodePageNext = pageEnd < total;
            OnPropertyChanged(nameof(CanWatchSelectedEpisode));
            OnPropertyChanged(nameof(CanDownloadSelectedEpisode));
            WatchNowCommand.NotifyCanExecuteChanged();
            WatchViaDownloadCommand.NotifyCanExecuteChanged();
        }

        private void RefreshSelectedEpisodeButton()
        {
            foreach (var option in EpisodeOptions)
            {
                option.IsSelected = option.Number == SelectedEpisode;
            }
        }

        private void EnsureSelectedEpisodeIsAvailable()
        {
            if (!int.TryParse(SelectedEpisode, out int selected) || selected < 1)
            {
                SelectedEpisode = AvailableEpisodeCount > 0 ? "1" : "0";
                return;
            }

            if (AvailableEpisodeCount > 0 && selected > AvailableEpisodeCount)
            {
                SelectedEpisode = AvailableEpisodeCount.ToString();
            }
        }

        private void ResetScraperActivity()
        {
            ScraperActivityItems.Clear();
            ScraperStatusHeadline = "Ready";
            ScraperStatusDetail = "Choose an episode and start playback to see resolver progress.";
        }

        private void AddScraperActivity(string message, string state = "Working")
        {
            void AddOnUiThread()
            {
                ScraperStatusHeadline = state;
                ScraperStatusDetail = message;
                ScraperActivityItems.Add(new ScraperActivityItem
                {
                    State = state,
                    Message = message,
                    Timestamp = DateTime.Now.ToString("HH:mm:ss")
                });

                while (ScraperActivityItems.Count > 8)
                {
                    ScraperActivityItems.RemoveAt(0);
                }
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.InvokeAsync(AddOnUiThread);
            }
            else
            {
                AddOnUiThread();
            }
        }

        public void CancelActiveWork()
        {
            if (!_lifecycleCts.IsCancellationRequested)
            {
                _lifecycleCts.Cancel();
            }

            if (_dubLookupCts is { IsCancellationRequested: false })
            {
                _dubLookupCts.Cancel();
            }

            if (_routingCts is { IsCancellationRequested: false })
            {
                _routingCts.Cancel();
            }

            if (_temporaryWatchCts is { IsCancellationRequested: false })
            {
                _temporaryWatchCts.Cancel();
            }

            AddScraperActivity("Tab closed; active scraping was cancelled. Queued downloads continue in Downloads.", "Stopped");
        }

        [RelayCommand]
        private void SelectEpisode(EpisodeOptionViewModel? episode)
        {
            if (episode != null && episode.IsAvailable)
            {
                SelectedEpisode = episode.Number;
            }
        }

        [RelayCommand]
        private void PreviousEpisodePage()
        {
            if (!CanMoveEpisodePagePrevious)
            {
                return;
            }

            _episodePageStart -= EpisodePageSize;
            RebuildEpisodeButtons();
        }

        [RelayCommand]
        private void NextEpisodePage()
        {
            if (!CanMoveEpisodePageNext)
            {
                return;
            }

            _episodePageStart += EpisodePageSize;
            RebuildEpisodeButtons();
        }

        private async Task LoadMalStatusAsync(MediaResult media)
        {
            if (media.IdMal <= 0)
            {
                MalProgressText = "MAL: no linked anime id";
                return;
            }

            string token = await _malOAuthService.GetValidAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
            {
                MalProgressText = media.TotalEpisodes > 0
                    ? $"MAL linked. Connect MyAnimeList for list progress. Total: {media.TotalEpisodes}"
                    : "MAL linked. Connect MyAnimeList for list progress.";
                return;
            }

            IsMalStatusLoading = true;
            int expectedMalId = media.IdMal;

            try
            {
                var mal = new MalRestApi(_malOAuthService, _config);
                var status = await mal.GetAnimeStatusAsync(expectedMalId);
                if (Media?.IdMal != expectedMalId)
                {
                    return;
                }

                if (status == null)
                {
                    MalProgressText = "MAL status unavailable";
                    return;
                }

                if (status.TotalEpisodes > 0 && Media != null)
                {
                    Media.TotalEpisodes = status.TotalEpisodes;
                    OnPropertyChanged(nameof(EpisodeProgressText));
                    OnPropertyChanged(nameof(AudioAvailabilityText));
                    OnPropertyChanged(nameof(CanWatchSelectedEpisode));
                    ConfigureEpisodeButtons();
                }

                string statusText = string.IsNullOrWhiteSpace(status.Status)
                    ? "not in list"
                    : status.Status.Replace('_', ' ');
                string rewatchText = status.IsRewatching
                    ? $" - rewatching ({status.NumTimesRewatched} completed)"
                    : string.Empty;
                string total = status.TotalEpisodes > 0 ? status.TotalEpisodes.ToString() : "?";
                MalProgressText = $"MAL: {status.WatchedEpisodes}/{total} {statusText}{rewatchText}";
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"MAL status load failed: {ex.Message}", "WARNING");
                MalProgressText = "MAL status unavailable";
            }
            finally
            {
                IsMalStatusLoading = false;
            }
        }

        [RelayCommand(CanExecute = nameof(IsDubChecking))]
        private void CancelDubCheck() => _dubLookupCts?.Cancel();

        private async Task ResolveDubAvailabilityAsync(MediaResult media, CancellationTokenSource operation, int generation)
        {
            using var ownedOperation = operation;
            CancellationToken token = operation.Token;
            bool IsCurrent() => !_isDisposed && !token.IsCancellationRequested &&
                generation == Volatile.Read(ref _dubLookupGeneration) && ReferenceEquals(Media, media);
            if (!ShouldResolveDubAvailability(media))
            {
                if (ReferenceEquals(_dubLookupCts, operation)) _dubLookupCts = null;
                return;
            }

            IsDubChecking = true;
            DubCheckStatus = "Checking dubbed episode availability\u2026";
            long latestSequence = -1;
            void ApplyUpdate(DubAvailabilityUpdate update)
            {
                if (!IsCurrent() || update.Sequence <= latestSequence) return;
                latestSequence = update.Sequence;
                ApplyDubResult(media, update.Result, clearSummary: update.IsComplete &&
                    update.Providers.Any(provider => provider.Outcome == DubProviderOutcome.IdentityRejected));
                string failures = string.Join("; ", update.Providers
                    .Where(provider => provider.Outcome is not (DubProviderOutcome.Completed or DubProviderOutcome.Pending))
                    .Select(provider => $"{provider.Provider}: {provider.Outcome switch
                    {
                        DubProviderOutcome.TimedOut => "timed out",
                        DubProviderOutcome.Backoff => "waiting after a provider failure",
                        DubProviderOutcome.IdentityRejected => "identity conflict",
                        _ => "unavailable"
                    }}"));
                DubCheckStatus = !update.IsComplete
                    ? $"{(update.Result.Checked ? update.Result.Verified ? "Verified episode result available" : "Badge summary available" : "Availability unknown")}; checking {update.PendingProviders} provider(s)\u2026"
                    : failures.Length > 0 ? $"Lookup incomplete. {failures}" : update.Result.Detail;
            }

            try
            {
                AddScraperActivity("Checking provider badges for dubbed episode availability.", "Checking");
                var result = await _dubAvailabilityService.CheckAsync(
                    media,
                    token,
                    bypassCache: false,
                    mode: DubAvailabilityCheckMode.Verified,
                    progress: new Progress<DubAvailabilityUpdate>(ApplyUpdate));
                token.ThrowIfCancellationRequested();
                if (!IsCurrent())
                {
                    return;
                }

                ApplyUpdate(new(result, result.ProviderOutcomes, true) { Sequence = long.MaxValue });
                if (result.Checked)
                {
                    string state = result.Verified
                        ? result.DubEpisodes > 0 ? "Verified" : "None"
                        : result.DubEpisodes > 0 ? "Summary" : "Checked";
                    AddScraperActivity(result.Detail, state);
                }
                else
                {
                    AddScraperActivity(result.Detail, "Unknown");
                    OnPropertyChanged(nameof(AudioAvailabilityText));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (!_isDisposed && generation == Volatile.Read(ref _dubLookupGeneration) && ReferenceEquals(Media, media))
                    DubCheckStatus = "Dub check canceled. Available results retained.";
                UniversalMediaOS.Core.Helpers.AppLogger.Log(
                    $"Dub availability check cancelled for '{media.OfficialTitle}'.");
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Dub availability check failed: {ex.Message}", "WARNING");
                if (IsCurrent())
                {
                    DubCheckStatus = "Dub availability could not be checked.";
                    AddScraperActivity("Dub availability check could not reach the provider.", "Unknown");
                }
            }
            finally
            {
                if (generation == Volatile.Read(ref _dubLookupGeneration)) IsDubChecking = false;
                if (ReferenceEquals(_dubLookupCts, operation)) _dubLookupCts = null;
            }
        }

        private static void ApplyDubResult(MediaResult media, DubAvailabilityResult result, bool clearSummary = false)
        {
            if (media.IsDubAvailabilityVerified && !result.Verified) return;
            if (!result.Checked && !clearSummary) return;
            media.AvailableDubEpisodes = result.Checked ? Math.Max(0, result.DubEpisodes) : 0;
            media.HighestContiguousDubEpisode = result.Verified ? Math.Max(0, result.HighestContiguousDubEpisode) : 0;
            media.DubbedEpisodeNumbers = result.Verified ? result.DubbedEpisodeNumbers : Array.Empty<decimal>();
            media.DubAvailabilityState = result.Verified ? MediaDubAvailabilityState.Verified
                : result.Checked ? MediaDubAvailabilityState.Summary : MediaDubAvailabilityState.Unknown;
            media.DubAvailabilityChecked = result.Checked;
        }

        internal static bool ShouldResolveDubAvailability(MediaResult? media)
        {
            return media != null &&
                   !media.IsDubAvailabilityVerified &&
                   !string.IsNullOrWhiteSpace(media.OfficialTitle);
        }

        [RelayCommand]
        private void GoBack()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("GoBack command invoked. Closing details tab.");
            WeakReferenceMessenger.Default.Send(new CloseTabMessage(this));
        }

        [RelayCommand]
        private void OpenVaDetect()
        {
            if (Media == null)
            {
                return;
            }

            WeakReferenceMessenger.Default.Send(new NavigateToVaDetectMessage(Media));
        }

        [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanWatchSelectedEpisode))]
        private async Task WatchNowAsync()
        {
            if (Media == null || !CanWatchSelectedEpisode) return;

            UniversalMediaOS.Core.Helpers.AppLogger.Log($"WatchNowAsync invoked for: '{Media.OfficialTitle}' (ID: {Media.Id}), Episode: '{SelectedEpisode}', Provider discovery: automatic indexes");
            IsRouting = true;
            _routingCts?.Cancel();
            _routingCts?.Dispose();
            _routingCts = new CancellationTokenSource();
            var token = _routingCts.Token;
            ScraperActivityItems.Clear();
            AddScraperActivity($"Queued {Media.OfficialTitle} episode {SelectedEpisode}.", "Queued");
            WeakReferenceMessenger.Default.Send(new ToastNotificationMessage("Initiating stream routing switchboard..."));

            try
            {
                string audioPref = SelectedAudioMode.Equals("Dub", StringComparison.OrdinalIgnoreCase) ? "dub" : "sub";

                string episodeNum = SelectedEpisode;
                // Watch and episode navigation always use the refreshed index pool.
                // A saved manual domain must not pin an obsolete provider.
                string providerDomain = string.Empty;

                Action<string> logger = (msg) =>
                {
                    AddScraperActivity(msg, "Resolving");
                    bool showToast =
                        msg.StartsWith(">", StringComparison.Ordinal) ||
                        msg.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("rotating away", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("Resolve success", StringComparison.OrdinalIgnoreCase);
                    if (!showToast)
                    {
                        return;
                    }

                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher != null)
                    {
                        dispatcher.InvokeAsync(() => WeakReferenceMessenger.Default.Send(new ToastNotificationMessage(msg)));
                    }
                    else
                    {
                        WeakReferenceMessenger.Default.Send(new ToastNotificationMessage(msg));
                    }
                };

                logger($"Resolving: {Media.OfficialTitle} Ep {episodeNum} ({audioPref})");
                string[] titleAliases = [Media.EnglishTitle, Media.RomajiTitle];
                string[] titleSynonyms = Media.Synonyms is { Count: > 0 }
                    ? Media.Synonyms.GetRange(0, Math.Min(8, Media.Synonyms.Count)).ToArray()
                    : [];
                PlaybackSource? source = null;
                SourceTier requestedTier = SourceTier.Tier1_PythonScraper;

                var (dialogResult, selectedTier) = _dialogService.ShowSourceSelection();

                if (dialogResult)
                {
                    switch (selectedTier)
                    {
                        case SelectedSourceTier.Stream_Auto:
                            requestedTier = SourceTier.Tier1_PythonScraper;
                            logger("Stream selected: looking for usable video to play directly in the app...");
                            source = await _routingEngine.ResolveBestSourceAsync(
                                Media.OfficialTitle, episodeNum, providerDomain, logger,
                                SourceTier.Tier1_PythonScraper,
                                token,
                                 audioPref,
                                 titleAliases,
                                 Media.Id,
                                 Media.IdMal,
                                 titleSynonyms);
                            break;

                        case SelectedSourceTier.Stream_WebView:
                            requestedTier = SourceTier.Tier2_WebViewEmbed;
                            logger("Website selected as a last resort: looking for the episode player...");
                            source = await _routingEngine.ResolveBestSourceAsync(
                                Media.OfficialTitle, episodeNum, providerDomain, logger,
                                SourceTier.Tier2_WebViewEmbed,
                                token,
                                audioPref,
                                titleAliases,
                                Media.Id,
                                Media.IdMal,
                                titleSynonyms);
                            break;

                        default:
                            break;
                    }
                }

                if (source != null)
                {
                    token.ThrowIfCancellationRequested();
                    AddScraperActivity("Source resolved successfully. Opening playback tab.", "Ready");

                    string seriesTitle = Media.OfficialTitle;
                    int aniListId = Media.Id;
                    int malId = Media.IdMal;
                    int totalEpisodes = Media.TotalEpisodes;
                    int availableEpisodes = AvailableEpisodeCount;
                    var episodeContext = new EpisodePlaybackContext(
                        1,
                        Math.Max(1, availableEpisodes),
                        async (episode, navigationToken) =>
                        {
                            PlaybackSource? episodeSource = await _routingEngine.ResolveBestSourceAsync(
                                seriesTitle,
                                episode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                providerDomain,
                                msg => UniversalMediaOS.Core.Helpers.AppLogger.Log($"[Episode navigation] {msg}"),
                                requestedTier,
                                navigationToken,
                                 audioPref,
                                 titleAliases,
                                 aniListId,
                                 malId,
                                 titleSynonyms);

                            return episodeSource == null
                                ? null
                                : new ResolvedEpisodePlayback(
                                    episodeSource.UrlOrPath,
                                    $"{seriesTitle} - Ep {episode}",
                                    episodeSource.Tier == SourceTier.Tier2_WebViewEmbed,
                                    episodeSource.EmbedOrigin,
                                    episodeSource.Subtitles,
                                    episodeSource.AudioNotice);
                        }, audioPref, new AnimePlaybackIdentity(aniListId, malId));

                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    Action playAction = () =>
                    {
                        if (source.Tier == SourceTier.Tier1_PythonScraper)
                        {
                            WeakReferenceMessenger.Default.Send(
                                new PlayMediaMessage(source.UrlOrPath,
                                    seriesTitle + " - Ep " + episodeNum,
                                    isWebView: false,
                                    referer: source.EmbedOrigin,
                                    malId: malId,
                                    episodeNumber: episodeNum,
                                    totalEpisodes: totalEpisodes,
                                    episodeContext: episodeContext,
                                    subtitles: source.Subtitles,
                                    audioNotice: source.AudioNotice));
                        }
                        else if (source.Tier == SourceTier.Tier2_WebViewEmbed)
                        {
                            WeakReferenceMessenger.Default.Send(
                                new PlayMediaMessage(source.UrlOrPath,
                                    seriesTitle + " - Ep " + episodeNum,
                                    isWebView: true,
                                    malId: malId,
                                    episodeNumber: episodeNum,
                                    totalEpisodes: totalEpisodes,
                                    episodeContext: episodeContext));
                        }
                    };

                    if (dispatcher != null)
                        await dispatcher.InvokeAsync(playAction);
                    else
                        playAction();
                }
                else
                {
                    AddScraperActivity(requestedTier == SourceTier.Tier2_WebViewEmbed
                        ? "No matching website player was found. Try another episode or retry later."
                        : audioPref == "dub"
                            ? "No usable dub stream was found. Try another episode, choose Sub, or explicitly select Open website."
                            : "No usable stream was found. Retry later or explicitly select Open website.", "Stopped");
                }
            }
            catch (OperationCanceledException)
            {
                AddScraperActivity("Playback resolution cancelled.", "Stopped");
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Playback routing error: {ex}", "ERROR");
                AddScraperActivity($"Playback error: {ex.Message}", "Error");
                WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"Playback Error: {ex.Message}"));
            }
            finally
            {
                IsRouting = false;
                _routingCts?.Dispose();
                _routingCts = null;
            }
        }

        [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanDownloadSelectedEpisode))]
        private async Task WatchViaDownloadAsync()
        {
            if (Media == null || !int.TryParse(SelectedEpisode, out int episode) || !CanDownloadSelectedEpisode) return;
            MediaResult media = Media;
            string audio = SelectedAudioMode;
            IsTemporaryWatchDownloading = true;
            _temporaryWatchCts?.Dispose();
            _temporaryWatchCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
            var token = _temporaryWatchCts.Token;
            AddScraperActivity($"Finding a downloadable copy of {media.OfficialTitle} episode {episode} ({audio}).", "Searching");
            try
            {
                TemporaryEpisodeWatchResult result = await _temporaryEpisodeWatch.DownloadAsync(
                    media.Id, media.OfficialTitle, new[] { media.EnglishTitle, media.RomajiTitle }.Concat(media.Synonyms ?? []),
                    episode, audio, message => AddScraperActivity(message, "Downloading"), token,
                    media.Format, media.TotalEpisodes);
                bool handedToPlayer = false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    WeakReferenceMessenger.Default.Send(new PlayMediaMessage(
                        result.FilePath, $"{media.OfficialTitle} - Ep {episode}",
                        malId: media.IdMal,
                        episodeNumber: episode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        totalEpisodes: media.TotalEpisodes,
                        // This lease owns one file; adjacent episodes require a new explicit choice.
                        episodeContext: new EpisodePlaybackContext(episode, episode,
                            (_, _) => Task.FromResult<ResolvedEpisodePlayback?>(null), audio,
                            new AnimePlaybackIdentity(media.Id, media.IdMal)),
                        audioNotice: result.AudioNotice,
                        temporaryWatchLease: result.Lease,
                        localCaptionPaths: result.CaptionPaths));
                    handedToPlayer = true;
                    AddScraperActivity("Downloaded episode opened in the native player. Its temporary file stays until that tab closes.", "Ready");
                }
                finally { if (!handedToPlayer) result.Lease.Dispose(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                AddScraperActivity("Temporary episode download cancelled.", "Stopped");
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Temporary episode watch failed: {ex}", "WARNING");
                AddScraperActivity(ex.Message, "Error");
                WeakReferenceMessenger.Default.Send(new ToastNotificationMessage(ex.Message));
            }
            finally
            {
                IsTemporaryWatchDownloading = false;
                _temporaryWatchCts?.Dispose();
                _temporaryWatchCts = null;
            }
        }

        [RelayCommand]
        private void CancelTemporaryWatch() => _temporaryWatchCts?.Cancel();

        [RelayCommand]
        private void DownloadSeason()
        {
            if (Media == null) return;

            if (_downloadJob != null && !_downloadJob.CanRemove)
            {
                WeakReferenceMessenger.Default.Send(
                    new ToastNotificationMessage("This season is already in Downloads. Pause, resume, or cancel it there."));
                return;
            }

            bool confirmed = _dialogService.ShowConfirmDialog(
                $"Queue a season download for '{Media.OfficialTitle}' using {SelectedAudioMode} preference? The download continues if you close this tab and can be paused or resumed from Downloads.",
                "Confirm Season Download");
            if (!confirmed)
            {
                AddScraperActivity("Season download cancelled before it was queued.", "Stopped");
                return;
            }

            UniversalMediaOS.Core.Helpers.AppLogger.Log($"Queueing season download for '{Media.OfficialTitle}' (ID: {Media.Id}).");
            ObserveDownloadJob(_downloadQueue.Enqueue(
                Media.OfficialTitle,
                SelectedAudioMode,
                Media.IdMal,
                Media.TotalEpisodes));
            AddScraperActivity($"Season download queued for {Media.OfficialTitle}. Manage it from Downloads.", "Downloading");
            WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"Season download queued: {Media.OfficialTitle}"));
        }

        private void ObserveDownloadJob(DownloadQueueJob job)
        {
            if (_downloadJob != null) _downloadJob.PropertyChanged -= DownloadJob_PropertyChanged;
            _downloadJob = job;
            _downloadJob.PropertyChanged += DownloadJob_PropertyChanged;
            UpdateDownloadButton(job);
        }

        private void DownloadJob_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not DownloadQueueJob job ||
                e.PropertyName is not (nameof(DownloadQueueJob.Status) or nameof(DownloadQueueJob.Progress))) return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.InvokeAsync(() => UpdateDownloadButton(job));
            else
                UpdateDownloadButton(job);
        }

        private void UpdateDownloadButton(DownloadQueueJob job)
        {
            IsDownloading = job.Status is DownloadJobStatus.Pending or DownloadJobStatus.Running or DownloadJobStatus.Pausing;
            DownloadButtonText = job.Status switch
            {
                DownloadJobStatus.Pending => "Queued in Downloads",
                DownloadJobStatus.Running => $"Downloading {job.Progress:F0}%",
                DownloadJobStatus.Pausing => "Pausing...",
                DownloadJobStatus.Paused => "Paused in Downloads",
                DownloadJobStatus.Cancelling => "Cancelling...",
                DownloadJobStatus.Completed => "Downloaded",
                DownloadJobStatus.Failed => "Failed - see Downloads",
                DownloadJobStatus.Cancelled => "Season Download",
                _ => "Season Download"
            };
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            CancelActiveWork();
            if (_observedMedia != null)
            {
                _observedMedia.PropertyChanged -= Media_PropertyChanged;
                _observedMedia = null;
            }

            _routingCts?.Dispose();
            _routingCts = null;
            _temporaryWatchCts?.Dispose();
            _temporaryWatchCts = null;
            _dubLookupCts = null;
            _lifecycleCts.Dispose();
            if (_downloadJob != null)
            {
                _downloadJob.PropertyChanged -= DownloadJob_PropertyChanged;
                _downloadJob = null;
            }
        }
    }
}
