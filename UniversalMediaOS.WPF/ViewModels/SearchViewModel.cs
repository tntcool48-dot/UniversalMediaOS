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

        [RelayCommand(IncludeCancelCommand = true)]
        private async Task SearchAsync(CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(SearchQuery)) return;

            IsSearching = true;
            try
            {
                var results = await _searchService.SearchAnimeAsync(SearchQuery, token);
                SearchResults.ReplaceRange(results);
            }
            catch (OperationCanceledException)
            {
                // Ignoring cancellation
            }
            finally
            {
                IsSearching = false;
            }
        }

        [RelayCommand]
        private async Task DownloadAsync(MediaResult result)
        {
            if (result == null) return;
            
            CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new ToastNotificationMessage($"Download initialized: {result.OfficialTitle}"));
            
            // This relies on the SeasonDownloader backend working nicely with async
            await _seasonDownloader.DownloadSeasonAsync(
                result.OfficialTitle, 
                msg => 
                {
                    CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger.Default.Send(new ToastNotificationMessage(msg));
                }, 
                pct => 
                {
                    // For now, percentage updates might spam the toast, so we skip or throttle.
                    // We'll leave it empty to avoid spamming the toast.
                }
            );
        }
    }
}
