using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class PlaybackViewModel : ObservableObject, IDisposable
    {
        private LibVLC _libVLC;
        private Media? _currentMedia;
        
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
        private string _embedUrl = string.Empty;

        [ObservableProperty]
        private bool _isWebViewActive;

        public PlaybackViewModel()
        {
            AppLogger.Log("Initializing PlaybackViewModel and LibVLC player...");
            LibVLCSharp.Shared.Core.Initialize();
            _libVLC = new LibVLC(enableDebugLogs: false);
            _mediaPlayer = new MediaPlayer(_libVLC);
            
            _mediaPlayer.Playing      += (s, e) => RunOnDispatcher(() => {
                IsPlaying = true;
                AppLogger.Log($"LibVLC playing event fired for: '{MediaTitle}'");
            });
            _mediaPlayer.Paused       += (s, e) => RunOnDispatcher(() => {
                IsPlaying = false;
                AppLogger.Log($"LibVLC paused event fired for: '{MediaTitle}'");
            });
            _mediaPlayer.Stopped      += (s, e) => RunOnDispatcher(() => {
                IsPlaying = false;
                AppLogger.Log($"LibVLC stopped event fired for: '{MediaTitle}'");
            });
            _mediaPlayer.TimeChanged  += MediaPlayer_TimeChanged;
            _mediaPlayer.LengthChanged += (s, e) => RunOnDispatcher(() => {
                PlaybackDuration = e.Length;
                AppLogger.Log($"LibVLC length changed: {e.Length} ms for: '{MediaTitle}'");
            });
        }

        private DateTime _lastTimeUpdate = DateTime.MinValue;
        private volatile bool _isUpdatingTimeFromPlayer;

        private void RunOnDispatcher(Action action)
        {
            if (App.Current?.Dispatcher is { } dispatcher)
            {
                dispatcher.InvokeAsync(action);
            }
            else
            {
                action();
            }
        }

        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            // Throttle UI updates to roughly 250ms per the constraint
            if ((DateTime.Now - _lastTimeUpdate).TotalMilliseconds > 250)
            {
                _lastTimeUpdate = DateTime.Now;
                RunOnDispatcher(() =>
                {
                    _isUpdatingTimeFromPlayer = true;
                    try
                    {
                        PlaybackTime = e.Time;
                    }
                    finally
                    {
                        _isUpdatingTimeFromPlayer = false;
                    }
                });
            }
        }

        partial void OnPlaybackTimeChanged(double value)
        {
            if (_isUpdatingTimeFromPlayer) return;

            if (MediaPlayer != null && Math.Abs(MediaPlayer.Time - value) > 1500)
            {
                AppLogger.Log($"User requested seek from {MediaPlayer.Time} ms to {value} ms");
                MediaPlayer.Time = (long)value;
            }
        }

        // ── Deferred local playback support ──
        // Stores the media path/url when LoadMedia is called before the VideoView is ready.
        [ObservableProperty] private string _pendingMediaPath = string.Empty;
        private Media? _pendingMedia;

        public void PlayPending()
        {
            if (_pendingMedia == null || string.IsNullOrEmpty(PendingMediaPath)) return;
            AppLogger.Log($"PlayPending: Replaying media '{PendingMediaPath}' now that VideoView is ready.");
            try
            {
                MediaPlayer.Play(_pendingMedia);
                PendingMediaPath = string.Empty;
                _pendingMedia = null;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"PlayPending failed: {ex.Message}", "ERROR");
            }
        }

        public void LoadMedia(string urlOrPath, string title, string referer = "")
        {
            AppLogger.Log($"LoadMedia invoked: Title='{title}', UrlOrPath='{urlOrPath}', Referer='{referer}'");
            MediaTitle = title;
            IsWebViewActive = false;
            EmbedUrl = string.Empty;
            
            string path = urlOrPath;
            try
            {
                path = Uri.UnescapeDataString(path);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to unescape media path: {ex.Message}", "WARNING");
            }

            if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    path = new Uri(path).LocalPath;
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Failed to parse file URI to local path: {ex.Message}", "WARNING");
                }
            }

            bool isLocal = !path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
                           !path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            
            if (isLocal)
            {
                path = path.Replace('/', System.IO.Path.DirectorySeparatorChar);
                isLocal = System.IO.File.Exists(path);
            }
            
            var type = isLocal ? FromType.FromPath : FromType.FromLocation;
            AppLogger.Log($"Selected media source type: isLocal={isLocal}, ResolvedPath='{path}'");
            
            try
            {
                _currentMedia?.Dispose();
                _pendingMedia?.Dispose();
                _pendingMedia = null;

                if (isLocal)
                {
                    string escapedUri = new Uri(path).AbsoluteUri;
                    AppLogger.Log($"Converting local path to escaped Uri for LibVLC: '{escapedUri}'");
                    _currentMedia = new Media(_libVLC, escapedUri, FromType.FromLocation);
                }
                else
                {
                    _currentMedia = new Media(_libVLC, path, type);
                    if (!string.IsNullOrEmpty(referer))
                    {
                        AppLogger.Log($"Adding HTTP referer option to LibVLC: {referer}");
                        _currentMedia.AddOption(":http-referrer=" + referer);
                    }
                    _currentMedia.AddOption(":http-user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                }

                // Store as pending — PlaybackView.Loaded will call PlayPending() once the VideoView HWND is ready.
                // This prevents "stopped immediately" when the view hasn't rendered yet.
                _pendingMedia = _currentMedia;
                PendingMediaPath = urlOrPath;
                AppLogger.Log($"Media staged as pending. PlaybackView.Loaded will trigger playback.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Error setting up LibVLC media player: {ex.Message}", "ERROR");
            }
        }

        public void LoadEmbed(string embedUrl, string title)
        {
            AppLogger.Log($"LoadEmbed invoked: Title='{title}', EmbedUrl='{embedUrl}'");
            MediaTitle = title;
            StopAndRelease();
            IsWebViewActive = true;
            EmbedUrl = embedUrl;
        }

        [RelayCommand]
        private void TogglePlayPause()
        {
            AppLogger.Log("TogglePlayPause command invoked.");
            if (MediaPlayer.IsPlaying)
            {
                AppLogger.Log("Pausing media player.");
                MediaPlayer.Pause();
            }
            else
            {
                AppLogger.Log("Starting/Resuming media player.");
                MediaPlayer.Play();
            }
        }

        [RelayCommand]
        private void Stop()
        {
            AppLogger.Log("Stop command invoked.");
            StopAndRelease();
        }

        public void StopAndRelease()
        {
            AppLogger.Log("Releasing media player stream resources...");
            try
            {
                if (MediaPlayer.IsPlaying)
                {
                    MediaPlayer.Stop();
                }
                _currentMedia?.Dispose();
                _currentMedia = null;
                AppLogger.Log("Media player stream resources successfully released.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Error during media release: {ex.Message}", "WARNING");
            }
        }

        public void Dispose()
        {
            AppLogger.Log("Disposing PlaybackViewModel.");
            StopAndRelease();
            MediaPlayer.Dispose();
            _libVLC.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
