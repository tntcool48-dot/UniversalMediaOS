using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia.Books;

namespace UniversalMediaOS.WPF.ViewModels;

public sealed partial class BookBrowseViewModel : ObservableObject, IDisposable
{
    private const string DefaultBrowseQuery = "classic literature";
    private readonly BookCatalogService _catalogService;
    private readonly LocalBookImportService _localImportService;
    private readonly IReadingProgressStore _progressStore;
    private readonly CancellationTokenSource _lifecycleCts = new();
    private CancellationTokenSource? _searchCts;
    private int _searchGeneration;
    private bool _isInitialized;
    private bool _isDisposed;

    public BookBrowseViewModel(
        BookCatalogService catalogService,
        LocalBookImportService localImportService,
        IReadingProgressStore progressStore)
    {
        _catalogService = catalogService ??
            throw new ArgumentNullException(nameof(catalogService));
        _localImportService = localImportService ??
            throw new ArgumentNullException(nameof(localImportService));
        _progressStore = progressStore ??
            throw new ArgumentNullException(nameof(progressStore));
    }

    public ObservableCollection<BookRecord> Books { get; } = new();

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage =
        "Search every enabled book catalog, or import a local EPUB/PDF.";

    [ObservableProperty]
    private int _totalAvailable;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public int ResultCount => Books.Count;

    public bool HasBooks => Books.Count > 0;

    public void Initialize()
    {
        if (_isInitialized || _isDisposed)
        {
            return;
        }

        _isInitialized = true;
        _ = LoadDefaultBrowseAsync(_lifecycleCts.Token);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed || _isInitialized)
        {
            return;
        }

        _isInitialized = true;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifecycleCts.Token,
            cancellationToken);
        await LoadDefaultBrowseAsync(linked.Token);
    }

    [RelayCommand(IncludeCancelCommand = true, AllowConcurrentExecutions = false)]
    private async Task SearchBooksAsync(CancellationToken cancellationToken)
    {
        string query = SearchQuery.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            await LoadDefaultBrowseAsync(cancellationToken);
            return;
        }

        await SearchCoreAsync(
            query,
            $"Results for \u201C{query}\u201D",
            cancellationToken);
    }

    [RelayCommand]
    private static void OpenDetails(BookRecord? book)
    {
        if (book != null)
        {
            WeakReferenceMessenger.Default.Send(new NavigateToBookDetailsMessage(book));
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task ShowContinueReadingAsync()
    {
        CancellationTokenSource operation = BeginSearch(
            _lifecycleCts.Token,
            out int generation);
        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = "Loading recent books\u2026";
        try
        {
            IReadOnlyList<BookReadingProgress> progress = await _progressStore
                .GetAllAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(generation))
            {
                return;
            }

            Books.Clear();
            foreach (BookReadingProgress entry in progress
                         .Where(IsDiscoverableProgress)
                         .GroupBy(entry => entry.BookId, StringComparer.OrdinalIgnoreCase)
                         .Select(group => group
                             .OrderByDescending(entry => entry.UpdatedAtUtc)
                             .First())
                         .OrderByDescending(entry => entry.UpdatedAtUtc))
            {
                Books.Add(ToRecentBook(entry));
            }

            TotalAvailable = Books.Count;
            StatusMessage = Books.Count == 0
                ? "No recent books yet. Open an edition and save progress first."
                : $"{Books.Count} recent book{(Books.Count == 1 ? string.Empty : "s")} ready to continue";
            NotifyCollectionStateChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "Recent reading history could not be loaded.";
            StatusMessage = "Book catalogs are still available.";
            AppLogger.Log($"Recent book history load failed: {ex.Message}", "WARNING");
        }
        finally
        {
            if (IsCurrent(generation))
            {
                IsBusy = false;
            }

            ClearSearch(operation);
        }
    }

    public async Task<BookRecord?> ImportLocalFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed)
        {
            return null;
        }

        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = "Reading local book metadata\u2026";
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                _lifecycleCts.Token,
                cancellationToken);
            LocalBookImportResult imported = await _localImportService
                .ImportAsync(filePath, linked.Token);
            BookRecord? existing = Books.FirstOrDefault(book =>
                book.Id.Equals(imported.Book.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                Books.Remove(existing);
            }

            Books.Insert(0, imported.Book);
            TotalAvailable = Math.Max(TotalAvailable, Books.Count);
            StatusMessage = $"Imported {imported.Book.Title}";
            NotifyCollectionStateChanged();
            WeakReferenceMessenger.Default.Send(
                new NavigateToBookDetailsMessage(imported.Book));
            return imported.Book;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested ||
            _lifecycleCts.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (
            ex is IOException or InvalidDataException or
            NotSupportedException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "The local book could not be imported.";
            AppLogger.Log($"Local book import failed: {ex.Message}", "WARNING");
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task LoadDefaultBrowseAsync(CancellationToken cancellationToken)
    {
        return SearchCoreAsync(
            DefaultBrowseQuery,
            "Featured public catalog titles",
            cancellationToken);
    }

    private async Task SearchCoreAsync(
        string query,
        string description,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource operation = BeginSearch(cancellationToken, out int generation);
        IsBusy = true;
        ErrorMessage = string.Empty;
        StatusMessage = "Searching book catalogs\u2026";
        try
        {
            BookSearchPage page = await _catalogService
                .SearchAsync(query, cancellationToken: operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(generation))
            {
                return;
            }

            Books.Clear();
            foreach (BookRecord book in page.Items)
            {
                Books.Add(book);
            }

            TotalAvailable = page.Total;
            StatusMessage = Books.Count == 0
                ? "No matching books were found."
                : description;
            NotifyCollectionStateChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidOperationException)
        {
            ErrorMessage = "Book catalogs are unavailable right now.";
            StatusMessage = "Your existing results were left unchanged.";
            AppLogger.Log($"Book catalog search failed: {ex.Message}", "WARNING");
        }
        finally
        {
            if (IsCurrent(generation))
            {
                IsBusy = false;
            }

            ClearSearch(operation);
        }
    }

    private CancellationTokenSource BeginSearch(
        CancellationToken cancellationToken,
        out int generation)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifecycleCts.Token,
            cancellationToken);
        generation = Interlocked.Increment(ref _searchGeneration);
        return _searchCts;
    }

    private void ClearSearch(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_searchCts, operation))
        {
            _searchCts = null;
        }

        operation.Dispose();
    }

    private bool IsCurrent(int generation)
    {
        return !_lifecycleCts.IsCancellationRequested &&
               generation == Volatile.Read(ref _searchGeneration);
    }

    private void NotifyCollectionStateChanged()
    {
        OnPropertyChanged(nameof(ResultCount));
        OnPropertyChanged(nameof(HasBooks));
    }

    private static bool IsDiscoverableProgress(BookReadingProgress progress)
    {
        return !string.IsNullOrWhiteSpace(progress.BookId) &&
               !string.IsNullOrWhiteSpace(progress.BookTitle) &&
               !string.IsNullOrWhiteSpace(progress.AssetId) &&
               !string.IsNullOrWhiteSpace(progress.AssetLocation) &&
               !progress.IsCompleted &&
               progress.AssetFormat is BookFileFormat.Epub or BookFileFormat.Pdf;
    }

    private static BookRecord ToRecentBook(BookReadingProgress progress)
    {
        bool legacyAnnaRights = progress.AssetId.StartsWith("annas:", StringComparison.Ordinal) &&
            progress.AssetAccess == BookAccessKind.PublicDomain;
        var asset = new BookAsset
        {
            Id = progress.AssetId,
            BookId = progress.BookId,
            DisplayName = string.IsNullOrWhiteSpace(progress.AssetDisplayName)
                ? progress.BookTitle
                : progress.AssetDisplayName,
            Format = progress.AssetFormat,
            Access = legacyAnnaRights ? BookAccessKind.RightsUnverified : progress.AssetAccess,
            Location = progress.AssetLocation,
            SourceLabel = progress.AssetSourceLabel,
            RightsStatement = legacyAnnaRights ? "Rights have not been verified for this indexed file." : progress.AssetRightsStatement,
            IsDownloadAllowed = progress.AssetDownloadAllowed,
            ExpectedMd5 = progress.AssetExpectedMd5
        };
        return new BookRecord
        {
            Id = progress.BookId,
            Source = progress.BookSource,
            Title = progress.BookTitle,
            Authors = progress.BookAuthors,
            CoverUrl = progress.CoverUrl,
            Assets = [asset],
            Subjects = progress.IsCompleted
                ? ["Completed"]
                : [$"{progress.Percent}% read"]
        };
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _lifecycleCts.Cancel();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _lifecycleCts.Dispose();
    }
}
