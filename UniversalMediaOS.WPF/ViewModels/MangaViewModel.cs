using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.WPF.ViewModels
{
    /// <summary>
    /// View modes for the Manga view:
    ///   0 = Search Results grid
    ///   1 = Chapter list for selected manga
    ///   2 = Vertical scroll page reader
    ///   3 = External WebView reader (for chapters with externalUrl only)
    /// </summary>
    public partial class MangaViewModel : ObservableObject
    {
        private readonly MangaService _mangaService;

        // ── Search ──────────────────────────────────────────
        [ObservableProperty] private string _searchQuery = string.Empty;
        [ObservableProperty] private bool _isSearching;

        public Helpers.ObservableRangeCollection<MangaSearchResult> MangaResults { get; } = new();

        // ── View Mode ────────────────────────────────────────
        [ObservableProperty] private int _currentViewMode; // 0=results, 1=chapters, 2=pages, 3=webview

        // ── Selected Manga ───────────────────────────────────
        [ObservableProperty] private MangaSearchResult? _selectedManga;
        [ObservableProperty] private bool _isLoadingChapters;
        public Helpers.ObservableRangeCollection<MangaChapter> Chapters { get; } = new();

        // ── Selected Chapter / Page Reader ───────────────────
        [ObservableProperty] private MangaChapter? _selectedChapter;
        [ObservableProperty] private bool _isLoadingPages;
        public Helpers.ObservableRangeCollection<string> PageUrls { get; } = new();

        // ── External WebView ─────────────────────────────────
        [ObservableProperty] private string _externalUrl = string.Empty;

        // ── Breadcrumb label ─────────────────────────────────
        [ObservableProperty] private string _breadcrumb = string.Empty;

        public MangaViewModel(MangaService mangaService)
        {
            _mangaService = mangaService;
        }

        // ── Search Command ───────────────────────────────────
        [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
        private async Task SearchMangaAsync(CancellationToken token)
        {
            AppLogger.Log($"SearchMangaAsync invoked. Query='{SearchQuery}'");
            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                AppLogger.Log("SearchMangaAsync: Query is empty, skipping.", "WARNING");
                return;
            }

            IsSearching = true;
            try
            {
                AppLogger.Log($"Querying manga service for: '{SearchQuery}'...");
                var results = await _mangaService.SearchMangaAsync(SearchQuery, token);
                MangaResults.ReplaceRange(results);

                CurrentViewMode = 0;
                AppLogger.Log($"SearchMangaAsync complete. Found {results.Count} results.");
            }
            catch (OperationCanceledException)
            {
                AppLogger.Log("SearchMangaAsync command was cancelled by user.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"SearchMangaAsync failed. Error: {ex.Message}", "ERROR");
            }
            finally
            {
                IsSearching = false;
            }
        }

        // ── Read Command (from results grid) ─────────────────
        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task ReadAsync(MangaSearchResult? manga)
        {
            if (manga == null) return;
            AppLogger.Log($"ReadCommand invoked for manga: '{manga.Title}' (Id={manga.Id})");

            SelectedManga = manga;
            Chapters.Clear();
            PageUrls.Clear();
            CurrentViewMode = 1;
            Breadcrumb = manga.Title;
            IsLoadingChapters = true;

            try
            {
                var chapters = await _mangaService.GetChaptersAsync(manga.Id);
                AppLogger.Log($"Loaded {chapters.Count} chapters for '{manga.Title}'");
                Chapters.ReplaceRange(chapters);

                if (Chapters.Count == 0)
                {
                    AppLogger.Log("No chapters found for this manga.", "WARNING");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to load chapters: {ex.Message}", "ERROR");
            }
            finally
            {
                IsLoadingChapters = false;
            }
        }

        // ── Select Chapter Command ────────────────────────────
        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task SelectChapterAsync(MangaChapter? chapter)
        {
            if (chapter == null) return;
            AppLogger.Log($"SelectChapterCommand invoked: Ch {chapter.ChapterNumber} - '{chapter.Title}' (Id={chapter.Id})");

            SelectedChapter = chapter;
            Breadcrumb = $"{SelectedManga?.Title ?? "Manga"} › Ch. {chapter.ChapterNumber}";

            // If chapter has an external URL, open in WebView
            if (!string.IsNullOrEmpty(chapter.ExternalUrl))
            {
                AppLogger.Log($"Chapter has externalUrl='{chapter.ExternalUrl}' — opening WebView reader.");
                ExternalUrl = chapter.ExternalUrl;
                CurrentViewMode = 3;
                return;
            }

            // Otherwise fetch pages from MangaDex at-home server
            PageUrls.Clear();
            CurrentViewMode = 2;
            IsLoadingPages = true;

            try
            {
                var pages = await _mangaService.GetPageUrlsAsync(chapter.Id);
                AppLogger.Log($"Loaded {pages.Count} pages for chapter '{chapter.ChapterNumber}'");
                PageUrls.ReplaceRange(pages);

                if (PageUrls.Count == 0)
                {
                    AppLogger.Log("No page URLs found for this chapter — might be external-only.", "WARNING");
                    // Fallback: go back to chapter list
                    CurrentViewMode = 1;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to load chapter pages: {ex.Message}", "ERROR");
                CurrentViewMode = 1;
            }
            finally
            {
                IsLoadingPages = false;
            }
        }

        // ── Go Back Command ───────────────────────────────────
        [RelayCommand]
        private void GoBack()
        {
            AppLogger.Log($"GoBackCommand invoked from mode={CurrentViewMode}");
            switch (CurrentViewMode)
            {
                case 3:
                case 2:
                    // Back to chapters
                    PageUrls.Clear();
                    ExternalUrl = string.Empty;
                    CurrentViewMode = 1;
                    Breadcrumb = SelectedManga?.Title ?? "Manga";
                    break;
                case 1:
                    // Back to search results
                    Chapters.Clear();
                    SelectedManga = null;
                    CurrentViewMode = 0;
                    Breadcrumb = string.Empty;
                    break;
                default:
                    break;
            }
        }
    }
}
