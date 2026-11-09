using System.Data.Common;

namespace VisionIQ.Ingestor.Checkpoints;

/// <summary>
/// Checkpoints in evidence.connector_checkpoints (S04-14), written with the API database role.
/// The table comes from Alembic revision 20261030_0002.
/// </summary>
public sealed class DbCheckpointStore : ICheckpointStore
{
    public const string SelectSql =
        "SELECT cursor_value, updated_at FROM evidence.connector_checkpoints WHERE source_id = @source_id AND scope = @scope";

    // Guarded, not a blind upsert: TimeCursor.Format() puts a fixed-width ISO-8601 timestamp first, which
    // sorts lexicographically the same as chronologically, so a plain text ">=" comparison is a correct,
    // cheap monotonic check. Without this guard, two overlapping runs for the same (source_id, scope) --
    // e.g. a scheduled sync firing again before the previous one finished -- could race: whichever write
    // lands last at the database wins, even if it represents LESS progress than the one it overwrites.
    // The WHERE clause on DO UPDATE makes Postgres skip the write entirely (affecting 0 rows) rather than
    // regress the checkpoint; SaveAsync below turns that into an explicit, loud failure instead of a
    // silent no-op, because a silently-skipped save is exactly the kind of thing "always on" unattended
    // operation must never hide.
    public const string UpsertSql =
        "INSERT INTO evidence.connector_checkpoints (source_id, scope, cursor_value, updated_at) "
        + "VALUES (@source_id, @scope, @cursor_value, @updated_at) "
        + "ON CONFLICT (source_id, scope) DO UPDATE SET cursor_value = EXCLUDED.cursor_value, updated_at = EXCLUDED.updated_at "
        + "WHERE evidence.connector_checkpoints.cursor_value <= EXCLUDED.cursor_value";

    private readonly Func<DbConnection> connectionFactory;

    public DbCheckpointStore(Func<DbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        this.connectionFactory = connectionFactory;
    }

    public async Task<SyncCheckpoint?> GetAsync(string sourceId, string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var connection = connectionFactory();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = SelectSql;
                AddParameter(command, "source_id", sourceId);
                AddParameter(command, "scope", scope);
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }

                    return new SyncCheckpoint(sourceId, scope, reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1));
                }
            }
        }
    }

    public async Task SaveAsync(SyncCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var connection = connectionFactory();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = UpsertSql;
                AddParameter(command, "source_id", checkpoint.SourceId);
                AddParameter(command, "scope", checkpoint.Scope);
                AddParameter(command, "cursor_value", checkpoint.Cursor);
                AddParameter(command, "updated_at", checkpoint.UpdatedAt.ToUniversalTime());
                var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (affected == 0)
                {
                    // The WHERE guard on UpsertSql rejected this write: another run already saved a
                    // checkpoint that is equal to or further ahead than this one. Report it as a failure
                    // rather than returning normally, so the caller's existing "checkpoint could not be
                    // saved" handling applies -- this must never look like a successful save.
                    throw new InvalidOperationException(
                        "Checkpoint not saved: a concurrent run already advanced " + checkpoint.SourceId + "/"
                        + checkpoint.Scope + " to the same point or further. This run's progress was not lost "
                        + "(records were already written); only this checkpoint write was skipped.");
                }
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
