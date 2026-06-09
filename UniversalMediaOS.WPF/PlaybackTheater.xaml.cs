using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Controls;
using LibVLCSharp.Shared;
using UniversalMediaOS.Core.Casting;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Social;
using UniversalMediaOS.Core.Tracking;
using UniversalMediaOS.Core.Configuration;

namespace UniversalMediaOS.WPF
{
    public partial class PlaybackTheater : Window
    {
        private LibVLC _libVLC;
        private MediaPlayer _mediaPlayer;

        private bool _isWebViewInitialized = false;
        private DispatcherTimer _progressTimer;
        private DispatcherTimer _malTimer;
        private bool _isDraggingSlider = false;

        // Playback context tracking
        private int _mediaId;
        private string _showTitle = string.Empty;
        private string _episodeNo = "1";
        private string _audioPref = "";
        private string _mediaUrlOrPath = string.Empty;
        
        // Auto-Resume tracker
        private double _lastSavedSeconds = 0;
        private bool _hasPromptedResume = false;

        // AniSkip intervals
        private double _introStart = -1;
        private double _introEnd = -1;
        private double _outroStart = -1;
        private double _outroEnd = -1;
        private bool _aniSkipTriggered = false;

        // MAL progress sync
        private bool _malSynced = false;

        // Watch Party Sync
        private WatchPartySync? _partySync;
        private bool _isRemoteCommand = false;

        public PlaybackTheater()
        {
            InitializeComponent();
            
            // Init VLC
            LibVLCSharp.Shared.Core.Initialize();
            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);
            VlcPlayer.MediaPlayer = _mediaPlayer;

            // VLC events
            _mediaPlayer.TimeChanged += MediaPlayer_TimeChanged;
            _mediaPlayer.EndReached += MediaPlayer_EndReached;
            _mediaPlayer.Paused += MediaPlayer_Paused;
            _mediaPlayer.Playing += MediaPlayer_Playing;

            // Dispatcher Timers
            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _progressTimer.Tick += ProgressTimer_Tick;
            _progressTimer.Start();

            _malTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _malTimer.Tick += MalTimer_Tick;
            _malTimer.Start();

            Closed += PlaybackTheater_Closed;
        }

        public async void InitializeMedia(int mediaId, string showTitle, string episodeNo, string audioPref)
        {
            _mediaId = mediaId;
            _showTitle = showTitle;
            _episodeNo = episodeNo;
            _audioPref = audioPref;

            // Fetch AniSkip timestamps
            try
            {
                var skipService = new AniSkipIntegration();
                var skipTimes = await skipService.GetSkipTimesAsync(mediaId, int.TryParse(episodeNo, out int ep) ? ep : 1);
                if (skipTimes != null)
                {
                    if (skipTimes.Intro != null)
                    {
                        _introStart = skipTimes.Intro.Start * 1000; // ms
                        _introEnd = skipTimes.Intro.End * 1000;
                    }
                    if (skipTimes.Outro != null)
                    {
                        _outroStart = skipTimes.Outro.Start * 1000;
                        _outroEnd = skipTimes.Outro.End * 1000;
                    }
                    Console.WriteLine($"[AniSkip] Loaded OP: {_introStart/1000}s - {_introEnd/1000}s | ED: {_outroStart/1000}s - {_outroEnd/1000}s");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AniSkip] Fetch failed: {ex.Message}");
            }

            // Warm up Casting cache in background
            try
            {
                using var db = new DatabaseContext();
                var castingMatcher = new HybridSourceMatcher(db);
                await castingMatcher.FetchAndCacheCastAsync(mediaId, showTitle);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Casting] Cache warm up failed: {ex.Message}");
            }
        }

        private void PlaybackTheater_Closed(object? sender, EventArgs e)
        {
            // Save final resume timestamp
            SavePlaybackPosition(force: true);

            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
            _libVLC.Dispose();
            _progressTimer.Stop();
            _malTimer.Stop();

            if (_partySync != null)
            {
                _partySync = null;
            }
        }

        private async System.Threading.Tasks.Task EnsureWebViewAsync()
        {
            if (!_isWebViewInitialized)
            {
                await WebViewPlayer.EnsureCoreWebView2Async(null);
                _isWebViewInitialized = true;
            }
        }

        public void PlayLocalOrHttp(string url)
        {
            _mediaUrlOrPath = url;
            WebViewPlayer.Visibility = Visibility.Hidden;
            VlcPlayer.Visibility = Visibility.Visible;
            ControlsOverlay.Visibility = Visibility.Visible;
            
            bool isLocal = File.Exists(url);
            var mediaType = isLocal ? FromType.FromPath : FromType.FromLocation;
            
            _mediaPlayer.Play(new Media(_libVLC, url, mediaType));
            Console.WriteLine($"[VLC] PlayLocalOrHttp. isLocal={isLocal} | path={url}");
        }

        public async System.Threading.Tasks.Task PlayEmbedAsync(string embedUrl)
        {
            _mediaUrlOrPath = embedUrl;
            VlcPlayer.Visibility = Visibility.Hidden;
            ControlsOverlay.Visibility = Visibility.Collapsed;
            WebViewPlayer.Visibility = Visibility.Visible;
            SidebarOverlay.Visibility = Visibility.Collapsed;
            
            _mediaPlayer.Stop();
            
            await EnsureWebViewAsync();
            WebViewPlayer.CoreWebView2.Navigate(embedUrl);
        }

        // ── Auto-Resume Wiring ───────────────────────────────────────
        private void PromptAndResumePlayback()
        {
            if (_hasPromptedResume || _mediaPlayer.Length <= 0) return;
            _hasPromptedResume = true;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    using var db = new DatabaseContext();
                    double savedSecs = db.GetResumeState(_mediaId.ToString(), _episodeNo);

                    if (savedSecs > 5 && savedSecs < (_mediaPlayer.Length / 1000.0) - 20)
                    {
                        var ts = TimeSpan.FromSeconds(savedSecs);
                        var result = MessageBox.Show(
                            $"Would you like to resume playing '{_showTitle}' Ep {_episodeNo} from {ts:hh\\:mm\\:ss}?",
                            "Resume Playback",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Question);

                        if (result == MessageBoxResult.Yes)
                        {
                            _mediaPlayer.Time = (long)(savedSecs * 1000);
                            Console.WriteLine($"[Auto-Resume] Restored position to {savedSecs}s.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Auto-Resume] Error prompting: {ex.Message}");
                }
            });
        }

        private void SavePlaybackPosition(bool force = false)
        {
            if (_mediaPlayer == null || _mediaId == 0) return;

            double currentSecs = _mediaPlayer.Time / 1000.0;
            if (currentSecs <= 0 || _mediaPlayer.Length <= 0) return;

            // Only save if it changed by more than 5s, or forced on window exit
            if (force || Math.Abs(currentSecs - _lastSavedSeconds) > 5)
            {
                _lastSavedSeconds = currentSecs;
                try
                {
                    using var db = new DatabaseContext();
                    db.SaveResumeState(_mediaId.ToString(), _episodeNo, currentSecs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Auto-Resume] Save error: {ex.Message}");
                }
            }
        }

        // ── Timers and VLC Events ────────────────────────────────────
        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            // Trigger auto-resume prompter once length is known
            if (!_hasPromptedResume)
            {
                PromptAndResumePlayback();
            }

            Dispatcher.Invoke(() =>
            {
                // AniSkip OP / ED Trigger Logic
                long time = e.Time;
                bool inIntro = (_introStart > 0 && _introEnd > 0 && time >= _introStart && time <= _introEnd);
                bool inOutro = (_outroStart > 0 && _outroEnd > 0 && time >= _outroStart && time <= _outroEnd);

                if (inIntro || inOutro)
                {
                    if (inIntro) AniSkipOverlay.ToolTip = "Skip Opening";
                    if (inOutro) AniSkipOverlay.ToolTip = "Skip Ending";
                    
                    AniSkipOverlay.Visibility = Visibility.Visible;
                }
                else
                {
                    AniSkipOverlay.Visibility = Visibility.Collapsed;
                }
            });
        }

        private void MediaPlayer_EndReached(object? sender, EventArgs e)
        {
            // Clear resume state on 100% complete
            try
            {
                using var db = new DatabaseContext();
                db.SaveResumeState(_mediaId.ToString(), _episodeNo, 0);
            }
            catch { }

            Dispatcher.Invoke(() => Close());
        }

        private void MediaPlayer_Paused(object? sender, EventArgs e)
        {
            // Immediate UI adjustments on the UI thread
            Dispatcher.Invoke(() =>
            {
                SidebarOverlay.Visibility = Visibility.Visible;
                PlayPauseBtn.Content = "▶";
            });

            // Perform database and Watch Party network actions in background task
            int mediaId = _mediaId;
            string showTitle = _showTitle;
            bool shouldSyncParty = (_partySync != null && !_isRemoteCommand);

            System.Threading.Tasks.Task.Run(async () =>
            {
                if (shouldSyncParty && _partySync != null)
                {
                    try { await _partySync.SendPlayPauseAsync(false); } catch { }
                }

                try
                {
                    using var db = new DatabaseContext();
                    var matcher = new HybridSourceMatcher(db);
                    var cast = await matcher.FetchAndCacheCastAsync(mediaId, showTitle);
                    
                    // Dispatch casting list back to the UI thread
                    Dispatcher.Invoke(() =>
                    {
                        CharactersList.ItemsSource = cast;
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Casting Overlay] Error populating cast: {ex.Message}");
                }
            });
        }

        private void MediaPlayer_Playing(object? sender, EventArgs e)
        {
            // Immediate UI adjustments on the UI thread
            Dispatcher.Invoke(() =>
            {
                SidebarOverlay.Visibility = Visibility.Collapsed;
                PlayPauseBtn.Content = "⏸";
            });

            // Signal Watch Party play state in background task
            if (_partySync != null && !_isRemoteCommand)
            {
                System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await _partySync.SendPlayPauseAsync(true); } catch { }
                });
            }
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isDraggingSlider && _mediaPlayer.Length > 0)
            {
                TimeSlider.Maximum = _mediaPlayer.Length;
                TimeSlider.Value = _mediaPlayer.Time;
            }

            // Save resume position automatically
            SavePlaybackPosition();
        }

        private async void MalTimer_Tick(object? sender, EventArgs e)
        {
            if (_malSynced || _mediaId == 0 || _mediaPlayer.Length <= 0) return;

            double ratio = (double)_mediaPlayer.Time / _mediaPlayer.Length;
            if (ratio >= 0.90) // 90% completion
            {
                _malSynced = true;
                _malTimer.Stop();

                var swapper = new DomainHotSwapper(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"));
                string malToken = swapper.GetSetting("MalOAuthToken");

                if (!string.IsNullOrEmpty(malToken))
                {
                    Console.WriteLine($"[MAL Sync] 90% watched threshold hit. Triggering progress sync...");
                    try
                    {
                        var mal = new MalRestApi(malToken);
                        bool ok = await mal.UpdateProgressAsync(_mediaId, int.TryParse(_episodeNo, out int ep) ? ep : 1);
                        Console.WriteLine(ok ? "[MAL Sync] Success!" : "[MAL Sync] Update failed (unauthorized token).");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MAL Sync] Error: {ex.Message}");
                    }
                }
            }
        }

        // ── Controls Handlers ────────────────────────────────────────
        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            long seekTime = 0;
            long currentTime = _mediaPlayer.Time;

            if (_introStart > 0 && currentTime >= _introStart && currentTime <= _introEnd)
            {
                seekTime = (long)_introEnd;
            }
            else if (_outroStart > 0 && currentTime >= _outroStart && currentTime <= _outroEnd)
            {
                seekTime = (long)_outroEnd;
            }

            if (seekTime > 0)
            {
                _mediaPlayer.Time = seekTime;
                AniSkipOverlay.Visibility = Visibility.Collapsed;
                Console.WriteLine($"[AniSkip] Skipped segment to {seekTime/1000}s.");
                
                // Broadcast party seek
                if (_partySync != null)
                {
                    _partySync.SendSeekAsync(seekTime);
                }
            }
        }

        private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
            }
            else
            {
                _mediaPlayer.Play();
            }
        }

        private void TimeSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private async void TimeSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            long seekTime = (long)TimeSlider.Value;
            _mediaPlayer.Time = seekTime;
            _isDraggingSlider = false;

            if (_partySync != null)
            {
                await _partySync.SendSeekAsync(seekTime);
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_mediaPlayer != null)
                _mediaPlayer.Volume = (int)e.NewValue;
        }

        private void FullscreenBtn_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullscreen();
        }

        private void VlcPlayer_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ToggleFullscreen();
        }

        private void ToggleFullscreen()
        {
            if (WindowStyle == WindowStyle.None)
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = WindowState.Normal;
                ControlsOverlay.Visibility = Visibility.Visible;
            }
            else
            {
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
            }
        }

        // ── Sidebar Controls ─────────────────────────────────────────
        private void CloseSidebar_Click(object sender, RoutedEventArgs e)
        {
            _mediaPlayer.Play();
        }

        private async void VoiceActor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string vaName)
            {
                try
                {
                    using var db = new DatabaseContext();
                    var matcher = new HybridSourceMatcher(db);
                    var roles = await matcher.GetCharacterSwapGalleryAsync(vaName);
                    
                    if (roles.Count > 0)
                    {
                        SwapGalleryList.ItemsSource = roles;
                        SwapGalleryPanel.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        MessageBox.Show($"{vaName} has no other recorded roles in this media system database catalog.", "Character Swap Gallery", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Swap Gallery] Error: {ex.Message}");
                }
            }
        }

        private void CloseSwapGallery_Click(object sender, RoutedEventArgs e)
        {
            SwapGalleryPanel.Visibility = Visibility.Collapsed;
        }

        // ── Watch Party Lobby Sync ──────────────────────────────────
        private async void PartyJoin_Click(object sender, RoutedEventArgs e)
        {
            string room = PartyRoomTxt.Text.Trim();
            if (string.IsNullOrEmpty(room))
            {
                MessageBox.Show("Please enter a valid Lobby Room Code.", "Lobby Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            PartyJoinBtn.IsEnabled = false;
            PartyStatusTxt.Text = "Status: Connecting...";

            try
            {
                _partySync = new WatchPartySync();
                _partySync.RemotePlayPauseRequested += PartySync_RemotePlayPauseRequested;
                _partySync.RemoteSeekRequested += PartySync_RemoteSeekRequested;

                // Sync URL from config setting, fallback to standard localhost hub
                var swapper = new DomainHotSwapper(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"));
                string syncHub = swapper.GetSetting("PythonScraperBase");
                if (string.IsNullOrEmpty(syncHub)) syncHub = "http://localhost:8000";
                
                string hubUrl = $"{syncHub.TrimEnd('/')}/partyHub?room={Uri.EscapeDataString(room)}";

                await _partySync.ConnectAsync(hubUrl);
                
                PartyStatusTxt.Text = $"Status: Active | Room: {room}";
                PartyJoinBtn.Content = "Connected";
            }
            catch (Exception ex)
            {
                PartyStatusTxt.Text = "Status: Connection Failed";
                PartyJoinBtn.IsEnabled = true;
                MessageBox.Show($"Failed to connect to SignalR watch party lobby:\n{ex.Message}", "Sync Failure", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void PartySync_RemoteSeekRequested(object? sender, long timeTicks)
        {
            Dispatcher.Invoke(() =>
            {
                _isRemoteCommand = true;
                _mediaPlayer.Time = timeTicks;
                _isRemoteCommand = false;
                Console.WriteLine($"[Party Sync] Received remote seek instruction: {timeTicks/1000}s.");
            });
        }

        private void PartySync_RemotePlayPauseRequested(object? sender, bool isPlaying)
        {
            Dispatcher.Invoke(() =>
            {
                _isRemoteCommand = true;
                if (isPlaying && !_mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Play();
                }
                else if (!isPlaying && _mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Pause();
                }
                _isRemoteCommand = false;
                Console.WriteLine($"[Party Sync] Received remote state change: isPlaying={isPlaying}.");
            });
        }
    }
}
