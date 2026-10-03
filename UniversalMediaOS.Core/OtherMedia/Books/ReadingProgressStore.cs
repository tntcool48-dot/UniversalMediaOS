using System.Text.Json;

namespace UniversalMediaOS.Core.OtherMedia.Books;

public interface IReadingProgressStore
{
    Task<BookReadingProgress?> GetAsync(
        string bookId,
        string assetId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BookReadingProgress>> GetAllAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        BookReadingProgress progress,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        string bookId,
        string assetId,
        CancellationToken cancellationToken = default);
}

public sealed class JsonReadingProgressStore : IReadingProgressStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _storagePath;
    private Dictionary<string, BookReadingProgress>? _entries;

    public JsonReadingProgressStore(string? storagePath = null)
    {
        _storagePath = Path.GetFullPath(storagePath ?? Path.Combine(
            UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory,
            "UniversalMediaOS",
            "book_reading_progress.json"));
    }

    public async Task<BookReadingProgress?> GetAsync(
        string bookId,
        string assetId,
        CancellationToken cancellationToken = default)
    {
        ValidateKeyPart(bookId, nameof(bookId));
        ValidateKeyPart(assetId, nameof(assetId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, BookReadingProgress> entries = await LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            return entries.TryGetValue(BuildKey(bookId, assetId), out BookReadingProgress? progress)
                ? progress
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<BookReadingProgress>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, BookReadingProgress> entries = await LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            return entries.Values
                .OrderByDescending(progress => progress.UpdatedAtUtc)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        BookReadingProgress progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ValidateKeyPart(progress.BookId, nameof(progress.BookId));
        ValidateKeyPart(progress.AssetId, nameof(progress.AssetId));
        BookReadingProgress normalized = progress with
        {
            ChapterIndex = Math.Max(0, progress.ChapterIndex),
            PageNumber = Math.Max(0, progress.PageNumber),
            Fraction = Math.Clamp(progress.Fraction, 0, 1),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, BookReadingProgress> entries = await LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            entries[BuildKey(normalized.BookId, normalized.AssetId)] = normalized;
            await PersistAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(
        string bookId,
        string assetId,
        CancellationToken cancellationToken = default)
    {
        ValidateKeyPart(bookId, nameof(bookId));
        ValidateKeyPart(assetId, nameof(assetId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, BookReadingProgress> entries = await LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (entries.Remove(BuildKey(bookId, assetId)))
            {
                await PersistAsync(entries, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, BookReadingProgress>> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (_entries != null)
        {
            return _entries;
        }

        if (!File.Exists(_storagePath))
        {
            _entries = new Dictionary<string, BookReadingProgress>(
                StringComparer.OrdinalIgnoreCase);
            return _entries;
        }

        try
        {
            await using var stream = new FileStream(
                _storagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            Dictionary<string, BookReadingProgress>? persisted =
                await JsonSerializer.DeserializeAsync<Dictionary<string, BookReadingProgress>>(
                        stream,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            _entries = persisted == null
                ? new Dictionary<string, BookReadingProgress>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, BookReadingProgress>(
                    persisted,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            _entries = new Dictionary<string, BookReadingProgress>(
                StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            _entries = new Dictionary<string, BookReadingProgress>(
                StringComparer.OrdinalIgnoreCase);
        }

        return _entries;
    }

    private async Task PersistAsync(
        IReadOnlyDictionary<string, BookReadingProgress> entries,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_storagePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = _storagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        entries,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _storagePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string BuildKey(string bookId, string assetId)
    {
        return $"{bookId.Length}:{bookId}{assetId}";
    }

    private static void ValidateKeyPart(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty progress key is required.", parameterName);
        }
    }
}
