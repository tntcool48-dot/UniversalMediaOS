using System.Collections.Concurrent;
using UniversalMediaOS.Core.Data;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;

namespace UniversalMediaOS.Core.Services;

/// <summary>Orders catalog playback progress across independent player tabs. SQLite owns unit
/// positions; Movie/TV library summaries describe only the most recently watched unit.</summary>
public sealed class PlaybackProgressService
{
    private readonly AudiovisualLibraryService _library;
    private readonly Func<DatabaseContext> _createDatabase;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(string Work, string Unit), long> _owners = new();
    private readonly ConcurrentDictionary<string, long> _activeWorkOwners = new();
    private readonly ConcurrentDictionary<(string Work, string Unit), long> _latestWrites = new();
    private long _sequence;

    public PlaybackProgressService(AudiovisualLibraryService library)
        : this(library, () => new DatabaseContext()) { }

    internal PlaybackProgressService(AudiovisualLibraryService library, Func<DatabaseContext> createDatabase)
    {
        _library = library;
        _createDatabase = createDatabase;
    }

    public (PlaybackProgressSession Session, double Position) Open(AudiovisualPlaybackContext context)
    {
        if (context.UnitKey == null) throw new ArgumentException("Progress requires an established unit.", nameof(context));
        return Open(new PlaybackProgressContext(context.WorkKey, context.UnitKey, context));
    }

    public (PlaybackProgressSession Session, double Position) Open(PlaybackProgressContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.WorkKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.UnitKey);
        if (!_gate.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("Waiting to load catalog playback progress.");
        try
        {
            using var database = _createDatabase();
            database.Database.EnsureCreated();
            var rows = database.ResumeStates.Where(row => row.MediaId == context.WorkKey && row.EpisodeId == context.UnitKey).Take(2).ToArray();
            double position = rows.Length == 1 ? rows[0].PositionSeconds : 0;
            if (rows.Length == 0 && context.LegacyWorkKey != null && context.LegacyUnitKey != null)
            {
                var legacy = database.ResumeStates.Where(row => row.MediaId == context.LegacyWorkKey &&
                    row.EpisodeId == context.LegacyUnitKey).Take(2).ToArray();
                if (legacy.Length == 1)
                {
                    position = legacy[0].PositionSeconds;
                    database.SaveResumeState(context.WorkKey, context.UnitKey, position);
                    AppLogger.Log($"[Resume] Imported verified alias '{context.LegacyWorkKey}/{context.LegacyUnitKey}' into '{context.WorkKey}/{context.UnitKey}'; legacy row retained.");
                }
            }
            var session = new PlaybackProgressSession(context, Interlocked.Increment(ref _sequence));
            _owners[(context.WorkKey, context.UnitKey)] = session.Owner;
            _activeWorkOwners[context.WorkKey] = session.Owner;
            return (session, position);
        }
        finally { _gate.Release(); }
    }

    public PlaybackProgressSession TakeOwnership(PlaybackProgressSession session)
    {
        var key = (session.Context.WorkKey, session.Context.UnitKey!);
        if (_owners.GetValueOrDefault(key) == session.Owner)
        {
            _activeWorkOwners[session.Context.WorkKey] = session.Owner;
            return session;
        }
        var updated = session with { Owner = Interlocked.Increment(ref _sequence) };
        _owners[key] = updated.Owner;
        _activeWorkOwners[session.Context.WorkKey] = updated.Owner;
        return updated;
    }

    public PlaybackProgressWrite? Capture(PlaybackProgressSession session, double position, double duration, bool completed)
    {
        if (!double.IsFinite(position) || !double.IsFinite(duration) || position < 0 || duration < 0) return null;
        var key = (session.Context.WorkKey, session.Context.UnitKey!);
        // Closing an older paused tab must not replace the newer provider's progress.
        if (_owners.GetValueOrDefault(key) != session.Owner) return null;
        long sequence = Interlocked.Increment(ref _sequence);
        _latestWrites.AddOrUpdate(key, sequence, (_, previous) => Math.Max(previous, sequence));
        return new(session, completed ? 0 : position, duration, sequence, DateTimeOffset.UtcNow);
    }

    public async Task SaveAsync(PlaybackProgressWrite write)
    {
        var context = write.Session.Context;
        var key = (context.WorkKey, context.UnitKey!);
        bool Current() => _owners.GetValueOrDefault(key) == write.Session.Owner &&
            _latestWrites.GetValueOrDefault(key) == write.Sequence;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Current()) return;
            using var database = _createDatabase();
            await database.Database.EnsureCreatedAsync().ConfigureAwait(false);
            if (!Current()) return;
            await database.SaveResumeStateAsync(context.WorkKey, context.UnitKey!, write.Position).ConfigureAwait(false);
            AppLogger.Log($"[Resume] Saved position {write.Position:0.0}s for media '{context.WorkKey}', episode '{context.UnitKey}'.");
            // The immutable observation time prevents another unit's delayed save from
            // changing the last-unit summary. A JSON failure cannot undo SQLite progress.
            try
            {
                if (context.Audiovisual is { } audiovisual && _activeWorkOwners.GetValueOrDefault(context.WorkKey) == write.Session.Owner)
                    await _library.RecordProgressAsync(AudiovisualLibraryKey.Create(audiovisual.Identity, context.WorkKey),
                        audiovisual.Title, audiovisual.PosterUrl, audiovisual.Unit.SeasonNumber, audiovisual.Unit.EpisodeNumber,
                        write.Position, write.Duration, observedUtc: write.ObservedUtc).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Resume] Movie/TV library summary could not be saved: {ex.Message}", "WARNING");
            }
        }
        finally { _gate.Release(); }
    }
}

public sealed record PlaybackProgressContext(string WorkKey, string UnitKey,
    AudiovisualPlaybackContext? Audiovisual = null, string? LegacyWorkKey = null, string? LegacyUnitKey = null);
public sealed record PlaybackProgressSession(PlaybackProgressContext Context, long Owner);
public sealed record PlaybackProgressWrite(PlaybackProgressSession Session, double Position,
    double Duration, long Sequence, DateTimeOffset ObservedUtc);
