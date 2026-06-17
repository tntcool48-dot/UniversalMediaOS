using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Helpers;

namespace UniversalMediaOS.Core.Data
{
    public class DatabaseContext : DbContext
    {
        public DbSet<ResumeState> ResumeStates { get; set; } = null!;
        public DbSet<DubCastHash> DubHashes { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            string dbPath = "";
            try
            {
                string? appData = Environment.GetEnvironmentVariable("APPDATA");
                if (string.IsNullOrEmpty(appData))
                {
                    appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                }
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
                dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "media_os.db");
            }
            else
            {
                string? dir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }

            optionsBuilder.UseSqlite($"Data Source={dbPath};Cache=Shared;");
            optionsBuilder.AddInterceptors(new SqlitePragmaInterceptor());
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
}
