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
    public partial class MangaViewModel : ObservableObject, IDisposable
    {
        private readonly MangaService _mangaService;
        private readonly CancellationTokenSource _lifecycleCts = new();
        private CancellationTokenSource? _searchCts;
        private CancellationTokenSource? _chapterCts;
        private CancellationTokenSource? _pageCts;
        private int _searchGeneration;
        private int _chapterGeneration;
        private int _pageGeneration;
        private bool _isInitialized;
        private bool _isDisposed;

        // ── Search ──────────────────────────────────────────
        [ObservableProperty] private string _searchQuery = string.Empty;
        [ObservableProperty] private bool _isSearching;
        [ObservableProperty] private string _resultsDescription = "Loading MangaDex recommendations";

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
        [ObservableProperty] private string _readerStatus = string.Empty;

        public MangaViewModel(MangaService mangaService)
        {
            _mangaService = mangaService;
        }

        public void Initialize()
        {
            if (_isInitialized || _isDisposed)
            {
                return;
            }

            _isInitialized = true;
            _ = LoadRecommendationsAsync(_lifecycleCts.Token);
        }

        public void CancelActiveWork()
        {
            if (!_lifecycleCts.IsCancellationRequested)
            {
                _lifecycleCts.Cancel();
            }

            _searchCts?.Cancel();
            _chapterCts?.Cancel();
            _pageCts?.Cancel();
        }

        private async Task LoadRecommendationsAsync(CancellationToken token)
        {
            var linkedCts = BeginOperation(ref _searchCts, out int generation, token, OperationKind.Search);
            token = linkedCts.Token;
            IsSearching = true;
            ResultsDescription = "Trending manga from MangaDex";

            try
            {
                var results = await _mangaService.GetRecommendedMangaAsync(token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentSearchGeneration(generation))
                {
                    return;
                }

                MangaResults.ReplaceRange(results);
                CurrentViewMode = 0;

                if (results.Count == 0)
                {
                    ResultsDescription = "No MangaDex recommendations found";
                }

                AppLogger.Log($"Loaded {results.Count} MangaDex recommendations.");
            }
            catch (OperationCanceledException)
            {
                AppLogger.Log("Manga recommendations load was cancelled.");
            }
            catch (Exception ex)
            {
                ResultsDescription = "MangaDex recommendations failed to load";
                AppLogger.Log($"Failed to load MangaDex recommendations: {ex.Message}", "ERROR");
            }
            finally
            {
                if (IsCurrentSearchGeneration(generation))
                {
                    IsSearching = false;
                }

                ClearOperation(ref _searchCts, linkedCts);
            }
        }

        // ── Search Command ───────────────────────────────────
        [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
        private async Task SearchMangaAsync(CancellationToken token)
        {
            AppLogger.Log($"SearchMangaAsync invoked. Query='{SearchQuery}'");
            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                await LoadRecommendationsAsync(token);
                return;
            }

            var linkedCts = BeginOperation(ref _searchCts, out int generation, token, OperationKind.Search);
            token = linkedCts.Token;
            IsSearching = true;
            ResultsDescription = $"MangaDex results for \"{SearchQuery.Trim()}\"";
            try
            {
                AppLogger.Log($"Querying manga service for: '{SearchQuery}'...");
                var results = await _mangaService.SearchMangaAsync(SearchQuery, token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentSearchGeneration(generation))
                {
                    return;
                }

                MangaResults.ReplaceRange(results);

                CurrentViewMode = 0;
                if (results.Count == 0)
                {
                    ResultsDescription = $"No MangaDex results for \"{SearchQuery.Trim()}\"";
                }

                AppLogger.Log($"SearchMangaAsync complete. Found {results.Count} results.");
            }
            catch (OperationCanceledException)
            {
                AppLogger.Log("SearchMangaAsync command was cancelled by user.");
            }
            catch (Exception ex)
            {
                ResultsDescription = "MangaDex search failed; showing previous results";
                AppLogger.Log($"SearchMangaAsync failed. Error: {ex.Message}", "ERROR");
            }
            finally
            {
                if (IsCurrentSearchGeneration(generation))
                {
                    IsSearching = false;
                }

                ClearOperation(ref _searchCts, linkedCts);
            }
        }

        // ── Read Command (from results grid) ─────────────────
        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task ReadAsync(MangaSearchResult? manga)
        {
            if (manga == null) return;
            AppLogger.Log($"ReadCommand invoked for manga: '{manga.Title}' (Id={manga.Id})");
            var linkedCts = BeginOperation(ref _chapterCts, out int generation, CancellationToken.None, OperationKind.Chapter);
            var token = linkedCts.Token;
            _pageCts?.Cancel();

            SelectedManga = manga;
            Chapters.Clear();
            PageUrls.Clear();
            CurrentViewMode = 1;
            Breadcrumb = manga.Title;
            ReaderStatus = "Loading chapters...";
            IsLoadingChapters = true;

            try
            {
                var chapters = await _mangaService.GetChaptersAsync(manga.Id, token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentChapterGeneration(generation) ||
                    !string.Equals(SelectedManga?.Id, manga.Id, StringComparison.Ordinal))
                {
                    return;
                }

                AppLogger.Log($"Loaded {chapters.Count} chapters for '{manga.Title}'");
                Chapters.ReplaceRange(chapters);
                ReaderStatus = chapters.Count == 0 ? "No English chapters are available for this manga." : string.Empty;

                if (Chapters.Count == 0)
                {
                    AppLogger.Log("No chapters found for this manga.", "WARNING");
                }
            }
            catch (OperationCanceledException)
            {
                AppLogger.Log($"Chapter load cancelled for '{manga.Title}'.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to load chapters: {ex.Message}", "ERROR");
                if (IsCurrentChapterGeneration(generation))
                    ReaderStatus = "Could not load chapters. Go back and retry.";
            }
            finally
            {
                if (IsCurrentChapterGeneration(generation))
                {
                    IsLoadingChapters = false;
                }

                ClearOperation(ref _chapterCts, linkedCts);
            }
        }

        // ── Select Chapter Command ────────────────────────────
        [RelayCommand(AllowConcurrentExecutions = false)]
        private async Task SelectChapterAsync(MangaChapter? chapter)
        {
            if (chapter == null) return;
            AppLogger.Log($"SelectChapterCommand invoked: Ch {chapter.ChapterNumber} - '{chapter.Title}' (Id={chapter.Id})");
            var linkedCts = BeginOperation(ref _pageCts, out int generation, CancellationToken.None, OperationKind.Page);
            var token = linkedCts.Token;

            SelectedChapter = chapter;
            Breadcrumb = $"{SelectedManga?.Title ?? "Manga"} \u203A Ch. {chapter.ChapterNumber}";
            ReaderStatus = string.Empty;

            // If chapter has an external URL, open in WebView
            if (!string.IsNullOrEmpty(chapter.ExternalUrl))
            {
                AppLogger.Log($"Chapter has externalUrl='{chapter.ExternalUrl}' — opening WebView reader.");
                ExternalUrl = chapter.ExternalUrl;
                CurrentViewMode = 3;
                ClearOperation(ref _pageCts, linkedCts);
                return;
            }

            // Otherwise fetch pages from MangaDex at-home server
            PageUrls.Clear();
            CurrentViewMode = 2;
            IsLoadingPages = true;
            ReaderStatus = "Loading chapter pages...";

            try
            {
                var pages = await _mangaService.GetPageUrlsAsync(chapter.Id, token);
                token.ThrowIfCancellationRequested();
                if (!IsCurrentPageGeneration(generation) ||
                    !string.Equals(SelectedChapter?.Id, chapter.Id, StringComparison.Ordinal))
                {
                    return;
                }

                AppLogger.Log($"Loaded {pages.Count} pages for chapter '{chapter.ChapterNumber}'");
                PageUrls.ReplaceRange(pages);
                ReaderStatus = pages.Count == 0 ? "No pages are available for this chapter. Select another chapter." : string.Empty;

                if (PageUrls.Count == 0)
                {
                    AppLogger.Log("No page URLs found for this chapter — might be external-only.", "WARNING");
                    // Fallback: go back to chapter list
                    CurrentViewMode = 1;
                }
            }
            catch (OperationCanceledException)
            {
                AppLogger.Log($"Page load cancelled for chapter '{chapter.ChapterNumber}'.");
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to load chapter pages: {ex.Message}", "ERROR");
                if (IsCurrentPageGeneration(generation))
                {
                    CurrentViewMode = 1;
                    ReaderStatus = "Could not load chapter pages. Select a chapter to retry.";
                }
            }
            finally
            {
                if (IsCurrentPageGeneration(generation))
                {
                    IsLoadingPages = false;
                }

                ClearOperation(ref _pageCts, linkedCts);
            }
        }

        // ── Go Back Command ───────────────────────────────────
        [RelayCommand]
        private void GoBack()
        {
            AppLogger.Log($"GoBackCommand invoked from mode={CurrentViewMode}");
            ReaderStatus = string.Empty;
            switch (CurrentViewMode)
            {
                case 3:
                case 2:
                    // Back to chapters
                    _pageCts?.Cancel();
                    PageUrls.Clear();
                    ExternalUrl = string.Empty;
                    CurrentViewMode = 1;
                    Breadcrumb = SelectedManga?.Title ?? "Manga";
                    break;
                case 1:
                    // Back to search results
                    _chapterCts?.Cancel();
                    _pageCts?.Cancel();
                    Chapters.Clear();
                    SelectedManga = null;
                    CurrentViewMode = 0;
                    Breadcrumb = string.Empty;
                    break;
                default:
                    break;
            }
        }

        private enum OperationKind
        {
            Search,
            Chapter,
            Page
        }

        private CancellationTokenSource BeginOperation(
            ref CancellationTokenSource? operationCts,
            out int generation,
            CancellationToken commandToken,
            OperationKind kind)
        {
            operationCts?.Cancel();
            operationCts?.Dispose();
            operationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token, commandToken);

            generation = kind switch
            {
                OperationKind.Search => Interlocked.Increment(ref _searchGeneration),
                OperationKind.Chapter => Interlocked.Increment(ref _chapterGeneration),
                _ => Interlocked.Increment(ref _pageGeneration)
            };

            return operationCts;
        }

        private static void ClearOperation(ref CancellationTokenSource? operationCts, CancellationTokenSource completedCts)
        {
            if (ReferenceEquals(operationCts, completedCts))
            {
                operationCts = null;
            }

            completedCts.Dispose();
        }

        private bool IsCurrentSearchGeneration(int generation)
        {
            return !_lifecycleCts.IsCancellationRequested &&
                   generation == Volatile.Read(ref _searchGeneration);
        }

        private bool IsCurrentChapterGeneration(int generation)
        {
            return !_lifecycleCts.IsCancellationRequested &&
                   generation == Volatile.Read(ref _chapterGeneration);
        }

        private bool IsCurrentPageGeneration(int generation)
        {
            return !_lifecycleCts.IsCancellationRequested &&
                   generation == Volatile.Read(ref _pageGeneration);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            CancelActiveWork();
            _searchCts?.Dispose();
            _chapterCts?.Dispose();
            _pageCts?.Dispose();
            _lifecycleCts.Dispose();
        }
    }
}
