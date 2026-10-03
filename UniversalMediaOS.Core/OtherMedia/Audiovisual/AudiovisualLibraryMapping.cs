using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UniversalMediaOS.Core.OtherMedia;

public static class AudiovisualLibraryMapping
{
    public static AudiovisualIdentity ToIdentity(AudiovisualLibraryKey key)
    {
        var form = key.ContentForm;
        var ids = key.ProviderIds?.ToArray() ?? [];
        if (key.WorkKey == null && key.ExternalId.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(key.ExternalId.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int tmdb) && tmdb > 0)
        {
            if (form == AudiovisualContentForm.Unknown)
                form = key.Kind switch { AudiovisualMediaKind.Movie => AudiovisualContentForm.Feature,
                    AudiovisualMediaKind.Television => AudiovisualContentForm.Series, _ => AudiovisualContentForm.Unknown };
            if (form != AudiovisualContentForm.Unknown)
                ids = [new("tmdb", form == AudiovisualContentForm.Feature ? "movie" : "tv", tmdb.ToString(CultureInfo.InvariantCulture))];
        }
        var tmdbId = ids.FirstOrDefault(id => id.Provider == "tmdb" &&
            id.Namespace == (form == AudiovisualContentForm.Feature ? "movie" : "tv"));
        return new() { Kind = key.Kind, ContentForm = form, Title = key.Title, OriginalTitle = key.Title,
            Year = key.Year, PrimaryId = ids.FirstOrDefault(), ExternalIds = Array.AsReadOnly(ids),
            TmdbId = tmdbId != null && int.TryParse(tmdbId.Value, out int parsed) ? parsed : null,
            ImdbId = ids.FirstOrDefault(id => id.Provider == "imdb" && id.Namespace == "title")?.Value ?? string.Empty };
    }

    public static AudiovisualMediaItem ToMediaItem(AudiovisualLibraryEntry entry, AudiovisualMediaKind? category = null) => new()
    {
        PersistedWorkKey = entry.Key.WorkKey,
        Identity = ToIdentity(entry.Key) with { Kind = category ?? entry.Key.Kind, Title = entry.Title },
        Title = entry.Title, PosterUrl = entry.PosterUrl
    };

    internal static Dictionary<string, AudiovisualLibraryEntry> Migrate(IReadOnlyList<AudiovisualLibraryEntry> rows,
        IReadOnlyList<JsonElement> rawRows)
    {
        var result = new Dictionary<string, AudiovisualLibraryEntry>(StringComparer.Ordinal);
        for (int index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            ValidateLegacy(row);
            var identity = ToIdentity(row.Key);
            // Keep ambiguous/duplicate legacy records independently, with repeatable opaque identities.
            string? opaque = AudiovisualIdentityKeys.GetIds(identity).Count == 0
                ? "av:local:legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    index.ToString(CultureInfo.InvariantCulture) + ":" + rawRows[index].GetRawText()))).ToLowerInvariant()
                : null;
            var key = AudiovisualLibraryKey.Create(identity, opaque);
            var migrated = row with { Key = key, Aliases = Array.AsReadOnly(new[] { row.Key.StableId }),
                Categories = Array.AsReadOnly(new[] { row.Key.Kind }), LegacyRecords = Array.AsReadOnly(new[] { rawRows[index].Clone() }) };
            result[key.StableId] = result.TryGetValue(key.StableId, out var existing) ? Merge(existing, migrated) : migrated;
        }
        return result;
    }

    private static AudiovisualLibraryEntry Merge(AudiovisualLibraryEntry first, AudiovisualLibraryEntry second)
    {
        if (first.Key.ContentForm != second.Key.ContentForm ||
            AudiovisualIdentityKeys.HasConflictingIds(ToIdentity(first.Key), ToIdentity(second.Key)))
            throw new InvalidDataException("Conflicting library identities cannot be merged.");
        var ordered = new[] { first, second }.OrderByDescending(e => e.UpdatedUtc)
            .ThenBy(e => e.Aliases.FirstOrDefault(), StringComparer.Ordinal).ToArray();
        var display = ordered[0];
        var opened = ordered.OrderByDescending(e => e.LastOpenedUtc).First();
        return display with
        {
            IsFavorite = first.IsFavorite || second.IsFavorite,
            LastOpenedUtc = opened.LastOpenedUtc, LastSeasonNumber = opened.LastSeasonNumber,
            LastEpisodeNumber = opened.LastEpisodeNumber, PositionSeconds = opened.PositionSeconds,
            DurationSeconds = opened.DurationSeconds,
            Aliases = Array.AsReadOnly(first.Aliases.Concat(second.Aliases).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()),
            Categories = Array.AsReadOnly(first.Categories.Concat(second.Categories).Distinct().ToArray()),
            LegacyRecords = Array.AsReadOnly(first.LegacyRecords.Concat(second.LegacyRecords).ToArray())
        };
    }

    internal static void ValidateLegacy(AudiovisualLibraryEntry entry)
    {
        if (entry?.Key == null || string.IsNullOrWhiteSpace(entry.Key.ExternalId) || entry.Title == null ||
            !Enum.IsDefined(entry.Key.Kind) || !Enum.IsDefined(entry.Key.ContentForm) || !Enum.IsDefined(entry.Status) ||
            !double.IsFinite(entry.PositionSeconds) || !double.IsFinite(entry.DurationSeconds))
            throw new InvalidDataException("Invalid audiovisual library entry; original file preserved.");
    }

    internal static Dictionary<string, AudiovisualLibraryEntry> ValidateVersion2(IEnumerable<AudiovisualLibraryEntry> rows)
    {
        var result = new Dictionary<string, AudiovisualLibraryEntry>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            ValidateLegacy(row);
            if (string.IsNullOrWhiteSpace(row.Key.WorkKey) || !row.Key.WorkKey.StartsWith("av:", StringComparison.Ordinal) ||
                row.Aliases == null || row.Categories == null || row.LegacyRecords == null ||
                row.Categories.Any(c => !Enum.IsDefined(c)) || row.Aliases.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Invalid version-2 audiovisual identity.");
            var identity = ToIdentity(row.Key);
            if (AudiovisualIdentityKeys.HasConflictingIds(identity, identity))
                throw new InvalidDataException("Conflicting provider identities in library entry.");
            var frozen = row with { Key = row.Key with { ProviderIds = Array.AsReadOnly(AudiovisualIdentityKeys.GetIds(identity).ToArray()) },
                Aliases = Array.AsReadOnly(row.Aliases.ToArray()), Categories = Array.AsReadOnly(row.Categories.ToArray()),
                LegacyRecords = Array.AsReadOnly(row.LegacyRecords.Select(r => r.Clone()).ToArray()) };
            if (!result.TryAdd(frozen.Key.StableId, frozen))
                throw new InvalidDataException("Duplicate version-2 work key; original file preserved.");
        }
        return result;
    }
}

internal sealed record AudiovisualLibraryDocument(int Version, IReadOnlyList<AudiovisualLibraryEntry> Entries);
