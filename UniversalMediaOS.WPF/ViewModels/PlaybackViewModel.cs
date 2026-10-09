using UniversalMediaOS.Core.OtherMedia;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Streaming;
using UniversalMediaOS.Core.Tracking;

namespace UniversalMediaOS.WPF.ViewModels
{
    public sealed record PlaybackQualityOption(string Label, string Url, int Height = 0, long Bandwidth = 0)
    {
        public override string ToString() => Label;
    }

    internal sealed record WebPlaybackRequestContext(
        string Referer,
        string ContentType,
        string UserAgent,
        string Cookie,
        IReadOnlyDictionary<string, string> RequestHeaders);

    public partial class PlaybackViewModel : ObservableObject, IDisposable
    {
        private readonly LibVLC _libVLC;
        private readonly Helpers.NativePlaybackEngine.Lease _nativeEngineLease;
        private readonly EventHandler<EventArgs> _nativeOpening;
        private readonly EventHandler<MediaPlayerBufferingEventArgs> _nativeBuffering;
        private readonly EventHandler<EventArgs> _nativePlaying;
        private readonly EventHandler<EventArgs> _nativePaused;
        private readonly EventHandler<EventArgs> _nativeStopped;
        private readonly EventHandler<EventArgs> _nativeError;
        private readonly EventHandler<MediaPlayerESAddedEventArgs> _nativeEsAdded;
        private readonly EventHandler<MediaPlayerESDeletedEventArgs> _nativeEsDeleted;
        private readonly EventHandler<MediaPlayerLengthChangedEventArgs> _nativeLengthChanged;
        private readonly DatabaseContext _databaseContext;
        private readonly HlsLoopbackProxy? _hlsProxy;
        private readonly PlaybackProgressService _playbackProgress;
        private PlaybackProgressContext? _progressContext;
        private readonly SemaphoreSlim _resumeDatabaseLock = new(1, 1);
        private readonly System.Windows.Threading.Dispatcher? _creationDispatcher =
            System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
        private readonly object _resumePersistenceGate = new();
        private readonly Dictionary<long, Task> _pendingResumeSaves = new();
        private ResumeSaveSlot? _queuedResumeSave;
        private sealed record ResumeSaveSnapshot(string MediaId, string EpisodeId, double Position,
            double Duration, bool Ended, long Sequence, DateTimeOffset ObservedUtc);
        private sealed class ResumeSaveSlot(ResumeSaveSnapshot snapshot)
        {
            public ResumeSaveSnapshot Snapshot { get; set; } = snapshot;
            public Task Completion { get; set; } = Task.CompletedTask;
            public bool Captured { get; set; }
        }
        private readonly List<Task> _pendingResumeLoads = [];
        private Task<(PlaybackProgressSession? Session, double Position)> _resumeLoadRead =
            Task.FromResult<(PlaybackProgressSession?, double)>((null, 0));
        private Task<PlaybackProgressSession?> _progressSessionReady = Task.FromResult<PlaybackProgressSession?>(null);
        private CancellationTokenSource? _resumeLoadCancellation;
        private bool _resumeLoadHasAcceptedSave;
        internal Task ResumeLoadCompleted { get; private set; } = Task.CompletedTask;
        internal int PendingResumeSaveCount
        {
            get { lock (_resumePersistenceGate) return _pendingResumeSaves.Count(entry => !entry.Value.IsCompleted); }
        }
        internal int PendingResumeLoadCount
        {
            get { lock (_resumePersistenceGate) return _pendingResumeLoads.Count(task => !task.IsCompleted); }
        }
        private bool _resumeReadApplied = true;
        private bool _playWhenResumeLoaded;
        private Media? _currentMedia;
        private Media? _pendingMedia;
        private DateTime _lastTimeUpdate = DateTime.MinValue;
        private DateTime _lastMalProgressCheck = DateTime.MinValue;
        private DateTime _lastResumeSaveUtc = DateTime.MinValue;
        private volatile bool _isUpdatingTimeFromPlayer;
        private volatile bool _isDisposing;
        private int _malId;
        private int _trackingEpisodeNumber;
        private bool _malProgressSynced;
        private readonly MalOAuthService? _malOAuthService;
        private readonly DomainHotSwapper? _malProgressConfig;
        private readonly object _malProgressGate = new();
        private CancellationTokenSource? _malProgressSyncCts;
        private long _malProgressGeneration;
        private Task _malProgressSyncTask = Task.CompletedTask;
        internal Task PendingMalProgressSync { get { lock (_malProgressGate) return _malProgressSyncTask; } }
        internal TimeSpan MalProgressSyncTimeout { get; init; } = TimeSpan.FromSeconds(45);
        private string _resumeMediaId = string.Empty;
        private string _resumeEpisodeId = string.Empty;
        private double _pendingResumePositionSeconds;
        private double _lastSavedResumeSeconds = double.NaN;
        private bool _resumeSeekApplied;
        private bool _playbackEnded;
        private bool _resumePersistenceClosed;
        private long _resumeWriteSequence;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Media, string Unit), long> _latestResumeWrites = new();
        private string _currentEpisodeNumber = string.Empty;
        private int _currentTotalEpisodes;
        private readonly WatchTogetherClientService? _watchTogetherClient;
        private readonly PlaybackSyncController? _playbackSync;
        private volatile bool _isApplyingRemoteSyncCommand;
        private int _remoteSyncCommandGeneration;
        private string _lastMediaSource = string.Empty;
        private string _lastMediaReferer = string.Empty;
        private string _lastMediaContentType = string.Empty;
        private string _lastMediaUserAgent = string.Empty;
        private string _lastMediaCookie = string.Empty;
        private string _lastValidatedHlsVariant = string.Empty;
        private IReadOnlyDictionary<string, string> _lastMediaRequestHeaders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string _webFallbackUrl = string.Empty;
        private string _lastEpisodeNumber = string.Empty;
        private int _lastTotalEpisodes;
        private int _lastMalId;
        private bool _stopRequested;
        private bool _lastLoadWasWeb;
        private int _startupWatchGeneration;
        private int _playbackGeneration;
        private EpisodePlaybackContext? _episodeContext;
        public AudiovisualPlaybackContext? AudiovisualContext { get; private set; }
        private CancellationTokenSource? _episodeNavigationCts;
        private int _qualityDiscoveryGeneration;
        private bool _suppressQualitySelection;
        private bool _autoAdvanceRequested;
        private string _activeQualityUrl = string.Empty;
        private IReadOnlyList<MediaSubtitleTrack> _lastMediaSubtitles = [];
        private IReadOnlyList<string> _lastLocalCaptionPaths = [];
        private readonly List<string> _subtitleProxySessions = [];
        private readonly HashSet<string> _mediaProxySessions = [];
        private string? _preferredCaptionKey;
        private PlaybackTrackOption? _attachedCaption;
        private string? _lastEnabledCaptionKey;
        private bool _suppressTrackSelection;
        private bool _restoringCurrentPosition;
        private string? _preferredAudioKey;
        private bool _restoreCaptionSelection;
        private bool _restoreAudioSelection;

        private static readonly HttpClient QualityManifestClient = new()
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        private static readonly TimeSpan ResumeSaveInterval = TimeSpan.FromSeconds(10);
        private const double MinimumResumePositionSeconds = 5;
        private const double ResumeSaveDeltaSeconds = 5;
        private const double ResumeEndToleranceSeconds = 20;
        private const double MinimumCredibleVideoDurationSeconds = 120;
        internal const string CompletionWatchTogetherAction = "pause";

        public bool IsDisposed { get; private set; }
        public event EventHandler<WebPlaybackCommandEventArgs>? WebPlaybackCommandRequested;

        private bool _isTabActive = true;
        private bool _pauseWhenStarted;
        public bool IsTabActive => _isTabActive;

        public void SetTabActive(bool active)
        {
            if (_isDisposing || IsDisposed || _isTabActive == active) return;
            SetProperty(ref _isTabActive, active, nameof(IsTabActive));
            if (!active)
            {
                _pauseWhenStarted = true;
                DeactivateWatchTogetherPlayback();
                PauseForInactiveTab();
            }
        }

        private void PauseForInactiveTab()
        {
            if (IsWebViewActive)
                WebPlaybackCommandRequested?.Invoke(this,
                    new WebPlaybackCommandEventArgs("pause", GetCurrentPlaybackPositionSeconds()));
            else if (MediaPlayer.IsPlaying)
                MediaPlayer.SetPause(true);
            IsPlaying = false;
            if (PlaybackDuration > 0) PlaybackStatusText = "Paused";
            else if (!IsWebViewActive && !string.IsNullOrEmpty(PendingMediaPath))
            {
                IsPlaybackBusy = false;
                PlaybackStatusText = "Ready to play";
            }
            SaveCurrentResumePosition(force: true, synchronous: false);
        }

        [ObservableProperty] private string _browserAudioStatus = string.Empty;

        private bool PauseAfterNativeStart()
        {
            if (IsTabActive && !_pauseWhenStarted) return false;
            if (IsTabActive && _restoringCurrentPosition && _pendingResumePositionSeconds > 0 &&
                (!_resumeSeekApplied || MediaPlayer.Time < _pendingResumePositionSeconds * 1000 + 250)) return true;
            PauseForInactiveTab();
            return true;
        }

        public void ActivateWatchTogetherPlayback()
        {
            if (!IsDisposed && IsTabActive)
            {
                _playbackSync?.ActivatePlayback(this);
                if (_playbackSync is { CanSend: true, IsHost: false })
                {
                    SendWatchTogetherPayload(new { action = "request_sync" });
                }
            }
        }

        public void DeactivateWatchTogetherPlayback()
        {
            _playbackSync?.DeactivatePlayback(this);
        }

        [ObservableProperty]
        private MediaPlayer _mediaPlayer;

        [ObservableProperty]
        private double _playbackTime;

        [ObservableProperty]
        private double _playbackDuration;

        [ObservableProperty]
        private bool _isPlaying;

        [ObservableProperty]
        private string _mediaTitle = string.Empty;

        [ObservableProperty]
        private string _episodeInfo = string.Empty;

        [ObservableProperty]
        private string _embedUrl = string.Empty;

        [ObservableProperty]
        private bool _isWebViewActive;

        [ObservableProperty]
        private string _pendingMediaPath = string.Empty;

        [ObservableProperty]
        private string _sourceInput = string.Empty;

        [ObservableProperty]
        private string _currentTimeText = "00:00";

        [ObservableProperty]
        private string _durationText = "00:00";

        [ObservableProperty]
        private string _playPauseText = "Play";

        [ObservableProperty]
        private int _volume = 100;

        [ObservableProperty]
        private double _playbackRate = 1.0;

        public ObservableCollection<PlaybackTrackOption> CaptionOptions { get; } = new();
        public ObservableCollection<PlaybackTrackOption> AudioOptions { get; } = new();

        [ObservableProperty]
        private PlaybackTrackOption? _selectedCaption;

        [ObservableProperty]
        private PlaybackTrackOption? _selectedAudioTrack;

        public double[] PlaybackRates { get; } = [0.5, 0.75, 1.0, 1.25, 1.5, 2.0];

        [ObservableProperty]
        private bool _audioTracksAvailable;

        [ObservableProperty]
        private string _audioButtonText = "Audio";

        [ObservableProperty]
        private string _audioNotice = string.Empty;

        [ObservableProperty]
        private bool _captionsAvailable;

        [ObservableProperty]
        private string _captionButtonText = "CC";

        [ObservableProperty]
        private bool _isSeekingFromSlider;

        [ObservableProperty]
        private bool _isPlaybackBusy;

        [ObservableProperty]
        private bool _hasPlaybackError;

        [ObservableProperty]
        private string _playbackStatusText = "Ready";

        [ObservableProperty]
        private string _playbackErrorText = string.Empty;

        [ObservableProperty]
        private bool _hasWebFallback;

        [ObservableProperty]
        private bool _isEpisodeNavigationBusy;

        [ObservableProperty]
        private bool _autoPlayNext = true;

        [ObservableProperty]
        private bool _isQualitySelectionAvailable;

        [ObservableProperty]
        private bool _isPictureInPictureMode;

        [ObservableProperty]
        private PlaybackQualityOption? _selectedQuality;

        public ObservableCollection<PlaybackQualityOption> QualityOptions { get; } = new();

        public bool CanGoToPreviousEpisode =>
            !IsDisposed && !_isDisposing &&
            !IsEpisodeNavigationBusy &&
            TryGetNavigationEpisode(out int episode) &&
            episode > _episodeContext!.FirstEpisode;

        public bool CanGoToNextEpisode =>
            !IsDisposed && !_isDisposing &&
            !IsEpisodeNavigationBusy &&
            TryGetNavigationEpisode(out int episode) &&
            episode < _episodeContext!.LastEpisode;

        public bool NativeControlsEnabled => !IsWebViewActive;

        partial void OnSourceInputChanged(string value) => OpenSourceCommand.NotifyCanExecuteChanged();

        public PlaybackViewModel(DatabaseContext databaseContext)
            : this(databaseContext, null, null, null, null)
        {
        }

        public PlaybackViewModel(
            DatabaseContext databaseContext,
            WatchTogetherClientService? watchTogetherClient,
            PlaybackSyncController? playbackSync,
            MalOAuthService? malOAuthService = null,
            HlsLoopbackProxy? hlsProxy = null,
            PlaybackProgressService? playbackProgress = null,
            DomainHotSwapper? malProgressConfig = null)
        {
            _databaseContext = databaseContext ?? throw new ArgumentNullException(nameof(databaseContext));
            _hlsProxy = hlsProxy;
            _playbackProgress = playbackProgress ?? new(new AudiovisualLibraryService());
            _watchTogetherClient = watchTogetherClient;
            _playbackSync = playbackSync;
            _malOAuthService = malOAuthService;
            _malProgressConfig = malProgressConfig;
            if (_watchTogetherClient != null)
            {
                _watchTogetherClient.MessageReceived += WatchTogetherClient_MessageReceived;
            }

            AppLogger.Log("Initializing PlaybackViewModel and LibVLC player...");
            _nativeEngineLease = Helpers.NativePlaybackEngine.ForProcess.Acquire();
            _libVLC = _nativeEngineLease.Engine;
            try { _mediaPlayer = new MediaPlayer(_libVLC) { Volume = Volume }; }
            catch
            {
                _nativeEngineLease.Dispose();
                throw;
            }

            _nativeOpening = (s, e) => RunOnDispatcher(() =>
            {
                IsPlaybackBusy = true;
                HasPlaybackError = false;
                PlaybackErrorText = string.Empty;
                PlaybackStatusText = "Opening stream...";
            });
            _nativeBuffering = (s, e) => RunOnDispatcher(() =>
            {
                if (_stopRequested || IsWebViewActive || HasPlaybackError) return;
                bool buffering = e.Cache < 99.5f;
                IsPlaybackBusy = buffering;
                if (buffering)
                {
                    PlaybackStatusText = $"Buffering {Math.Clamp(e.Cache, 0, 100):0}%";
                }
                else if (_mediaPlayer.State == VLCState.Paused || _pauseWhenStarted)
                {
                    // Paused seeks finish buffering without another Playing or
                    // Paused event. Clear the last percentage when data is ready.
                    PlaybackStatusText = "Paused";
                }
                else if (IsPlaying)
                {
                    PlaybackStatusText = "Playing";
                }
            });
            _nativePlaying = (s, e) =>
            {
                RunOnDispatcher(() =>
                {
                    IsPlaying = true;
                    IsPlaybackBusy = false;
                    HasPlaybackError = false;
                    PlaybackErrorText = string.Empty;
                    PlaybackStatusText = "Playing";
                    Interlocked.Increment(ref _startupWatchGeneration);
                    RefreshCaptionState();
                    MediaPlayer.SetRate((float)PlaybackRate);
                    AppLogger.Log($"LibVLC playing event fired for: '{MediaTitle}'");
                    ApplyPendingResumeSeek();
                    if (PauseAfterNativeStart()) return;
                    PublishPlaybackAction("play", GetCurrentPlaybackPositionSeconds());
                });
            };
            _nativePaused = (s, e) =>
            {
                if (_isDisposing || IsDisposed) return;
                RunOnDispatcher(() =>
                {
                    SaveCurrentResumePosition(force: true, synchronous: false);
                    IsPlaying = false;
                    IsPlaybackBusy = false;
                    PlaybackStatusText = "Paused";
                    RefreshCaptionState();
                    AppLogger.Log($"LibVLC paused event fired for: '{MediaTitle}'");
                    PublishPlaybackAction("pause", GetCurrentPlaybackPositionSeconds());
                });
            };
            _nativeStopped = (s, e) => RunOnDispatcher(() =>
            {
                IsPlaying = false;
                PlaybackTime = 0;
                IsPlaybackBusy = false;
                if (_stopRequested && !HasPlaybackError)
                {
                    PlaybackStatusText = "Stopped";
                }
                RefreshCaptionState();
                AppLogger.Log($"LibVLC stopped event fired for: '{MediaTitle}'");
            });
            _nativeError = (s, e) =>
                ReportPlaybackError("The selected stream could not be played. Retry it or open the browser fallback.");
            _nativeEsAdded = (_, e) => RunOnDispatcher(() =>
            {
                if (e.Type == TrackType.Text && _preferredCaptionKey != null)
                    _restoreCaptionSelection = true;
                if (e.Type == TrackType.Audio && _preferredAudioKey != null)
                    _restoreAudioSelection = true;
                RefreshCaptionState();
            });
            _nativeEsDeleted = (_, _) => RunOnDispatcher(RefreshCaptionState);
            _nativeLengthChanged = (s, e) => RunOnDispatcher(() =>
            {
                PlaybackDuration = e.Length;
                RefreshCaptionState();
                AppLogger.Log($"LibVLC length changed: {e.Length} ms for: '{MediaTitle}'");
                ApplyPendingResumeSeek();
                PauseAfterNativeStart();
            });

            _mediaPlayer.Opening += _nativeOpening;
            _mediaPlayer.Buffering += _nativeBuffering;
            _mediaPlayer.Playing += _nativePlaying;
            _mediaPlayer.Paused += _nativePaused;
            _mediaPlayer.Stopped += _nativeStopped;
            _mediaPlayer.EncounteredError += _nativeError;
            _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
            _mediaPlayer.ESAdded += _nativeEsAdded;
            _mediaPlayer.ESDeleted += _nativeEsDeleted;
            _mediaPlayer.EndReached += MediaPlayer_EndReached;
            _mediaPlayer.LengthChanged += _nativeLengthChanged;

            PlayPauseText = Helpers.LocalizationRuntime.Translate("Play");
            CaptionButtonText = Helpers.LocalizationRuntime.Translate("CC");
            Helpers.LocalizationRuntime.LanguageChanged += LocalizationRuntime_LanguageChanged;
        }

        internal void RunOnDispatcher(Action action, System.Windows.Threading.Dispatcher? targetDispatcher = null)
        {
            if (_isDisposing || IsDisposed) return;
            int generation = Volatile.Read(ref _playbackGeneration);

            // Stop/dispose raises native events while earlier events may still be
            // queued. Check again when dispatched, before touching the VLC handle.
            void RunIfAlive()
            {
                if (!_isDisposing && !IsDisposed && generation == Volatile.Read(ref _playbackGeneration)) action();
            }

            if ((targetDispatcher ?? App.Current?.Dispatcher ?? _creationDispatcher) is { } dispatcher)
            {
                if (!dispatcher.HasShutdownStarted) dispatcher.InvokeAsync(RunIfAlive);
            }
            else
            {
                RunIfAlive();
            }
        }

        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            if (_isDisposing || IsDisposed || _stopRequested) return;
            if ((DateTime.Now - _lastTimeUpdate).TotalMilliseconds <= 250)
            {
                return;
            }

            _lastTimeUpdate = DateTime.Now;
            RunOnDispatcher(() =>
            {
                if (_stopRequested || IsWebViewActive) return;
                if (e.Time > 0)
                {
                    Interlocked.Increment(ref _startupWatchGeneration);
                    IsPlaybackBusy = false;
                    HasPlaybackError = false;
                    PlaybackErrorText = string.Empty;
                    PlaybackStatusText = "Playing";
                    RefreshCaptionState();
                    ApplyPendingResumeSeek();
                    PauseAfterNativeStart();
                }

                _isUpdatingTimeFromPlayer = true;
                try
                {
                    // A queued pre-seek event can arrive after a reload's seek. Read
                    // the current native clock so it cannot overwrite the restored UI.
                    PlaybackTime = Math.Max(0, MediaPlayer.Time);
                }
                finally
                {
                    _isUpdatingTimeFromPlayer = false;
                }
                TrySyncMalFromProgress(PlaybackTime, MediaPlayer.Length > 0 ? MediaPlayer.Length : PlaybackDuration, ended: false);
                if (ResumeLoadCompleted.IsCompleted)
                    QueueResumeSave(PlaybackTime / 1000.0, ended: false, force: false, synchronous: false);
            });
        }

        partial void OnPlaybackTimeChanged(double value)
        {
            CurrentTimeText = FormatTime(value);

            if (IsDisposed || IsWebViewActive || _isUpdatingTimeFromPlayer || IsSeekingFromSlider)
            {
                return;
            }

            if (MediaPlayer != null)
            {
                long duration = MediaPlayer.Length > 0 ? MediaPlayer.Length : (long)PlaybackDuration;
                if (duration <= 0)
                {
                    return;
                }

                long current = Math.Max(0, MediaPlayer.Time);
                long requested = Math.Clamp((long)value, 0, duration);
                if (Math.Abs(current - requested) > 1500)
                {
                    AppLogger.Log($"User requested seek from {current} ms to {requested} ms");
                    MediaPlayer.Time = requested;
                    if (Math.Abs(PlaybackTime - requested) > 1)
                    {
                        PlaybackTime = requested;
                    }
                }
            }
        }

        partial void OnPlaybackDurationChanged(double value)
        {
            DurationText = FormatTime(value);
        }

        partial void OnIsPlayingChanged(bool value)
        {
            PlayPauseText = Helpers.LocalizationRuntime.Translate(value ? "Pause" : "Play");
        }

        partial void OnIsWebViewActiveChanged(bool value)
        {
            OnPropertyChanged(nameof(NativeControlsEnabled));
            RefreshLocalizedPlaybackText();
        }

        partial void OnVolumeChanged(int value)
        {
            int normalized = Math.Clamp(value, 0, 100);
            if (MediaPlayer != null)
            {
                MediaPlayer.Volume = normalized;
            }
        }

        partial void OnPlaybackRateChanged(double value)
        {
            double normalized = PlaybackRates.Contains(value) ? value : 1.0;
            if (Math.Abs(value - normalized) > 0.001)
            {
                PlaybackRate = normalized;
                return;
            }

            if (IsWebViewActive)
            {
                WebPlaybackCommandRequested?.Invoke(this, new WebPlaybackCommandEventArgs("rate", normalized));
            }
            else if (MediaPlayer != null)
            {
                MediaPlayer.SetRate((float)normalized);
            }
        }

        partial void OnIsEpisodeNavigationBusyChanged(bool value)
        {
            UpdateEpisodeNavigationState();
        }

        partial void OnSelectedQualityChanged(PlaybackQualityOption? value)
        {
            if (_suppressQualitySelection || value == null ||
                value.Url.Equals(_activeQualityUrl, StringComparison.Ordinal))
            {
                return;
            }

            ReloadNativeSelection(value);
        }

        partial void OnSelectedCaptionChanging(PlaybackTrackOption? oldValue, PlaybackTrackOption? newValue)
        {
            if (!_suppressTrackSelection && !IsWebViewActive && !IsDisposed &&
                newValue?.Key == "off" && oldValue is { Key: not "off" })
                _lastEnabledCaptionKey = oldValue.Key;
        }

        partial void OnSelectedCaptionChanged(PlaybackTrackOption? value)
        {
            if (_suppressTrackSelection || value == null || IsWebViewActive || IsDisposed) return;
            if (value.Key != "off") _lastEnabledCaptionKey = value.Key;
            _preferredCaptionKey = value.Key.StartsWith("unverified:", StringComparison.Ordinal) ? null : value.Key;
            _restoreCaptionSelection = true;
            if (value.Subtitle != null || _attachedCaption != null)
            {
                if (value.Key != _attachedCaption?.Key) ReloadNativeSelection(SelectedQuality);
            }
            else
            {
                if (MediaPlayer.SetSpu(value.NativeId)) _restoreCaptionSelection = false;
                RefreshCaptionState();
            }
        }

        partial void OnSelectedAudioTrackChanged(PlaybackTrackOption? value)
        {
            if (_suppressTrackSelection || value == null || IsWebViewActive || IsDisposed) return;
            if (MediaPlayer.SetAudioTrack(value.NativeId))
            {
                _preferredAudioKey = value.Key.StartsWith("unverified:", StringComparison.Ordinal) ? null : value.Key;
                _restoreAudioSelection = false;
            }
            RefreshAudioTrackState();
        }

        public void PlayPending()
        {
            if (!IsTabActive || _pendingMedia == null || string.IsNullOrEmpty(PendingMediaPath))
            {
                return;
            }

            if (!_resumeReadApplied)
            {
                _playWhenResumeLoaded = true;
                return;
            }
            _playWhenResumeLoaded = false;

            AppLogger.Log($"PlayPending: Replaying media '{PendingMediaPath}' now that VideoView is ready.");
            try
            {
                _stopRequested = false;
                IsPlaybackBusy = true;
                HasPlaybackError = false;
                PlaybackErrorText = string.Empty;
                PlaybackStatusText = "Starting playback...";
                StartPlaybackStartupWatch(isWeb: false);
                MediaPlayer.Volume = Volume;
                MediaPlayer.Play(_pendingMedia);
                PendingMediaPath = string.Empty;
                _pendingMedia = null;
                ApplyPendingResumeSeek();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"PlayPending failed: {ex.Message}", "ERROR");
                ReportPlaybackError("The stream could not be started. Retry it or open the browser fallback.");
            }
        }

        public void BeginUserSeek()
        {
            _resumeSeekApplied = true;
            IsSeekingFromSlider = true;
        }

        public void CommitUserSeek(double value)
        {
            if (IsWebViewActive)
            {
                double durationMilliseconds = PlaybackDuration;
                if (durationMilliseconds > 0)
                {
                    long requestedMilliseconds = ClampSeekMilliseconds(value, durationMilliseconds);
                    PlaybackTime = requestedMilliseconds;
                    WebPlaybackCommandRequested?.Invoke(
                        this,
                        new WebPlaybackCommandEventArgs("seek", requestedMilliseconds / 1000.0));
                    PublishPlaybackAction("seek", requestedMilliseconds / 1000.0);
                }

                IsSeekingFromSlider = false;
                return;
            }

            if (MediaPlayer == null)
            {
                IsSeekingFromSlider = false;
                return;
            }

            long duration = MediaPlayer.Length > 0 ? MediaPlayer.Length : (long)PlaybackDuration;
            if (duration <= 0)
            {
                IsSeekingFromSlider = false;
                return;
            }

            long requested = ClampSeekMilliseconds(value, duration);

            long current = Math.Max(0, MediaPlayer.Time);
            AppLogger.Log($"User committed seek from {current} ms to {requested} ms");
            MediaPlayer.Time = requested;
            PlaybackTime = requested;
            IsSeekingFromSlider = false;
            PublishPlaybackAction("seek", requested / 1000.0);
        }

        public void LoadMedia(
            string urlOrPath,
            string title,
            string referer = "",
            string episodeNumber = "",
            int totalEpisodes = 0,
            int malId = 0,
            EpisodePlaybackContext? episodeContext = null,
            string contentType = "",
            string userAgent = "",
            string cookie = "",
            IReadOnlyDictionary<string, string>? requestHeaders = null,
            AudiovisualPlaybackContext? audiovisualContext = null,
            IEnumerable<MediaSubtitleTrack>? subtitles = null,
            string audioNotice = "",
            double? reloadPositionSeconds = null,
            bool? pauseAfterReload = null,
            IEnumerable<string>? localCaptionPaths = null,
            string? validatedHlsVariant = null)
        {
            if (_isDisposing || IsDisposed) return;
            CancelEpisodeNavigation();
            if (localCaptionPaths != null)
            {
                string mediaDirectory = Path.GetDirectoryName(Path.GetFullPath(urlOrPath)) ?? string.Empty;
                _lastLocalCaptionPaths = localCaptionPaths.Where(path =>
                        Path.GetDirectoryName(Path.GetFullPath(path))?.Equals(mediaDirectory, StringComparison.OrdinalIgnoreCase) == true &&
                        File.Exists(path) &&
                        new[] { ".srt", ".ass", ".ssa", ".vtt" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
            }
            else if (!string.Equals(urlOrPath, _lastMediaSource, StringComparison.Ordinal))
            {
                _lastLocalCaptionPaths = [];
            }
            if (!string.Equals(urlOrPath, _lastMediaSource, StringComparison.Ordinal) &&
                _lastEnabledCaptionKey?.StartsWith("unverified:", StringComparison.Ordinal) == true)
                _lastEnabledCaptionKey = null;
            SourceInput = urlOrPath;
            IReadOnlyDictionary<string, string> capturedHeaders = CopyRequestHeaders(requestHeaders);
            string effectiveContentType = contentType ?? string.Empty;
            string effectiveReferer = FirstNonEmpty(referer, FindHeader(capturedHeaders, "Referer"));
            string effectiveUserAgent = FirstNonEmpty(userAgent, FindHeader(capturedHeaders, "User-Agent"));
            string effectiveCookie = FirstNonEmpty(cookie, FindHeader(capturedHeaders, "Cookie"));

            if (validatedHlsVariant != null) _lastValidatedHlsVariant = validatedHlsVariant;
            else if (!string.Equals(urlOrPath, _lastMediaSource, StringComparison.Ordinal)) _lastValidatedHlsVariant = string.Empty;

            AppLogger.Log($"LoadMedia invoked: Title='{title}', UrlOrPath='{urlOrPath}', Referer='{effectiveReferer}', MalId={malId}");
            SaveCurrentResumePosition(force: true, synchronous: false);
            _stopRequested = true;
            if (_currentMedia != null || _pendingMedia != null) MediaPlayer.Stop();
            Interlocked.Increment(ref _playbackGeneration);
            ResetQualityOptions();
            _lastMediaSource = urlOrPath;
            _lastMediaReferer = effectiveReferer;
            _lastMediaContentType = effectiveContentType;
            _lastMediaUserAgent = effectiveUserAgent;
            _lastMediaCookie = effectiveCookie;
            _lastMediaRequestHeaders = capturedHeaders;
            _lastMediaSubtitles = MediaSubtitleTrack.Copy(subtitles)
                .Concat(_lastLocalCaptionPaths.Select(path =>
                {
                    string fileName = Path.GetFileNameWithoutExtension(path);
                    string language = fileName.EndsWith(".en", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith(".eng", StringComparison.OrdinalIgnoreCase) ? "en" :
                        fileName.EndsWith(".ar", StringComparison.OrdinalIgnoreCase) ||
                        fileName.EndsWith(".ara", StringComparison.OrdinalIgnoreCase) ? "ar" : string.Empty;
                    return new MediaSubtitleTrack(new Uri(Path.GetFullPath(path)).AbsoluteUri,
                        string.IsNullOrEmpty(language) ? "Downloaded subtitles" :
                        language == "en" ? "English (downloaded)" : "Arabic (downloaded)", language);
                })).ToArray();
            AudioNotice = audioNotice ?? string.Empty;
            _restoreCaptionSelection = true;
            _restoreAudioSelection = true;
            _webFallbackUrl = Uri.TryCreate(effectiveReferer, UriKind.Absolute, out var fallbackUri) &&
                               (fallbackUri.Scheme == Uri.UriSchemeHttp || fallbackUri.Scheme == Uri.UriSchemeHttps)
                ? effectiveReferer
                : string.Empty;
            _lastEpisodeNumber = episodeNumber;
            _lastTotalEpisodes = totalEpisodes;
            _lastMalId = malId;
            _lastLoadWasWeb = false;
            _episodeContext = episodeContext;
            AudiovisualContext = audiovisualContext;
            _autoAdvanceRequested = false;
            _stopRequested = false;
            MediaTitle = title;
            _currentEpisodeNumber = episodeNumber;
            _currentTotalEpisodes = totalEpisodes;
            UpdateEpisodeNavigationState();
            EpisodeInfo = BuildEpisodeInfo(title, episodeNumber, totalEpisodes);
            ConfigureTracking(malId, episodeNumber, title);
            ConfigureResumeState(malId, episodeNumber, title, urlOrPath);
            if (reloadPositionSeconds.HasValue)
            {
                _pendingResumePositionSeconds = Math.Max(0, reloadPositionSeconds.Value);
                _resumeSeekApplied = false;
                _restoringCurrentPosition = true;
            }
            _pauseWhenStarted = pauseAfterReload ?? !IsTabActive;
            IsWebViewActive = false;
            EmbedUrl = string.Empty;
            PlaybackTime = 0;
            PlaybackDuration = 0;
            CaptionsAvailable = false;
            CaptionButtonText = Helpers.LocalizationRuntime.Translate("CC");
            IsPlaybackBusy = true;
            HasPlaybackError = false;
            PlaybackErrorText = string.Empty;
            PlaybackStatusText = "Preparing stream...";
            HasWebFallback = !string.IsNullOrWhiteSpace(_webFallbackUrl);
            StartPlaybackStartupWatch(isWeb: false);

            string path = NormalizeMediaSource(urlOrPath);

            bool isLocal = !path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                           !path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            if (!isLocal)
            {
                if (!TryPrepareAuthenticatedNetworkSource(
                    path,
                    effectiveContentType,
                    effectiveUserAgent,
                    effectiveCookie,
                    effectiveReferer,
                    capturedHeaders,
                    out path))
                {
                    ReportPlaybackError(
                        "The stream requires request headers that could not be preserved. Retry it or choose another source.");
                    return;
                }
            }

            if (isLocal)
            {
                path = path.Replace('/', System.IO.Path.DirectorySeparatorChar);
                if (!System.IO.File.Exists(path))
                {
                    ReportPlaybackError($"The video file no longer exists: {path}");
                    return;
                }
            }

            var type = isLocal ? FromType.FromPath : FromType.FromLocation;
            AppLogger.Log($"Selected media source type: isLocal={isLocal}, ResolvedPath='{path}'");

            try
            {
                ReleaseCurrentMedia(path);

                if (isLocal)
                {
                    string escapedUri = new Uri(path).AbsoluteUri;
                    AppLogger.Log($"Converting local path to escaped Uri for LibVLC: '{escapedUri}'");
                    _currentMedia = new Media(_libVLC, escapedUri, FromType.FromLocation);
                }
                else
                {
                    _currentMedia = new Media(_libVLC, path, type);
                    if (!string.IsNullOrEmpty(effectiveReferer))
                    {
                        AppLogger.Log($"Adding HTTP referer option to LibVLC: {effectiveReferer}");
                        _currentMedia.AddOption(":http-referrer=" + effectiveReferer);
                    }

                    string libVlcUserAgent = string.IsNullOrWhiteSpace(effectiveUserAgent)
                        ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                        : effectiveUserAgent;
                    _currentMedia.AddOption(":http-user-agent=" + libVlcUserAgent);
                }

                if (_episodeContext?.AudioPreference == "dub")
                    _currentMedia.AddOption(":audio-language=eng,en");
                _currentMedia.AddOption(":sub-language=eng,en");
                AttachExternalSubtitles(_currentMedia);
                RefreshCaptionState();
                _pendingMedia = _currentMedia;
                PendingMediaPath = urlOrPath;
                AppLogger.Log("Media staged as pending. PlaybackView.Loaded will trigger playback.");
                BeginQualityDiscovery(path);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Error setting up LibVLC media player: {ex.Message}", "ERROR");
                ReportPlaybackError("The stream could not be prepared. Retry it or open the browser fallback.");
            }
        }

        [RelayCommand(CanExecute = nameof(CanOpenSource))]
        private void OpenSource()
        {
            string source = SourceInput.Trim();
            var saved = File.Exists(source) ? AuthorizedMediaDownloadService.ReadLibraryPlayback(source) : null;
            if (saved != null)
            {
                string title = saved.Context.Unit.IsFeature ? saved.Context.Title
                    : $"{saved.Context.Title} · S{saved.Context.Unit.SeasonNumber:00}E{saved.Context.Unit.EpisodeNumber:00}";
                LoadMedia(source, title, episodeNumber: saved.Context.Unit.EpisodeNumber?.ToString() ?? string.Empty,
                    audiovisualContext: saved.Context, localCaptionPaths: saved.CaptionPaths, audioNotice: saved.AudioNotice);
            }
            else LoadMedia(source, BuildSourceTitle(source));
            PlayPending();
        }

        private void AttachExternalSubtitles(Media media)
        {
            var choices = PlaybackTrackOption.ExternalCaptions(_lastMediaSubtitles);
            PlaybackTrackOption? selected = _preferredCaptionKey == null
                ? choices.FirstOrDefault(option => option.Subtitle!.IsEnglish) ??
                  choices.FirstOrDefault(option => option.Subtitle!.IsDefault) ?? choices.FirstOrDefault()
                : choices.FirstOrDefault(option => option.Key == _preferredCaptionKey);
            _attachedCaption = null;
            if (selected == null) return;
            var track = selected.Subtitle!;
            try
            {
                string location = track.Url;
                bool localCaption = Uri.TryCreate(track.Url, UriKind.Absolute, out var captionUri) && captionUri.IsFile;
                if (_hlsProxy != null && !localCaption)
                {
                    _hlsProxy.Start();
                    string sessionId = _hlsProxy.RegisterSession(new ProxySession(
                        track.Url, track.UserAgent, track.Cookie, null, track.Referer,
                        DateTime.UtcNow, track.RequestHeaders, track.InlineVtt));
                    _subtitleProxySessions.Add(sessionId);
                    location = _hlsProxy.CreateSubtitleUrl(sessionId, track.Url, track.Label);
                }
                else if (RequiresHeaderPreservingProxy(track.Cookie ?? "", track.RequestHeaders ??
                    new Dictionary<string, string>()))
                {
                    AppLogger.Log("Caption attachment skipped: its request context requires the media proxy.", "WARNING");
                    return;
                }

                // VLC priority 4 explicitly selects this slave. Attaching one provider file
                // avoids associating generic decoder IDs with asynchronously loaded labels.
                if (media.AddSlave(MediaSlaveType.Subtitle, 4u, location)) _attachedCaption = selected;
                else AppLogger.Log("The native player could not attach an external caption track.", "WARNING");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"External caption attachment failed: {ex.Message}", "WARNING");
            }
        }

        private bool CanOpenSource() => !string.IsNullOrWhiteSpace(SourceInput);

        internal static string BuildSourceTitle(string source)
        {
            string sourcePath = Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && !uri.IsFile
                ? uri.AbsolutePath
                : source;
            string title = Path.GetFileNameWithoutExtension(sourcePath);
            return string.IsNullOrWhiteSpace(title)
                ? "Media source"
                : Uri.UnescapeDataString(title);
        }

        internal static string NormalizeMediaSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return source;
            }

            if (source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // Network URLs frequently contain percent-encoded nested URLs and signed
                // query strings. Unescaping the whole value corrupts those signatures.
                return source;
            }

            if (source.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    return new Uri(source).LocalPath;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Failed to parse file URI to local path: {ex.Message}", "WARNING");
                    return source;
                }
            }

            try
            {
                return Uri.UnescapeDataString(source);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to unescape local media path: {ex.Message}", "WARNING");
                return source;
            }
        }

        private bool TryPrepareAuthenticatedNetworkSource(
            string source,
            string contentType,
            string userAgent,
            string cookie,
            string referer,
            IReadOnlyDictionary<string, string> requestHeaders,
            out string preparedSource)
        {
            preparedSource = source;
            bool dash = LooksLikeDashSource(source, contentType);
            bool requiresProxy = dash || RequiresHeaderPreservingProxy(cookie, requestHeaders) || !string.IsNullOrWhiteSpace(_lastValidatedHlsVariant);
            if ((!dash && !HasCapturedRequestContext(userAgent, cookie, referer, requestHeaders) && string.IsNullOrWhiteSpace(_lastValidatedHlsVariant)) ||
                !Uri.TryCreate(source, UriKind.Absolute, out Uri? remoteUri) ||
                (remoteUri.Scheme != Uri.UriSchemeHttp && remoteUri.Scheme != Uri.UriSchemeHttps))
            {
                return true;
            }

            if (_hlsProxy?.OwnsStreamUrl(source) == true)
            {
                return true;
            }

            if (_hlsProxy == null)
            {
                return !requiresProxy;
            }

            try
            {
                _hlsProxy.Start();
                if (!_hlsProxy.IsRunning)
                {
                    AppLogger.Log(
                        requiresProxy
                            ? "Authenticated media proxy is unavailable; playback was stopped to avoid dropping required headers."
                            : "Authenticated media proxy is unavailable; falling back to direct LibVLC playback.",
                        "WARNING");
                    return !requiresProxy;
                }

                string sessionId = _hlsProxy.RegisterSession(new ProxySession(
                    RemoteM3U8Url: source,
                    UserAgent: userAgent,
                    Cookie: cookie,
                    KeyUrl: null,
                    Referer: referer,
                    CreatedAt: DateTime.UtcNow,
                    RequestHeaders: requestHeaders));
                _mediaProxySessions.Add(sessionId);

                preparedSource = dash ? _hlsProxy.CreateDashUrl(sessionId) : LooksLikeHlsSource(source, contentType)
                    ? _hlsProxy.CreateStreamUrl(sessionId)
                    : _hlsProxy.CreateMediaUrl(sessionId, source);
                if (LooksLikeHlsSource(source, contentType) && !string.IsNullOrWhiteSpace(_lastValidatedHlsVariant))
                    preparedSource = _hlsProxy.CreateVariantUrl(preparedSource,
                        _hlsProxy.CreateStreamUrl(sessionId, _lastValidatedHlsVariant));
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Authenticated media proxy setup failed: {ex.Message}", "WARNING");
                return !requiresProxy;
            }
        }

        internal static bool LooksLikeHlsSource(string source, string? contentType)
        {
            if (!string.IsNullOrWhiteSpace(contentType) &&
                (contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
                 contentType.Contains("m3u8", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
                   uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool LooksLikeDashSource(string source, string? contentType) =>
            contentType?.Contains("dash+xml", StringComparison.OrdinalIgnoreCase) == true ||
            (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
             uri.AbsolutePath.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase));

        private static bool HasCapturedRequestContext(
            string userAgent,
            string cookie,
            string referer,
            IReadOnlyDictionary<string, string> requestHeaders) =>
            !string.IsNullOrWhiteSpace(userAgent) ||
            !string.IsNullOrWhiteSpace(cookie) ||
            !string.IsNullOrWhiteSpace(referer) ||
            requestHeaders.Count > 0;

        internal static bool RequiresHeaderPreservingProxy(
            string? cookie,
            IReadOnlyDictionary<string, string>? requestHeaders)
        {
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                return true;
            }

            return requestHeaders?.Any(header =>
                !header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(header.Value)) == true;
        }

        private static IReadOnlyDictionary<string, string> CopyRequestHeaders(
            IReadOnlyDictionary<string, string>? requestHeaders)
        {
            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (requestHeaders == null)
            {
                return copy;
            }

            foreach (var header in requestHeaders)
            {
                if (!string.IsNullOrWhiteSpace(header.Key) && header.Value != null)
                {
                    copy[header.Key] = header.Value;
                }
            }

            return copy;
        }

        private static string FindHeader(IReadOnlyDictionary<string, string> headers, string name)
        {
            if (headers.TryGetValue(name, out string? value))
            {
                return value ?? string.Empty;
            }

            foreach (var header in headers)
            {
                if (header.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return header.Value ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static string FirstNonEmpty(string? preferred, string? fallback) =>
            !string.IsNullOrWhiteSpace(preferred)
                ? preferred
                : fallback ?? string.Empty;

        public void LoadEmbed(
            string embedUrl,
            string title,
            string episodeNumber = "",
            int totalEpisodes = 0,
            int malId = 0,
            EpisodePlaybackContext? episodeContext = null,
            string referer = "",
            string contentType = "",
            string userAgent = "",
            string cookie = "",
            IReadOnlyDictionary<string, string>? requestHeaders = null,
            AudiovisualPlaybackContext? audiovisualContext = null)
        {
            if (_isDisposing || IsDisposed) return;
            IReadOnlyDictionary<string, string> capturedHeaders = CopyRequestHeaders(requestHeaders);
            string effectiveReferer = FirstNonEmpty(referer, FindHeader(capturedHeaders, "Referer"));
            string effectiveUserAgent = FirstNonEmpty(userAgent, FindHeader(capturedHeaders, "User-Agent"));
            string effectiveCookie = FirstNonEmpty(cookie, FindHeader(capturedHeaders, "Cookie"));
            AppLogger.Log($"LoadEmbed invoked: Title='{title}', EmbedUrl='{embedUrl}', MalId={malId}");
            StopAndRelease();
            _lastMediaSource = embedUrl;
            _lastMediaReferer = effectiveReferer;
            _lastMediaContentType = contentType ?? string.Empty;
            _lastMediaUserAgent = effectiveUserAgent;
            _lastMediaCookie = effectiveCookie;
            _lastMediaRequestHeaders = capturedHeaders;
            _lastEpisodeNumber = episodeNumber;
            _lastTotalEpisodes = totalEpisodes;
            _lastMalId = malId;
            _lastLoadWasWeb = true;
            _episodeContext = episodeContext;
            AudiovisualContext = audiovisualContext;
            _autoAdvanceRequested = false;
            _stopRequested = false;
            MediaTitle = title;
            _currentEpisodeNumber = episodeNumber;
            _currentTotalEpisodes = totalEpisodes;
            UpdateEpisodeNavigationState();
            EpisodeInfo = BuildEpisodeInfo(title, episodeNumber, totalEpisodes);
            ConfigureTracking(malId, episodeNumber, title);
            ConfigureResumeState(malId, episodeNumber, title, embedUrl);
            IsWebViewActive = true;
            BrowserAudioStatus = string.IsNullOrEmpty(BrowserAudioPreference) ? string.Empty
                : $"Selecting {BrowserAudioPreference.ToUpperInvariant()} on the site…";
            IsPlaying = false;
            IsPlaybackBusy = true;
            HasPlaybackError = false;
            PlaybackErrorText = string.Empty;
            PlaybackStatusText = "Opening web player...";
            HasWebFallback = false;
            ResetQualityOptions();
            StartPlaybackStartupWatch(isWeb: true);
            PlayPauseText = Helpers.LocalizationRuntime.Translate("Play");
            // A retry uses the same URL. Still notify the view after the new
            // playback state is ready so it actually reloads the document.
            if (EmbedUrl == embedUrl)
                OnPropertyChanged(nameof(EmbedUrl));
            else
                EmbedUrl = embedUrl;
        }

        internal string BrowserAudioPreference => _episodeContext?.AudioPreference ?? string.Empty;

        internal WebPlaybackRequestContext GetWebPlaybackRequestContext() => new(
            _lastMediaReferer,
            _lastMediaContentType,
            _lastMediaUserAgent,
            _lastMediaCookie,
            _lastMediaRequestHeaders);

        public void ReportWebPageLoaded()
        {
            if (IsDisposed || !IsWebViewActive || IsPlaying || HasPlaybackError) return;
            IsPlaybackBusy = false;
            PlaybackStatusText = "Press Play in the page";
            AppLogger.Log("[Playback startup] Browser document loaded; waiting for video or user interaction.");
        }

        public void ReportWebPlaybackProgress(double currentSeconds, double durationSeconds, bool ended, bool? paused = null)
        {
            if (IsDisposed || !IsWebViewActive)
            {
                return;
            }

            bool credibleVideo = durationSeconds >= MinimumCredibleVideoDurationSeconds;
            if (!IsTabActive && paused == false && !ended)
            {
                PauseForInactiveTab();
                paused = true;
            }
            if (credibleVideo)
            {
                PlaybackDuration = durationSeconds * 1000;
                if (!ended && paused.HasValue)
                {
                    if (!IsPlaying && !paused.Value) RequestProgressOwnership();
                    IsPlaying = !paused.Value;
                    PlaybackStatusText = paused.Value ? "Paused" : "Playing";
                }
            }

            if (currentSeconds >= 0 && (credibleVideo || durationSeconds <= 0))
            {
                PlaybackTime = currentSeconds * 1000;
            }

            if (ended)
            {
                if (_playbackEnded)
                {
                    return;
                }

                if (!IsCredibleCompletion(currentSeconds, durationSeconds))
                {
                    AppLogger.Log($"Ignored a non-credible WebView ended event at {currentSeconds:0.0}s / {durationSeconds:0.0}s (likely an ad or preview).", "WARNING");
                    return;
                }

                _playbackEnded = true;
                IsPlaying = false;
                IsPlaybackBusy = false;
                PlaybackStatusText = "Finished";
                PublishPlaybackAction(CompletionWatchTogetherAction, currentSeconds, hostOnly: true);
            }
            else if (credibleVideo && currentSeconds > 0)
            {
                Interlocked.Increment(ref _startupWatchGeneration);
                IsPlaybackBusy = false;
                HasPlaybackError = false;
                PlaybackErrorText = string.Empty;
                PlaybackStatusText = IsPlaying ? "Playing" : PlaybackStatusText;
            }

            if (credibleVideo)
            {
                TrySyncMalFromProgress(PlaybackTime, PlaybackDuration, ended);
                QueueResumeSave(currentSeconds, ended, force: ended, synchronous: ended);
                if (ended)
                {
                    TryAutoPlayNextEpisode();
                }
            }
        }

        public void ReportWebPlaybackAction(string action, double currentSeconds, double durationSeconds)
        {
            if (IsDisposed || !IsWebViewActive || string.IsNullOrWhiteSpace(action))
            {
                return;
            }

            string normalized = action.Trim().ToLowerInvariant();
            if (!IsTabActive && normalized is "play" or "playing" or "buffer_resume")
            {
                PauseForInactiveTab();
                return;
            }
            if (normalized == "play-blocked")
            {
                IsPlaying = false;
                IsPlaybackBusy = false;
                PlaybackStatusText = "Press Play in the page";
                return;
            }
            bool credibleVideo = durationSeconds >= MinimumCredibleVideoDurationSeconds;
            if (durationSeconds > 0 && !credibleVideo)
            {
                AppLogger.Log($"Ignored WebView '{normalized}' telemetry for a {durationSeconds:0.0}s ad/preview candidate.");
                return;
            }

            if (credibleVideo)
            {
                PlaybackDuration = durationSeconds * 1000;
                if (normalized == "play") RequestProgressOwnership();
            }
            if (normalized is "seek" or "seeked" or "seeking") _resumeSeekApplied = true;

            if (currentSeconds >= 0)
            {
                PlaybackTime = currentSeconds * 1000;
            }

            string syncAction = normalized switch
            {
                "play" => "play",
                "pause" => "pause",
                "seek" or "seeked" or "seeking" => "seek",
                "waiting" or "buffer_pause" => "buffer_pause",
                "playing" or "buffer_resume" => "buffer_resume",
                "ended" => CompletionWatchTogetherAction,
                _ => string.Empty
            };

            if (syncAction is "play" or "buffer_resume")
            {
                Interlocked.Increment(ref _startupWatchGeneration);
                IsPlaying = true;
                IsPlaybackBusy = false;
                HasPlaybackError = false;
                PlaybackErrorText = string.Empty;
                PlaybackStatusText = "Playing";
            }
            else if (syncAction == "buffer_pause")
            {
                IsPlaybackBusy = true;
                PlaybackStatusText = "Buffering...";
            }
            else if (syncAction == "pause")
            {
                IsPlaying = false;
                IsPlaybackBusy = false;
                PlaybackStatusText = "Paused";
            }
            if (normalized == "ended")
            {
                if (_playbackEnded)
                {
                    return;
                }

                if (!IsCredibleCompletion(currentSeconds, durationSeconds))
                {
                    AppLogger.Log($"Ignored a non-credible WebView ended action at {currentSeconds:0.0}s / {durationSeconds:0.0}s.", "WARNING");
                    return;
                }

                _playbackEnded = true;
                IsPlaybackBusy = false;
                PlaybackStatusText = "Finished";
                TrySyncMalFromProgress(PlaybackDuration, PlaybackDuration, ended: true);
                QueueResumeSave(0, ended: true, force: true, synchronous: true);
                PublishPlaybackAction(CompletionWatchTogetherAction, currentSeconds, hostOnly: true);
                TryAutoPlayNextEpisode();
            }
            else if (!string.IsNullOrEmpty(syncAction))
            {
                PublishPlaybackAction(syncAction, currentSeconds);
            }
        }

        internal static bool IsCredibleCompletion(double positionSeconds, double durationSeconds, bool isLocalFile = false)
        {
            if (double.IsNaN(positionSeconds) || double.IsInfinity(positionSeconds) || positionSeconds < 0 ||
                double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds) || durationSeconds < 0)
            {
                return false;
            }

            if (durationSeconds >= MinimumCredibleVideoDurationSeconds)
            {
                return positionSeconds >= durationSeconds * 0.85 ||
                       positionSeconds >= Math.Max(0, durationSeconds - 60);
            }

            // A user-opened/downloaded local file can legitimately be short.
            // Provider and browser sources retain the minimum-duration guard.
            if (isLocalFile && durationSeconds > 0)
                return positionSeconds > 0 && positionSeconds >= durationSeconds * 0.95 &&
                       positionSeconds <= durationSeconds + 2;

            // LibVLC can occasionally omit length for a valid stream. Require meaningful
            // watch time before treating an unknown-length end as episode completion.
            return durationSeconds <= 0 && positionSeconds >= 600;
        }

        public bool TryConsumePendingWebResumePosition(out double seconds)
        {
            seconds = 0;
            if (!IsWebViewActive || _resumeSeekApplied || _pendingResumePositionSeconds <= MinimumResumePositionSeconds)
            {
                return false;
            }

            seconds = _pendingResumePositionSeconds;
            _resumeSeekApplied = true;
            AppLogger.Log($"[Resume] Passing saved WebView position {seconds:0.0}s to playback bridge.");
            return true;
        }

        [RelayCommand]
        private void TogglePlayPause()
        {
            AppLogger.Log("TogglePlayPause command invoked.");
            if (!IsTabActive || _isDisposing || IsDisposed) return;
            if (!IsPlaying) RequestProgressOwnership();
            if (IsWebViewActive)
            {
                _pauseWhenStarted = false;
                string action = IsPlaying ? "pause" : "play";
                WebPlaybackCommandRequested?.Invoke(
                    this,
                    new WebPlaybackCommandEventArgs(action, GetCurrentPlaybackPositionSeconds()));
                // The page confirms playback; dispatching a command alone does
                // not establish that it reached a video or that play succeeded.
                return;
            }

            if ((MediaPlayer.IsPlaying || IsPlaying) && !_pauseWhenStarted)
            {
                AppLogger.Log("Pausing media player.");
                _pauseWhenStarted = true;
                IsPlaying = false;
                MediaPlayer.SetPause(true);
            }
            else
            {
                AppLogger.Log("Starting/resuming media player.");
                _pauseWhenStarted = false;
                if (_playbackEnded) ReloadNativeSelection(null, restart: true);
                else if (!string.IsNullOrEmpty(PendingMediaPath)) PlayPending();
                else MediaPlayer.Play();
            }
        }

        [RelayCommand]
        private void SkipBackward()
        {
            SeekRelative(TimeSpan.FromSeconds(-10));
        }

        [RelayCommand]
        private void SkipForward()
        {
            SeekRelative(TimeSpan.FromSeconds(30));
        }

        [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanGoToPreviousEpisode))]
        private Task PreviousEpisodeAsync()
        {
            return TryGetNavigationEpisode(out int episode)
                ? NavigateToEpisodeAsync(episode - 1) : Task.CompletedTask;
        }

        [RelayCommand(AllowConcurrentExecutions = false, CanExecute = nameof(CanGoToNextEpisode))]
        private Task NextEpisodeAsync()
        {
            return TryGetNavigationEpisode(out int episode)
                ? NavigateToEpisodeAsync(episode + 1) : Task.CompletedTask;
        }

        [RelayCommand]
        private void ToggleCaptions()
        {
            if (_isDisposing || IsDisposed || IsWebViewActive) return;
            RefreshCaptionState();
            if (CaptionOptions.Count <= 1) return;
            if (SelectedCaption is { Key: not "off" })
                SelectedCaption = CaptionOptions.First(option => option.Key == "off");
            else
            {
                // Off/on restores the requested language, rather than cycling
                // to the next language or guessing when that file is absent.
                var requested = _lastEnabledCaptionKey == null
                    ? CaptionOptions.FirstOrDefault(option => option.Key != "off")
                    : UniqueTrack(CaptionOptions, _lastEnabledCaptionKey);
                if (requested != null) SelectedCaption = requested;
            }
        }

        [RelayCommand]
        private void CycleCaptions()
        {
            if (IsWebViewActive)
            {
                return;
            }

            RefreshCaptionState();
            if (CaptionOptions.Count <= 1) return;
            int index = SelectedCaption == null ? -1 : CaptionOptions.IndexOf(SelectedCaption);
            SelectedCaption = CaptionOptions[(index + 1) % CaptionOptions.Count];
        }

        [RelayCommand]
        private void CycleAudioTrack()
        {
            if (IsWebViewActive) return;
            RefreshAudioTrackState();
            if (AudioOptions.Count <= 1) return;
            int index = SelectedAudioTrack == null ? -1 : AudioOptions.IndexOf(SelectedAudioTrack);
            SelectedAudioTrack = AudioOptions[(index + 1) % AudioOptions.Count];
        }

        [RelayCommand]
        private void Stop()
        {
            AppLogger.Log("Stop command invoked.");
            _stopRequested = true;
            StopAndRelease();
            IsWebViewActive = false;
            EmbedUrl = string.Empty;
            EpisodeInfo = string.Empty;
            IsPlaybackBusy = false;
            HasPlaybackError = false;
            PlaybackErrorText = string.Empty;
            PlaybackStatusText = "Stopped";
        }

        [RelayCommand]
        private void RetryPlayback()
        {
            if (string.IsNullOrWhiteSpace(_lastMediaSource))
            {
                ReportPlaybackError("There is no stream to retry.");
                return;
            }

            string title = MediaTitle;
            if (_lastLoadWasWeb)
            {
                LoadEmbed(
                    _lastMediaSource,
                    title,
                    _lastEpisodeNumber,
                    _lastTotalEpisodes,
                    _lastMalId,
                    _episodeContext,
                    _lastMediaReferer,
                    _lastMediaContentType,
                    _lastMediaUserAgent,
                    _lastMediaCookie,
                    _lastMediaRequestHeaders,
                    AudiovisualContext);
            }
            else
            {
                ReloadNativeSelection(SelectedQuality, resumeOnError: true);
            }
        }

        [RelayCommand]
        private void OpenWebFallback()
        {
            if (string.IsNullOrWhiteSpace(_webFallbackUrl))
            {
                return;
            }

            string fallbackUrl = _webFallbackUrl;
            string title = MediaTitle;
            LoadEmbed(fallbackUrl, title, _lastEpisodeNumber, _lastTotalEpisodes, _lastMalId, _episodeContext,
                audiovisualContext: AudiovisualContext);
        }

        public void ReportPlaybackError(string message)
        {
            Interlocked.Increment(ref _startupWatchGeneration);
            string safeMessage = string.IsNullOrWhiteSpace(message)
                ? "Playback failed. Retry the stream or use the browser fallback."
                : message.Trim();

            AppLogger.Log($"Playback error: {safeMessage}", "ERROR");
            RunOnDispatcher(() =>
            {
                IsPlaying = false;
                IsPlaybackBusy = false;
                HasPlaybackError = true;
                PlaybackErrorText = safeMessage;
                PlaybackStatusText = "Playback failed";
            });
        }

        private void StartPlaybackStartupWatch(bool isWeb)
        {
            int generation = Interlocked.Increment(ref _startupWatchGeneration);
            _ = WatchPlaybackStartupAsync(generation, isWeb);
        }

        private async Task WatchPlaybackStartupAsync(int generation, bool isWeb)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            CheckPlaybackStartup(generation, isWeb);
        }

        internal int PlaybackStartupGeneration => Volatile.Read(ref _startupWatchGeneration);

        internal void CheckPlaybackStartup(int generation, bool isWeb)
        {
            RunOnDispatcher(() =>
            {
                if (generation != Volatile.Read(ref _startupWatchGeneration)) return;
                // A hidden tab may still have media queued for its first Play.
                // It has not attempted playback and cannot have timed out.
                if (!isWeb && !string.IsNullOrEmpty(PendingMediaPath)) return;

                if (isWeb)
                {
                    // Missing telemetry is not a failed page. A provider may be
                    // loading, waiting for Play, or hosting video in a child frame.
                    // Keep it interactive; Retry and Stop remain user actions.
                    IsPlaybackBusy = false;
                    PlaybackStatusText = "Still waiting; use Play in the page";
                    AppLogger.Log("[Playback startup] No browser video telemetry after 30 seconds; keeping the page available.", "WARNING");
                    return;
                }

                ReportPlaybackError("The stream did not start within 30 seconds. Retry it or open the browser fallback.");
            });
        }

        private void SeekRelative(TimeSpan offset)
        {
            if (IsWebViewActive)
            {
                double durationMilliseconds = PlaybackDuration;
                long requestedMilliseconds = ClampSeekMilliseconds(
                    (GetCurrentPlaybackPositionSeconds() + offset.TotalSeconds) * 1000.0,
                    durationMilliseconds > 0 ? durationMilliseconds : long.MaxValue);
                double requestedSeconds = requestedMilliseconds / 1000.0;
                _resumeSeekApplied = true;
                PlaybackTime = requestedMilliseconds;
                WebPlaybackCommandRequested?.Invoke(
                    this,
                    new WebPlaybackCommandEventArgs("seek", requestedSeconds));
                PublishPlaybackAction("seek", requestedSeconds);
                return;
            }

            if (MediaPlayer == null)
            {
                return;
            }

            long duration = MediaPlayer.Length > 0 ? MediaPlayer.Length : (long)PlaybackDuration;
            if (duration <= 0)
            {
                return;
            }

            long requested = ClampSeekMilliseconds(MediaPlayer.Time + offset.TotalMilliseconds, duration);
            _resumeSeekApplied = true;
            MediaPlayer.Time = requested;
            PlaybackTime = MediaPlayer.Time;
            AppLogger.Log($"Seeked {offset.TotalSeconds:+0;-0;0}s to {MediaPlayer.Time} ms");
            PublishPlaybackAction("seek", MediaPlayer.Time / 1000.0);
        }

        internal static long ClampSeekMilliseconds(double requestedMilliseconds, double durationMilliseconds)
        {
            if (!double.IsFinite(requestedMilliseconds) ||
                !double.IsFinite(durationMilliseconds) ||
                durationMilliseconds <= 0)
            {
                return 0;
            }

            long duration = durationMilliseconds >= long.MaxValue
                ? long.MaxValue
                : Math.Max(0, (long)durationMilliseconds);
            if (requestedMilliseconds <= 0)
            {
                return 0;
            }

            if (requestedMilliseconds >= duration)
            {
                return duration;
            }

            return (long)requestedMilliseconds;
        }

        private bool TryGetNavigationEpisode(out int episode)
        {
            episode = 0;
            // A title cannot establish a provider unit for a special, an
            // unknown unit or a source outside this entry's numbered range.
            return _episodeContext != null &&
                int.TryParse(_currentEpisodeNumber, NumberStyles.None, CultureInfo.InvariantCulture, out episode) &&
                episode >= _episodeContext.FirstEpisode && episode <= _episodeContext.LastEpisode;
        }

        private void CancelEpisodeNavigation()
        {
            CancellationTokenSource? pending = _episodeNavigationCts;
            if (pending == null) return;
            _episodeNavigationCts = null;
            pending.Cancel();
            IsEpisodeNavigationBusy = false;
            // The asynchronous operation owns disposal after its resolver
            // returns; cancellation must not invalidate its captured token.
        }

        private async Task NavigateToEpisodeAsync(int episode)
        {
            EpisodePlaybackContext? context = _episodeContext;
            if (_isDisposing || IsDisposed || context == null || episode < context.FirstEpisode || episode > context.LastEpisode)
            {
                return;
            }

            CancelEpisodeNavigation();
            var cts = new CancellationTokenSource();
            _episodeNavigationCts = cts;
            CancellationToken token = cts.Token;
            int generation = Volatile.Read(ref _playbackGeneration);
            bool IsCurrent() => !_isDisposing && !IsDisposed && !token.IsCancellationRequested &&
                ReferenceEquals(_episodeNavigationCts, cts) && ReferenceEquals(_episodeContext, context) &&
                generation == Volatile.Read(ref _playbackGeneration);
            IsEpisodeNavigationBusy = true;
            HasPlaybackError = false;
            PlaybackErrorText = string.Empty;
            IsPlaybackBusy = true;
            PlaybackStatusText = $"Resolving episode {episode}...";
            bool resolutionAccepted = false;

            try
            {
                ResolvedEpisodePlayback? resolved = await context.ResolveAsync(episode, token);
                if (!IsCurrent()) return;
                if (resolved == null || string.IsNullOrWhiteSpace(resolved.Source))
                {
                    ReportPlaybackError(context.AudioPreference == "dub"
                        ? $"No playable dub was found for episode {episode}."
                        : $"No playable source was found for episode {episode}.");
                    return;
                }

                // The completed lookup is no longer pending when its source
                // is handed to the player. Do not cancel its token as part of
                // that same successful load.
                resolutionAccepted = true;
                _episodeNavigationCts = null;
                IsEpisodeNavigationBusy = false;
                string episodeNumber = episode.ToString(CultureInfo.InvariantCulture);
                if (resolved.IsWebView)
                {
                    LoadEmbed(
                        resolved.Source,
                        resolved.Title,
                        episodeNumber,
                        _lastTotalEpisodes,
                        _lastMalId,
                        context,
                        referer: resolved.Referer);
                }
                else
                {
                    LoadMedia(
                        resolved.Source,
                        resolved.Title,
                        resolved.Referer,
                        episodeNumber,
                        _lastTotalEpisodes,
                        _lastMalId,
                        context,
                        subtitles: resolved.Subtitles,
                        audioNotice: resolved.AudioNotice);
                    PlayPending();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // A newer navigation request or tab disposal superseded this one.
            }
            catch (Exception ex)
            {
                if (!IsCurrent() && !resolutionAccepted) return;
                if (_isDisposing || IsDisposed) return;
                AppLogger.Log($"Episode {episode} navigation failed: {ex.Message}", "ERROR");
                ReportPlaybackError($"Episode {episode} could not be opened: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_episodeNavigationCts, cts))
                {
                    _episodeNavigationCts = null;
                    IsEpisodeNavigationBusy = false;
                }
                cts.Dispose();
            }
        }

        private void TryAutoPlayNextEpisode()
        {
            if (!IsTabActive || _autoAdvanceRequested || !AutoPlayNext || !CanGoToNextEpisode || IsDisposed)
            {
                return;
            }

            _autoAdvanceRequested = true;
            if (!TryGetNavigationEpisode(out int currentEpisode)) return;
            int nextEpisode = currentEpisode + 1;
            RunOnDispatcher(() => _ = NavigateToEpisodeAsync(nextEpisode));
        }

        private void UpdateEpisodeNavigationState()
        {
            OnPropertyChanged(nameof(CanGoToPreviousEpisode));
            OnPropertyChanged(nameof(CanGoToNextEpisode));
            PreviousEpisodeCommand.NotifyCanExecuteChanged();
            NextEpisodeCommand.NotifyCanExecuteChanged();
        }

        private void ResetQualityOptions()
        {
            Interlocked.Increment(ref _qualityDiscoveryGeneration);
            _suppressQualitySelection = true;
            try
            {
                QualityOptions.Clear();
                SelectedQuality = null;
                IsQualitySelectionAvailable = false;
                _activeQualityUrl = string.Empty;
            }
            finally
            {
                _suppressQualitySelection = false;
            }
        }

        private void BeginQualityDiscovery(string source)
        {
            ResetQualityOptions();
            if (LooksLikeDashSource(_lastMediaSource, _lastMediaContentType) || _hlsProxy?.OwnsStreamUrl(source) != true)
            {
                return;
            }

            int generation = Volatile.Read(ref _qualityDiscoveryGeneration);
            _ = DiscoverQualityOptionsAsync(source, generation);
        }

        private async Task DiscoverQualityOptionsAsync(string masterUrl, int generation)
        {
            try
            {
                string manifest = await QualityManifestClient.GetStringAsync(masterUrl);
                IReadOnlyList<PlaybackQualityOption> variants = ParseHlsQualityOptions(manifest);
                if (variants.Count < 2 || generation != Volatile.Read(ref _qualityDiscoveryGeneration) || IsDisposed)
                {
                    return;
                }

                RunOnDispatcher(() =>
                {
                    if (generation != Volatile.Read(ref _qualityDiscoveryGeneration) || IsDisposed)
                    {
                        return;
                    }

                    _suppressQualitySelection = true;
                    try
                    {
                        QualityOptions.Clear();
                        var automatic = new PlaybackQualityOption("Auto", masterUrl);
                        QualityOptions.Add(automatic);
                        foreach (PlaybackQualityOption option in variants)
                        {
                            QualityOptions.Add(manifest.Contains("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase)
                                ? option with { Url = _hlsProxy!.CreateVariantUrl(masterUrl, option.Url) }
                                : option);
                        }

                        _activeQualityUrl = masterUrl;
                        SelectedQuality = automatic;
                        IsQualitySelectionAvailable = true;
                    }
                    finally
                    {
                        _suppressQualitySelection = false;
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Log($"HLS quality discovery was unavailable: {ex.Message}", "WARNING");
            }
        }

        private void ReloadNativeSelection(PlaybackQualityOption? option, bool resumeOnError = false, bool restart = false)
        {
            if (_isDisposing || IsDisposed || IsWebViewActive || string.IsNullOrWhiteSpace(_lastMediaSource)) return;
            double positionSeconds = restart ? 0 : GetCurrentPlaybackPositionSeconds();
            bool paused = !IsTabActive || (!restart &&
                (_pauseWhenStarted || (!IsPlaying && !(resumeOnError && HasPlaybackError))));
            PlaybackQualityOption[] options = QualityOptions.ToArray();
            string source = option?.Url ?? _lastMediaSource;
            // No deferred continuation may replace a newer episode or a disposed player.
            // Set seek/pause state before PendingMediaPath can start the new input.
            LoadMedia(source, MediaTitle, _lastMediaReferer, _lastEpisodeNumber, _lastTotalEpisodes,
                _lastMalId, _episodeContext, _lastMediaContentType, _lastMediaUserAgent, _lastMediaCookie,
                _lastMediaRequestHeaders, AudiovisualContext, _lastMediaSubtitles, AudioNotice,
                reloadPositionSeconds: positionSeconds, pauseAfterReload: paused);
            if (options.Length > 0)
            {
                Interlocked.Increment(ref _qualityDiscoveryGeneration);
                _suppressQualitySelection = true;
                try
                {
                    QualityOptions.Clear();
                    foreach (PlaybackQualityOption quality in options) QualityOptions.Add(quality);
                    _activeQualityUrl = source;
                    SelectedQuality = QualityOptions.FirstOrDefault(candidate => candidate.Url == source);
                    IsQualitySelectionAvailable = QualityOptions.Count > 2;
                }
                finally { _suppressQualitySelection = false; }
            }
            PlayPending();
        }

        internal bool RedrawPausedNativeFrame()
        {
            if (_isDisposing || IsDisposed || IsWebViewActive || !IsTabActive || IsPlaying ||
                IsSeekingFromSlider || MediaPlayer.State != VLCState.Paused || !MediaPlayer.IsSeekable)
                return false;
            long position = MediaPlayer.Time;
            if (position < 0) return false;
            // Moving a paused Direct3D surface between displays can leave it
            // black. Seek the existing input to its own clock to decode another
            // frame, without starting playback or publishing a user seek.
            MediaPlayer.Time = position;
            AppLogger.Log($"[PlaybackView] Redrew paused native frame at {position} ms after display change.");
            return true;
        }

        internal static IReadOnlyList<PlaybackQualityOption> ParseHlsQualityOptions(string manifest)
        {
            if (string.IsNullOrWhiteSpace(manifest) ||
                !manifest.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<PlaybackQualityOption>();
            }

            string[] lines = manifest.Replace("\r", string.Empty).Split('\n');
            var variants = new List<PlaybackQualityOption>();
            string? streamInfo = null;
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                {
                    streamInfo = line;
                    continue;
                }

                if (streamInfo == null || line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                Match resolution = Regex.Match(streamInfo, @"(?:^|[:,])RESOLUTION=\d+x(?<height>\d+)", RegexOptions.IgnoreCase);
                Match bandwidthMatch = Regex.Match(streamInfo, @"(?:^|[:,])(?:AVERAGE-)?BANDWIDTH=(?<rate>\d+)", RegexOptions.IgnoreCase);
                int height = resolution.Success && int.TryParse(resolution.Groups["height"].Value, out int parsedHeight)
                    ? parsedHeight
                    : 0;
                long bandwidth = bandwidthMatch.Success && long.TryParse(bandwidthMatch.Groups["rate"].Value, out long parsedRate)
                    ? parsedRate
                    : 0;
                string label = height > 0
                    ? $"{height}p"
                    : bandwidth > 0
                        ? $"{bandwidth / 1_000_000d:0.#} Mbps"
                        : "Variant";
                variants.Add(new PlaybackQualityOption(label, line, height, bandwidth));
                streamInfo = null;
            }

            return variants
                .Where(variant => Uri.TryCreate(variant.Url, UriKind.Absolute, out _))
                .GroupBy(variant => variant.Height > 0 ? $"h:{variant.Height}" : $"u:{variant.Url}", StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(variant => variant.Bandwidth).First())
                .OrderByDescending(variant => variant.Height)
                .ThenByDescending(variant => variant.Bandwidth)
                .ToArray();
        }

        public void StopAndRelease(bool saveResume = true)
        {
            if (IsDisposed)
            {
                return;
            }

            AppLogger.Log("Releasing media player stream resources...");
            try
            {
                CancelEpisodeNavigation();
                Interlocked.Increment(ref _startupWatchGeneration);
                _stopRequested = true;
                if (saveResume)
                {
                    SaveCurrentResumePosition(force: true, synchronous: false);
                }
                Interlocked.Increment(ref _playbackGeneration);

                if (MediaPlayer.IsPlaying || _currentMedia != null || _pendingMedia != null)
                {
                    MediaPlayer.Stop();
                }

                ReleaseCurrentMedia();
                PendingMediaPath = string.Empty;
                PlaybackTime = 0;
                PlaybackDuration = 0;
                CaptionsAvailable = false;
                CaptionButtonText = Helpers.LocalizationRuntime.Translate("CC");
                AudioTracksAvailable = false;
                AudioButtonText = "Audio";
                AppLogger.Log("Media player stream resources successfully released.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Error during media release: {ex.Message}", "WARNING");
            }
        }

        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            RunOnDispatcher(() =>
            {
                if (_stopRequested || IsWebViewActive) return;
                double positionSeconds = GetCurrentPlaybackPositionSeconds();
                double durationSeconds = (MediaPlayer.Length > 0 ? MediaPlayer.Length : PlaybackDuration) / 1000.0;
                bool isLocalFile = Uri.TryCreate(_currentMedia?.Mrl, UriKind.Absolute, out var source) && source.IsFile;
                if (!IsCredibleCompletion(positionSeconds, durationSeconds, isLocalFile))
                {
                    AppLogger.Log($"LibVLC ended before a credible episode completion ({positionSeconds:0.0}s / {durationSeconds:0.0}s).", "WARNING");
                    ReportPlaybackError(positionSeconds > 0
                        ? "The video ended, but completion could not be verified. Retry it or choose another source."
                        : "The stream ended before video playback began. Retry it or open the browser fallback.");
                    return;
                }

                _playbackEnded = true;
                IsPlaying = false;
                IsPlaybackBusy = false;
                PlaybackStatusText = "Finished";
                TrySyncMalFromProgress(PlaybackDuration, PlaybackDuration, ended: true);
                QueueResumeSave(0, ended: true, force: true, synchronous: true);
                PublishPlaybackAction(CompletionWatchTogetherAction, durationSeconds, hostOnly: true);
                TryAutoPlayNextEpisode();
            });
        }

        private void ReleaseCurrentMedia(string? preparedSource = null)
        {
            if (_pendingMedia != null && !ReferenceEquals(_pendingMedia, _currentMedia))
            {
                _pendingMedia.Dispose();
            }

            _pendingMedia = null;
            _currentMedia?.Dispose();
            _currentMedia = null;
            foreach (string sessionId in _mediaProxySessions.ToArray())
            {
                if (preparedSource != null && _hlsProxy?.UsesSession(preparedSource, sessionId) == true) continue;
                _hlsProxy?.UnregisterSession(sessionId);
                _mediaProxySessions.Remove(sessionId);
            }
            foreach (string sessionId in _subtitleProxySessions)
                _hlsProxy?.UnregisterSession(sessionId);
            _subtitleProxySessions.Clear();
        }

        private static PlaybackTrackOption? UniqueTrack(IReadOnlyList<PlaybackTrackOption> choices, string? key) =>
            choices.Count(choice => choice.Key == key) == 1 ? choices.First(choice => choice.Key == key) : null;

        private static void UpdateTrackOptions(ObservableCollection<PlaybackTrackOption> target,
            IReadOnlyList<PlaybackTrackOption> options)
        {
            if (target.SequenceEqual(options)) return;
            target.Clear();
            foreach (var option in options) target.Add(option);
        }

        private IReadOnlyList<PlaybackTrackOption> NativeTrackOptions(TrackType type)
        {
            var tracks = type == TrackType.Text ? MediaPlayer.SpuDescription : MediaPlayer.AudioTrackDescription;
            var metadata = _currentMedia?.Tracks ?? [];
            return (tracks ?? []).Where(track => track.Id >= 0).Select(track =>
            {
                var native = metadata.Where(item => item.TrackType == type && item.Id == track.Id)
                    .Select(item => (MediaTrack?)item).FirstOrDefault();
                return PlaybackTrackOption.Native(track.Id, track.Name ?? "", native?.Language ?? "",
                    native?.Description ?? "", type == TrackType.Text ? "Subtitle" : "Audio");
            }).ToArray();
        }

        private void RefreshCaptionState()
        {
            if (_isDisposing || IsDisposed) return;
            RefreshAudioTrackState();
            _suppressTrackSelection = true;
            try
            {
                if (IsWebViewActive)
                {
                    CaptionOptions.Clear(); SelectedCaption = null; CaptionsAvailable = false;
                    CaptionButtonText = Helpers.LocalizationRuntime.Translate("Site CC");
                    return;
                }
                // Provider captions are selected by their actual URL; native captions use
                // decoded stream IDs only when no provider files were supplied.
                var choices = _currentMedia == null ? [] : _lastMediaSubtitles.Count > 0
                    ? PlaybackTrackOption.ExternalCaptions(_lastMediaSubtitles) : NativeTrackOptions(TrackType.Text);
                var off = new PlaybackTrackOption("off", Helpers.LocalizationRuntime.Translate("CC Off"));
                UpdateTrackOptions(CaptionOptions, new[] { off }.Concat(choices).ToArray());
                CaptionsAvailable = choices.Count > 0;
                if (_lastMediaSubtitles.Count > 0)
                {
                    if (_attachedCaption == null) MediaPlayer.SetSpu(-1);
                    SelectedCaption = _attachedCaption == null ? off : CaptionOptions.FirstOrDefault(choice => choice.Key == _attachedCaption.Key);
                }
                else
                {
                    if (_restoreCaptionSelection)
                    {
                        var preferred = _preferredCaptionKey == "off" ? off : UniqueTrack(choices, _preferredCaptionKey);
                        if (preferred != null && MediaPlayer.SetSpu(preferred.NativeId)) _restoreCaptionSelection = false;
                    }
                    SelectedCaption = CaptionOptions.FirstOrDefault(choice => choice.NativeId == MediaPlayer.Spu);
                }
                CaptionButtonText = SelectedCaption == null || SelectedCaption.Key == "off"
                    ? off.Label : $"CC {SelectedCaption.Label}";
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Unable to inspect caption tracks: {ex.Message}", "WARNING");
                CaptionsAvailable = false;
            }
            finally { _suppressTrackSelection = false; }
        }

        private void RefreshAudioTrackState()
        {
            if (_isDisposing || IsDisposed) return;
            _suppressTrackSelection = true;
            try
            {
                var choices = IsWebViewActive || _currentMedia == null ? [] : NativeTrackOptions(TrackType.Audio);
                UpdateTrackOptions(AudioOptions, choices);
                AudioTracksAvailable = choices.Count > 1;
                if (_restoreAudioSelection && _preferredAudioKey != null)
                {
                    var preferred = UniqueTrack(choices, _preferredAudioKey);
                    if (preferred != null && MediaPlayer.SetAudioTrack(preferred.NativeId)) _restoreAudioSelection = false;
                }
                SelectedAudioTrack = choices.FirstOrDefault(choice => choice.NativeId == MediaPlayer.AudioTrack);
                AudioButtonText = IsWebViewActive ? "Site Audio" : SelectedAudioTrack == null ? "Audio" : $"Audio {SelectedAudioTrack.Label}";
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Unable to inspect audio tracks: {ex.Message}", "WARNING");
                AudioTracksAvailable = false;
            }
            finally { _suppressTrackSelection = false; }
        }

        private void LocalizationRuntime_LanguageChanged(object? sender, EventArgs e)
        {
            RunOnDispatcher(RefreshLocalizedPlaybackText);
        }

        private void RefreshLocalizedPlaybackText()
        {
            PlayPauseText = Helpers.LocalizationRuntime.Translate(IsWebViewActive ? "Web" : IsPlaying ? "Pause" : "Play");
            RefreshCaptionState();

            if (!string.IsNullOrWhiteSpace(MediaTitle))
            {
                EpisodeInfo = BuildEpisodeInfo(MediaTitle, _currentEpisodeNumber, _currentTotalEpisodes);
            }
        }

        private static string BuildEpisodeInfo(string title, string episodeNumber, int totalEpisodes)
        {
            string episode = string.IsNullOrWhiteSpace(episodeNumber)
                ? ExtractEpisodeNumber(title)
                : episodeNumber.Trim();

            if (string.IsNullOrWhiteSpace(episode))
            {
                return totalEpisodes > 0
                    ? $"{totalEpisodes} {Helpers.LocalizationRuntime.Translate("episodes")}"
                    : string.Empty;
            }

            string episodeLabel = Helpers.LocalizationRuntime.Translate("Episode");
            string ofLabel = Helpers.LocalizationRuntime.Translate("of");
            return totalEpisodes > 0
                ? $"{episodeLabel} {episode} {ofLabel} {totalEpisodes}"
                : $"{episodeLabel} {episode}";
        }

        private static string ExtractEpisodeNumber(string title)
        {
            const string marker = " - Ep ";
            int index = title.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return index >= 0 ? title[(index + marker.Length)..].Trim() : string.Empty;
        }

        private void ConfigureTracking(int malId, string episodeNumber, string title)
        {
            int episode = ResolveEpisodeNumber(episodeNumber, title);
            lock (_malProgressGate)
            {
                if (_malId == malId && _trackingEpisodeNumber == episode) return;
                _malProgressSyncCts?.Cancel();
                _malProgressSyncCts = null;
                _malProgressGeneration++;
                _malId = malId;
                _trackingEpisodeNumber = episode;
                _malProgressSynced = false;
                _lastMalProgressCheck = DateTime.MinValue;
            }
        }

        private void ConfigureResumeState(int malId, string episodeNumber, string title, string sourceKey)
        {
            lock (_resumePersistenceGate)
            {
                // A source with accepted progress still needs its read/session
                // for that captured write. Unused superseded reads can leave
                // the queue without touching the ordered saves around them.
                if (!_resumeLoadHasAcceptedSave) _resumeLoadCancellation?.Cancel();
                _resumeLoadCancellation?.Dispose();
                _resumeLoadCancellation = null;
                _resumeLoadHasAcceptedSave = false;
            }
            _progressSessionReady = Task.FromResult<PlaybackProgressSession?>(null);
            _progressContext = null;
            _resumePersistenceClosed = false;
            _restoringCurrentPosition = false;
            _playbackEnded = false;
            _resumeSeekApplied = false;
            _pendingResumePositionSeconds = 0;
            _lastSavedResumeSeconds = double.NaN;
            _lastResumeSaveUtc = DateTime.MinValue;
            _resumeLoadRead = Task.FromResult<(PlaybackProgressSession?, double)>((null, 0));
            ResumeLoadCompleted = Task.CompletedTask;
            _resumeReadApplied = false;
            _playWhenResumeLoaded = false;
            if (AudiovisualContext is { } audiovisual)
            {
                _resumeMediaId = audiovisual.WorkKey;
                // An unresolved unit must not fall through to title/URL guessing.
                _resumeEpisodeId = audiovisual.UnitKey ?? string.Empty;
                if (audiovisual.UnitKey != null)
                    _progressContext = new(audiovisual.WorkKey, audiovisual.UnitKey, audiovisual);
            }
            else if (_episodeContext?.ResumeIdentity != null)
            {
                _progressContext = _episodeContext.CreateProgressContext(episodeNumber);
                _resumeMediaId = _progressContext?.WorkKey ?? string.Empty;
                _resumeEpisodeId = _progressContext?.UnitKey ?? string.Empty;
            }
            else
            {
                _resumeMediaId = ResolveResumeMediaId(malId, title, sourceKey);
                _resumeEpisodeId = ResolveResumeEpisodeId(episodeNumber, title, sourceKey);
            }

            if (!HasResumeKey())
            {
                _resumeReadApplied = true;
                AppLogger.Log("[Resume] Resume tracking skipped because no stable media key was available.", "WARNING");
                return;
            }

            LoadResumePosition();
        }

        private static int ResolveEpisodeNumber(string episodeNumber, string title)
        {
            if (int.TryParse(episodeNumber, out int parsed) && parsed > 0)
            {
                return parsed;
            }

            string extracted = ExtractEpisodeNumber(title);
            return int.TryParse(extracted, out parsed) && parsed > 0 ? parsed : 0;
        }

        private static string ResolveResumeMediaId(int malId, string title, string sourceKey)
        {
            if (malId > 0)
            {
                return malId.ToString(CultureInfo.InvariantCulture);
            }

            string titleWithoutEpisode = StripEpisodeSuffix(title);
            if (!string.IsNullOrWhiteSpace(titleWithoutEpisode))
            {
                return "title:" + titleWithoutEpisode.Trim().ToLowerInvariant();
            }

            return string.IsNullOrWhiteSpace(sourceKey)
                ? string.Empty
                : "source:" + StableHash(sourceKey);
        }

        private static string ResolveResumeEpisodeId(string episodeNumber, string title, string sourceKey)
        {
            string episode = string.IsNullOrWhiteSpace(episodeNumber)
                ? ExtractEpisodeNumber(title)
                : episodeNumber.Trim();

            if (!string.IsNullOrWhiteSpace(episode))
            {
                return episode;
            }

            return string.IsNullOrWhiteSpace(sourceKey)
                ? "default"
                : "source:" + StableHash(sourceKey);
        }

        private static string StripEpisodeSuffix(string title)
        {
            const string marker = " - Ep ";
            int index = title.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return index >= 0 ? title[..index] : title;
        }

        private static string StableHash(string value)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
            return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
        }

        private bool HasResumeKey()
        {
            return !string.IsNullOrWhiteSpace(_resumeMediaId) &&
                   !string.IsNullOrWhiteSpace(_resumeEpisodeId);
        }

        private void LoadResumePosition()
        {
            if (!HasResumeKey()) return;
            string media = _resumeMediaId;
            string unit = _resumeEpisodeId;
            int generation = Volatile.Read(ref _playbackGeneration);
            var dispatcher = App.Current?.Dispatcher ??
                System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
            lock (_resumePersistenceGate)
            {
                // Freeze older-unit observations before reserving the ordered read.
                _queuedResumeSave = null;
                _pendingResumeLoads.RemoveAll(task => task.IsCompleted);
                // A source reload must read after the old unit's accepted saves,
                // including a save which itself awaits that unit's first load.
                Task preceding = Task.WhenAll(_pendingResumeSaves.Values.Concat(_pendingResumeLoads));
                _resumeLoadCancellation = new CancellationTokenSource();
                _resumeLoadRead = ReadResumePositionAsync(_progressContext, media, unit, preceding, _resumeLoadCancellation.Token);
                _progressSessionReady = LoadedSessionAsync(_resumeLoadRead);
                _pendingResumeLoads.Add(_resumeLoadRead);
                _playbackProgress.TrackPendingPersistence(_resumeLoadRead);
                ResumeLoadCompleted = ApplyLoadedResumeAsync(_resumeLoadRead, generation, media, unit, dispatcher);
            }
        }

        private async Task<(PlaybackProgressSession? Session, double Position)> ReadResumePositionAsync(
            PlaybackProgressContext? context, string media, string unit, Task preceding, CancellationToken cancellationToken)
        {
            try
            {
                if (context != null) return await _playbackProgress.OpenAsync(context, preceding, cancellationToken).ConfigureAwait(false);
                _ = _databaseContext.Database.GetDbConnection(); // Freeze this player's destination before yielding.
                await preceding.WaitAsync(cancellationToken).ConfigureAwait(false);
                await _resumeDatabaseLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    double position = await Task.Run(() => EnsureResumeDatabaseCreated()
                        ? _databaseContext.GetResumeState(media, unit) : 0, cancellationToken).ConfigureAwait(false);
                    return (null, position);
                }
                finally { _resumeDatabaseLock.Release(); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return (null, 0);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Resume] Failed to load resume state: {ex.Message}", "WARNING");
                return (null, 0);
            }
        }

        private async Task ApplyLoadedResumeAsync(Task<(PlaybackProgressSession? Session, double Position)> read,
            int generation, string media, string unit, System.Windows.Threading.Dispatcher? dispatcher)
        {
            var loaded = await read.ConfigureAwait(false);
            void Apply()
            {
                if (_isDisposing || IsDisposed || generation != Volatile.Read(ref _playbackGeneration) ||
                    media != _resumeMediaId || unit != _resumeEpisodeId) return;
                // An explicit reload/seek or completed state wins over a late read.
                if (!_restoringCurrentPosition && !_resumeSeekApplied && !_playbackEnded &&
                    loaded.Position > MinimumResumePositionSeconds)
                    _pendingResumePositionSeconds = loaded.Position;
                AppLogger.Log($"[Resume] Loaded saved position {loaded.Position:0.0}s for media '{media}', episode '{unit}'.");
                _resumeReadApplied = true;
                if (_playWhenResumeLoaded && IsTabActive) PlayPending();
                ApplyPendingResumeSeek();
            }
            if (dispatcher is { HasShutdownStarted: false }) await dispatcher.InvokeAsync(Apply);
            else if (dispatcher == null) Apply();
        }

        private void ApplyPendingResumeSeek()
        {
            if (IsDisposed || IsWebViewActive || _resumeSeekApplied || _pendingResumePositionSeconds <= 0)
            {
                return;
            }

            if (MediaPlayer == null)
            {
                return;
            }

            long durationMilliseconds = MediaPlayer.Length > 0 ? MediaPlayer.Length : (long)PlaybackDuration;
            if (durationMilliseconds <= 0)
            {
                return;
            }

            double durationSeconds = durationMilliseconds / 1000.0;
            if (!_restoringCurrentPosition && _pendingResumePositionSeconds >= Math.Max(0, durationSeconds - ResumeEndToleranceSeconds))
            {
                AppLogger.Log($"[Resume] Ignored saved position {_pendingResumePositionSeconds:0.0}s because it is too close to the end of '{MediaTitle}'.");
                _resumeSeekApplied = true;
                return;
            }

            long targetMilliseconds = Math.Max(0, (long)(_pendingResumePositionSeconds * 1000));
            try
            {
                MediaPlayer.Time = targetMilliseconds;
                PlaybackTime = targetMilliseconds;
                _resumeSeekApplied = true;
                AppLogger.Log($"[Resume] Restored '{MediaTitle}' to {_pendingResumePositionSeconds:0.0}s.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Resume] Failed to seek to saved position: {ex.Message}", "WARNING");
            }
        }

        private void SaveCurrentResumePosition(bool force, bool synchronous)
        {
            if (_playbackEnded)
            {
                return;
            }

            double positionSeconds = GetCurrentPlaybackPositionSeconds();
            QueueResumeSave(positionSeconds, ended: false, force: force, synchronous: synchronous);
        }

        private double GetCurrentPlaybackPositionSeconds()
        {
            if (IsWebViewActive)
            {
                return PlaybackTime / 1000.0;
            }

            try
            {
                if (MediaPlayer != null && MediaPlayer.Time > 0)
                {
                    return MediaPlayer.Time / 1000.0;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Resume] Failed to read player time for resume save: {ex.Message}", "WARNING");
            }

            return PlaybackTime / 1000.0;
        }

        private void QueueResumeSave(double positionSeconds, bool ended, bool force, bool synchronous)
        {
            Task save;
            lock (_resumePersistenceGate)
            {
                if (_resumePersistenceClosed || !HasResumeKey() || (_playbackEnded && !ended))
                {
                    return;
                }

                double valueToSave = ended ? 0 : positionSeconds;
                if (!ended && valueToSave <= MinimumResumePositionSeconds)
                {
                    return;
                }

                DateTime now = DateTime.UtcNow;
                string mediaId = _resumeMediaId;
                string episodeId = _resumeEpisodeId;
                if (!ended && !force)
                {
                    if (now - _lastResumeSaveUtc < ResumeSaveInterval)
                    {
                        return;
                    }

                    if (!double.IsNaN(_lastSavedResumeSeconds) &&
                        Math.Abs(valueToSave - _lastSavedResumeSeconds) < ResumeSaveDeltaSeconds)
                    {
                        return;
                    }
                }

                _lastResumeSaveUtc = now;
                _lastSavedResumeSeconds = valueToSave;
                _resumeLoadHasAcceptedSave = true;
                long sequence = Interlocked.Increment(ref _resumeWriteSequence);
                _latestResumeWrites.AddOrUpdate((mediaId, episodeId), sequence, (_, previous) => Math.Max(previous, sequence));

                var snapshot = new ResumeSaveSnapshot(mediaId, episodeId, valueToSave,
                    Math.Max(0, PlaybackDuration / 1000.0), ended, sequence, DateTimeOffset.UtcNow);
                if (_queuedResumeSave is { Captured: false } queued &&
                    queued.Snapshot.MediaId == mediaId && queued.Snapshot.EpisodeId == episodeId)
                {
                    // Already accepted ordering stays fixed; only its superseded
                    // observation changes. No additional context/task is queued.
                    queued.Snapshot = snapshot;
                    save = queued.Completion;
                }
                else
                {
                    var slot = new ResumeSaveSlot(snapshot);
                    _queuedResumeSave = slot;
                    save = _progressContext != null
                        ? SaveCatalogProgressAfterLoadAsync(_progressSessionReady, slot)
                        : SaveLegacyProgressAfterLoadAsync(_resumeLoadRead, slot);
                    slot.Completion = save;
                    foreach (long finished in _pendingResumeSaves.Where(entry => entry.Value.IsCompleted).Select(entry => entry.Key).ToArray())
                        _pendingResumeSaves.Remove(finished);
                    if (!save.IsCompleted) _pendingResumeSaves[sequence] = save;
                    _playbackProgress.TrackPendingPersistence(save);
                }
            }

            if (synchronous)
            {
                try { save.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); }
                catch (TimeoutException) { AppLogger.Log("[Resume] Final save is still finishing in the background.", "WARNING"); }
            }
        }

        private ResumeSaveSnapshot? CaptureQueuedResumeSave(ResumeSaveSlot slot)
        {
            lock (_resumePersistenceGate)
            {
                slot.Captured = true;
                if (ReferenceEquals(_queuedResumeSave, slot)) _queuedResumeSave = null;
                var snapshot = slot.Snapshot;
                return _latestResumeWrites.GetValueOrDefault((snapshot.MediaId, snapshot.EpisodeId)) == snapshot.Sequence
                    ? snapshot : null;
            }
        }

        private async Task SaveCatalogProgressAfterLoadAsync(Task<PlaybackProgressSession?> session, ResumeSaveSlot slot)
        {
            try
            {
                await _playbackProgress.SaveWhenOpenedAsync(session, () =>
                {
                    var snapshot = CaptureQueuedResumeSave(slot);
                    return snapshot == null ? null : new PlaybackProgressObservation(snapshot.Position,
                        snapshot.Duration, snapshot.Ended, snapshot.ObservedUtc);
                }).ConfigureAwait(false);
            }
            catch (Exception ex) { AppLogger.Log($"[Resume] Failed to save catalog progress: {ex.Message}", "WARNING"); }
        }

        private static async Task<PlaybackProgressSession?> LoadedSessionAsync(
            Task<(PlaybackProgressSession? Session, double Position)> read) => (await read.ConfigureAwait(false)).Session;

        private void RequestProgressOwnership()
        {
            if (_progressContext == null) return;
            lock (_resumePersistenceGate)
            {
                // A later explicit owner must not move an accepted write across it.
                _queuedResumeSave = null;
                _pendingResumeLoads.RemoveAll(task => task.IsCompleted);
                _progressSessionReady = _playbackProgress.TakeOwnershipWhenOpenedAsync(_progressSessionReady);
                _pendingResumeLoads.Add(_progressSessionReady);
                _playbackProgress.TrackPendingPersistence(_progressSessionReady);
            }
        }

        internal Task FlushResumeAsync()
        {
            if (IsDisposed || _isDisposing) return Task.CompletedTask;
            SaveCurrentResumePosition(force: true, synchronous: false);
            lock (_resumePersistenceGate)
                return Task.WhenAll(_pendingResumeSaves.Values.Concat(_pendingResumeLoads));
        }

        private async Task SaveLegacyProgressAfterLoadAsync(Task<(PlaybackProgressSession? Session, double Position)> read,
            ResumeSaveSlot slot)
        {
            await read.ConfigureAwait(false);
            await SaveResumePositionAsync(slot).ConfigureAwait(false);
        }

        private async Task SaveResumePositionAsync(ResumeSaveSlot slot)
        {
            try
            {
                await _resumeDatabaseLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await Task.Run(() =>
                    {
                        var snapshot = CaptureQueuedResumeSave(slot);
                        if (snapshot == null || !EnsureResumeDatabaseCreated()) return;
                        if (_latestResumeWrites.GetValueOrDefault((snapshot.MediaId, snapshot.EpisodeId)) != snapshot.Sequence) return;
                        // Captured writes remain valid after the player closes. Its
                        // database and semaphore live until every accepted save ends.
                        _databaseContext.SaveResumeState(snapshot.MediaId, snapshot.EpisodeId, snapshot.Position);
                        AppLogger.Log(snapshot.Ended
                            ? $"[Resume] Cleared saved position for media '{snapshot.MediaId}', episode '{snapshot.EpisodeId}'."
                            : $"[Resume] Saved position {snapshot.Position:0.0}s for media '{snapshot.MediaId}', episode '{snapshot.EpisodeId}'.");
                    }).ConfigureAwait(false);
                }
                finally
                {
                    _resumeDatabaseLock.Release();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Resume] Failed to save resume state asynchronously: {ex.Message}", "WARNING");
            }
        }

        private bool EnsureResumeDatabaseCreated()
        {
            try
            {
                _databaseContext.Database.EnsureCreated();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Resume] Failed to initialize resume database: {ex.Message}", "WARNING");
                return false;
            }
        }

        private void CloseResumePersistence()
        {
            Task pending;
            lock (_resumePersistenceGate)
            {
                _resumePersistenceClosed = true;
                _queuedResumeSave = null;
                pending = Task.WhenAll(_pendingResumeSaves.Values.Concat(_pendingResumeLoads));
                _pendingResumeSaves.Clear();
                _pendingResumeLoads.Clear();
            }
            _ = DisposeResumeResourcesAsync(pending);
        }

        private async Task DisposeResumeResourcesAsync(Task pending)
        {
            try { await pending.ConfigureAwait(false); }
            catch (Exception ex)
            {
                AppLogger.Log($"[Resume] Pending save failed during close: {ex.Message}", "WARNING");
            }
            finally
            {
                _databaseContext.Dispose();
                _resumeDatabaseLock.Dispose();
                _resumeLoadCancellation?.Dispose();
            }
        }

        private void TrySyncMalFromProgress(double currentMilliseconds, double durationMilliseconds, bool ended)
        {
            lock (_malProgressGate)
            {
                if (_isDisposing || _malProgressSynced || _malProgressSyncCts != null ||
                    _malId <= 0 || _trackingEpisodeNumber <= 0) return;

                if (!ended)
                {
                    if (durationMilliseconds <= 0 || currentMilliseconds / durationMilliseconds < 0.85) return;
                    if ((DateTime.UtcNow - _lastMalProgressCheck).TotalSeconds < 20) return;
                }

                var config = _malProgressConfig ?? (App.Current as App)?.Services?.GetService<DomainHotSwapper>();
                if (config == null || config.GetSetting("AutoSyncMal") != "true") return;
                var malOAuth = _malOAuthService ?? (App.Current as App)?.Services?.GetService<MalOAuthService>();
                string accessToken = config.GetSetting("MalOAuthToken");
                if (malOAuth != null ? !malOAuth.HasAnyToken : string.IsNullOrWhiteSpace(accessToken)) return;

                // Capture the API/profile and unit before token refresh or any
                // request can yield to a source change.
                var mal = malOAuth != null ? new MalRestApi(malOAuth, config) : new MalRestApi(accessToken, config);
                var operation = new CancellationTokenSource(MalProgressSyncTimeout);
                _malProgressSyncCts = operation;
                _lastMalProgressCheck = DateTime.UtcNow;
                _malProgressSyncTask = SyncMalProgressAsync(mal, config, _malId, _trackingEpisodeNumber,
                    _malProgressGeneration, operation);
            }
        }

        private async Task SyncMalProgressAsync(MalRestApi mal, DomainHotSwapper config, int malId, int episode,
            long generation, CancellationTokenSource operation)
        {
            using (operation)
            {
                try
                {
                    for (int attempt = 1; attempt <= 3; attempt++)
                    {
                        operation.Token.ThrowIfCancellationRequested();
                        if (config.GetSetting("AutoSyncMal") != "true") return;
                        bool ok = false;
                        try { ok = await mal.UpdateProgressAsync(malId, episode, operation.Token, preserveHigherProgress: true); }
                        catch (OperationCanceledException) when (operation.IsCancellationRequested) { throw; }
                        catch (Exception ex) { AppLogger.Log($"[MAL Sync] Attempt failed: {ex.GetType().Name}.", "WARNING"); }
                        if (ok)
                        {
                            lock (_malProgressGate)
                            {
                                if (!_isDisposing && !operation.IsCancellationRequested && generation == _malProgressGeneration &&
                                    ReferenceEquals(_malProgressSyncCts, operation)) _malProgressSynced = true;
                            }
                            AppLogger.Log($"[MAL Sync] Progress confirmed for anime {malId}, episode {episode}.");
                            return;
                        }
                        if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(attempt == 1 ? 2 : 5), operation.Token);
                    }
                    AppLogger.Log($"[MAL Sync] Update failed for anime {malId}, episode {episode} after 3 attempts; playback can retry.", "WARNING");
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested)
                {
                    AppLogger.Log($"[MAL Sync] Pending update cancelled for anime {malId}, episode {episode}.");
                }
                finally
                {
                    lock (_malProgressGate)
                        if (ReferenceEquals(_malProgressSyncCts, operation)) _malProgressSyncCts = null;
                }
            }
        }

        private void WatchTogetherClient_MessageReceived(object? sender, WatchTogetherMessageEventArgs e)
        {
            try
            {
                if (_playbackSync?.IsActivePlayback(this) != true)
                {
                    return;
                }

                string type = TryGetJsonString(e.Payload, "type");
                if (type.Equals("sync_request", StringComparison.OrdinalIgnoreCase))
                {
                    SendSyncResponse(TryGetJsonString(e.Payload, "target_id"));
                    return;
                }

                if (type.Equals("initial_sync", StringComparison.OrdinalIgnoreCase))
                {
                    double baseSeconds = TryGetJsonDouble(e.Payload, "timestamp");
                    string initialAction = TryGetJsonBool(e.Payload, "is_playing", out bool isPlaying)
                        ? isPlaying ? "play" : "pause"
                        : "seek";
                    ApplyRemotePlaybackAction(initialAction, _playbackSync?.ToLocalTime(baseSeconds) ?? baseSeconds);
                    return;
                }

                if (!type.Equals("action", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string action = TryGetJsonString(e.Payload, "action");
                if (string.IsNullOrWhiteSpace(action))
                {
                    return;
                }

                double basePosition = TryGetJsonDouble(e.Payload, "timestamp");
                if (basePosition <= 0)
                {
                    basePosition = TryGetJsonDouble(e.Payload, "currentTime");
                }

                if (basePosition <= 0)
                {
                    basePosition = TryGetJsonDouble(e.Payload, "time");
                }

                ApplyRemotePlaybackAction(action, _playbackSync?.ToLocalTime(basePosition) ?? basePosition);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Watch Together] Failed to apply incoming sync message: {ex.Message}", "WARNING");
            }
        }

        private void SendSyncResponse(string targetId)
        {
            if (string.IsNullOrWhiteSpace(targetId) ||
                _watchTogetherClient == null ||
                _playbackSync == null ||
                !_playbackSync.IsHost ||
                !_playbackSync.CanSend ||
                !_playbackSync.IsActivePlayback(this))
            {
                return;
            }

            SendWatchTogetherPayload(_playbackSync.CreateSyncResponsePayload(targetId, GetCurrentPlaybackPositionSeconds(), IsPlaying));
        }

        private void ApplyRemotePlaybackAction(string action, double localSeconds)
        {
            if (IsDisposed || string.IsNullOrWhiteSpace(action))
            {
                return;
            }

            string normalized = action.Trim().ToLowerInvariant();
            if (normalized == "close_room")
            {
                return;
            }

            RunOnDispatcher(() =>
            {
                if (IsDisposed || !IsTabActive)
                {
                    return;
                }

                int generation = Interlocked.Increment(ref _remoteSyncCommandGeneration);
                _isApplyingRemoteSyncCommand = true;
                try
                {
                    double safeSeconds = Math.Max(0, localSeconds);
                    long targetMilliseconds = (long)(safeSeconds * 1000);
                    if (IsWebViewActive)
                    {
                        WebPlaybackCommandRequested?.Invoke(this, new WebPlaybackCommandEventArgs(normalized, safeSeconds));
                        if (normalized is "play" or "buffer_resume")
                        {
                            IsPlaying = true;
                        }
                        else if (normalized is "pause" or "buffer_pause")
                        {
                            IsPlaying = false;
                        }

                        PlaybackTime = targetMilliseconds;
                        return;
                    }

                    if (MediaPlayer == null)
                    {
                        return;
                    }

                    long duration = MediaPlayer.Length > 0 ? MediaPlayer.Length : (long)PlaybackDuration;
                    bool canSeek = duration <= 0 || targetMilliseconds <= duration + 2000;
                    bool shouldSeek = canSeek &&
                                      (normalized is "seek" or "play" or "pause" or "buffer_pause" or "buffer_resume") &&
                                      Math.Abs(MediaPlayer.Time - targetMilliseconds) > 1500;

                    if (shouldSeek)
                    {
                        MediaPlayer.Time = Math.Max(0, targetMilliseconds);
                        PlaybackTime = MediaPlayer.Time;
                    }

                    if (normalized is "play" or "buffer_resume")
                    {
                        _pauseWhenStarted = false;
                        MediaPlayer.Play();
                    }
                    else if (normalized is "pause" or "buffer_pause")
                    {
                        _pauseWhenStarted = true;
                        MediaPlayer.SetPause(true);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"[Watch Together] Failed to apply remote playback command '{normalized}': {ex.Message}", "WARNING");
                }
                finally
                {
                    _ = ReleaseRemoteCommandFlagAsync(generation);
                }
            });
        }

        private async Task ReleaseRemoteCommandFlagAsync(int generation)
        {
            await Task.Delay(2000);
            if (generation == Volatile.Read(ref _remoteSyncCommandGeneration))
            {
                _isApplyingRemoteSyncCommand = false;
            }
        }

        private void PublishPlaybackAction(string action, double localSeconds, bool hostOnly = false)
        {
            if (IsDisposed ||
                _isApplyingRemoteSyncCommand ||
                string.IsNullOrWhiteSpace(action) ||
                _watchTogetherClient == null ||
                _playbackSync == null ||
                !_playbackSync.CanSend ||
                !_playbackSync.IsActivePlayback(this))
            {
                return;
            }

            if (hostOnly && !_playbackSync.IsHost)
            {
                return;
            }

            SendWatchTogetherPayload(_playbackSync.CreateActionPayload(action, localSeconds, MediaTitle));
        }

        private void SendWatchTogetherPayload(object payload)
        {
            if (_watchTogetherClient == null)
            {
                return;
            }

            _ = SendWatchTogetherPayloadAsync(payload);
        }

        private async Task SendWatchTogetherPayloadAsync(object payload)
        {
            try
            {
                // Start the send immediately so the client service sees actions in the
                // same order in which the player raised them.
                await _watchTogetherClient!.SendAsync(payload);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Watch Together] Failed to send playback sync payload: {ex.Message}", "WARNING");
            }
        }

        private static string TryGetJsonString(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }

        private static double TryGetJsonDouble(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(name, out var value) &&
                   value.ValueKind == JsonValueKind.Number &&
                   value.TryGetDouble(out double parsed)
                ? parsed
                : 0;
        }

        private static bool TryGetJsonBool(JsonElement element, string name, out bool parsed)
        {
            parsed = false;
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(name, out var value) ||
                value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return false;
            }

            parsed = value.GetBoolean();
            return true;
        }

        private static string FormatTime(double milliseconds)
        {
            if (milliseconds <= 0 || double.IsNaN(milliseconds) || double.IsInfinity(milliseconds))
            {
                return "00:00";
            }

            var span = TimeSpan.FromMilliseconds(milliseconds);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}";
        }

        public void Dispose()
        {
            if (_isDisposing || IsDisposed)
            {
                return;
            }

            _isDisposing = true;
            lock (_malProgressGate)
            {
                _malProgressSyncCts?.Cancel();
                _malProgressSyncCts = null;
                _malProgressGeneration++;
            }
            AppLogger.Log("Disposing PlaybackViewModel.");
            try
            {
                if (_watchTogetherClient != null)
                {
                    _watchTogetherClient.MessageReceived -= WatchTogetherClient_MessageReceived;
                }

                DeactivateWatchTogetherPlayback();

                CancelEpisodeNavigation();
                Interlocked.Increment(ref _qualityDiscoveryGeneration);

                Helpers.LocalizationRuntime.LanguageChanged -= LocalizationRuntime_LanguageChanged;
                SaveCurrentResumePosition(force: true, synchronous: true);
                StopAndRelease(saveResume: false);
                // LibVLCSharp event managers hold native GCHandles until the
                // final subscription is removed; disposing MediaPlayer alone
                // does not release those roots in the pinned package version.
                MediaPlayer.Opening -= _nativeOpening;
                MediaPlayer.Buffering -= _nativeBuffering;
                MediaPlayer.Playing -= _nativePlaying;
                MediaPlayer.Paused -= _nativePaused;
                MediaPlayer.Stopped -= _nativeStopped;
                MediaPlayer.EncounteredError -= _nativeError;
                MediaPlayer.TimeChanged -= MediaPlayer_TimeChanged;
                MediaPlayer.ESAdded -= _nativeEsAdded;
                MediaPlayer.ESDeleted -= _nativeEsDeleted;
                MediaPlayer.EndReached -= MediaPlayer_EndReached;
                MediaPlayer.LengthChanged -= _nativeLengthChanged;
                MediaPlayer.Dispose();
                _nativeEngineLease.Dispose();
            }
            finally
            {
                CloseResumePersistence();
                IsDisposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }

    public sealed class WebPlaybackCommandEventArgs : EventArgs
    {
        public WebPlaybackCommandEventArgs(string action, double positionSeconds)
        {
            Action = action;
            PositionSeconds = positionSeconds;
        }

        public string Action { get; }
        public double PositionSeconds { get; }
    }
}
