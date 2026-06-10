using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using System;

namespace UniversalMediaOS.WPF.ViewModels
{
    public partial class MangaViewModel : ObservableObject
    {
        private readonly MangaService _mangaService;

        [ObservableProperty]
        private string _searchQuery = string.Empty;

        [ObservableProperty]
        private bool _isSearching;

        public ObservableCollection<MangaSearchResult> MangaResults { get; } = new();

        public MangaViewModel(MangaService mangaService)
        {
            _mangaService = mangaService;
        }

        [RelayCommand(IncludeCancelCommand = true)]
        private async Task SearchMangaAsync(CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(SearchQuery)) return;

            IsSearching = true;
            try
            {
                var results = await _mangaService.SearchMangaAsync(SearchQuery, token);
                MangaResults.Clear();
                foreach (var result in results)
                {
                    MangaResults.Add(result);
                }
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
    }
}
