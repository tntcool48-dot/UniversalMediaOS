using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using UniversalMediaOS.Core.OtherMedia;
using UniversalMediaOS.Core.OtherMedia.Books;
using UniversalMediaOS.Core.Search;
using UniversalMediaOS.Core.Services;
using UniversalMediaOS.WPF.Helpers;

namespace UniversalMediaOS.WPF.ViewModels;

public sealed class TrackedMediaItem
{
    public string Title { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Year { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Progress { get; init; } = string.Empty;
    public string Availability { get; init; } = string.Empty;
    public string Score { get; init; } = string.Empty;
    public string CoverImageUrl { get; init; } = string.Empty;
    public string Accent { get; init; } = "#8B5CF6";
    public MediaResult? Anime { get; init; }
    public AudiovisualMediaItem? Audiovisual { get; init; }
    public BookRecord? Book { get; init; }
}

public sealed partial class MyListViewModel : ObservableObject, IDisposable
{
    private readonly FavoriteMediaService _favoriteMediaService;
    private readonly AudiovisualLibraryService _audiovisualLibraryService;
    private readonly IReadingProgressStore _readingProgressStore;
    private readonly IDialogService _dialogService;
    private readonly CancellationTokenSource _lifecycleCts = new();
    private List<TrackedMediaItem> _allItems = new();
    private bool _isDisposed;

    private string _selectedFilter = "All";

    public ObservableRangeCollection<TrackedMediaItem> Items { get; } = new();

    public int Total => _allItems.Count;
    public int Completed => _allItems.Count(item => IsCompleted(item.Status));
    public int Watching => _allItems.Count(item => IsInProgress(item.Status));
    public string AverageScore
    {
        get
        {
            var scores = _allItems
                .Select(item => double.TryParse(
                    item.Score,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double score)
                        ? score
                        : 0)
                .Where(score => score > 0)
                .ToList();
            return scores.Count == 0
                ? "-"
                : scores.Average().ToString("0.0", CultureInfo.InvariantCulture);
        }
    }

    public string AllFilterText => $"All ({Total})";
    public string WatchingFilterText => $"In progress ({Watching})";
    public string CompletedFilterText => $"Completed ({Completed})";
    public bool HasItems => Items.Count > 0;

    public MyListViewModel(
        FavoriteMediaService favoriteMediaService,
        AudiovisualLibraryService audiovisualLibraryService,
        IReadingProgressStore readingProgressStore,
        IDialogService dialogService)
    {
        _favoriteMediaService = favoriteMediaService ??
            throw new ArgumentNullException(nameof(favoriteMediaService));
        _audiovisualLibraryService = audiovisualLibraryService ??
            throw new ArgumentNullException(nameof(audiovisualLibraryService));
        _readingProgressStore = readingProgressStore ??
            throw new ArgumentNullException(nameof(readingProgressStore));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

        _favoriteMediaService.FavoritesChanged += MediaLibraryChanged;
        _audiovisualLibraryService.LibraryChanged += MediaLibraryChanged;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void ShowAll() => ApplyFilter("All");

    [RelayCommand]
    private void ShowWatching() => ApplyFilter("Watching");

    [RelayCommand]
    private void ShowCompleted() => ApplyFilter("Completed");

    [RelayCommand]
    private static void OpenDetails(TrackedMediaItem? item)
    {
        if (item?.Anime != null)
        {
            WeakReferenceMessenger.Default.Send(new NavigateToDetailsMessage(item.Anime));
        }
        else if (item?.Audiovisual != null)
        {
            WeakReferenceMessenger.Default.Send(
                new NavigateToAudiovisualDetailsMessage(item.Audiovisual));
        }
        else if (item?.Book != null)
        {
            WeakReferenceMessenger.Default.Send(new NavigateToBookDetailsMessage(item.Book));
        }
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RemoveFavoriteAsync(TrackedMediaItem? item)
    {
        if (item == null ||
            !_dialogService.ShowConfirmDialog(
                $"Remove '{item.Title}' from My List?",
                "Remove from My List"))
        {
            return;
        }

        if (item.Anime != null)
        {
            _favoriteMediaService.Toggle(item.Anime);
        }
        else if (item.Audiovisual != null)
        {
            await _audiovisualLibraryService.RemoveAsync(
                CreateLibraryKey(item.Audiovisual),
                _lifecycleCts.Token);
        }
        else if (item.Book != null)
        {
            IReadOnlyList<BookReadingProgress> progress = await _readingProgressStore
                .GetAllAsync(_lifecycleCts.Token);
            foreach (BookReadingProgress entry in progress.Where(entry =>
                         entry.BookId.Equals(item.Book.Id, StringComparison.OrdinalIgnoreCase)))
            {
                await _readingProgressStore.RemoveAsync(
                    entry.BookId,
                    entry.AssetId,
                    _lifecycleCts.Token);
            }
        }

        WeakReferenceMessenger.Default.Send(
            new ToastNotificationMessage($"Removed from My List: {item.Title}"));
        await RefreshAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            List<TrackedMediaItem> anime = _favoriteMediaService.GetFavorites()
                .Select(ToTrackedAnime)
                .ToList();
            Task<IReadOnlyList<AudiovisualLibraryEntry>> audiovisualTask =
                _audiovisualLibraryService.GetAllAsync(
                    kind: null,
                    _lifecycleCts.Token);
            Task<IReadOnlyList<BookReadingProgress>> booksTask =
                _readingProgressStore.GetAllAsync(_lifecycleCts.Token);
            await Task.WhenAll(audiovisualTask, booksTask);
            _lifecycleCts.Token.ThrowIfCancellationRequested();

            IEnumerable<TrackedMediaItem> audiovisual = audiovisualTask.Result
                .Where(entry =>
                    entry.IsFavorite ||
                    entry.Status != AudiovisualLibraryStatus.None ||
                    entry.LastOpenedUtc.HasValue)
                .Select(ToTrackedAudiovisual);
            IEnumerable<TrackedMediaItem> books = booksTask.Result
                .Where(progress =>
                    !string.IsNullOrWhiteSpace(progress.BookId) &&
                    !string.IsNullOrWhiteSpace(progress.BookTitle))
                .GroupBy(progress => progress.BookId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(progress => progress.UpdatedAtUtc)
                    .First())
                .Select(ToTrackedBook);

            _allItems = anime
                .Concat(audiovisual)
                .Concat(books)
                .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Kind, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            ApplyFilter(_selectedFilter);
            NotifySummaryChanged();
        }
        catch (OperationCanceledException) when (_lifecycleCts.IsCancellationRequested)
        {
        }
    }

    private void MediaLibraryChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() => RefreshCommand.Execute(null));
        }
        else
        {
            RefreshCommand.Execute(null);
        }
    }

    private void ApplyFilter(string filter)
    {
        _selectedFilter = filter;
        IEnumerable<TrackedMediaItem> filtered = filter switch
        {
            "Watching" => _allItems.Where(item => IsInProgress(item.Status)),
            "Completed" => _allItems.Where(item => IsCompleted(item.Status)),
            _ => _allItems
        };

        Items.ReplaceRange(filtered);
        OnPropertyChanged(nameof(HasItems));
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(Completed));
        OnPropertyChanged(nameof(Watching));
        OnPropertyChanged(nameof(AverageScore));
        OnPropertyChanged(nameof(AllFilterText));
        OnPropertyChanged(nameof(WatchingFilterText));
        OnPropertyChanged(nameof(CompletedFilterText));
    }

    private static bool IsInProgress(string status)
    {
        return status.Contains("Releasing", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("Ongoing", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("Watching", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("Reading", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompleted(string status)
    {
        return status.Contains("Finished", StringComparison.OrdinalIgnoreCase) ||
               status.Contains("Completed", StringComparison.OrdinalIgnoreCase);
    }

    private static TrackedMediaItem ToTrackedAnime(FavoriteMediaRecord record)
    {
        return new TrackedMediaItem
        {
            Title = record.Title,
            Kind = string.IsNullOrWhiteSpace(record.Kind) ? "Anime" : record.Kind,
            Year = record.Year,
            Status = "Favorite",
            Availability = string.IsNullOrWhiteSpace(record.Status) ? string.Empty : $"Availability: {record.Status}",
            Progress = string.IsNullOrWhiteSpace(record.Progress) ? string.Empty : $"Available: {record.Progress}",
            Score = string.IsNullOrWhiteSpace(record.Rating) ? "-" : record.Rating,
            CoverImageUrl = record.CoverImageUrl,
            Accent = "#8B5CF6",
            Anime = ToMediaResult(record)
        };
    }

    private static TrackedMediaItem ToTrackedAudiovisual(AudiovisualLibraryEntry entry)
    {
        string status = entry.Status switch
        {
            AudiovisualLibraryStatus.Completed => "Completed",
            AudiovisualLibraryStatus.Watching => "Watching",
            AudiovisualLibraryStatus.Planned => "Planned",
            _ => entry.IsFavorite ? "Favorite" : "Opened"
        };
        string progress = entry.LastEpisodeNumber is { } episode
            ? $"S{entry.LastSeasonNumber ?? 1:00}E{episode:00}"
            : entry.DurationSeconds > 0
                ? $"{Math.Clamp(entry.PositionSeconds * 100 / entry.DurationSeconds, 0, 100):0}%"
                : string.Empty;
        string kind = entry.Key.Kind switch
        {
            AudiovisualMediaKind.Movie => "Movie",
            AudiovisualMediaKind.Television => "TV Show",
            AudiovisualMediaKind.Cartoon => "Cartoon",
            _ => "Media"
        };
        string accent = entry.Key.Kind switch
        {
            AudiovisualMediaKind.Movie => "#06B6D4",
            AudiovisualMediaKind.Television => "#10B981",
            AudiovisualMediaKind.Cartoon => "#F97316",
            _ => "#06B6D4"
        };
        var media = AudiovisualLibraryMapping.ToMediaItem(entry);

        return new TrackedMediaItem
        {
            Title = entry.Title,
            Kind = kind,
            Year = entry.Key.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Status = status,
            Progress = progress,
            Score = "-",
            CoverImageUrl = entry.PosterUrl,
            Accent = accent,
            Audiovisual = media
        };
    }

    private static TrackedMediaItem ToTrackedBook(BookReadingProgress progress)
    {
        var asset = new BookAsset
        {
            Id = progress.AssetId,
            BookId = progress.BookId,
            DisplayName = string.IsNullOrWhiteSpace(progress.AssetDisplayName)
                ? progress.BookTitle
                : progress.AssetDisplayName,
            Format = progress.AssetFormat,
            Access = progress.AssetAccess,
            Location = progress.AssetLocation,
            SourceLabel = progress.AssetSourceLabel,
            RightsStatement = progress.AssetRightsStatement,
            IsDownloadAllowed = progress.AssetDownloadAllowed
        };
        var book = new BookRecord
        {
            Id = progress.BookId,
            Source = progress.BookSource,
            Title = progress.BookTitle,
            Authors = progress.BookAuthors,
            CoverUrl = progress.CoverUrl,
            Assets = [asset]
        };

        return new TrackedMediaItem
        {
            Title = progress.BookTitle,
            Kind = "Book",
            Status = progress.IsCompleted ? "Completed" : "Reading",
            Progress = $"{progress.Percent}%",
            Score = "-",
            CoverImageUrl = progress.CoverUrl,
            Accent = "#F59E0B",
            Book = book
        };
    }

    private static AudiovisualLibraryKey CreateLibraryKey(AudiovisualMediaItem item)
    {
        return AudiovisualLibraryKey.Create(item.Identity, item.PersistedWorkKey);
    }

    private static MediaResult ToMediaResult(FavoriteMediaRecord record)
    {
        int availableEpisodes = ParseEpisodeProgress(record.Progress);
        return new MediaResult
        {
            Id = record.AniListId,
            IdMal = record.MalId,
            OfficialTitle = record.Title,
            EnglishTitle = record.Title,
            RomajiTitle = record.Title,
            DisplayYear = record.Year,
            DisplayStatus = record.Status,
            DisplayRating = record.Rating,
            CoverImageUrl = record.CoverImageUrl,
            AvailableSubEpisodes = availableEpisodes,
            IsFavorite = true
        };
    }

    private static int ParseEpisodeProgress(string progress)
    {
        if (string.IsNullOrWhiteSpace(progress))
        {
            return 0;
        }

        string digits = new(progress.Where(char.IsDigit).ToArray());
        return int.TryParse(
            digits,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int parsed)
                ? parsed
                : 0;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _favoriteMediaService.FavoritesChanged -= MediaLibraryChanged;
        _audiovisualLibraryService.LibraryChanged -= MediaLibraryChanged;
        _lifecycleCts.Cancel();
        _lifecycleCts.Dispose();
    }
}
