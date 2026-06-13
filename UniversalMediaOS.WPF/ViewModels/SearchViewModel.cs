using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaOS.Core.Search;
using System;
using UniversalMediaOS.WPF.Helpers;
using CommunityToolkit.Mvvm.Messaging;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class SearchViewModel : ObservableObject
    {
        private readonly FuzzyShieldSearch _searchService;
        private readonly UniversalMediaOS.Core.Archiving.SeasonDownloader _seasonDownloader;

        [ObservableProperty]
        private string _searchQuery = string.Empty;

        [ObservableProperty]
        private bool _isSearching;

        public ObservableRangeCollection<MediaResult> SearchResults { get; } = new();

        public SearchViewModel(FuzzyShieldSearch searchService, UniversalMediaOS.Core.Archiving.SeasonDownloader seasonDownloader)
        {
            _searchService = searchService;
            _seasonDownloader = seasonDownloader;
        }

        [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
        private async Task SearchAsync(CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(SearchQuery)) return;

            UniversalMediaOS.Core.Helpers.AppLogger.Log($"SearchAsync invoked. Query: '{SearchQuery}'");
            IsSearching = true;
            try
            {
                var results = await _searchService.SearchAnimeAsync(SearchQuery, token);
                SearchResults.ReplaceRange(results);
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"SearchAsync complete. Found {results.Count} results.");
            }
            catch (OperationCanceledException)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log("SearchAsync cancelled by user.");
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"SearchAsync failed. Error: {ex.Message}", "ERROR");
                throw;
            }
            finally
            {
                IsSearching = false;
            }
        }

        [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
        private async Task DownloadAsync(MediaResult result, CancellationToken token)
        {
            if (result == null) return;
            
            UniversalMediaOS.Core.Helpers.AppLogger.Log($"DownloadAsync invoked for: '{result.OfficialTitle}' (ID: {result.Id})");
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"Download initialized: {result.OfficialTitle}"));
            
            try
            {
                bool success = await _seasonDownloader.DownloadSeasonAsync(
                    result.OfficialTitle, 
                    msg => 
                    {
                        UniversalMediaOS.Core.Helpers.AppLogger.Log($"[Download] {msg}");
                        CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new ToastNotificationMessage(msg));
                    }, 
                    pct => 
                    {
                        // percentage
                    },
                    token
                );
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Download finished. Success: {success}");
            }
            catch (OperationCanceledException)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Download cancelled by user for: '{result.OfficialTitle}'");
                CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"Download cancelled: {result.OfficialTitle}"));
            }
            catch (Exception ex)
            {
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"Download failed. Error: {ex.Message}", "ERROR");
                throw;
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
    }
}
