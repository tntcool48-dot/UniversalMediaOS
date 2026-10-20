using System.Text.Json;
using System.Text.Json.Serialization;
using UniversalMediaOS.Core.Helpers;
using UniversalMediaOS.Core.OtherMedia;

namespace UniversalMediaOS.Core.Services;

/// <summary>A file-backed acceptance boundary which never waits for the media database.
/// The file sits beside its database and holds only accepted observations; ownership is
/// process-local, like catalog sessions, so claiming a unit performs no file IO.</summary>
internal sealed class DurableResumeJournal
{
    private readonly object _queueGate = new();
    private Task _tail = Task.CompletedTask;
    private readonly List<Task> _unobserved = [];
    private readonly Dictionary<string, StageSlot> _queuedStages = [];
    private readonly Dictionary<(string Database, string Work, string Unit), string> _owners = [];
    private readonly Dictionary<(string Database, string Work), string> _activeWorkOwners = [];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    internal PlaybackProgressClaim Begin(string databasePath, PlaybackProgressContext context,
        PlaybackProgressSummary? summary)
    {
        var claim = new PlaybackProgressClaim(databasePath, context, Guid.NewGuid().ToString("N"), summary);
        lock (_queueGate)
        {
            // A newer player for this unit supersedes older claims, including their late callbacks.
            _owners[(Key(databasePath), context.WorkKey, context.UnitKey)] = claim.Token;
            _activeWorkOwners[(Key(databasePath), context.WorkKey)] = claim.Token;
        }
        return claim;
    }

    internal Task<PlaybackProgressReceipt?> StageAsync(PlaybackProgressClaim claim,
        PlaybackProgressObservation observation)
    {
        if (!double.IsFinite(observation.Position) || !double.IsFinite(observation.Duration) ||
            observation.Position < 0 || observation.Duration < 0)
            return Task.FromResult<PlaybackProgressReceipt?>(null);
        lock (_queueGate)
        {
            // Ownership is decided in observation order, before any file IO.
            if (_owners.GetValueOrDefault((Key(claim.DatabasePath), claim.Context.WorkKey, claim.Context.UnitKey)) != claim.Token)
                return Task.FromResult<PlaybackProgressReceipt?>(null);
            if (_queuedStages.TryGetValue(claim.Token, out var queued))
            {
                queued.Observation = observation;
                return queued.Completion;
            }
            var slot = new StageSlot(observation);
            _queuedStages[claim.Token] = slot;
            slot.Completion = Enqueue(() =>
            {
                PlaybackProgressObservation captured;
                lock (_queueGate)
                {
                    captured = slot.Observation;
                    if (_queuedStages.GetValueOrDefault(claim.Token) == slot) _queuedStages.Remove(claim.Token);
                }
                return MutateAsync<PlaybackProgressReceipt?>(claim.DatabasePath, document =>
                {
                    long revision = checked(++document.Revision);
                    var receipt = new PlaybackProgressReceipt(claim.DatabasePath, document.JournalId, revision,
                        claim.Token, claim.Context.WorkKey, claim.Context.UnitKey,
                        captured.Completed ? 0 : captured.Position, captured.Duration,
                        captured.Completed, captured.ObservedUtc, claim.Summary);
                    document.Pending.RemoveAll(value => value.WorkKey == receipt.WorkKey && value.UnitKey == receipt.UnitKey);
                    document.Pending.Add(receipt);
                    return receipt;
                });
            });
            return slot.Completion;
        }
    }

    internal Task<PlaybackProgressReceipt[]> ReadPendingAsync(string databasePath)
        => Enqueue(() => ReadAsync(databasePath, document => document.Pending.OrderBy(value => value.Revision).ToArray()));

    internal Task<PlaybackProgressReceipt?> FindPendingAsync(PlaybackProgressReceipt receipt)
        => Enqueue(() => ReadAsync(receipt.DatabasePath, document => document.Pending.SingleOrDefault(value =>
            value.JournalId == receipt.JournalId && value.Revision == receipt.Revision &&
            value.WorkKey == receipt.WorkKey && value.UnitKey == receipt.UnitKey)));

    /// <summary>A replayed observation from another process has no local owner; the library
    /// summary orders it by observation time instead.</summary>
    internal bool MayUpdateSummary(PlaybackProgressReceipt receipt)
    {
        lock (_queueGate)
            return !_activeWorkOwners.TryGetValue((Key(receipt.DatabasePath), receipt.WorkKey), out string? active) ||
                active == receipt.OwnerToken;
    }

    internal Task AcknowledgeAsync(PlaybackProgressReceipt receipt)
        => Enqueue(() => MutateAsync(receipt.DatabasePath, document =>
        {
            document.Pending.RemoveAll(value => value.JournalId == receipt.JournalId && value.Revision == receipt.Revision &&
                value.WorkKey == receipt.WorkKey && value.UnitKey == receipt.UnitKey);
            return true;
        }));

    /// <summary>No accepted observation is still being written, and no write failure is unreported.</summary>
    internal bool IsSettled
    {
        get { lock (_queueGate) return _unobserved.All(task => task.IsCompletedSuccessfully); }
    }

    internal async Task FlushAsync()
    {
        Task[] pending;
        lock (_queueGate) pending = _unobserved.ToArray();
        try
        {
            // Include failures instead of reporting a successful durable acknowledgement.
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        finally
        {
            // Concurrent flushes still see in-flight work; a reported failure is not repeated.
            lock (_queueGate) _unobserved.RemoveAll(task => task.IsFaulted && pending.Contains(task));
        }
    }

    private Task<T> Enqueue<T>(Func<Task<T>> operation)
    {
        lock (_queueGate)
        {
            Task preceding = _tail;
            Task<T> next = Task.Run(async () =>
            {
                try { await preceding.ConfigureAwait(false); } catch { /* Each operation retains its own failure. */ }
                return await operation().ConfigureAwait(false);
            });
            _tail = next;
            _unobserved.RemoveAll(task => task.IsCompletedSuccessfully || task.IsCanceled);
            _unobserved.Add(next);
            return next;
        }
    }

    private static async Task<T> ReadAsync<T>(string databasePath, Func<JournalDocument, T> read)
    {
        using var lease = await AcquireAsync(databasePath).ConfigureAwait(false);
        return read(Load(databasePath));
    }

    private static async Task<T> MutateAsync<T>(string databasePath, Func<JournalDocument, T> mutate)
    {
        using var lease = await AcquireAsync(databasePath).ConfigureAwait(false);
        JournalDocument document = Load(databasePath);
        T result = mutate(document);
        string path = JournalPath(databasePath);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            // Only this operation's own unfinished temporary file may be removed.
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        return result;
    }

    internal static string JournalPath(string databasePath) => databasePath + ".resume-pending.json";

    private static async Task<FileStream> AcquireAsync(string databasePath)
    {
        string path = JournalPath(databasePath) + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(20).ConfigureAwait(false); }
        }
    }

    /// <summary>Call only while holding the journal lock.</summary>
    private static JournalDocument Load(string databasePath)
    {
        string path = JournalPath(databasePath);
        if (!File.Exists(path)) return new JournalDocument();
        try
        {
            JournalDocument document = Parse(path);
            // The file is bound to its database by location: a moved profile or drive alias
            // still names the same journal. In-process receipts route by the current path.
            document.Pending = document.Pending.Select(value => value with { DatabasePath = databasePath }).ToList();
            return document;
        }
        catch (InvalidDataException ex)
        {
            // Unreadable records cannot be replayed. Keep their bytes for diagnosis, but do not
            // let one damaged sidecar stop every later position from becoming durable.
            string retained = $"{path}.damaged-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            File.Move(path, retained);
            AppLogger.Log($"[Resume] {ex.Message} It was retained as '{Path.GetFileName(retained)}'.", "ERROR");
            return new JournalDocument();
        }
    }

    private static JournalDocument Parse(string path)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > 16 * 1024 * 1024)
            throw new InvalidDataException("The pending-resume journal has an invalid size.");
        JournalDocument? document;
        try { document = JsonSerializer.Deserialize<JournalDocument>(File.ReadAllBytes(path), JsonOptions); }
        catch (JsonException ex) { throw new InvalidDataException("The pending-resume journal is malformed.", ex); }
        if (document == null || document.SchemaVersion != 2 ||
            !Guid.TryParseExact(document.JournalId, "N", out _) || document.Revision < 0 || document.Pending == null)
            throw new InvalidDataException("The pending-resume journal has an unknown format.");
        if (document.Pending.GroupBy(value => (value.WorkKey, value.UnitKey)).Any(group => group.Count() != 1) ||
            document.Pending.Any(value => !ValidReceipt(value, document)))
            throw new InvalidDataException("The pending-resume journal contains invalid records.");
        return document;
    }

    private static bool ValidReceipt(PlaybackProgressReceipt? value, JournalDocument document) =>
        value != null && value.JournalId == document.JournalId &&
        value.Revision > 0 && value.Revision <= document.Revision && Guid.TryParseExact(value.OwnerToken, "N", out _) &&
        !string.IsNullOrWhiteSpace(value.WorkKey) && !string.IsNullOrWhiteSpace(value.UnitKey) &&
        double.IsFinite(value.Position) && value.Position >= 0 && double.IsFinite(value.Duration) && value.Duration >= 0 &&
        (!value.Completed || value.Position == 0) &&
        (value.Summary == null || (!string.IsNullOrWhiteSpace(value.Summary.StoragePath) &&
            Path.IsPathFullyQualified(value.Summary.StoragePath) && value.Summary.Key != null && value.Summary.Key.WorkKey == value.WorkKey));

    internal static bool PathsEqual(string? left, string? right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Key(string databasePath) =>
        OperatingSystem.IsWindows() ? databasePath.ToUpperInvariant() : databasePath;

    private sealed class JournalDocument
    {
        [JsonRequired]
        public int SchemaVersion { get; set; } = 2;
        [JsonRequired]
        public string JournalId { get; set; } = Guid.NewGuid().ToString("N");
        [JsonRequired]
        public long Revision { get; set; }
        [JsonRequired]
        public List<PlaybackProgressReceipt> Pending { get; set; } = [];
    }

    private sealed class StageSlot(PlaybackProgressObservation observation)
    {
        public PlaybackProgressObservation Observation { get; set; } = observation;
        public Task<PlaybackProgressReceipt?> Completion { get; set; } = Task.FromResult<PlaybackProgressReceipt?>(null);
    }
}

public sealed class PlaybackProgressClaim
{
    public string DatabasePath { get; }
    public PlaybackProgressContext Context { get; }
    internal string Token { get; }
    internal PlaybackProgressSummary? Summary { get; }

    internal PlaybackProgressClaim(string databasePath, PlaybackProgressContext context, string token, PlaybackProgressSummary? summary)
    { DatabasePath = databasePath; Context = context; Token = token; Summary = summary; }
}

public sealed record PlaybackProgressReceipt(string DatabasePath, string JournalId, long Revision, string OwnerToken,
    string WorkKey, string UnitKey, double Position, double Duration, bool Completed, DateTimeOffset ObservedUtc,
    PlaybackProgressSummary? Summary = null);

public sealed record PlaybackProgressSummary(string StoragePath, AudiovisualLibraryKey Key, string Title,
    string PosterUrl, int? SeasonNumber, int? EpisodeNumber);
