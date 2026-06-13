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

                var torrents = await _routingEngine.GetTorrentsAsync(Media.OfficialTitle + audioPref, episodeNum, logger);

                // Open the Switchboard Selection window via dialog service
                var (dialogResult, selectedTier, selectedTorrent) = _dialogService.ShowSourceSelection(torrents);

                if (dialogResult)
                {
                    switch (selectedTier)
                    {
                        case SelectedSourceTier.Tier1_Torrent:
                            logger("Tier 1 selected — scraping Nyaa P2P networks...");
                            if (torrents.Count == 0)
                            {
                                logger("No torrents found on Nyaa/AnimeTosho. Select Tier 2 or Tier 3 fallbacks.");
                                break;
                            }

                            if (selectedTorrent != null)
                            {
                                source = await _routingEngine.InjectTorrentAsync(selectedTorrent, logger);
                            }
                            break;

                        case SelectedSourceTier.Tier2_Consumet:
                            logger("Tier 2 selected — querying Consumet scraper...");
                            source = await _routingEngine.ResolveBestSourceAsync(Media.OfficialTitle + audioPref, episodeNum, providerDomain, logger, SourceTier.Tier2_ConsumetHttp);
                            break;

                        case SelectedSourceTier.Tier3_WebProvider:
                            logger("Tier 3 selected — launching WebView2 embed...");
                            source = await _routingEngine.ResolveBestSourceAsync(Media.OfficialTitle + audioPref, episodeNum, providerDomain, logger, SourceTier.Tier3_WebViewEmbed);
                            break;
                    }
                }

                if (source != null)
                {
                    logger($"Source resolved successfully. Loading playback...");

                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    Action playAction = () =>
                    {
                        if (source.Tier == SourceTier.Tier1_LocalP2P)
                        {
                            if (File.Exists(source.UrlOrPath))
                            {
                                if (_config.GetSetting("AutoPlayAfterDownload") != "false")
                                {
                                    WeakReferenceMessenger.Default.Send(new PlayMediaMessage(source.UrlOrPath, Media.OfficialTitle + " - Ep " + episodeNum));
                                }
                                else
                                {
                                    logger("Download complete. AutoPlay disabled.");
                                }
                            }
                            else
                            {
                                logger($"ERROR: Download file inaccessible: {source.UrlOrPath}");
                            }
                        }
                        else if (source.Tier == SourceTier.Tier2_ConsumetHttp)
                        {
                            WeakReferenceMessenger.Default.Send(new PlayMediaMessage(source.UrlOrPath, Media.OfficialTitle + " - Ep " + episodeNum, isWebView: false, referer: source.EmbedOrigin));
                        }
                        else if (source.Tier == SourceTier.Tier3_WebViewEmbed)
                        {
                            WeakReferenceMessenger.Default.Send(new PlayMediaMessage(source.UrlOrPath, Media.OfficialTitle + " - Ep " + episodeNum, isWebView: true));
                        }
                    };

                    if (dispatcher != null)
                    {
                        await dispatcher.InvokeAsync(playAction);
                    }
                    else
                    {
                        playAction();
                    }
                }
                else
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

        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task DownloadSeasonAsync()
        {
            if (Media == null || IsDownloading) return;

            UniversalMediaOS.Core.Helpers.AppLogger.Log($"DownloadSeasonAsync invoked for: '{Media.OfficialTitle}' (ID: {Media.Id})");
            IsDownloading = true;
            DownloadButtonText = "Queued...";
            WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"Batch Season Download Queued: {Media.OfficialTitle}"));

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
                            dispatcher.InvokeAsync(() => { DownloadButtonText = $"Downloading {pct:F0}%"; });
                        }
                        else
                        {
                            DownloadButtonText = $"Downloading {pct:F0}%";
                        }
                    });

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
            catch (Exception ex)
            {
                DownloadButtonText = "Failed";
                WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"Download Error: {ex.Message}"));
            }
            finally
            {
                IsDownloading = false;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    var dispatcher = System.Windows.Application.Current?.Dispatcher;
                    if (dispatcher != null)
                    {
                        await dispatcher.InvokeAsync(() =>
                        {
                            if (DownloadButtonText == "Done ✔" || DownloadButtonText == "Failed")
                            {
                                DownloadButtonText = "📥 Season Download";
                            }
                        });
                    }
                    else
                    {
                        if (DownloadButtonText == "Done ✔" || DownloadButtonText == "Failed")
                        {
                            DownloadButtonText = "📥 Season Download";
                        }
                    }
                });
            }
        }
    }
}
