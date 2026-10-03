using System.Globalization;
using System.Text.RegularExpressions;

namespace UniversalMediaOS.Core.OtherMedia;

public sealed record AudiovisualExternalId(string Provider, string Namespace, string Value);

public static class AudiovisualIdentityKeys
{
    public static AudiovisualExternalId Normalize(AudiovisualExternalId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        string provider = (id.Provider ?? "").Trim().ToLowerInvariant();
        string space = (id.Namespace ?? "").Trim().ToLowerInvariant();
        string value = id.Value ?? "";
        if (!Regex.IsMatch(provider, @"\A[a-z0-9][a-z0-9_.-]{0,63}\z") ||
            !Regex.IsMatch(space, @"\A[a-z0-9][a-z0-9_.-]{0,63}\z") ||
            string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl) || value != value.Trim())
            throw new ArgumentException("Invalid provider identity.", nameof(id));

        if (provider == "tmdb")
        {
            if (space is not ("movie" or "tv") || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number) || number <= 0)
                throw new ArgumentException("TMDB identity requires movie/tv and a positive numeric ID.", nameof(id));
            value = number.ToString(CultureInfo.InvariantCulture);
        }
        if (provider == "imdb" && (space != "title" || !Regex.IsMatch(value, @"\Att[0-9]+\z")))
            throw new ArgumentException("IMDb identity requires a title ID.", nameof(id));

        return new(provider, space, value);
    }

    public static string ExternalKey(AudiovisualExternalId id)
    {
        var normalized = Normalize(id);
        return $"av:{normalized.Provider}:{normalized.Namespace}:{Uri.EscapeDataString(normalized.Value)}";
    }

    // A browse tab is not evidence that a TMDB ID belongs to movie or TV.
    public static IReadOnlyList<AudiovisualExternalId> GetIds(AudiovisualIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Enum.IsDefined(identity.ContentForm)) throw new ArgumentException("Invalid content form.", nameof(identity));
        var ids = new List<AudiovisualExternalId>();
        if (identity.PrimaryId != null) ids.Add(Normalize(identity.PrimaryId));
        ids.AddRange((identity.ExternalIds ?? Array.Empty<AudiovisualExternalId>()).Select(Normalize));
        if (identity.TmdbId is > 0 && identity.ContentForm != AudiovisualContentForm.Unknown)
            ids.Add(Normalize(new("tmdb", identity.ContentForm == AudiovisualContentForm.Feature ? "movie" : "tv", identity.TmdbId.Value.ToString(CultureInfo.InvariantCulture))));
        if (!string.IsNullOrWhiteSpace(identity.ImdbId)) ids.Add(Normalize(new("imdb", "title", identity.ImdbId)));
        if (identity.ContentForm != AudiovisualContentForm.Unknown && ids.Any(id => id.Provider == "tmdb" &&
            id.Namespace != (identity.ContentForm == AudiovisualContentForm.Feature ? "movie" : "tv")))
            throw new ArgumentException("Content form conflicts with provider namespace.", nameof(identity));
        return ids.Distinct().ToArray();
    }

    public static bool HasConflictingIds(AudiovisualIdentity first, AudiovisualIdentity second)
    {
        var all = GetIds(first).Concat(GetIds(second)).ToArray();
        return all.GroupBy(id => (id.Provider, id.Namespace)).Any(group => group.Select(id => id.Value).Distinct(StringComparer.Ordinal).Count() > 1)
            || all.Where(id => id.Provider == "tmdb").Select(id => id.Namespace).Distinct().Count() > 1;
    }

    public static string CreateWorkKey(AudiovisualIdentity identity, string? persistedKey = null)
    {
        if (!string.IsNullOrWhiteSpace(persistedKey)) return persistedKey;
        if (HasConflictingIds(identity, identity)) throw new ArgumentException("Conflicting work IDs.", nameof(identity));
        var ids = GetIds(identity);
        var primary = identity.PrimaryId != null ? Normalize(identity.PrimaryId) : ids.FirstOrDefault();
        return primary != null ? ExternalKey(primary) : "av:local:" + Guid.NewGuid().ToString("N");
    }

    public static string CreateUnitKey(AudiovisualContentForm form, AudiovisualUnit? unit,
        bool verifiedSpecial = false, AudiovisualExternalId? verifiedEpisodeId = null)
    {
        if (form == AudiovisualContentForm.Feature && (unit == null || unit.IsFeature)) return "feature";
        if (form != AudiovisualContentForm.Series) throw new ArgumentException("A known content form and exact unit are required.");
        if (unit?.SeasonNumber is int season && unit.EpisodeNumber is int episode && episode > 0 && (season > 0 || season == 0 && verifiedSpecial))
            return FormattableString.Invariant($"season:{season}:episode:{episode}");
        if (verifiedEpisodeId != null)
        {
            var id = Normalize(verifiedEpisodeId);
            if (id.Namespace != "episode") throw new ArgumentException("Expected a verified episode identity.");
            return ExternalKey(id);
        }
        throw new ArgumentException("Missing or invalid episode numbering.", nameof(unit));
    }
}
