using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace UniversalMediaOS.Core.Data;

public partial class DatabaseContext
{
    private readonly string? _resumeDestinationPath;

    public DatabaseContext() { }

    internal DatabaseContext(string frozenDatabasePath)
    {
        if (!Path.IsPathFullyQualified(frozenDatabasePath))
            throw new ArgumentException("A resume destination must be an absolute database path.", nameof(frozenDatabasePath));
        _resumeDestinationPath = Path.GetFullPath(frozenDatabasePath);
    }

    internal async Task EnsureResumePersistenceSchemaAsync(CancellationToken cancellationToken = default)
    {
        await Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        // EnsureCreated does not add tables to an existing user database.
        await Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS ResumeJournalRevisions (
                MediaId TEXT NOT NULL,
                EpisodeId TEXT NOT NULL,
                JournalId TEXT NOT NULL,
                Revision INTEGER NOT NULL,
                AppliedPosition REAL NULL,
                PRIMARY KEY (MediaId, EpisodeId)
            );
            """, cancellationToken).ConfigureAwait(false);
        // Tables created by the first journal build lack the applied position; existing rows keep NULL.
        var connection = Database.GetDbConnection();
        bool opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var columns = connection.CreateCommand();
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ResumeJournalRevisions') WHERE name = 'AppliedPosition';";
            if (Convert.ToInt64(await columns.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0)
                await Database.ExecuteSqlRawAsync("ALTER TABLE ResumeJournalRevisions ADD COLUMN AppliedPosition REAL NULL;",
                    cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (opened) await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <param name="basePosition">The row value the observing session last read for this unit, or null when
    /// unknown (records written before it was recorded).</param>
    internal async Task<bool> ApplyJournaledResumeAsync(string mediaId, string episodeId, double position,
        string journalId, long revision, double? basePosition = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(episodeId);
        if (!double.IsFinite(position) || position < 0 || revision <= 0 || !Guid.TryParseExact(journalId, "N", out _))
            throw new ArgumentException("The journaled resume record is invalid.");
        await EnsureResumePersistenceSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var connection = Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT JournalId, Revision, AppliedPosition FROM ResumeJournalRevisions WHERE MediaId = $media AND EpisodeId = $unit;";
        var media = command.CreateParameter(); media.ParameterName = "$media"; media.Value = mediaId;
        var unit = command.CreateParameter(); unit.ParameterName = "$unit"; unit.Value = episodeId;
        command.Parameters.Add(media); command.Parameters.Add(unit);
        double? applied = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Revisions order observations within one journal only. A recreated journal
                // (deleted or quarantined sidecar) holds newer observations than its old binding.
                if (reader.GetString(0) == journalId && reader.GetInt64(1) >= revision)
                    return false;
                if (!reader.IsDBNull(2)) applied = reader.GetDouble(2);
            }
        }
        if (basePosition is double known)
        {
            command.CommandText = "SELECT PositionSeconds FROM ResumeStates WHERE MediaId = $media AND EpisodeId = $unit LIMIT 1;";
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is double current &&
                !SamePosition(current, known) && !(applied is double last && SamePosition(current, last)))
            {
                // Since the observing session read the row and since the journal last applied it, a writer
                // without this journal (such as an older build) saved the unit later. Keep that newer save.
                UniversalMediaOS.Core.Helpers.AppLogger.Log($"[Resume] Kept a later position {current:0.0}s saved outside the journal for media '{mediaId}', episode '{episodeId}'; the pending {position:0.0}s is older.");
                return false;
            }
        }
        // Update before insert: older user databases need not carry the unique (MediaId, EpisodeId) index.
        command.CommandText = "UPDATE ResumeStates SET PositionSeconds = $position WHERE MediaId = $media AND EpisodeId = $unit;";
        var value = command.CreateParameter(); value.ParameterName = "$position"; value.Value = position;
        command.Parameters.Add(value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            command.CommandText = "INSERT INTO ResumeStates (MediaId, EpisodeId, PositionSeconds) VALUES ($media, $unit, $position);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        command.CommandText = """
            INSERT INTO ResumeJournalRevisions (MediaId, EpisodeId, JournalId, Revision, AppliedPosition)
            VALUES ($media, $unit, $journal, $revision, $position)
            ON CONFLICT (MediaId, EpisodeId) DO UPDATE SET JournalId = excluded.JournalId, Revision = excluded.Revision,
                AppliedPosition = excluded.AppliedPosition;
            """;
        var id = command.CreateParameter(); id.ParameterName = "$journal"; id.Value = journalId;
        var order = command.CreateParameter(); order.ParameterName = "$revision"; order.Value = revision;
        command.Parameters.Add(id); command.Parameters.Add(order);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool SamePosition(double left, double right) => Math.Abs(left - right) < 1e-9;
}
