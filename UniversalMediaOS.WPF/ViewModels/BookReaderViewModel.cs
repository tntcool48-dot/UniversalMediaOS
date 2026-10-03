using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia.Books;

namespace UniversalMediaOS.WPF.ViewModels;

public sealed partial class BookReaderViewModel : ObservableObject, IDisposable
{
    private readonly BookReaderService _readerService;
    private readonly IReadingProgressStore _progressStore;
    private readonly CancellationTokenSource _lifecycleCts = new();
    private CancellationTokenSource? _loadCts;
    private bool _isDisposed;

    public BookReaderViewModel(
        BookReaderService readerService,
        IReadingProgressStore progressStore)
    {
        _readerService = readerService ??
            throw new ArgumentNullException(nameof(readerService));
        _progressStore = progressStore ??
            throw new ArgumentNullException(nameof(progressStore));
    }

    public ObservableCollection<BookReaderChapter> Chapters { get; } = new();

    [ObservableProperty]
    private BookRecord? _book;

    [ObservableProperty]
    private BookAsset? _asset;

    [ObservableProperty]
    private BookReaderDocument? _document;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _currentLocation = string.Empty;

    [ObservableProperty]
    private int _currentChapterIndex;

    [ObservableProperty]
    private int _currentPage = 1;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool IsEpub => Document?.Format == BookFileFormat.Epub;

    public bool IsPdf => Document?.Format == BookFileFormat.Pdf;

    public bool HasChapters => Chapters.Count > 0;

    public string CurrentChapterTitle =>
        Chapters.ElementAtOrDefault(CurrentChapterIndex)?.Title ??
        (IsPdf ? $"Page {Math.Max(1, CurrentPage)}" : string.Empty);

    public string ProgressText =>
        $"{Math.Clamp(ProgressPercent, 0, 100):0}%";

    private bool CanGoPrevious =>
        IsPdf ? CurrentPage > 1 : IsEpub && CurrentChapterIndex > 0;

    private bool CanGoNext =>
        IsPdf ||
        IsEpub &&
        CurrentChapterIndex >= 0 &&
        CurrentChapterIndex < Chapters.Count - 1;

    public async Task InitializeAsync(
        BookRecord book,
        BookAsset asset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(asset);
        if (_isDisposed)
        {
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(
            _lifecycleCts.Token,
            cancellationToken);
        CancellationToken token = _loadCts.Token;
        Book = book;
        Asset = asset;
        Document = null;
        Chapters.Clear();
        CurrentLocation = string.Empty;
        ErrorMessage = string.Empty;
        StatusMessage = "Preparing reader\u2026";
        IsLoading = true;
        NotifyReaderStateChanged();

        try
        {
            BookReaderDocument document = await _readerService.PrepareAsync(asset, token);
            BookReadingProgress? progress = await _progressStore
                .GetAsync(book.Id, asset.Id, token);
            token.ThrowIfCancellationRequested();

            Document = document;
            foreach (BookReaderChapter chapter in document.Chapters)
            {
                Chapters.Add(chapter);
            }

            CurrentPage = Math.Max(1, progress?.PageNumber ?? 1);
            ProgressPercent = Math.Clamp((progress?.Fraction ?? 0) * 100, 0, 100);
            if (document.Format == BookFileFormat.Epub)
            {
                int restoredIndex = Math.Clamp(
                    progress?.ChapterIndex ?? 0,
                    0,
                    Math.Max(0, Chapters.Count - 1));
                SetCurrentChapter(restoredIndex, updateProgress: progress == null);
            }
            else
            {
                CurrentChapterIndex = 0;
                CurrentLocation = BuildPdfLocation(document.StartLocation, CurrentPage);
            }

            StatusMessage = progress == null
                ? "Ready to read"
                : $"Resumed at {progress.Percent}%";
            NotifyReaderStateChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (
            ex is IOException or InvalidDataException or
            HttpRequestException or UnauthorizedAccessException or
            NotSupportedException or InvalidOperationException)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "This edition could not be opened.";
            AppLogger.Log($"Book reader preparation failed: {ex.Message}", "ERROR");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousChapterAsync()
    {
        if (IsPdf)
        {
            CurrentPage = Math.Max(1, CurrentPage - 1);
        }
        else
        {
            SetCurrentChapter(CurrentChapterIndex - 1, updateProgress: true);
        }

        await TrySaveProgressAsync(isCompleted: false, _lifecycleCts.Token);
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextChapterAsync()
    {
        if (IsPdf)
        {
            CurrentPage = checked(CurrentPage + 1);
        }
        else
        {
            SetCurrentChapter(CurrentChapterIndex + 1, updateProgress: true);
        }

        await TrySaveProgressAsync(isCompleted: false, _lifecycleCts.Token);
    }

    [RelayCommand]
    private async Task SelectChapterAsync(BookReaderChapter? chapter)
    {
        if (chapter == null)
        {
            return;
        }

        SetCurrentChapter(chapter.Index, updateProgress: true);
        await TrySaveProgressAsync(isCompleted: false, _lifecycleCts.Token);
    }

    [RelayCommand]
    private async Task SaveProgressAsync()
    {
        if (await TrySaveProgressAsync(isCompleted: false, _lifecycleCts.Token))
        {
            StatusMessage = "Reading progress saved.";
        }
    }

    [RelayCommand]
    private async Task MarkCompletedAsync()
    {
        ProgressPercent = 100;
        if (IsEpub && Chapters.Count > 0)
        {
            SetCurrentChapter(Chapters.Count - 1, updateProgress: false);
        }

        if (await TrySaveProgressAsync(isCompleted: true, _lifecycleCts.Token))
        {
            StatusMessage = "Book marked complete.";
        }
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        try
        {
            await TrySaveProgressAsync(
                isCompleted: ProgressPercent >= 99.5,
                _lifecycleCts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        WeakReferenceMessenger.Default.Send(new CloseTabMessage(this));
    }

    public Task SaveCurrentProgressAsync(CancellationToken cancellationToken = default)
    {
        return SaveProgressCoreAsync(
            isCompleted: ProgressPercent >= 99.5,
            cancellationToken);
    }

    private void SetCurrentChapter(int index, bool updateProgress)
    {
        if (Chapters.Count == 0)
        {
            return;
        }

        CurrentChapterIndex = Math.Clamp(index, 0, Chapters.Count - 1);
        CurrentLocation = Chapters[CurrentChapterIndex].Location;
        CurrentPage = CurrentChapterIndex + 1;
        if (updateProgress)
        {
            ProgressPercent = ((CurrentChapterIndex + 1d) / Chapters.Count) * 100d;
        }

        NotifyReaderStateChanged();
    }

    private async Task SaveProgressCoreAsync(
        bool isCompleted,
        CancellationToken cancellationToken)
    {
        if (Book == null || Asset == null || Document == null)
        {
            return;
        }

        double fraction = isCompleted
            ? 1
            : Math.Clamp(ProgressPercent / 100d, 0, 1);
        var progress = new BookReadingProgress
        {
            BookId = Book.Id,
            AssetId = Asset.Id,
            BookTitle = Book.Title,
            BookAuthors = Book.Authors,
            CoverUrl = Book.CoverUrl,
            BookSource = Book.Source,
            AssetDisplayName = Asset.DisplayName,
            AssetLocation = Asset.Location,
            AssetSourceLabel = Asset.SourceLabel,
            AssetRightsStatement = Asset.RightsStatement,
            AssetFormat = Asset.Format,
            AssetAccess = Asset.Access,
            AssetDownloadAllowed = Asset.IsDownloadAllowed,
            ChapterIndex = Math.Max(0, CurrentChapterIndex),
            PageNumber = Math.Max(1, CurrentPage),
            Location = CurrentLocation,
            Fraction = fraction,
            IsCompleted = isCompleted
        };
        await _progressStore.SaveAsync(progress, cancellationToken);
    }

    private async Task<bool> TrySaveProgressAsync(
        bool isCompleted,
        CancellationToken cancellationToken)
    {
        try
        {
            await SaveProgressCoreAsync(isCompleted, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "Reading progress could not be saved.";
            AppLogger.Log($"Book progress save failed: {ex.Message}", "WARNING");
            return false;
        }
    }

    private void NotifyReaderStateChanged()
    {
        OnPropertyChanged(nameof(IsEpub));
        OnPropertyChanged(nameof(IsPdf));
        OnPropertyChanged(nameof(HasChapters));
        OnPropertyChanged(nameof(CurrentChapterTitle));
        OnPropertyChanged(nameof(ProgressText));
        PreviousChapterCommand.NotifyCanExecuteChanged();
        NextChapterCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentChapterIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentChapterTitle));
        PreviousChapterCommand.NotifyCanExecuteChanged();
        NextChapterCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentPageChanged(int value)
    {
        if (value < 1)
        {
            CurrentPage = 1;
            return;
        }

        if (IsPdf && Document != null)
        {
            CurrentLocation = BuildPdfLocation(Document.StartLocation, value);
        }

        OnPropertyChanged(nameof(CurrentChapterTitle));
        PreviousChapterCommand.NotifyCanExecuteChanged();
        NextChapterCommand.NotifyCanExecuteChanged();
    }

    partial void OnProgressPercentChanged(double value)
    {
        OnPropertyChanged(nameof(ProgressText));
    }

    partial void OnDocumentChanged(BookReaderDocument? value)
    {
        NotifyReaderStateChanged();
    }

    private static string BuildPdfLocation(string location, int page)
    {
        if (!Uri.TryCreate(location, UriKind.Absolute, out Uri? uri))
        {
            return location;
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = $"page={Math.Max(1, page)}"
        };
        return builder.Uri.AbsoluteUri;
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
