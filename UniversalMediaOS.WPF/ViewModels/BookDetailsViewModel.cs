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

public sealed partial class BookDetailsViewModel : ObservableObject, IDisposable
{
    private readonly BookCatalogService _catalogService;
    private readonly InternetArchiveBookService _archiveService;
    private readonly AnnasArchiveBookProvider _annasService;
    private readonly IReadingProgressStore _progressStore;
    private readonly CancellationTokenSource _lifecycleCts = new();
    private CancellationTokenSource? _loadCts;
    private int _loadGeneration;
    private bool _isDisposed;

    public BookDetailsViewModel(
        BookCatalogService catalogService,
        InternetArchiveBookService archiveService,
        AnnasArchiveBookProvider annasService,
        IReadingProgressStore progressStore)
    {
        _catalogService = catalogService ??
            throw new ArgumentNullException(nameof(catalogService));
        _archiveService = archiveService ??
            throw new ArgumentNullException(nameof(archiveService));
        _annasService = annasService ??
            throw new ArgumentNullException(nameof(annasService));
        _progressStore = progressStore ??
            throw new ArgumentNullException(nameof(progressStore));
    }

    public ObservableCollection<BookAsset> Assets { get; } = new();

    [ObservableProperty]
    private BookRecord? _book;

    [ObservableProperty]
    private BookReadingProgress? _readingProgress;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _availabilityMessage = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool HasAssets => Assets.Count > 0;

    public string ProgressText => ReadingProgress == null
        ? "Not started"
        : ReadingProgress.IsCompleted
            ? "Completed"
            : $"{ReadingProgress.Percent}% read";

    public async Task InitializeAsync(
        BookRecord book,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (_isDisposed)
        {
            return;
        }

        CancellationTokenSource operation = BeginLoad(cancellationToken, out int generation);
        Book = book;
        ReadingProgress = null;
        ErrorMessage = string.Empty;
        ReplaceAssets(book.Assets);
        IsLoading = true;
        AvailabilityMessage = book.Source == BookCatalogSource.Local
            ? "Checking the local book\u2026"
            : "Checking available EPUB and PDF editions\u2026";

        try
        {
            IReadOnlyList<BookReadingProgress> savedProgress = await _progressStore
                .GetAllAsync(operation.Token);
            ReadingProgress = savedProgress
                .Where(progress => progress.BookId.Equals(
                    book.Id,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(progress => progress.UpdatedAtUtc)
                .FirstOrDefault();

            BookRecord enrichedBook = await _catalogService
                .GetDetailsAsync(book, operation.Token) ?? book;
            operation.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(generation))
            {
                return;
            }

            Book = enrichedBook;
            ReplaceAssets(book.Assets.Concat(enrichedBook.Assets));

            if (book.Source != BookCatalogSource.Local)
            {
                Task<IReadOnlyList<BookAsset>> archiveTask = FindAssetsSafelyAsync(
                    () => _archiveService.FindAssetsAsync(enrichedBook, operation.Token),
                    "Internet Archive",
                    operation.Token);
                Task<IReadOnlyList<BookAsset>> annasTask = FindAssetsSafelyAsync(
                    () => _annasService.FindAssetsAsync(enrichedBook, operation.Token),
                    "Anna's Archive",
                    operation.Token);

                await Task.WhenAll(archiveTask, annasTask);

                operation.Token.ThrowIfCancellationRequested();
                if (!IsCurrent(generation))
                {
                    return;
                }

                ReplaceAssets(
                    book.Assets
                        .Concat(enrichedBook.Assets)
                        .Concat(archiveTask.Result)
                        .Concat(annasTask.Result));
            }

            if (ReadingProgress == null && GetPreferredAsset() is { } preferred)
            {
                ReadingProgress = await _progressStore
                    .GetAsync(book.Id, preferred.Id, operation.Token);
            }

            if (!IsCurrent(generation))
            {
                return;
            }

            AvailabilityMessage = Assets.Count switch
            {
                0 => "No EPUB or PDF edition was found. Catalog metadata remains available.",
                1 => "1 readable edition available.",
                _ => $"{Assets.Count} readable editions available."
            };
            OnPropertyChanged(nameof(ProgressText));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidOperationException)
        {
            ErrorMessage = "Edition availability could not be checked.";
            AvailabilityMessage = Assets.Count == 0
                ? "Catalog metadata is still available."
                : "Showing editions already attached to this catalog record.";
            AppLogger.Log($"Book availability check failed: {ex.Message}", "WARNING");
        }
        finally
        {
            if (IsCurrent(generation))
            {
                IsLoading = false;
            }

            ClearLoad(operation);
        }
    }

    private bool CanReadPreferred => HasAssets;

    [RelayCommand(CanExecute = nameof(CanReadPreferred))]
    private void ReadPreferred()
    {
        SendReaderMessage(GetPreferredAsset());
    }

    [RelayCommand]
    private void OpenEdition(BookAsset? asset)
    {
        SendReaderMessage(asset);
    }

    [RelayCommand]
    private void GoBack()
    {
        WeakReferenceMessenger.Default.Send(new CloseTabMessage(this));
    }

    private void SendReaderMessage(BookAsset? asset)
    {
        if (Book != null && asset != null)
        {
            WeakReferenceMessenger.Default.Send(
                new NavigateToBookReaderMessage(Book, asset));
        }
    }

    private BookAsset? GetPreferredAsset()
    {
        if (!string.IsNullOrWhiteSpace(ReadingProgress?.AssetId))
        {
            BookAsset? savedAsset = Assets.FirstOrDefault(asset =>
                asset.Id.Equals(ReadingProgress.AssetId, StringComparison.OrdinalIgnoreCase));
            if (savedAsset != null)
            {
                return savedAsset;
            }
        }

        return Assets
            .OrderBy(asset => asset.Format == BookFileFormat.Epub ? 0 : 1)
            .ThenBy(asset => asset.Access == BookAccessKind.Local ? 0 : 1)
            .FirstOrDefault();
    }

    private void ReplaceAssets(IEnumerable<BookAsset> assets)
    {
        Assets.Clear();
        foreach (BookAsset asset in assets
                     .Where(IsReadableAsset)
                     .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
        {
            Assets.Add(asset);
        }

        OnPropertyChanged(nameof(HasAssets));
        ReadPreferredCommand.NotifyCanExecuteChanged();
    }

    private static bool IsReadableAsset(BookAsset asset)
    {
        if (asset.Format is not (BookFileFormat.Epub or BookFileFormat.Pdf))
        {
            return false;
        }

        return asset.Access == BookAccessKind.Local || asset.IsDownloadAllowed;
    }

    private static async Task<IReadOnlyList<BookAsset>> FindAssetsSafelyAsync(
        Func<Task<IReadOnlyList<BookAsset>>> loader,
        string providerName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await loader();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or InvalidOperationException or IOException or TimeoutException)
        {
            AppLogger.Log($"{providerName} book edition lookup failed: {ex.Message}", "WARNING");
            return Array.Empty<BookAsset>();
        }
    }

    private CancellationTokenSource BeginLoad(
        CancellationToken cancellationToken,
        out int generation)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifecycleCts.Token,
            cancellationToken);
        generation = Interlocked.Increment(ref _loadGeneration);
        return _loadCts;
    }

    private void ClearLoad(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_loadCts, operation))
        {
            _loadCts = null;
        }

        operation.Dispose();
    }

    private bool IsCurrent(int generation)
    {
        return !_lifecycleCts.IsCancellationRequested &&
               generation == Volatile.Read(ref _loadGeneration);
    }

    partial void OnReadingProgressChanged(BookReadingProgress? value)
    {
        OnPropertyChanged(nameof(ProgressText));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _lifecycleCts.Cancel();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _lifecycleCts.Dispose();
    }
}
