using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.Archiving;
using UniversalMediaOS.Core.Configuration;
using UniversalMediaOS.Core.Routing;
using UniversalMediaOS.Core.Search;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class AnimeDetailsViewModel : ObservableObject
    {
        private readonly DomainHotSwapper _config;
        private readonly TripleNetHandoff _routingEngine;
        private readonly SeasonDownloader _seasonDownloader;
        private readonly Helpers.IDialogService _dialogService;

        [ObservableProperty]
        private MediaResult? _media;

        [ObservableProperty]
        private string _selectedEpisode = "1";

        [ObservableProperty]
        private string _selectedProvider = string.Empty;

        [ObservableProperty]
        private bool _isRouting;

        [ObservableProperty]
        private bool _isDownloading;

        [ObservableProperty]
        private string _downloadButtonText = "📥 Season Download";

        public ObservableCollection<CustomSource> Providers { get; } = new();

        public AnimeDetailsViewModel(
            DomainHotSwapper config,
            TripleNetHandoff routingEngine,
            SeasonDownloader seasonDownloader,
            Helpers.IDialogService dialogService)
        {
            _config = config;
            _routingEngine = routingEngine;
            _seasonDownloader = seasonDownloader;
            _dialogService = dialogService;

            // Load scraping provider domains from config
            var customSources = _config.GetCustomSources();
            foreach (var src in customSources)
            {
                Providers.Add(src);
            }

            if (Providers.Count > 0)
            {
                SelectedProvider = Providers[0].Url;
            }
        }

        [RelayCommand]
        private void GoBack()
        {
            UniversalMediaOS.Core.Helpers.AppLogger.Log("GoBack command invoked. Returning to Search view.");
            WeakReferenceMessenger.Default.Send(new ToastNotificationMessage("Returning to search..."));
            WeakReferenceMessenger.Default.Send(new NavigateToDetailsMessage(null));
        }

        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task WatchNowAsync()
        {
            if (Media == null || IsRouting) return;

            UniversalMediaOS.Core.Helpers.AppLogger.Log($"WatchNowAsync invoked for: '{Media.OfficialTitle}' (ID: {Media.Id}), Episode: '{SelectedEpisode}', Provider: '{SelectedProvider}'");
            IsRouting = true;
            WeakReferenceMessenger.Default.Send(new ToastNotificationMessage("Initiating stream routing switchboard..."));

            try
            {
                // Read Dub preference directly
                string savedAudio = _config.GetSetting("DefaultAudioPref");
                string audioPref = (savedAudio == "Dub") ? " Dub" : "";

                string episodeNum = SelectedEpisode;
                string providerDomain = SelectedProvider;

                Action<string> logger = (msg) =>
                {
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

                logger($"▶ Resolving: {Media.OfficialTitle} Ep {episodeNum}{audioPref}");
                PlaybackSource? source = null;

                var (dialogResult, selectedTier) = _dialogService.ShowSourceSelection();

                if (dialogResult)
                {
                    switch (selectedTier)
                    {
                        case SelectedSourceTier.Stream_Auto:
                            logger("Auto stream: Python scraper → HLS proxy → WebView fallback...");
                            source = await _routingEngine.ResolveBestSourceAsync(
                                Media.OfficialTitle + audioPref, episodeNum, providerDomain, logger,
                                SourceTier.Tier1_PythonScraper);
                            break;

                        case SelectedSourceTier.Stream_WebView:
                            logger("WebView2 player selected — loading embedded browser...");
                            source = await _routingEngine.ResolveBestSourceAsync(
                                Media.OfficialTitle + audioPref, episodeNum, providerDomain, logger,
                                SourceTier.Tier2_WebViewEmbed);
                            break;

                        case SelectedSourceTier.Download_Season:
                            logger("P2P season download initiated via SeasonDownloader...");
                            await DownloadSeasonAsync();
                            break;

                        default:
                            break;
                    }
                }

                if (source != null)
                {
                    logger("Source resolved successfully. Loading playback...");

                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    Action playAction = () =>
                    {
                        if (source.Tier == SourceTier.Tier1_PythonScraper)
                        {
                            WeakReferenceMessenger.Default.Send(
                                new PlayMediaMessage(source.UrlOrPath,
                                    Media.OfficialTitle + " - Ep " + episodeNum,
                                    isWebView: false,
                                    referer: source.EmbedOrigin));
                        }
                        else if (source.Tier == SourceTier.Tier2_WebViewEmbed)
                        {
                            WeakReferenceMessenger.Default.Send(
                                new PlayMediaMessage(source.UrlOrPath,
                                    Media.OfficialTitle + " - Ep " + episodeNum,
                                    isWebView: true));
                        }
                    };

                    if (dispatcher != null)
                        await dispatcher.InvokeAsync(playAction);
                    else
                        playAction();
                }
                else if (selectedTier != SelectedSourceTier.Download_Season)
                {
                    logger("Playback resolution aborted.");
                }
            }
            catch (Exception ex)
            {
                WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"Playback Error: {ex.Message}"));
            }
            finally
            {
                IsRouting = false;
            }
        }

        private CancellationTokenSource? _downloadCts;

        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task DownloadSeasonAsync()
        {
            if (IsDownloading)
            {
                if (_downloadCts != null && !_downloadCts.IsCancellationRequested)
                {
                    UniversalMediaOS.Core.Helpers.AppLogger.Log("Cancellation requested by user clicking the download button again.");
                    _downloadCts.Cancel();
                }
                return;
            }

            if (Media == null) return;

            UniversalMediaOS.Core.Helpers.AppLogger.Log($"DownloadSeasonAsync invoked for: '{Media.OfficialTitle}' (ID: {Media.Id})");
            IsDownloading = true;
            DownloadButtonText = "Queued... (Cancel)";
            WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"Batch Season Download Queued: {Media.OfficialTitle}"));

            _downloadCts = new CancellationTokenSource();
            var token = _downloadCts.Token;

            try
            {
                bool success = await _seasonDownloader.DownloadSeasonAsync(
                    Media.OfficialTitle,
                    msg =>
                    {
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher != null)
                        {
                            dispatcher.InvokeAsync(() => WeakReferenceMessenger.Default.Send(new ToastNotificationMessage(msg)));
                        }
                        else
                        {
                            WeakReferenceMessenger.Default.Send(new ToastNotificationMessage(msg));
                        }
                    },
                    pct =>
                    {
                        var dispatcher = System.Windows.Application.Current?.Dispatcher;
                        if (dispatcher != null)
                        {
                            dispatcher.InvokeAsync(() => { DownloadButtonText = $"Downloading {pct:F0}% (Cancel)"; });
                        }
                        else
                        {
                            DownloadButtonText = $"Downloading {pct:F0}% (Cancel)";
                        }
                    },
                    token);

                if (success)
                {
                    DownloadButtonText = "Done ✔";
                    WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"Success: Season batch download complete for {Media.OfficialTitle}"));
                }
                else
                {
                    DownloadButtonText = "Failed";
                    WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"Failed: Season batch download failed for {Media.OfficialTitle}"));
                }
            }
            catch (OperationCanceledException)
            {
                DownloadButtonText = "Cancelled";
                WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"Download cancelled by user: {Media.OfficialTitle}"));
            }
            catch (Exception ex)
            {
                DownloadButtonText = "Failed";
                WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"Download Error: {ex.Message}"));
            }
            finally
            {
                IsDownloading = false;
                _downloadCts?.Dispose();
                _downloadCts = null;

                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher != null)
                    {
                        await dispatcher.InvokeAsync(() =>
                        {
                            if (DownloadButtonText == "Done ✔" || DownloadButtonText == "Failed" || DownloadButtonText == "Cancelled")
                            {
                                DownloadButtonText = "📥 Season Download";
                            }
                        });
                    }
                    else
                    {
                        if (DownloadButtonText == "Done ✔" || DownloadButtonText == "Failed" || DownloadButtonText == "Cancelled")
                        {
                            DownloadButtonText = "📥 Season Download";
                        }
                    }
                });
            }
        }
    }
}
