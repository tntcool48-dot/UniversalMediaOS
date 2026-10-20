using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
    private readonly DurableResumeJournal _durableJournal = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(string Work, string Unit), long> _owners = new();
    private readonly ConcurrentDictionary<string, long> _activeWorkOwners = new();
    private readonly ConcurrentDictionary<(string Work, string Unit), long> _latestWrites = new();
    private readonly ConcurrentDictionary<long, Task> _pendingPersistence = new();
    private long _persistenceSequence;
    private long _sequence;

    public const int PersistenceAdmissionThreshold = 64;
    public int PendingPersistenceCount => _pendingPersistence.Count;
    public bool IsPersistenceAdmissionFull => PendingPersistenceCount >= PersistenceAdmissionThreshold;

    public async Task WaitForPersistenceAdmissionAsync(CancellationToken cancellationToken)
    {
        while (IsPersistenceAdmissionFull)
        {
            Task[] pending = _pendingPersistence.Values.ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAny(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<T> RunSerializedPersistenceAsync<T>(Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        // Reserve ordering before an operation awaits its earlier read/save.
        // Legacy and canonical paths share one worker instead of busy-waiting
        // on SQLite from one ThreadPool thread per legacy player.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public Task RunSerializedPersistenceAsync(Func<Task> operation)
        => RunSerializedPersistenceAsync(async () => { await operation().ConfigureAwait(false); return 0; });

    public void TrackPendingPersistence(Task operation)
    {
        if (operation.IsCompletedSuccessfully) return;
        long id = Interlocked.Increment(ref _persistenceSequence);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPersistence[id] = observed.Task;
        _ = ObserveAsync();
        async Task ObserveAsync()
        {
            try { await operation.ConfigureAwait(false); }
            catch (Exception ex) { AppLogger.Log($"[Resume] Accepted persistence operation failed: {ex.Message}", "WARNING"); }
            finally
            {
                _pendingPersistence.TryRemove(id, out _);
                observed.TrySetResult();
            }
        }
    }

    public async Task FlushAsync()
    {
        // Includes accepted writes owned by tabs already removed from the UI.
        // Waiting never cancels those writes or touches native player handles.
        while (true)
        {
            Task[] pending = _pendingPersistence.Values.ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    public PlaybackProgressService(AudiovisualLibraryService library)
        : this(library, () => new DatabaseContext()) { }

    internal PlaybackProgressService(AudiovisualLibraryService library, Func<DatabaseContext> createDatabase)
    {
        _library = library;
        _createDatabase = createDatabase;
    }

    /// <summary>Claims an admitted identity without file IO, SQLite or its ordered worker.
    /// The newest claim for a unit owns its later observations.</summary>
    public PlaybackProgressClaim BeginDurableSession(PlaybackProgressContext context, DatabaseContext? boundDatabase = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.WorkKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.UnitKey);
        using var ownedDatabase = boundDatabase == null ? _createDatabase() : null;
        string destination = CaptureResumeDestination(boundDatabase ?? ownedDatabase!);
        PlaybackProgressSummary? summary = null;
        if (context.Audiovisual is { } audiovisual)
        {
            // As on the direct path, an unusable summary identity never blocks unit progress.
            try
            {
                summary = new(_library.StoragePath, AudiovisualLibraryKey.Create(audiovisual.Identity, context.WorkKey),
                    audiovisual.Title, audiovisual.PosterUrl, audiovisual.Unit.SeasonNumber, audiovisual.Unit.EpisodeNumber);
            }
            catch (ArgumentException ex)
            {
                AppLogger.Log($"[Resume] Movie/TV library summary is unavailable for '{context.WorkKey}': {ex.Message}", "WARNING");
            }
        }
        return _durableJournal.Begin(destination, context, summary);
    }

    public Task<PlaybackProgressReceipt?> StageDurableObservationAsync(PlaybackProgressClaim claim,
        PlaybackProgressObservation observation) => _durableJournal.StageAsync(claim, observation);

    /// <summary>Waits only for accepted file observations, never the SQLite writer gate.</summary>
    public Task FlushDurableObservationsAsync() => _durableJournal.FlushAsync();

    public Task CommitDurableObservationAsync(PlaybackProgressReceipt receipt)
        => CommitDurableObservationAsync(Task.FromResult<PlaybackProgressReceipt?>(receipt));

    /// <summary>Reserves SQLite order before waiting for the receipt or an already-started initial read.</summary>
    public Task CommitDurableObservationAsync(Task<PlaybackProgressReceipt?> receipt, Task? preceding = null)
        => CommitDurableObservationAsync(() => receipt, preceding);

    public async Task CommitDurableObservationAsync(Func<Task<PlaybackProgressReceipt?>> receipt, Task? preceding = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (preceding != null) await preceding.ConfigureAwait(false);
            var accepted = await receipt().ConfigureAwait(false);
            if (accepted != null) await CommitDurableUnderGateAsync(accepted).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Call within the shared persistence order, before reading this destination's resume rows.
    /// A replay failure leaves its records pending and never prevents reading the saved rows.</summary>
    public async Task ReplayDurableObservationsAsync(DatabaseContext boundDatabase)
    {
        try
        {
            string destination = CaptureResumeDestination(boundDatabase);
            if (!File.Exists(DurableResumeJournal.JournalPath(destination))) return;
            foreach (var receipt in await _durableJournal.ReadPendingAsync(destination).ConfigureAwait(false))
            {
                try { await CommitDurableUnderGateAsync(receipt).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    AppLogger.Log($"[Resume] Pending position for media '{receipt.WorkKey}', episode '{receipt.UnitKey}' stays journaled: {ex.Message}", "WARNING");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Resume] Pending positions could not be replayed: {ex.Message}", "WARNING");
        }
    }

    private async Task CommitDurableUnderGateAsync(PlaybackProgressReceipt requested)
    {
        // A stale acknowledgement cannot remove a newer observation. Always use the actual durable record.
        var receipt = await _durableJournal.FindPendingAsync(requested).ConfigureAwait(false);
        if (receipt == null) return;
        bool applied = await Task.Run(async () =>
        {
            using var database = new DatabaseContext(receipt.DatabasePath);
            return await database.ApplyJournaledResumeAsync(receipt.WorkKey, receipt.UnitKey,
                receipt.Position, receipt.JournalId, receipt.Revision).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (applied)
        {
            AppLogger.Log($"[Resume] Saved durable position {receipt.Position:0.0}s for media '{receipt.WorkKey}', episode '{receipt.UnitKey}'.");
            try
            {
                // Another profile's summary is never written into this library.
                if (receipt.Summary is { } captured && _durableJournal.MayUpdateSummary(receipt) &&
                    DurableResumeJournal.PathsEqual(captured.StoragePath, _library.StoragePath))
                    await _library.RecordProgressAsync(captured.Key, captured.Title, captured.PosterUrl,
                        captured.SeasonNumber, captured.EpisodeNumber, receipt.Position, receipt.Duration,
                        observedUtc: receipt.ObservedUtc).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Log($"[Resume] Movie/TV library summary could not be saved: {ex.Message}", "WARNING");
            }
        }
        await _durableJournal.AcknowledgeAsync(receipt).ConfigureAwait(false);
    }

    private static string CaptureResumeDestination(DatabaseContext database)
    {
        if (!IsFileBacked(database))
            throw new InvalidOperationException("Durable playback progress requires a file-backed database.");
        return Path.GetFullPath(database.Database.GetDbConnection().DataSource);
    }

    /// <summary>Explicit volatile stores have no crash-recovery destination.</summary>
    public static bool IsFileBacked(DatabaseContext database)
    {
        return database.Database.GetDbConnection() is SqliteConnection sqlite &&
            !string.IsNullOrWhiteSpace(sqlite.DataSource) && sqlite.DataSource != ":memory:" &&
            new SqliteConnectionStringBuilder(sqlite.ConnectionString).Mode != SqliteOpenMode.Memory;
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
            return ReadOpen(database, context);
        }
        finally { _gate.Release(); }
    }

    public async Task<(PlaybackProgressSession Session, double Position)> OpenAsync(
        PlaybackProgressContext context, Task? precedingSaves = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context.WorkKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.UnitKey);
        using var database = _createDatabase();
        // Capture the destination before a queued load yields, just as saves do.
        _ = database.Database.GetDbConnection();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (precedingSaves != null) await precedingSaves.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await Task.Run(() => ReadOpen(database, context), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private (PlaybackProgressSession Session, double Position) ReadOpen(DatabaseContext database, PlaybackProgressContext context)
    {
        // The replay worker uses only immutable file records, never native/player state.
        if (IsFileBacked(database))
            ReplayDurableObservationsAsync(database).GetAwaiter().GetResult();
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

    public async Task<PlaybackProgressSession?> TakeOwnershipWhenOpenedAsync(Task<PlaybackProgressSession?> opening)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = await opening.ConfigureAwait(false);
            return session == null ? null : TakeOwnership(session);
        }
        finally { _gate.Release(); }
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
        if (!Current()) return;
        using var database = _createDatabase();
        // Resolve this profile's connection before yielding; a delayed worker must
        // not pick up a subsequently changed data root or database setting.
        _ = database.Database.GetDbConnection();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await PersistAsync(database, write).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public Task SaveWhenOpenedAsync(Task<PlaybackProgressSession?> opening, double position, double duration,
        bool completed, DateTimeOffset observedUtc, Func<bool> isLatest)
        => SaveWhenOpenedAsync(opening, () => isLatest()
            ? new PlaybackProgressObservation(position, duration, completed, observedUtc) : null);

    public async Task SaveWhenOpenedAsync(Task<PlaybackProgressSession?> opening,
        Func<PlaybackProgressObservation?> capture)
    {
        using var database = _createDatabase();
        _ = database.Database.GetDbConnection();
        // Reserve order before awaiting the load. Otherwise a newer provider's
        // Open could overtake progress already accepted by its older player.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = await opening.ConfigureAwait(false);
            var observation = capture();
            if (session == null || observation == null) return;
            var write = Capture(session, observation.Position, observation.Duration, observation.Completed);
            if (write != null) await PersistAsync(database, write with { ObservedUtc = observation.ObservedUtc }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task PersistAsync(DatabaseContext database, PlaybackProgressWrite write)
    {
        var context = write.Session.Context;
        var key = (context.WorkKey, context.UnitKey!);
        bool Current() => _owners.GetValueOrDefault(key) == write.Session.Owner &&
            _latestWrites.GetValueOrDefault(key) == write.Sequence;
        // SQLite's async API can execute synchronously, including busy waits.
        // Acquire ordering first so an immediate Open still waits for this save.
        await Task.Run(async () =>
        {
            if (!Current()) return;
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
        }).ConfigureAwait(false);
    }
}

public sealed record PlaybackProgressContext(string WorkKey, string UnitKey,
    AudiovisualPlaybackContext? Audiovisual = null, string? LegacyWorkKey = null, string? LegacyUnitKey = null);
public sealed record PlaybackProgressSession(PlaybackProgressContext Context, long Owner);
public sealed record PlaybackProgressWrite(PlaybackProgressSession Session, double Position,
    double Duration, long Sequence, DateTimeOffset ObservedUtc);
public sealed record PlaybackProgressObservation(double Position, double Duration,
    bool Completed, DateTimeOffset ObservedUtc);
