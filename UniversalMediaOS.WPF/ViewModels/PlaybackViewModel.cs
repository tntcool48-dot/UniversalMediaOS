using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class PlaybackViewModel : ObservableObject, IDisposable
    {
        private LibVLC _libVLC;
        
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

        public PlaybackViewModel()
        {
            LibVLCSharp.Shared.Core.Initialize();
            _libVLC = new LibVLC(enableDebugLogs: false);
            _mediaPlayer = new MediaPlayer(_libVLC);
            
            _mediaPlayer.Playing      += (s, e) => App.Current.Dispatcher.InvokeAsync(() => IsPlaying = true);
            _mediaPlayer.Paused       += (s, e) => App.Current.Dispatcher.InvokeAsync(() => IsPlaying = false);
            _mediaPlayer.Stopped      += (s, e) => App.Current.Dispatcher.InvokeAsync(() => IsPlaying = false);
            _mediaPlayer.TimeChanged  += MediaPlayer_TimeChanged;
            _mediaPlayer.LengthChanged += (s, e) => App.Current.Dispatcher.InvokeAsync(() => PlaybackDuration = e.Length);
        }

        private DateTime _lastTimeUpdate = DateTime.MinValue;

        private void MediaPlayer_TimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
        {
            // Throttle UI updates to roughly 250ms per the constraint
            if ((DateTime.Now - _lastTimeUpdate).TotalMilliseconds > 250)
            {
                _lastTimeUpdate = DateTime.Now;
                App.Current.Dispatcher.InvokeAsync(() =>
                {
                    PlaybackTime = e.Time;
                });
            }
        }

        public void LoadMedia(string urlOrPath, string title)
        {
            MediaTitle = title;
            using var media = new Media(_libVLC, urlOrPath, FromType.FromLocation);
            MediaPlayer.Play(media);
        }

        [RelayCommand]
        private void TogglePlayPause()
        {
            if (MediaPlayer.IsPlaying)
                MediaPlayer.Pause();
            else
                MediaPlayer.Play();
        }

        [RelayCommand]
        private void Stop()
        {
            StopAndRelease();
        }

        public void StopAndRelease()
        {
            if (MediaPlayer.IsPlaying)
            {
                MediaPlayer.Stop();
            }
        }

        public void Dispose()
        {
            StopAndRelease();
            MediaPlayer.Dispose();
            _libVLC.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
