using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Services
{
    public sealed class EpisodeAlertHistoryEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = string.Empty;
        public int AniListId { get; set; }
        public int MalId { get; set; }
        public string Year { get; set; } = string.Empty;
        public int PreviousEpisode { get; set; }
        public int AvailableEpisode { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        [JsonIgnore]
        public string EpisodeText => AvailableEpisode > PreviousEpisode + 1
            ? $"Episodes {PreviousEpisode + 1}-{AvailableEpisode} are available"
            : $"Episode {AvailableEpisode} is available";

        [JsonIgnore]
        public string CreatedAtText => CreatedAtUtc.ToLocalTime().ToString("g");

        [JsonIgnore]
        public string MediaText => !string.IsNullOrWhiteSpace(Year) ? $"Anime - {Year}"
            : AniListId > 0 ? $"AniList {AniListId}" : MalId > 0 ? $"MAL {MalId}" : string.Empty;
    }

    /// <summary>
    /// Stores a small, local history of episode alerts so a transient toast is not
    /// the only way a user can discover that a favorite received a new episode.
    /// </summary>
    public sealed class EpisodeAlertHistoryService
    {
        public const int DefaultCapacity = 100;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        private readonly object _sync = new();
        private readonly string _filePath;
        private readonly int _capacity;
        private readonly List<EpisodeAlertHistoryEntry> _entries;

        public event EventHandler? HistoryChanged;

        public EpisodeAlertHistoryService()
            : this(GetDefaultPath(), DefaultCapacity)
        {
        }

        public EpisodeAlertHistoryService(string filePath, int capacity = DefaultCapacity)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("A history file path is required.", nameof(filePath));
            }

            _filePath = Path.GetFullPath(filePath);
            _capacity = Math.Clamp(capacity, 1, 1000);
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch (Exception ex)
                {
                    // History remains available for this app session even if the
                    // profile directory is temporarily read-only or unavailable.
                    AppLogger.Log($"Episode alert history will be in-memory only: {ex.Message}", "WARNING");
                }
            }

            _entries = Load();
            TrimLocked();
        }

        public IReadOnlyList<EpisodeAlertHistoryEntry> GetHistory()
        {
            lock (_sync)
            {
                return _entries
                    .Select(Clone)
                    .ToList();
            }
        }

        public IReadOnlyList<EpisodeAlertHistoryEntry> AddAlerts(IEnumerable<NewEpisodeAlert> alerts)
        {
            ArgumentNullException.ThrowIfNull(alerts);

            var added = alerts
                .Where(alert =>
                    alert.AvailableEpisode > Math.Max(0, alert.PreviousEpisode) &&
                    !string.IsNullOrWhiteSpace(alert.Title))
                .Select(alert => new EpisodeAlertHistoryEntry
                {
                    Title = alert.Title.Trim(),
                    AniListId = Math.Max(0, alert.AniListId),
                    MalId = Math.Max(0, alert.MalId),
                    Year = alert.Year?.Trim() ?? string.Empty,
                    PreviousEpisode = Math.Max(0, alert.PreviousEpisode),
                    AvailableEpisode = alert.AvailableEpisode,
                    CreatedAtUtc = DateTime.UtcNow
                })
                .ToList();

            if (added.Count == 0)
            {
                return Array.Empty<EpisodeAlertHistoryEntry>();
            }

            var actuallyAdded = new List<EpisodeAlertHistoryEntry>();
            lock (_sync)
            {
                // Each check already returns only newly increased availability. A
                // defensive duplicate check also prevents accidental double entries
                // if the monitor is invoked twice at the same time.
                foreach (EpisodeAlertHistoryEntry entry in added)
                {
                    bool duplicate = _entries.Any(existing =>
                        SameMedia(existing, entry) &&
                        existing.AvailableEpisode == entry.AvailableEpisode);
                    if (!duplicate)
                    {
                        _entries.Insert(0, entry);
                        actuallyAdded.Add(entry);
                    }
                }

                if (actuallyAdded.Count == 0)
                {
                    return Array.Empty<EpisodeAlertHistoryEntry>();
                }

                TrimLocked();
                SaveLocked();
            }

            HistoryChanged?.Invoke(this, EventArgs.Empty);
            return actuallyAdded.Select(Clone).ToList();
        }

        public void Clear()
        {
            bool changed;
            lock (_sync)
            {
                changed = _entries.Count > 0;
                if (!changed)
                {
                    return;
                }

                _entries.Clear();
                SaveLocked();
            }

            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }

        private List<EpisodeAlertHistoryEntry> Load()
        {
            if (!File.Exists(_filePath))
            {
                return new List<EpisodeAlertHistoryEntry>();
            }

            try
            {
                string json = File.ReadAllText(_filePath);
                return (JsonSerializer.Deserialize<List<EpisodeAlertHistoryEntry>>(json, JsonOptions) ?? [])
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Title) && entry.AvailableEpisode > 0)
                    .OrderByDescending(entry => entry.CreatedAtUtc)
                    .ToList();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to load episode alert history: {ex.Message}", "WARNING");
                return new List<EpisodeAlertHistoryEntry>();
            }
        }

        private void SaveLocked()
        {
            string temporaryPath = _filePath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_entries, JsonOptions));
                File.Move(temporaryPath, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Failed to save episode alert history: {ex.Message}", "WARNING");
                try { File.Delete(temporaryPath); } catch { }
            }
        }

        private void TrimLocked()
        {
            if (_entries.Count > _capacity)
            {
                _entries.RemoveRange(_capacity, _entries.Count - _capacity);
            }
        }

        private static EpisodeAlertHistoryEntry Clone(EpisodeAlertHistoryEntry entry) => new()
        {
            Id = entry.Id,
            Title = entry.Title,
            AniListId = entry.AniListId,
            MalId = entry.MalId,
            Year = entry.Year,
            PreviousEpisode = entry.PreviousEpisode,
            AvailableEpisode = entry.AvailableEpisode,
            CreatedAtUtc = entry.CreatedAtUtc
        };

        private static bool SameMedia(EpisodeAlertHistoryEntry existing, EpisodeAlertHistoryEntry entry)
        {
            if (existing.AniListId > 0 || entry.AniListId > 0)
                return existing.AniListId > 0 && existing.AniListId == entry.AniListId;
            if (existing.MalId > 0 || entry.MalId > 0)
                return existing.MalId > 0 && existing.MalId == entry.MalId;
            return existing.Title.Equals(entry.Title, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetDefaultPath()
        {
            string directory = Path.Combine(
                UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory,
                "UniversalMediaOS");
            return Path.Combine(directory, "episode-alert-history.json");
        }
    }
}
