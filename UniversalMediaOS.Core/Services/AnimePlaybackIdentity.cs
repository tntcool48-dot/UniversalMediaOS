using System.Globalization;

namespace UniversalMediaOS.Core.Services;

/// <summary>Catalog IDs captured for one anime entry, independent of title, audio and provider.</summary>
public sealed record AnimePlaybackIdentity(int AniListId, int MalId = 0)
{
    public string? WorkKey => AniListId > 0 ? "anime:anilist:" + AniListId.ToString(CultureInfo.InvariantCulture)
        : MalId > 0 ? "anime:mal:" + MalId.ToString(CultureInfo.InvariantCulture) : null;
    // Only an exact MAL ID supplied by the same catalog entry establishes this legacy alias.
    public string? LegacyWorkKey => MalId > 0 ? MalId.ToString(CultureInfo.InvariantCulture) : null;
}
