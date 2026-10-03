using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Data
{
    public class DatabaseContext : DbContext
    {
        private static readonly object LegacyMigrationLock = new();

        public DbSet<ResumeState> ResumeStates { get; set; } = null!;
        public DbSet<DubCastHash> DubHashes { get; set; } = null!;
        public DbSet<MalLibraryEntry> MalLibraryEntries { get; set; } = null!;
        public DbSet<VoiceCastRecord> VoiceCastRecords { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            string dbPath = "";
            try
            {
                string appData = UniversalMediaOS.Core.Helpers.AppDataPaths.RoamingBaseDirectory;
                string configPath = Path.Combine(appData, "UniversalMediaOS", "config.json");
                if (File.Exists(configPath))
                {
                    var config = new Configuration.DomainHotSwapper(configPath);
                    dbPath = config.GetSetting("DatabasePath") ?? "";
                }
            }
            catch { }

            if (string.IsNullOrEmpty(dbPath))
            {
                string appData = UniversalMediaOS.Core.Helpers.AppDataPaths.RoamingBaseDirectory;

                string dataDirectory = Path.Combine(appData, "UniversalMediaOS");
                Directory.CreateDirectory(dataDirectory);
                dbPath = Path.Combine(dataDirectory, "media_os.db");

                // Preserve data from older portable/debug builds which stored the
                // database beside the executable (often an unwritable install folder).
                string legacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "media_os.db");
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppDataPaths.DataRootEnvironmentVariable)) &&
                    !File.Exists(dbPath) &&
                    !legacyPath.Equals(dbPath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(legacyPath))
                {
                    if (!TryMigrateLegacyDatabase(legacyPath, dbPath))
                    {
                        // Keep using the authoritative legacy database for this
                        // run. This prevents EF/SQLite from creating an empty
                        // destination that would suppress the next migration try.
                        dbPath = legacyPath;
                    }
                }
            }

            string? dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            optionsBuilder.UseSqlite($"Data Source={dbPath};Cache=Shared;");
            optionsBuilder.AddInterceptors(new SqlitePragmaInterceptor());
        }

        internal static bool TryMigrateLegacyDatabase(string legacyPath, string destinationPath)
        {
            lock (LegacyMigrationLock)
            {
                if (File.Exists(destinationPath))
                {
                    return true;
                }

                string temporaryPath = destinationPath + $".migration-{Guid.NewGuid():N}.tmp";
                try
                {
                    string? destinationDirectory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrWhiteSpace(destinationDirectory))
                    {
                        Directory.CreateDirectory(destinationDirectory);
                    }

                    using (var source = new SqliteConnection(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = legacyPath,
                            Mode = SqliteOpenMode.ReadOnly,
                            Cache = SqliteCacheMode.Shared,
                            Pooling = false
                        }.ToString()))
                    using (var destination = new SqliteConnection(
                        new SqliteConnectionStringBuilder
                        {
                            DataSource = temporaryPath,
                            Mode = SqliteOpenMode.ReadWriteCreate,
                            Cache = SqliteCacheMode.Private,
                            Pooling = false
                        }.ToString()))
                    {
                        source.Open();
                        destination.Open();
                        source.BackupDatabase(destination);
                    }

                    try
                    {
                        // A move within the destination directory is atomic. The
                        // final path therefore never exists as a partial database.
                        File.Move(temporaryPath, destinationPath);
                    }
                    catch (IOException) when (File.Exists(destinationPath))
                    {
                        // Another process completed the same migration first.
                    }

                    return File.Exists(destinationPath);
                }
                catch (Exception ex)
                {
                    AppLogger.Log($"Failed to migrate the legacy media database: {ex.Message}", "WARNING");
                    return false;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }
                    }
                    catch (Exception cleanupException)
                    {
                        AppLogger.Log(
                            $"Failed to remove a temporary legacy database migration file: {cleanupException.Message}",
                            "WARNING");
                    }
                }
            }
        }

        private class SqlitePragmaInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.DbConnectionInterceptor
        {
            public override void ConnectionOpened(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                command.ExecuteNonQuery();
            }

            public override async Task ConnectionOpenedAsync(System.Data.Common.DbConnection connection, Microsoft.EntityFrameworkCore.Diagnostics.ConnectionEndEventData eventData, CancellationToken cancellationToken)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Composite unique index so we get one row per (MediaId, EpisodeId)
            modelBuilder.Entity<ResumeState>()
                .HasIndex(r => new { r.MediaId, r.EpisodeId })
                .IsUnique();

            // Index for fast O(log n) casting queries
            modelBuilder.Entity<DubCastHash>()
                .HasIndex(d => new { d.MediaId, d.CharacterName })
                .IsUnique();

            modelBuilder.Entity<MalLibraryEntry>()
                .HasIndex(m => m.MalId)
                .IsUnique();

            modelBuilder.Entity<VoiceCastRecord>()
                .HasIndex(v => new { v.MediaKey, v.LanguageMode, v.CharacterName, v.VoiceActorName })
                .IsUnique();

            modelBuilder.Entity<VoiceCastRecord>()
                .HasIndex(v => new { v.LanguageMode, v.VoiceActorName });
        }

        /// <summary>
        /// Ensures additive VA Detect tables exist even when the SQLite database predates these entities.
        /// </summary>
        public void EnsureVaDetectSchema()
        {
            Database.EnsureCreated();
            Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS MalLibraryEntries (
                    Id INTEGER NOT NULL CONSTRAINT PK_MalLibraryEntries PRIMARY KEY AUTOINCREMENT,
                    MalId INTEGER NOT NULL,
                    AniListId INTEGER NOT NULL,
                    DefaultTitle TEXT NOT NULL,
                    EnglishTitle TEXT NOT NULL,
                    NativeTitle TEXT NOT NULL,
                    ImageUrl TEXT NOT NULL,
                    ListStatus TEXT NOT NULL,
                    UserScore INTEGER NOT NULL,
                    WatchedEpisodes INTEGER NOT NULL,
                    TotalEpisodes INTEGER NOT NULL,
                    LastSyncedUtc TEXT NOT NULL
                );
                """);
            Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_MalLibraryEntries_MalId ON MalLibraryEntries (MalId);");

            Database.ExecuteSqlRaw("""
                CREATE TABLE IF NOT EXISTS VoiceCastRecords (
                    Id INTEGER NOT NULL CONSTRAINT PK_VoiceCastRecords PRIMARY KEY AUTOINCREMENT,
                    MediaKey TEXT NOT NULL,
                    MalId INTEGER NOT NULL,
                    AniListId INTEGER NOT NULL,
                    ShowTitle TEXT NOT NULL,
                    LanguageMode TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    RoleType TEXT NOT NULL,
                    CharacterName TEXT NOT NULL,
                    CharacterImageUrl TEXT NOT NULL,
                    VoiceActorName TEXT NOT NULL,
                    VoiceActorImageUrl TEXT NOT NULL,
                    ManualUrl TEXT NOT NULL,
                    FetchedAtUtc TEXT NOT NULL,
                    NotFound INTEGER NOT NULL
                );
                """);
            Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS IX_VoiceCastRecords_Key ON VoiceCastRecords (MediaKey, LanguageMode, CharacterName, VoiceActorName);");
            Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_VoiceCastRecords_ModeVa ON VoiceCastRecords (LanguageMode, VoiceActorName);");
        }

        /// <summary>
        /// Upserts a resume position for the given media/episode combination.
        /// </summary>
        public void SaveResumeState(string mediaId, string episodeId, double positionSeconds)
        {
            if (string.IsNullOrEmpty(mediaId) || string.IsNullOrEmpty(episodeId)) return;
            if (double.IsNaN(positionSeconds) || double.IsInfinity(positionSeconds) || positionSeconds < 0)
            {
                positionSeconds = 0.0;
            }

            // WAL mode + busy_timeout=5000 makes EF Core's per-SaveChanges implicit transactions safe.
            // Manual BeginTransaction wrappers cause lock contention under rapid seek saves.
            try
            {
                var existing = ResumeStates
                    .FirstOrDefault(r => r.MediaId == mediaId && r.EpisodeId == episodeId);

                if (existing != null)
                {
                    existing.PositionSeconds = positionSeconds;
                }
                else
                {
                    ResumeStates.Add(new ResumeState
                    {
                        MediaId = mediaId,
                        EpisodeId = episodeId,
                        PositionSeconds = positionSeconds
                    });
                }

                SaveChanges();
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Error saving resume state synchronously: {ex.Message}", "ERROR");
                throw;
            }
        }

        /// <summary>
        /// Upserts a resume position for the given media/episode combination asynchronously.
        /// </summary>
        public async Task SaveResumeStateAsync(string mediaId, string episodeId, double positionSeconds, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(mediaId) || string.IsNullOrEmpty(episodeId)) return;
            if (double.IsNaN(positionSeconds) || double.IsInfinity(positionSeconds) || positionSeconds < 0)
            {
                positionSeconds = 0.0;
            }

            try
            {
                var existing = await ResumeStates
                    .FirstOrDefaultAsync(r => r.MediaId == mediaId && r.EpisodeId == episodeId, token);

                if (existing != null)
                {
                    existing.PositionSeconds = positionSeconds;
                }
                else
                {
                    await ResumeStates.AddAsync(new ResumeState
                    {
                        MediaId = mediaId,
                        EpisodeId = episodeId,
                        PositionSeconds = positionSeconds
                    }, token);
                }

                await SaveChangesAsync(token);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Error saving resume state asynchronously: {ex.Message}", "ERROR");
                throw;
            }
        }

        /// <summary>
        /// Returns the saved resume position in seconds, or 0 if none exists.
        /// </summary>
        public double GetResumeState(string mediaId, string episodeId)
        {
            if (string.IsNullOrEmpty(mediaId) || string.IsNullOrEmpty(episodeId)) return 0.0;
            try
            {
                var state = ResumeStates
                    .FirstOrDefault(r => r.MediaId == mediaId && r.EpisodeId == episodeId);

                return state?.PositionSeconds ?? 0.0;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Error loading resume state: {ex.Message}", "ERROR");
                return 0.0;
            }
        }

        /// <summary>
        /// Returns the saved resume position in seconds asynchronously, or 0 if none exists.
        /// </summary>
        public async Task<double> GetResumeStateAsync(string mediaId, string episodeId, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(mediaId) || string.IsNullOrEmpty(episodeId)) return 0.0;
            try
            {
                var state = await ResumeStates
                    .FirstOrDefaultAsync(r => r.MediaId == mediaId && r.EpisodeId == episodeId, token);

                return state?.PositionSeconds ?? 0.0;
            }
            catch (Exception ex)
            {
                AppLogger.Log($"Error loading resume state asynchronously: {ex.Message}", "ERROR");
                return 0.0;
            }
        }
    }

    public class ResumeState
    {
        public long Id { get; set; } // Avoid PK integer ceiling limit
        public string MediaId { get; set; } = string.Empty;
        public string EpisodeId { get; set; } = string.Empty;
        public double PositionSeconds { get; set; }
    }

    public class DubCastHash
    {
        public long Id { get; set; } // Avoid PK integer ceiling limit
        public int MediaId { get; set; }
        public string ShowTitle { get; set; } = string.Empty;
        public string CharacterName { get; set; } = string.Empty;
        public string CharacterImageUrl { get; set; } = string.Empty;
        public string VoiceActorName { get; set; } = string.Empty;
        public string VoiceActorImageUrl { get; set; } = string.Empty;
    }

    public class MalLibraryEntry
    {
        public long Id { get; set; }
        public int MalId { get; set; }
        public int AniListId { get; set; }
        public string DefaultTitle { get; set; } = string.Empty;
        public string EnglishTitle { get; set; } = string.Empty;
        public string NativeTitle { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public string ListStatus { get; set; } = string.Empty;
        public int UserScore { get; set; }
        public int WatchedEpisodes { get; set; }
        public int TotalEpisodes { get; set; }
        public DateTime LastSyncedUtc { get; set; } = DateTime.UtcNow;
    }

    public class VoiceCastRecord
    {
        public long Id { get; set; }
        public string MediaKey { get; set; } = string.Empty;
        public int MalId { get; set; }
        public int AniListId { get; set; }
        public string ShowTitle { get; set; } = string.Empty;
        public string LanguageMode { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string RoleType { get; set; } = string.Empty;
        public string CharacterName { get; set; } = string.Empty;
        public string CharacterImageUrl { get; set; } = string.Empty;
        public string VoiceActorName { get; set; } = string.Empty;
        public string VoiceActorImageUrl { get; set; } = string.Empty;
        public string ManualUrl { get; set; } = string.Empty;
        public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;
        public bool NotFound { get; set; }
    }
}
