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
                PRIMARY KEY (MediaId, EpisodeId)
            );
            """, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> ApplyJournaledResumeAsync(string mediaId, string episodeId, double position,
        string journalId, long revision, CancellationToken cancellationToken = default)
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
        command.CommandText = "SELECT JournalId, Revision FROM ResumeJournalRevisions WHERE MediaId = $media AND EpisodeId = $unit;";
        var media = command.CreateParameter(); media.ParameterName = "$media"; media.Value = mediaId;
        var unit = command.CreateParameter(); unit.ParameterName = "$unit"; unit.Value = episodeId;
        command.Parameters.Add(media); command.Parameters.Add(unit);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            // Revisions order observations within one journal only. A recreated journal
            // (deleted or quarantined sidecar) holds newer observations than its old binding.
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                reader.GetString(0) == journalId && reader.GetInt64(1) >= revision)
                return false;
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
        command.Parameters.Remove(value);
        command.CommandText = """
            INSERT INTO ResumeJournalRevisions (MediaId, EpisodeId, JournalId, Revision) VALUES ($media, $unit, $journal, $revision)
            ON CONFLICT (MediaId, EpisodeId) DO UPDATE SET JournalId = excluded.JournalId, Revision = excluded.Revision;
            """;
        var id = command.CreateParameter(); id.ParameterName = "$journal"; id.Value = journalId;
        var order = command.CreateParameter(); order.ParameterName = "$revision"; order.Value = revision;
        command.Parameters.Add(id); command.Parameters.Add(order);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
