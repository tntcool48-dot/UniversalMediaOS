using System.Text.Json;

namespace UniversalMediaOS.Core.OtherMedia;

public enum AudiovisualLibraryStatus
{
    None,
    Planned,
    Watching,
    Completed
}

public sealed record AudiovisualLibraryKey(
    AudiovisualMediaKind Kind,
    string ExternalId,
    string Title,
    int? Year,
    AudiovisualContentForm ContentForm = AudiovisualContentForm.Unknown,
    string? WorkKey = null,
    IReadOnlyList<AudiovisualExternalId>? ProviderIds = null)
{
    public static AudiovisualLibraryKey Create(AudiovisualIdentity identity, string? persistedWorkKey = null)
    {
        string workKey = AudiovisualIdentityKeys.CreateWorkKey(identity, persistedWorkKey);
        return new(identity.Kind, workKey, identity.Title, identity.Year, identity.ContentForm,
            workKey, Array.AsReadOnly(AudiovisualIdentityKeys.GetIds(identity).ToArray()));
    }

    public static AudiovisualLibraryKey Create(
        AudiovisualMediaKind kind,
        int? tmdbId,
        string title,
        int? year,
        AudiovisualContentForm contentForm = AudiovisualContentForm.Unknown)
    {
        string normalizedTitle = string.Join(
            ' ',
            (title ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string externalId = tmdbId is > 0
            ? $"tmdb:{tmdbId.Value}"
            : $"title:{normalizedTitle}";

        return new AudiovisualLibraryKey(
            kind,
            externalId,
            (title ?? string.Empty).Trim(),
            year,
            contentForm);
    }

    public string StableId =>
        WorkKey ?? $"{Kind}:{ContentForm}:{ExternalId}:{Year?.ToString() ?? "unknown"}".ToLowerInvariant();
}

public sealed record AudiovisualLibraryEntry
{
    public required AudiovisualLibraryKey Key { get; init; }
    public required string Title { get; init; }
    public string PosterUrl { get; init; } = string.Empty;
    public bool IsFavorite { get; init; }
    public AudiovisualLibraryStatus Status { get; init; }
    public int? LastSeasonNumber { get; init; }
    public int? LastEpisodeNumber { get; init; }
    public double PositionSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public DateTimeOffset? LastOpenedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public IReadOnlyList<AudiovisualMediaKind> Categories { get; init; } = Array.Empty<AudiovisualMediaKind>();
    public IReadOnlyList<JsonElement> LegacyRecords { get; init; } = Array.Empty<JsonElement>();
}

/// <summary>
/// Separate persistence for movies, television, and cartoons. It intentionally
/// does not depend on the Anime favorites/history database.
/// </summary>
public sealed class AudiovisualLibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _storagePath;
    private readonly Action<string, string> _commit;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, AudiovisualLibraryEntry>? _entries;

    public AudiovisualLibraryService(string? storagePath = null) : this(storagePath, CommitFile) { }

    internal AudiovisualLibraryService(string? storagePath, Action<string, string> commit)
    {
        _commit = commit;
        _storagePath = storagePath ?? Path.Combine(
            UniversalMediaOS.Core.Helpers.AppDataPaths.LocalBaseDirectory,
            "UniversalMediaOS",
            "OtherMedia",
            "audiovisual-library.json");
    }

    public event EventHandler? LibraryChanged;

    public async Task<IReadOnlyList<AudiovisualLibraryEntry>> GetAllAsync(
        AudiovisualMediaKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return _entries!.Values
                .Where(entry => kind == null || entry.Key.Kind == kind || entry.Categories.Contains(kind.Value))
                .OrderByDescending(entry => entry.LastOpenedUtc ?? entry.UpdatedUtc)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AudiovisualLibraryEntry?> GetAsync(
        AudiovisualLibraryKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return FindEntry(key);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<AudiovisualLibraryEntry> SetFavoriteAsync(
        AudiovisualLibraryKey key,
        string title,
        string? posterUrl,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        return UpdateAsync(
            key,
            title,
            posterUrl,
            current => current with
            {
                IsFavorite = isFavorite,
                UpdatedUtc = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    public Task<AudiovisualLibraryEntry> SetStatusAsync(
        AudiovisualLibraryKey key,
        string title,
        string? posterUrl,
        AudiovisualLibraryStatus status,
        CancellationToken cancellationToken = default)
    {
        return UpdateAsync(
            key,
            title,
            posterUrl,
            current => current with
            {
                Status = status,
                UpdatedUtc = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    public Task<AudiovisualLibraryEntry> RecordOpenedAsync(
        AudiovisualLibraryKey key,
        string title,
        string? posterUrl,
        int? seasonNumber,
        int? episodeNumber,
        CancellationToken cancellationToken = default)
    {
        return UpdateAsync(
            key,
            title,
            posterUrl,
            current => current with
            {
                Status = current.Status == AudiovisualLibraryStatus.None
                    ? AudiovisualLibraryStatus.Watching
                    : current.Status,
                LastSeasonNumber = seasonNumber,
                LastEpisodeNumber = episodeNumber,
                PositionSeconds = current.LastSeasonNumber == seasonNumber && current.LastEpisodeNumber == episodeNumber
                    ? current.PositionSeconds : 0,
                DurationSeconds = current.LastSeasonNumber == seasonNumber && current.LastEpisodeNumber == episodeNumber
                    ? current.DurationSeconds : 0,
                LastOpenedUtc = DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    public Task<AudiovisualLibraryEntry> RecordProgressAsync(
        AudiovisualLibraryKey key,
        string title,
        string? posterUrl,
        int? seasonNumber,
        int? episodeNumber,
        double positionSeconds,
        double durationSeconds,
        CancellationToken cancellationToken = default,
        DateTimeOffset? observedUtc = null)
    {
        return UpdateAsync(
            key,
            title,
            posterUrl,
            current => current.LastOpenedUtc > observedUtc ? current : current with
            {
                Status = current.Status == AudiovisualLibraryStatus.None
                    ? AudiovisualLibraryStatus.Watching
                    : current.Status,
                LastSeasonNumber = seasonNumber,
                LastEpisodeNumber = episodeNumber,
                PositionSeconds = Math.Max(0, positionSeconds),
                DurationSeconds = Math.Max(0, durationSeconds),
                LastOpenedUtc = observedUtc ?? DateTimeOffset.UtcNow,
                UpdatedUtc = DateTimeOffset.UtcNow
            },
            cancellationToken);
    }

    public async Task RemoveAsync(
        AudiovisualLibraryKey key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        bool changed;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var entry = FindEntry(key);
            changed = entry != null;
            if (entry != null)
            {
                var candidate = new Dictionary<string, AudiovisualLibraryEntry>(_entries!, StringComparer.Ordinal);
                candidate.Remove(entry.Key.StableId);
                await SaveLockedAsync(candidate, cancellationToken).ConfigureAwait(false);
                _entries = candidate;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<AudiovisualLibraryEntry> UpdateAsync(
        AudiovisualLibraryKey key,
        string title,
        string? posterUrl,
        Func<AudiovisualLibraryEntry, AudiovisualLibraryEntry> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(update);

        AudiovisualLibraryEntry updated;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            AudiovisualLibraryEntry? existing = FindEntry(key);
            var canonical = key.WorkKey != null ? key : AudiovisualLibraryKey.Create(AudiovisualLibraryMapping.ToIdentity(key));
            AudiovisualLibraryEntry current = existing ??
                new AudiovisualLibraryEntry
                {
                    Key = canonical,
                    Aliases = key.WorkKey == null ? Array.AsReadOnly(new[] { key.StableId }) : Array.Empty<string>(),
                    Categories = Array.AsReadOnly(new[] { key.Kind }),
                    Title = string.IsNullOrWhiteSpace(title) ? key.Title : title.Trim(),
                    PosterUrl = posterUrl?.Trim() ?? string.Empty
                };

            var changed = update(current);
            if (ReferenceEquals(changed, current)) return current;
            updated = changed with
            {
                Title = string.IsNullOrWhiteSpace(title) ? current.Title : title.Trim(),
                PosterUrl = string.IsNullOrWhiteSpace(posterUrl) ? current.PosterUrl : posterUrl.Trim()
            };
            var incomingIdentity = AudiovisualLibraryMapping.ToIdentity(canonical);
            var currentIdentity = AudiovisualLibraryMapping.ToIdentity(current.Key);
            if (AudiovisualIdentityKeys.HasConflictingIds(currentIdentity, incomingIdentity) ||
                current.Key.ContentForm != canonical.ContentForm)
                throw new InvalidDataException("Incoming identity conflicts with the saved work.");
            updated = updated with
            {
                Key = current.Key with { Title = updated.Title, Year = canonical.Year ?? current.Key.Year,
                    ProviderIds = Array.AsReadOnly(AudiovisualIdentityKeys.GetIds(currentIdentity)
                        .Concat(AudiovisualIdentityKeys.GetIds(incomingIdentity)).Distinct().ToArray()) },
                Aliases = Array.AsReadOnly(current.Aliases.Append(key.StableId)
                    .Where(a => a != current.Key.StableId).Distinct(StringComparer.Ordinal).ToArray()),
                Categories = Array.AsReadOnly(current.Categories.Append(key.Kind).Distinct().ToArray())
            };
            var candidate = new Dictionary<string, AudiovisualLibraryEntry>(_entries!, StringComparer.Ordinal)
            { [updated.Key.StableId] = updated };
            candidate = AudiovisualLibraryMapping.ValidateVersion2(candidate.Values);
            await SaveLockedAsync(candidate, cancellationToken).ConfigureAwait(false);
            _entries = candidate;
            updated = candidate[updated.Key.StableId];
        }
        finally
        {
            _gate.Release();
        }

        LibraryChanged?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    private AudiovisualLibraryEntry? FindEntry(AudiovisualLibraryKey key)
    {
        if (_entries!.TryGetValue(key.StableId, out var direct)) return direct;
        var matches = _entries.Values.Where(e => e.Aliases.Contains(key.StableId, StringComparer.Ordinal)).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Ambiguous legacy library key; use the preserved work entry.");
        if (matches.Length == 1) return matches[0];
        var requestedIdentity = AudiovisualLibraryMapping.ToIdentity(key);
        var requestedIds = AudiovisualIdentityKeys.GetIds(requestedIdentity);
        if (requestedIds.Count > 0)
        {
            var byIdentity = _entries.Values.Where(e => e.Key.ContentForm == requestedIdentity.ContentForm &&
                AudiovisualIdentityKeys.GetIds(AudiovisualLibraryMapping.ToIdentity(e.Key)).Intersect(requestedIds).Any() &&
                !AudiovisualIdentityKeys.HasConflictingIds(AudiovisualLibraryMapping.ToIdentity(e.Key), requestedIdentity)).ToArray();
            if (byIdentity.Length > 1) throw new InvalidDataException("Multiple saved works share this provider identity; reconciliation required.");
            if (byIdentity.Length == 1) return byIdentity[0];
        }
        if (key.WorkKey == null)
        {
            var identity = AudiovisualLibraryMapping.ToIdentity(key);
            if (AudiovisualIdentityKeys.GetIds(identity).Count > 0)
                return _entries.GetValueOrDefault(AudiovisualLibraryKey.Create(identity).StableId);
        }
        return null;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_entries != null) return;
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_storagePath))
        {
            _entries = new(StringComparer.Ordinal);
            return;
        }

        byte[] original = await File.ReadAllBytesAsync(_storagePath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(original);
        Dictionary<string, AudiovisualLibraryEntry> candidate;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            var rows = JsonSerializer.Deserialize<List<AudiovisualLibraryEntry>>(original, JsonOptions)
                ?? throw new InvalidDataException("Invalid legacy library.");
            candidate = AudiovisualLibraryMapping.Migrate(rows, document.RootElement.EnumerateArray().ToArray());
            candidate = AudiovisualLibraryMapping.ValidateVersion2(candidate.Values);
            // Back up the exact bytes that were successfully read and validated; never overwrite a prior backup.
            string digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)).ToLowerInvariant();
            string backup = _storagePath + ".v1-" + digest + ".bak";
            if (!File.Exists(backup))
            {
                string backupTemporary = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await WriteNewFileAsync(backupTemporary, original, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(backupTemporary, backup);
                }
                finally { if (File.Exists(backupTemporary)) File.Delete(backupTemporary); }
            }
            if (!(await File.ReadAllBytesAsync(backup, cancellationToken).ConfigureAwait(false)).SequenceEqual(original))
                throw new InvalidDataException("Existing migration backup failed verification.");
            await SaveLockedAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var saved = JsonSerializer.Deserialize<AudiovisualLibraryDocument>(original, JsonOptions);
            if (saved is not { Version: 2, Entries: not null })
                throw new InvalidDataException("Unsupported audiovisual library version; original file preserved.");
            candidate = AudiovisualLibraryMapping.ValidateVersion2(saved.Entries);
        }
        _entries = candidate;
    }

    private static async Task WriteNewFileAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task SaveLockedAsync(Dictionary<string, AudiovisualLibraryEntry> candidate, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_storagePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = _storagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new AudiovisualLibraryDocument(2,
                candidate.Values.OrderBy(e => e.Key.StableId, StringComparer.Ordinal).ToArray()), JsonOptions);
            await WriteNewFileAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _commit(temporaryPath, _storagePath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void CommitFile(string temporary, string destination)
    {
        if (File.Exists(destination)) File.Replace(temporary, destination, null);
        else File.Move(temporary, destination);
    }
}
