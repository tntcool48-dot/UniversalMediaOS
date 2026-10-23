using System.Text.Json;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public sealed record MangaReadingProgress(
        string MangaId,
        string MangaTitle,
        string ChapterId,
        string ChapterNumber,
        int PageIndex,
        int PageCount,
        bool IsExternal,
        DateTime UpdatedAtUtc);

    /// <summary>
    /// Remembers the last chapter and page read for each MangaDex title, so a reader
    /// can continue after closing the tab or the app. Progress is keyed by the exact
    /// MangaDex manga and chapter IDs; a title or chapter number alone is never matched.
    /// </summary>
    public sealed class MangaReadingProgressStore
    {
        public const int Capacity = 500;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly object _sync = new();
        private readonly string _filePath;
        private Dictionary<string, MangaReadingProgress>? _entries;

        public MangaReadingProgressStore()
            : this(Path.Combine(AppDataPaths.LocalBaseDirectory, "UniversalMediaOS", "manga_reading_progress.json"))
        {
        }

        public MangaReadingProgressStore(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("A progress file path is required.", nameof(filePath));
            }

            _filePath = Path.GetFullPath(filePath);
        }

        public MangaReadingProgress? Get(string mangaId)
        {
            if (string.IsNullOrWhiteSpace(mangaId))
            {
                return null;
            }

            lock (_sync)
            {
                return Load().TryGetValue(mangaId, out MangaReadingProgress? progress) ? progress : null;
            }
        }

        public void Save(MangaReadingProgress progress)
        {
            ArgumentNullException.ThrowIfNull(progress);
            if (string.IsNullOrWhiteSpace(progress.MangaId) || string.IsNullOrWhiteSpace(progress.ChapterId))
            {
                throw new ArgumentException("Manga progress needs exact manga and chapter IDs.", nameof(progress));
            }

            MangaReadingProgress normalized = progress with
            {
                PageCount = Math.Max(0, progress.PageCount),
                PageIndex = Math.Clamp(progress.PageIndex, 0, Math.Max(0, progress.PageCount - 1)),
                UpdatedAtUtc = DateTime.UtcNow
            };

            lock (_sync)
            {
                Dictionary<string, MangaReadingProgress> entries = Load();
                entries[normalized.MangaId] = normalized;
                foreach (string stale in entries.Values
                             .OrderByDescending(entry => entry.UpdatedAtUtc)
                             .Skip(Capacity)
                             .Select(entry => entry.MangaId)
                             .ToArray())
                {
                    entries.Remove(stale);
                }

                Persist(entries);
            }
        }

        private Dictionary<string, MangaReadingProgress> Load()
        {
            if (_entries != null)
            {
                return _entries;
            }

            _entries = new Dictionary<string, MangaReadingProgress>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(_filePath) &&
                    JsonSerializer.Deserialize<List<MangaReadingProgress>>(File.ReadAllText(_filePath), JsonOptions) is { } saved)
                {
                    foreach (MangaReadingProgress entry in saved.Where(entry =>
                                 !string.IsNullOrWhiteSpace(entry.MangaId) && !string.IsNullOrWhiteSpace(entry.ChapterId)))
                    {
                        _entries[entry.MangaId] = entry;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Unreadable progress is not guessed at; reading still works and new progress replaces it.
                AppLogger.Log($"Manga reading progress could not be read: {ex.Message}", "WARNING");
            }

            return _entries;
        }

        private void Persist(Dictionary<string, MangaReadingProgress> entries)
        {
            string temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(entries.Values.ToList(), JsonOptions));
                File.Move(temporary, _filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLogger.Log($"Manga reading progress could not be saved: {ex.Message}", "WARNING");
            }
            finally
            {
                try { File.Delete(temporary); } catch (Exception) { }
            }
        }
    }
}
