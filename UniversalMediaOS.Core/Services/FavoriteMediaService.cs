using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.Search;

namespace UniversalMediaOS.Core.Services
{
    public sealed class FavoriteMediaRecord
    {
        public int AniListId { get; set; }
        public int MalId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Kind { get; set; } = "Anime";
        public string Year { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Rating { get; set; } = string.Empty;
        public string CoverImageUrl { get; set; } = string.Empty;
        public string Progress { get; set; } = string.Empty;
        public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed record NewEpisodeAlert(string Title, int PreviousEpisode, int AvailableEpisode);
    public sealed record FavoriteMediaAvailability(int AvailableEpisode, string Status);

    public sealed class FavoriteMediaService
    {
        private readonly string _filePath;
        private readonly object _sync = new();

        public event EventHandler? FavoritesChanged;

        public FavoriteMediaService() : this(null)
        {
        }

        internal FavoriteMediaService(string? filePath)
        {
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                _filePath = Path.GetFullPath(filePath);
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                return;
            }

            string appData = UniversalMediaOS.Core.Helpers.AppDataPaths.RoamingBaseDirectory;

            string dir = Path.Combine(appData, "UniversalMediaOS");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "favorites.json");
        }

        public IReadOnlyList<FavoriteMediaRecord> GetFavorites()
        {
            lock (_sync)
            {
                return LoadLocked()
                    .OrderByDescending(item => item.AddedAtUtc)
                    .ToList();
            }
        }

        public bool IsFavorite(MediaResult media)
        {
            if (media == null)
            {
                return false;
            }

            lock (_sync)
            {
                return LoadLocked().Any(record => Matches(record, media));
            }
        }

        public bool Toggle(MediaResult media)
        {
            if (media == null)
            {
                return false;
            }

            bool isFavorite;
            lock (_sync)
            {
                var items = LoadLocked();
                int existingIndex = items.FindIndex(record => Matches(record, media));
                if (existingIndex >= 0)
                {
                    items.RemoveAt(existingIndex);
                    isFavorite = false;
                }
                else
                {
                    items.Add(CreateRecord(media));
                    isFavorite = true;
                }

                SaveLocked(items);
            }

            media.IsFavorite = isFavorite;
            FavoritesChanged?.Invoke(this, EventArgs.Empty);
            return isFavorite;
        }

        public void ApplyFavorites(IEnumerable<MediaResult> media)
        {
            if (media == null)
            {
                return;
            }

            HashSet<string> keys;
            lock (_sync)
            {
                keys = LoadLocked().Select(BuildKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var item in media)
            {
                item.IsFavorite = keys.Contains(BuildKey(item));
            }
        }

        public IReadOnlyList<NewEpisodeAlert> UpdateEpisodeProgress(
            IReadOnlyDictionary<int, int> availableEpisodesByAniListId)
        {
            if (availableEpisodesByAniListId == null)
            {
                return Array.Empty<NewEpisodeAlert>();
            }

            return UpdateAvailability(availableEpisodesByAniListId.ToDictionary(
                item => item.Key,
                item => new FavoriteMediaAvailability(item.Value, string.Empty)));
        }

        public IReadOnlyList<NewEpisodeAlert> UpdateAvailability(
            IReadOnlyDictionary<int, FavoriteMediaAvailability> availabilityByAniListId)
        {
            if (availabilityByAniListId == null || availabilityByAniListId.Count == 0)
            {
                return Array.Empty<NewEpisodeAlert>();
            }

            var alerts = new List<NewEpisodeAlert>();
            bool changed = false;
            lock (_sync)
            {
                var items = LoadLocked();
                foreach (var record in items)
                {
                    if (record.AniListId <= 0 ||
                        !availabilityByAniListId.TryGetValue(record.AniListId, out var availability))
                    {
                        continue;
                    }

                    int available = Math.Max(0, availability.AvailableEpisode);
                    int previous = ParseSubProgress(record.Progress);
                    bool wasReleasing = record.Status.Contains("releas", StringComparison.OrdinalIgnoreCase) ||
                                        record.Status.Contains("airing", StringComparison.OrdinalIgnoreCase);
                    if (wasReleasing && previous > 0 && available > previous)
                    {
                        alerts.Add(new NewEpisodeAlert(record.Title, previous, available));
                    }

                    if (available > 0 && available != previous)
                    {
                        record.Progress = $"Sub {available}";
                        changed = true;
                    }

                    if (!string.IsNullOrWhiteSpace(availability.Status) &&
                        !record.Status.Equals(availability.Status, StringComparison.OrdinalIgnoreCase))
                    {
                        record.Status = availability.Status;
                        changed = true;
                    }
                }

                if (changed)
                {
                    SaveLocked(items);
                }
            }

            if (changed)
            {
                FavoritesChanged?.Invoke(this, EventArgs.Empty);
            }

            return alerts;
        }

        private List<FavoriteMediaRecord> LoadLocked()
        {
            if (!File.Exists(_filePath))
            {
                return new List<FavoriteMediaRecord>();
            }

            try
            {
                string json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<FavoriteMediaRecord>>(json) ?? new List<FavoriteMediaRecord>();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to load favorites: {ex.Message}", "WARNING");
                return new List<FavoriteMediaRecord>();
            }
        }

        private void SaveLocked(List<FavoriteMediaRecord> items)
        {
            string tempPath = _filePath + ".tmp";
            string json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }

        private static FavoriteMediaRecord CreateRecord(MediaResult media)
        {
            return new FavoriteMediaRecord
            {
                AniListId = media.Id,
                MalId = media.IdMal,
                Title = media.OfficialTitle,
                Kind = "Anime",
                Year = media.DisplayYear,
                Status = media.DisplayStatus,
                Rating = media.DisplayRating,
                CoverImageUrl = media.CoverImageUrl,
                Progress = media.AvailableSubEpisodes > 0
                    ? $"Sub {media.AvailableSubEpisodes}"
                    : media.TotalEpisodes > 0 ? $"{media.TotalEpisodes} eps" : "Episodes TBA",
                AddedAtUtc = DateTime.UtcNow
            };
        }

        private static bool Matches(FavoriteMediaRecord record, MediaResult media)
        {
            return BuildKey(record).Equals(BuildKey(media), StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildKey(FavoriteMediaRecord record)
        {
            if (record.AniListId > 0)
            {
                return $"anilist:{record.AniListId}";
            }

            return $"title:{Normalize(record.Title)}";
        }

        private static string BuildKey(MediaResult media)
        {
            if (media.Id > 0)
            {
                return $"anilist:{media.Id}";
            }

            return $"title:{Normalize(media.OfficialTitle)}";
        }

        private static string Normalize(string value)
        {
            return new string((value ?? string.Empty)
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());
        }

        private static int ParseSubProgress(string value)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !value.TrimStart().StartsWith("Sub ", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            string digits = new string(value.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out int parsed) ? parsed : 0;
        }
    }
}
