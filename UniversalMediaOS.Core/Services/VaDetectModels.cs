using System;
using System.Collections.Generic;
using System.Linq;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Search;

namespace UniversalMediaOS.Core.Services
{
    public enum VoiceLanguageMode
    {
        Sub,
        Dub
    }

    public sealed record VoiceCastMedia(
        string MediaKey,
        int MalId,
        int AniListId,
        string Title,
        string EnglishTitle,
        string RomajiTitle,
        string NativeTitle,
        string ImageUrl)
    {
        public static VoiceCastMedia FromMediaResult(MediaResult media)
        {
            string key = media.IdMal > 0
                ? $"mal:{media.IdMal}"
                : media.Id > 0
                    ? $"anilist:{media.Id}"
                    : $"title:{NormalizeKey(media.OfficialTitle)}";

            return new VoiceCastMedia(
                key,
                media.IdMal,
                media.Id,
                media.OfficialTitle,
                media.EnglishTitle,
                media.RomajiTitle,
                media.NativeTitle,
                media.CoverImageUrl);
        }

        public static VoiceCastMedia FromLibraryEntry(MalLibraryEntry entry)
        {
            string title = !string.IsNullOrWhiteSpace(entry.EnglishTitle)
                ? entry.EnglishTitle
                : entry.DefaultTitle;
            return new VoiceCastMedia(
                $"mal:{entry.MalId}",
                entry.MalId,
                entry.AniListId,
                title,
                entry.EnglishTitle,
                entry.DefaultTitle,
                entry.NativeTitle,
                entry.ImageUrl);
        }

        private static string NormalizeKey(string value)
        {
            return new string((value ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        }
    }

    public sealed record MalLibrarySyncResult(
        int ImportedCount,
        bool UsedPublicFallback,
        string Warning,
        DateTime SyncedAtUtc);

    public sealed record VoiceCastFetchResult(
        VoiceLanguageMode Mode,
        string Source,
        bool FromCache,
        bool NotFound,
        IReadOnlyList<VoiceCastRecord> Cast);

    public sealed record VoiceActorKnownRole(
        string AnimeTitle,
        int MalId,
        int AniListId,
        string ListStatus,
        int UserScore,
        string CharacterName,
        string CharacterImageUrl,
        string AnimeImageUrl,
        bool IsTopRated);

    public sealed record VoiceActorMatch(
        string VoiceActorName,
        string TargetCharacterName,
        string TargetCharacterImageUrl,
        string RoleType,
        string Group,
        IReadOnlyList<VoiceActorKnownRole> KnownFrom);

    public sealed record VoiceActorMatchResult(
        VoiceLanguageMode Mode,
        string Source,
        bool NotFound,
        IReadOnlyList<VoiceCastRecord> TargetCast,
        IReadOnlyList<VoiceActorMatch> Matches);

    public enum WatchRoomRole
    {
        None,
        Host,
        Peer
    }

    public enum WatchRoomConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
        Failed
    }
}
