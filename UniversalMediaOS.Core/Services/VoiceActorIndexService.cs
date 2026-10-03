using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using UniversalMediaOS.Core.Data;

namespace UniversalMediaOS.Core.Services
{
    public sealed class VoiceActorIndexService
    {
        private readonly VoiceCastService _voiceCastService;
        private readonly FavoriteMediaService _favoriteMediaService;

        public VoiceActorIndexService(VoiceCastService voiceCastService, FavoriteMediaService favoriteMediaService)
        {
            _voiceCastService = voiceCastService;
            _favoriteMediaService = favoriteMediaService;
        }

        public async Task<VoiceActorMatchResult> FindMatchesAsync(
            VoiceCastMedia target,
            VoiceLanguageMode mode,
            string manualUrl = "",
            CancellationToken token = default)
        {
            var targetFetch = await _voiceCastService.FetchAndCacheCastAsync(target, mode, manualUrl, token: token);
            if (targetFetch.NotFound || targetFetch.Cast.Count == 0)
            {
                return new VoiceActorMatchResult(mode, targetFetch.Source, targetFetch.NotFound, targetFetch.Cast, Array.Empty<VoiceActorMatch>());
            }

            await using var db = new DatabaseContext();
            db.EnsureVaDetectSchema();

            string modeText = mode.ToString();
            var library = await db.MalLibraryEntries.ToListAsync(token);
            var libraryByKey = library.ToDictionary(entry => $"mal:{entry.MalId}", StringComparer.OrdinalIgnoreCase);
            var favoriteMalIds = _favoriteMediaService.GetFavorites()
                .Where(item => item.MalId > 0)
                .Select(item => item.MalId)
                .ToHashSet();

            var cachedLibraryCast = await db.VoiceCastRecords
                .Where(record => record.LanguageMode == modeText &&
                                 !record.NotFound &&
                                 record.MediaKey != target.MediaKey)
                .ToListAsync(token);

            var byVoiceActor = cachedLibraryCast
                .Where(record => !string.IsNullOrWhiteSpace(record.VoiceActorName))
                .GroupBy(record => NormalizeVoiceActor(record.VoiceActorName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            var matches = new List<VoiceActorMatch>();
            foreach (var targetRole in targetFetch.Cast.Where(role => !string.IsNullOrWhiteSpace(role.VoiceActorName)))
            {
                string key = NormalizeVoiceActor(targetRole.VoiceActorName);
                if (!byVoiceActor.TryGetValue(key, out var knownRoles))
                {
                    continue;
                }

                var known = knownRoles
                    .Select(role => CreateKnownRole(role, libraryByKey, favoriteMalIds))
                    .Where(role => role != null)
                    .Cast<VoiceActorKnownRole>()
                    .GroupBy(role => $"{role.MalId}|{role.AniListId}|{role.CharacterName}", StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderByDescending(role => role.IsTopRated)
                    .ThenByDescending(role => role.UserScore)
                    .ThenBy(role => role.AnimeTitle)
                    .ToArray();
                if (known.Length == 0)
                {
                    continue;
                }

                string groupName = known.Any(role => favoriteMalIds.Contains(role.MalId))
                    ? "Favorites"
                    : ResolveGroupName(known[0].ListStatus);

                matches.Add(new VoiceActorMatch(
                    targetRole.VoiceActorName,
                    targetRole.CharacterName,
                    targetRole.CharacterImageUrl,
                    targetRole.RoleType,
                    groupName,
                    known));
            }

            return new VoiceActorMatchResult(
                mode,
                targetFetch.Source,
                false,
                targetFetch.Cast,
                matches
                    .OrderBy(match => GroupSort(match.Group))
                    .ThenBy(match => match.RoleType == "Main" ? 0 : 1)
                    .ThenBy(match => match.VoiceActorName)
                    .ToArray());
        }

        private static VoiceActorKnownRole? CreateKnownRole(
            VoiceCastRecord role,
            IReadOnlyDictionary<string, MalLibraryEntry> libraryByKey,
            IReadOnlySet<int> favoriteMalIds)
        {
            libraryByKey.TryGetValue(role.MediaKey, out var entry);
            string title = entry == null
                ? role.ShowTitle
                : !string.IsNullOrWhiteSpace(entry.EnglishTitle)
                    ? entry.EnglishTitle
                    : entry.DefaultTitle;
            int userScore = entry?.UserScore ?? 0;
            int malId = entry?.MalId ?? role.MalId;
            return new VoiceActorKnownRole(
                title,
                malId,
                entry?.AniListId > 0 ? entry.AniListId : role.AniListId,
                entry?.ListStatus ?? "Unknown",
                userScore,
                role.CharacterName,
                role.CharacterImageUrl,
                entry?.ImageUrl ?? string.Empty,
                userScore >= 9 || favoriteMalIds.Contains(malId));
        }

        private static string NormalizeVoiceActor(string value)
        {
            return string.Join(" ", (value ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private static string ResolveGroupName(string status)
        {
            return (status ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "completed" => "Completed",
                "watching" => "Watching",
                "plan to watch" => "Plan to Watch",
                _ => "Other"
            };
        }

        private static int GroupSort(string group)
        {
            return group switch
            {
                "Favorites" => 0,
                "Completed" => 1,
                "Watching" => 2,
                "Plan to Watch" => 3,
                _ => 4
            };
        }
    }
}
