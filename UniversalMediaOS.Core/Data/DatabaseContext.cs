using System;
using System.IO;
using System.Linq;
using Microsoft.EntityFrameworkCore;

namespace UniversalMediaOS.Core.Data
{
    public class DatabaseContext : DbContext
    {
        public DbSet<ResumeState> ResumeStates { get; set; } = null!;
        public DbSet<DubCastHash> DubHashes { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "media_os.db");
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Composite unique index so we get one row per (MediaId, EpisodeId)
            modelBuilder.Entity<ResumeState>()
                .HasIndex(r => new { r.MediaId, r.EpisodeId })
                .IsUnique();

            // Index for fast O(1) casting queries
            modelBuilder.Entity<DubCastHash>()
                .HasIndex(d => new { d.MediaId, d.CharacterName })
                .IsUnique();
        }

        /// <summary>
        /// Upserts a resume position for the given media/episode combination.
        /// </summary>
        public void SaveResumeState(string mediaId, string episodeId, double positionSeconds)
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

        /// <summary>
        /// Returns the saved resume position in seconds, or 0 if none exists.
        /// </summary>
        public double GetResumeState(string mediaId, string episodeId)
        {
            var state = ResumeStates
                .FirstOrDefault(r => r.MediaId == mediaId && r.EpisodeId == episodeId);

            return state?.PositionSeconds ?? 0.0;
        }
    }

    public class ResumeState
    {
        public int Id { get; set; }
        public string MediaId { get; set; } = string.Empty;
        public string EpisodeId { get; set; } = string.Empty;
        public double PositionSeconds { get; set; }
    }

    public class DubCastHash
    {
        public int Id { get; set; }
        public int MediaId { get; set; }
        public string ShowTitle { get; set; } = string.Empty;
        public string CharacterName { get; set; } = string.Empty;
        public string CharacterImageUrl { get; set; } = string.Empty;
        public string VoiceActorName { get; set; } = string.Empty;
        public string VoiceActorImageUrl { get; set; } = string.Empty;
    }
}
